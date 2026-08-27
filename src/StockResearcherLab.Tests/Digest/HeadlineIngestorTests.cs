using System.Globalization;
using System.Net;
using System.Text;
using Npgsql;
using StockResearcherLab.Core;
using StockResearcherLab.Core.Config;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;
using StockResearcherLab.Data.Eodhd;
using StockResearcherLab.Pipeline.Ingest;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Digest;

/// <summary>
/// Checkpoint 5.4. C29 HeadlineIngestor.
///
/// **No live call.** The payload shape is 5.1's measurement, recorded in
/// `docs/evidence/phase-5/sweep-20260824.txt`: `content`, `date`, `link`, `sentiment`,
/// `symbols`, `tags` and `title`, with no `source`.
///
/// **The assertions that carry the checkpoint are the two about the grain** [D-132].
/// `headline` is an event record with no unique index, so idempotence is by run scope
/// rather than by row identity, and the two ways that goes wrong are collapsing two
/// genuinely different articles into one and deleting a candidate the run never
/// reached.
/// </summary>
[Collection("database")]
public sealed class HeadlineIngestorTests
{
    private const string Marker = "SRLHL";
    private static readonly DateOnly Date = new(2026, 8, 12);

    // ------------------------------------------------------------- the grain ---

    /// <summary>
    /// **Two articles identical on every stored attribute but the body are two rows.**
    ///
    /// This is the fixture D-132 exists for. The repair that suggests itself for an
    /// event record with no grain is a unique index on the row's own attributes, and
    /// 5.1 measured articles sharing a title and a publication timestamp while
    /// differing in body. Under that index one of these two is silently discarded and
    /// the row count still looks plausible.
    /// </summary>
    [Fact]
    public async Task TwoArticlesIdenticalButForTheirBodyAreTwoRows()
    {
        var ct = TestContext.Current.CancellationToken;
        var ticker = Marker + "1.US";
        await ClearAsync(ct).ConfigureAwait(true);
        await SeedCandidatesAsync([ticker], ct).ConfigureAwait(true);

        var handler = new StubHandler
        {
            Payloads =
            {
                [ticker] = """
                    [
                      {"date":"2026-08-11 13:00:00 +00:00","title":"Same","link":"https://x/1","content":"First body"},
                      {"date":"2026-08-11 13:00:00 +00:00","title":"Same","link":"https://x/1","content":"Second body"}
                    ]
                    """,
            },
        };

        var written = await RunAsync(handler, ct).ConfigureAwait(true);

        Assert.Equal(2, written);

        var bodies = await ColumnAsync("content", ticker, ct).ConfigureAwait(true);
        Assert.Equal(["First body", "Second body"], bodies.OrderBy(b => b, StringComparer.Ordinal));

        await ClearAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// **Two runs over one date produce byte-identical contents**, asserted as a set
    /// comparison in both directions rather than on a row count. A count is equal in
    /// the case this exists to catch, where the second run appends its rows beside the
    /// first run's and the table doubles.
    /// </summary>
    [Fact]
    public async Task ARerunOverOneDateIsByteIdentical()
    {
        var ct = TestContext.Current.CancellationToken;
        var ticker = Marker + "2.US";
        await ClearAsync(ct).ConfigureAwait(true);
        await SeedCandidatesAsync([ticker], ct).ConfigureAwait(true);

        var handler = new StubHandler
        {
            Payloads =
            {
                [ticker] = """
                    [
                      {"date":"2026-08-11 09:00:00 +00:00","title":"A","link":"https://x/a","content":"Body A"},
                      {"date":"2026-08-10 09:00:00 +00:00","title":"B","link":"https://x/b","content":"Body B"}
                    ]
                    """,
            },
        };

        await RunAsync(handler, ct).ConfigureAwait(true);
        var first = await SnapshotAsync(ct).ConfigureAwait(true);

        await RunAsync(handler, ct).ConfigureAwait(true);
        var second = await SnapshotAsync(ct).ConfigureAwait(true);

        Assert.Equal(2, first.Count);
        Assert.Empty(first.Except(second, StringComparer.Ordinal));
        Assert.Empty(second.Except(first, StringComparer.Ordinal));

        await ClearAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// **The delete is scoped to the tickers this run fetched for, not to the date.**
    ///
    /// A run that cleared the whole date would, on a night it only reached half the
    /// candidates, leave the other half with no headlines and no error. Here a second
    /// candidate's rows are seeded by an earlier run and a later run over one ticker
    /// leaves them exactly as they were.
    /// </summary>
    [Fact]
    public async Task ARunOverOneCandidateLeavesAnotherCandidatesRowsAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var one = Marker + "3.US";
        var two = Marker + "4.US";
        await ClearAsync(ct).ConfigureAwait(true);

        // Both are candidates; the second already has a row from an earlier run.
        await SeedCandidatesAsync([one, two], ct).ConfigureAwait(true);
        await SeedHeadlineAsync(two, "Left alone", ct).ConfigureAwait(true);

        // The stage runs against a candidate list of one, which is what a partial
        // night looks like from the delete's point of view.
        var handler = new StubHandler
        {
            Payloads =
            {
                [one] = """[{"date":"2026-08-11 09:00:00 +00:00","title":"A","link":"https://x/a","content":"Body A"}]""",
            },
        };

        await RunAsync(handler, ct, onlyCandidates: [one]).ConfigureAwait(true);

        var survivors = await ColumnAsync("content", two, ct).ConfigureAwait(true);
        Assert.Equal(["Left alone"], survivors);

        await ClearAsync(ct).ConfigureAwait(true);
    }

    // ------------------------------------------------------- absence and zero ---

    /// <summary>
    /// A night with no candidate writes nothing and says the zero is expected, so the
    /// zero-row halt does not fire. C14's warm-up case one stage later [§18].
    /// </summary>
    [Fact]
    public async Task ANightWithNoCandidateWritesNothingAndSaysTheZeroIsExpected()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClearAsync(ct).ConfigureAwait(true);

        var stage = StageFor(new StubHandler());
        var result = await stage
            .ExecuteAsync(ContextFor(stage, Date), ct)
            .ConfigureAwait(true);

        Assert.Equal(0, result.RowsWritten);
        Assert.True(result.ZeroRowsExpected);
        Assert.Contains("no candidate", result.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// **An article with no body is stored with a null content, not an empty string,
    /// and `source` is null on every row.**
    ///
    /// Null there means the provider sent no body [D-131, `CLAUDE.md` §6], which is
    /// what the digest's article selection excludes and counts. `source` has no input
    /// in this payload at all, so writing anything into it would be inventing a value
    /// [5.1].
    /// </summary>
    [Fact]
    public async Task AnArticleWithNoBodyIsNullAndSourceIsNullOnEveryRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var ticker = Marker + "5.US";
        await ClearAsync(ct).ConfigureAwait(true);
        await SeedCandidatesAsync([ticker], ct).ConfigureAwait(true);

        var handler = new StubHandler
        {
            Payloads =
            {
                [ticker] = """
                    [
                      {"date":"2026-08-11 09:00:00 +00:00","title":"Has one","link":"https://x/a","content":"Body A"},
                      {"date":"2026-08-10 09:00:00 +00:00","title":"Has none","link":"https://x/b"}
                    ]
                    """,
            },
        };

        await RunAsync(handler, ct).ConfigureAwait(true);

        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FILTER (WHERE content IS NULL), " +
            "       count(*) FILTER (WHERE content = ''), " +
            "       count(*) FILTER (WHERE source IS NOT NULL) " +
            "FROM headline WHERE ticker = @t;", conn);
        cmd.Parameters.AddWithValue("t", ticker);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(true);

        Assert.True(await r.ReadAsync(ct).ConfigureAwait(true));
        Assert.Equal(1, r.GetInt64(0));
        Assert.Equal(0, r.GetInt64(1));
        Assert.Equal(0, r.GetInt64(2));

        await r.CloseAsync().ConfigureAwait(true);
        await ClearAsync(ct).ConfigureAwait(true);
    }

    // ---------------------------------------------------------- the declarations ---

    /// <summary>
    /// The declared sets, and the one that matters is what is missing. C29 may not read
    /// a score table, so a component that gathered more evidence for a better-scoring
    /// name throws before a connection opens rather than being forbidden by a comment
    /// [INVARIANT 7].
    /// </summary>
    [Fact]
    public void TheDeclaredSetsAreCandidateSetAndHeadlineAndNothingElse()
    {
        var stage = StageFor(new StubHandler());

        Assert.Equal(["candidate_set"], stage.ReadSet);
        Assert.DoesNotContain("screen_score_daily", stage.ReadSet);
        Assert.DoesNotContain("attribution", stage.ReadSet);

        // Both operations, on one table, in the enum's own order rather than in
        // alphabetical order of the operation name: `WriteOperation` declares Insert,
        // Update, Delete, so a sort by the value puts Insert first. Asserted against
        // the declaration rather than against what reads naturally, because the second
        // is how a passing test gets written around a set that is actually wrong.
        Assert.Equal(
            [("headline", WriteOperation.Insert), ("headline", WriteOperation.Delete)],
            stage.WriteSet.Select(w => (w.Table, w.Operation)).OrderBy(x => x.Operation).ToList());

        Assert.DoesNotContain(stage.WriteSet, w => w.Operation == WriteOperation.Update);
    }

    /// <summary>Reaching outside the declared read set throws before a connection opens.</summary>
    [Fact]
    public async Task ReachingForAScoreTableThrows()
    {
        var ct = TestContext.Current.CancellationToken;
        var stage = StageFor(new StubHandler());
        var data = new StageData(TestDatabase.ConnectionString, new DeclaredAccess(stage));

        await Assert.ThrowsAsync<UndeclaredTableAccessException>(
            () => data.ReadAsync("screen_score_daily", "SELECT 1;", ct));
    }

    // ------------------------------------------------------------------ plumbing ---

    private static HeadlineIngestor StageFor(StubHandler handler)
        => new(new EodhdClient(
            new HttpClient(handler) { BaseAddress = new Uri(EodhdUrl.BaseAddress) },
            "fake-token",
            new FixedClock(new DateTimeOffset(2026, 8, 12, 21, 0, 0, TimeSpan.Zero), Date)));

    private static StageContext ContextFor(HeadlineIngestor stage, DateOnly date)
        => new(
            date, 1,
            new StageData(TestDatabase.ConnectionString, new DeclaredAccess(stage)),
            new FixedClock(new DateTimeOffset(date.Year, date.Month, date.Day, 21, 0, 0, TimeSpan.Zero), date),
            new StubConfig());

    private static async Task<long> RunAsync(
        StubHandler handler, CancellationToken ct, IReadOnlyList<string>? onlyCandidates = null)
    {
        if (onlyCandidates is not null)
        {
            await KeepOnlyCandidatesAsync(onlyCandidates, ct).ConfigureAwait(false);
        }

        var stage = StageFor(handler);
        var result = await stage.ExecuteAsync(ContextFor(stage, Date), ct).ConfigureAwait(false);
        return result.RowsWritten;
    }

    private static async Task<IReadOnlyList<string>> SnapshotAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "SELECT ticker || '|' || coalesce(published_at::text,'') || '|' || coalesce(title,'') || '|' || " +
            "       coalesce(url,'') || '|' || coalesce(content,'') " +
            "FROM headline WHERE ticker LIKE @m ORDER BY 1;", conn);
        cmd.Parameters.AddWithValue("m", Marker + "%");

        var rows = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(r.GetString(0));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<string>> ColumnAsync(
        string column, string ticker, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {column} FROM headline WHERE ticker = @t AND {column} IS NOT NULL ORDER BY 1;", conn);
        cmd.Parameters.AddWithValue("t", ticker);

        var rows = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(r.GetString(0));
        }

        return rows;
    }

    private static async Task SeedCandidatesAsync(IReadOnlyList<string> tickers, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        foreach (var t in tickers)
        {
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO candidate_set (ticker, date, screens_surfacing, size_bucket) " +
                "VALUES (@t, @d, ARRAY['S1'], 'small') ON CONFLICT (ticker, date) DO NOTHING;", conn);
            cmd.Parameters.AddWithValue("t", t);
            cmd.Parameters.AddWithValue("d", Date);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task KeepOnlyCandidatesAsync(IReadOnlyList<string> keep, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM candidate_set WHERE date = @d AND ticker LIKE @m AND NOT (ticker = ANY(@k));", conn);
        cmd.Parameters.AddWithValue("d", Date);
        cmd.Parameters.AddWithValue("m", Marker + "%");
        cmd.Parameters.AddWithValue("k", keep.ToArray());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task SeedHeadlineAsync(string ticker, string content, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO headline (ticker, date, published_at, title, source, url, content) " +
            "VALUES (@t, @d, TIMESTAMPTZ '2026-08-09 09:00+00', 'Earlier', NULL, 'https://x/old', @c);", conn);
        cmd.Parameters.AddWithValue("t", ticker);
        cmd.Parameters.AddWithValue("d", Date);
        cmd.Parameters.AddWithValue("c", content);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task ClearAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM headline WHERE ticker LIKE @m; DELETE FROM candidate_set WHERE ticker LIKE @m;", conn);
        cmd.Parameters.AddWithValue("m", Marker + "%");
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Answers with 5.1's measured payload shape, per ticker, and `[]` for anything else.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public Dictionary<string, string> Payloads { get; } = new(StringComparer.Ordinal);

        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            var symbol = Between(query, "s=");
            Asked.Add(symbol);

            var body = Payloads.TryGetValue(symbol, out var p) ? p : "[]";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }

        private static string Between(string query, string marker)
        {
            var i = query.IndexOf("?" + marker, StringComparison.Ordinal);
            if (i < 0)
            {
                i = query.IndexOf("&" + marker, StringComparison.Ordinal);
            }

            if (i < 0)
            {
                return string.Empty;
            }

            var rest = query[(i + marker.Length + 1)..];
            var end = rest.IndexOf('&');
            return end < 0 ? rest : rest[..end];
        }
    }

    /// <summary>`digest.lookback_days` and nothing else; anything unexpected throws.</summary>
    private sealed class StubConfig : IConfigStore
    {
        public Task<ConfigRow?> ResolveAsync(string key, DateOnly asOf, CancellationToken ct = default)
            => string.Equals(key, "digest.lookback_days", StringComparison.Ordinal)
                ? Task.FromResult<ConfigRow?>(new ConfigRow(key, 1, "7", new DateOnly(2020, 1, 1)))
                : throw new InvalidOperationException(
                    $"C29 resolved '{key}', which 5.4 does not expect it to read.");

        public async Task<ConfigRow> RequireAsync(string key, DateOnly asOf, CancellationToken ct = default)
            => (await ResolveAsync(key, asOf, ct).ConfigureAwait(false))!;

        public Task<int?> ResolveVersionAsync(DateOnly asOf, CancellationToken ct = default)
            => throw new NotSupportedException("A stage does not resolve the store-wide config version.");

        public Task<int> RequireVersionAsync(DateOnly asOf, CancellationToken ct = default)
            => throw new NotSupportedException("A stage does not resolve the store-wide config version.");
    }
}
