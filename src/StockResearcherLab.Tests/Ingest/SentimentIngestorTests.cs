using System.Net;
using System.Text;
using System.Text.Json;
using Npgsql;
using StockResearcherLab.Core;
using StockResearcherLab.Core.Config;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;
using StockResearcherLab.Data.Eodhd;
using StockResearcherLab.Pipeline.Ingest;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Ingest;

/// <summary>
/// Checkpoint 1.6. Sentiment for the whole universe, with no pre-selection [D-23].
///
/// No live call. The field names this parser reads are the least-verified thing in
/// the phase, so the tests pin the behaviour around them: a shape that does not
/// match yields nothing rather than a fabricated row.
/// </summary>
[Collection("database")]
public sealed class SentimentIngestorTests
{
    private const string Marker = "SRLSENT";

    /// <summary>
    /// The documented envelope: an object keyed by ticker, each value an array of
    /// daily rows. 1.9 captured only the outer shape, so this is what live
    /// verification has to confirm.
    /// </summary>
    private const string Payload = """
        {
          "SRLSENT1.US": [
            {"date":"2026-08-03","count":4,"normalized":0.25},
            {"date":"2026-08-05","count":1,"normalized":-0.5}
          ],
          "SRLSENT2.US": [
            {"date":"2026-08-04","count":12,"normalized":0.9}
          ]
        }
        """;

    // ------------------------------------------------------------ the parser ---

    [Fact]
    public void TheEnvelopeIsReadAsTickerThenDailyRows()
    {
        using var doc = JsonDocument.Parse(Payload);
        var rows = SentimentIngestor.Parse(doc.RootElement);

        Assert.Equal(3, rows.Count);

        // Sorted by ticker then date, because COPY order reaches the table.
        Assert.Equal("SRLSENT1.US", rows[0].Ticker);
        Assert.Equal(new DateOnly(2026, 8, 3), rows[0].Date);
        Assert.Equal(4, rows[0].ArticleCount);
        Assert.Equal(0.25f, rows[0].Score);
        Assert.Equal(new DateOnly(2026, 8, 5), rows[1].Date);
        Assert.Equal("SRLSENT2.US", rows[2].Ticker);
    }

    /// <summary>
    /// The gap between 08-03 and 08-05 stays a gap. A day with no row is a day
    /// nobody wrote about, and a zero would say attention was exactly at baseline,
    /// which the screens read differently [D-12, CLAUDE.md section 6].
    /// </summary>
    [Fact]
    public void ADayWithNoRowIsNotFilledWithZero()
    {
        using var doc = JsonDocument.Parse(Payload);
        var rows = SentimentIngestor.Parse(doc.RootElement);

        var first = rows.Where(r => r.Ticker == "SRLSENT1.US").ToList();

        Assert.Equal(2, first.Count);
        Assert.DoesNotContain(first, r => r.Date == new DateOnly(2026, 8, 4));
    }

    /// <summary>
    /// A count that is absent stays null rather than becoming zero, which is the
    /// same distinction one level down.
    /// </summary>
    [Fact]
    public void AnAbsentCountOrScoreStaysNull()
    {
        using var doc = JsonDocument.Parse("""
            {"SRLSENT3.US":[{"date":"2026-08-05"}]}
            """);

        var row = Assert.Single(SentimentIngestor.Parse(doc.RootElement));

        Assert.Null(row.ArticleCount);
        Assert.Null(row.Score);
    }

    /// <summary>
    /// The field names are unverified against the provider, so a payload that does
    /// not match yields nothing rather than rows of nulls that would look like
    /// coverage. A shape change should show as absence, not as a silent flattening.
    /// </summary>
    [Fact]
    public void AShapeThatDoesNotMatchYieldsNothingRatherThanEmptyRows()
    {
        foreach (var body in new[]
                 {
                     "[]",
                     "\"NA\"",
                     """{"SRLSENT4.US":"NA"}""",
                     """{"SRLSENT4.US":[{"day":"2026-08-05","count":3}]}""",
                 })
        {
            using var doc = JsonDocument.Parse(body);
            Assert.Empty(SentimentIngestor.Parse(doc.RootElement));
        }
    }

    // --------------------------------------------------------- no narrowing ---

    /// <summary>
    /// The failure ARCHITECTURE.html section 20 names. Sentiment was once pulled for
    /// the top 400 by prior screen score, so a thinly covered name having the exact
    /// coverage spike the sentiment screen exists to catch had no data and could
    /// never be found.
    ///
    /// Asserted on the request: every universe name must appear in some call's
    /// symbol list, and the count of names asked for must equal the universe.
    /// </summary>
    [Fact]
    public async Task EveryUniverseNameIsAskedForAndNothingIsNarrowed()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClearAsync(ct).ConfigureAwait(true);

        var seeded = Enumerable.Range(1, 37)
            .Select(i => $"{Marker}{i:D2}.US")
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        await SeedUniverseAsync(seeded, ct).ConfigureAwait(true);

        // Asserted against whatever `security` actually holds rather than against
        // the seeded names alone. The stage reads the whole table by design, so a
        // test that only counted its own rows would pass while the stage narrowed
        // everything else away.
        var universeSize = await UniverseCountAsync(ct).ConfigureAwait(true);

        var handler = new RecordingHandler();
        var stage = new SentimentIngestor(
            new EodhdClient(new HttpClient(handler), "fake-token", new FixedClock(
                new DateTimeOffset(2026, 8, 7, 21, 0, 0, TimeSpan.Zero), new DateOnly(2026, 8, 7))));

        await stage.ExecuteAsync(ContextFor(stage, new DateOnly(2026, 8, 7), perCall: 10), ct)
            .ConfigureAwait(true);

        var asked = handler.Symbols.SelectMany(s => s.Split(',')).ToList();

        Assert.Equal(universeSize, asked.Distinct(StringComparer.Ordinal).Count());
        foreach (var t in seeded)
        {
            Assert.Contains(t, asked);
        }

        // Batched rather than one call per name.
        Assert.Equal((universeSize + 9) / 10, handler.Symbols.Count);

        await ClearAsync(ct).ConfigureAwait(true);
    }

    [Fact]
    public void TheDeclaredSetsMatchTheCatalogue()
    {
        var stage = new SentimentIngestor(
            new EodhdClient(new HttpClient(new RecordingHandler()), "fake-token", new FixedClock(
                new DateTimeOffset(2026, 8, 7, 21, 0, 0, TimeSpan.Zero), new DateOnly(2026, 8, 7))));

        Assert.Contains("SentimentIngestor", ArchitectureDocument.ComponentNames());

        // Two writes since 3.8, where this asserted one. `sentiment_fetch_attempt` is
        // what the sweep resumes on [D-99, 0011], and the two are named rather than
        // counted: a count would pass on any second write and this test is about which
        // ones there are.
        var write = Assert.Single(
            stage.WriteSet, w => string.Equals(w.Table, "sentiment_daily", StringComparison.Ordinal));
        Assert.Equal(SentimentIngestor.Columns, write.Columns);

        var attempt = Assert.Single(
            stage.WriteSet, w => string.Equals(w.Table, "sentiment_fetch_attempt", StringComparison.Ordinal));
        Assert.Equal(SentimentIngestor.AttemptColumns, attempt.Columns);

        Assert.Equal(2, stage.WriteSet.Count);

        // The universe, and `price_daily` since 3.8 for the in-window delisted names
        // [D-101], read by the range pass alone. **It must still not read a score**,
        // which is the property this line was written for.
        Assert.Equal(["security", "price_daily"], stage.ReadSet);
    }

    // --------------------------------------------------------------- helpers ---

    private static StageContext ContextFor(SentimentIngestor stage, DateOnly date, int perCall)
        => new(
            date, 1,
            new StageData(TestDatabase.ConnectionString, new DeclaredAccess(stage)),
            new FixedClock(new DateTimeOffset(date.Year, date.Month, date.Day, 21, 0, 0, TimeSpan.Zero), date),
            new StubConfig(perCall));

    private static async Task<int> UniverseCountAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM security WHERE is_active;", conn);
        return (int)(long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    private static async Task SeedUniverseAsync(IReadOnlyList<string> tickers, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        foreach (var t in tickers)
        {
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO security (ticker, is_active) VALUES (@t, true) ON CONFLICT (ticker) DO NOTHING;", conn);
            cmd.Parameters.AddWithValue("t", t);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task ClearAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        foreach (var sql in new[]
                 {
                     "DELETE FROM sentiment_daily WHERE ticker LIKE @p;",
                     "DELETE FROM security WHERE ticker LIKE @p;",
                 })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("p", Marker + "%");
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private sealed class StubConfig(int perCall) : IConfigStore
    {
        public Task<ConfigRow?> ResolveAsync(string key, DateOnly asOf, CancellationToken ct = default)
            => Task.FromResult<ConfigRow?>(new ConfigRow(
                key, 1,
                key.EndsWith("tickers_per_call", StringComparison.Ordinal)
                    ? perCall.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : "30",
                new DateOnly(2020, 1, 1)));

        public async Task<ConfigRow> RequireAsync(string key, DateOnly asOf, CancellationToken ct = default)
            => (await ResolveAsync(key, asOf, ct).ConfigureAwait(false))!;

        // A stage resolves keys, never the store-wide version: that is the runner's
        // to resolve and the stage's to be handed [checkpoint 1.13]. Throwing rather
        // than answering is what makes a stage reaching for it a failure here.
        public Task<int?> ResolveVersionAsync(DateOnly asOf, CancellationToken ct = default)
            => throw new NotSupportedException("A stage does not resolve the store-wide config version.");

        public Task<int> RequireVersionAsync(DateOnly asOf, CancellationToken ct = default)
            => throw new NotSupportedException("A stage does not resolve the store-wide config version.");
    }

    /// <summary>Records the symbol list of every call and answers with nothing.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Symbols { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            const string marker = "s=";
            var i = query.IndexOf("?" + marker, StringComparison.Ordinal) >= 0
                ? query.IndexOf("?" + marker, StringComparison.Ordinal) + marker.Length + 1
                : query.IndexOf("&" + marker, StringComparison.Ordinal) + marker.Length + 1;

            if (i > 0)
            {
                var rest = query[i..];
                var end = rest.IndexOf('&');
                Symbols.Add(end < 0 ? rest : rest[..end]);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }
}
