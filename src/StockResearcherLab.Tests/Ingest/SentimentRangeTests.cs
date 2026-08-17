using System.Globalization;
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
/// The half of 3.8's pool D-101 added, asserted directly.
///
/// **This is the half that is not optional and the half that is easy to get subtly
/// wrong.** A delisted name admitted to a reconstructed 2021 universe with no sentiment
/// rows does not arrive as unknown: `article_count` zero-fills and its z-score is
/// computed against a baseline of zeros while `sentiment_score` stays null, so a
/// degenerate value ranks where the design assumed a gap would abstain. It ranks the
/// same way for every name that later failed, and S3 is one of the two screens §20
/// names as doing the most to keep this system off megacaps.
///
/// **The set has two edges and both are tested.** A delisted name with a bar inside the
/// window is in; one whose last bar is before the window start is out and costs nothing.
/// The second edge is what keeps 3.8 at 98,515 units rather than at the whole 32,611
/// delisted list.
///
/// **The end-to-end sweep is not exercised here and open item 33 says why.** C04's live
/// half is the universe as of its date, read from `security_daily` [3.12], and the suite resolves its
/// connection string from `appsettings.Secrets.json` [open items 10 and 26], so a range
/// execution in a test walks the live universe and stamps `sentiment_fetch_attempt` for
/// every real ticker. The pool half below is safe on either database, the delisted list
/// being served by this fixture's own handler, so the intersection is bounded by the
/// fixture whatever `price_daily` holds.
/// </summary>
[Collection("database")]
public sealed class SentimentRangeTests
{
    private const string Prefix = "SRLSNT";

    /// <summary>Delisted, and trading inside the window. In the pool.</summary>
    private const string Inside = Prefix + "IN.US";

    /// <summary>Delisted before the window start. Out, and it costs nothing.</summary>
    private const string Before = Prefix + "OUT.US";

    /// <summary>Delisted and never in `price_daily` at all. Out for the same reason.</summary>
    private const string NoBars = Prefix + "GONE.US";

    private static readonly DateOnly WindowStart = new(2021, 1, 4);

    private static readonly DateOnly RunDate = new(2026, 8, 5);

    [Fact]
    public async Task TheDelistedHalfIsTheAdmittedNamesCarryingABarInsideTheWindow()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct).ConfigureAwait(true);

        var pool = await DelistedAsync(ct).ConfigureAwait(true);

        Assert.Contains(Inside, pool);
    }

    /// <summary>
    /// **The edge that keeps the sweep affordable.** 15,749 of the 32,611 admitted
    /// delisted names stopped trading before the window start, so a pool taking the
    /// list whole would be about half as much again in units and every one of those
    /// calls would buy days for a period no evaluation date reaches.
    /// </summary>
    [Fact]
    public async Task ANameThatStoppedTradingBeforeTheWindowIsNotInThePool()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct).ConfigureAwait(true);

        var pool = await DelistedAsync(ct).ConfigureAwait(true);

        Assert.DoesNotContain(Before, pool);
    }

    /// <summary>
    /// A name with no bars at all is out, which is a different fact from one whose bars
    /// stop early and reaches the same answer.
    /// </summary>
    [Fact]
    public async Task ANameWithNoBarsAtAllIsNotInThePool()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct).ConfigureAwait(true);

        var pool = await DelistedAsync(ct).ConfigureAwait(true);

        Assert.DoesNotContain(NoBars, pool);
    }

    // ----------------------------------------------------------- harness ---

    private static async Task<IReadOnlySet<string>> DelistedAsync(CancellationToken ct)
    {
        var stage = new SentimentIngestor(Client());

        var context = new StageContext(
            RunDate, 1,
            new StageData(TestDatabase.ConnectionString, new DeclaredAccess(stage)),
            new FixedClock(RunDate),
            new NoConfig());

        return await BackfillPool.DelistedWithBarsInWindowAsync(
            Client(), context, WindowStart, ct).ConfigureAwait(false);
    }

    private static EodhdClient Client()
        => new(new HttpClient(new DelistedHandler()) { BaseAddress = new Uri(EodhdUrl.BaseAddress) },
            "test-token", new FixedClock(RunDate));

    /// <summary>
    /// This fixture's rows only. `Inside` gets bars that reach past the window start,
    /// `Before` gets bars that stop the day before it, and `NoBars` gets none.
    /// </summary>
    private static async Task ResetAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);

        await using (var cmd = new NpgsqlCommand(
            "DELETE FROM price_daily WHERE ticker LIKE @p;", conn))
        {
            cmd.Parameters.AddWithValue("p", Prefix + "%");
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await SeedAsync(conn, Inside, WindowStart, WindowStart.AddDays(30), ct).ConfigureAwait(false);
        await SeedAsync(conn, Before, WindowStart.AddDays(-60), WindowStart.AddDays(-1), ct).ConfigureAwait(false);
    }

    private static async Task SeedAsync(
        NpgsqlConnection conn, string ticker, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO price_daily (ticker, date, open, high, low, close, adj_close, volume)
            SELECT @t, d::date, 10, 10, 10, 10, 10, 100000
            FROM generate_series(@f::date, @s::date, INTERVAL '1 day') AS d
            ON CONFLICT (ticker, date) DO NOTHING;
            """, conn);

        cmd.Parameters.AddWithValue("t", ticker);
        cmd.Parameters.AddWithValue("f", from);
        cmd.Parameters.AddWithValue("s", to);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private sealed class FixedClock(DateOnly today) : IClock
    {
        public DateTimeOffset UtcNow => new(today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        public DateOnly Today => today;
    }

    /// <summary>
    /// Refuses every key that could change the set, which is an assertion rather than a
    /// shortcut: the delisted half is derived from the symbol list and `price_daily`
    /// alone, and a resolution reaching this stub would mean a configured value had
    /// entered the pool definition where D-101 states it as a rule.
    ///
    /// **`universe.pool_statement_timeout_seconds` is served rather than refused, and
    /// that narrows what this fixture proves** [D-102]. It was refused with the rest
    /// until 3.8's second day, when the pool read timed out at the connection string's
    /// 300 while C03 gave the identical statement 1800. The bound belongs on the
    /// statement, and it cannot change the answer: the same tickers come back under any
    /// value of it, or none do and the run fails. So the assertion here is now the
    /// narrower one, that no key entering the *definition* resolves, and it is narrower
    /// on the distinction open item 34 has not settled: an operational key changes
    /// whether a run finishes, a parametric one changes what it produces.
    /// </summary>
    private sealed class NoConfig : IConfigStore
    {
        private const string OperationalBound = "universe.pool_statement_timeout_seconds";

        public Task<ConfigRow?> ResolveAsync(string key, DateOnly asOf, CancellationToken ct = default)
            => key == OperationalBound
                ? Task.FromResult<ConfigRow?>(new ConfigRow(key, 1, "1800", asOf))
                : throw new NotSupportedException($"The pool read '{key}'. Its definition takes no configuration.");

        public Task<ConfigRow> RequireAsync(string key, DateOnly asOf, CancellationToken ct = default)
            => key == OperationalBound
                ? Task.FromResult(new ConfigRow(key, 1, "1800", asOf))
                : throw new NotSupportedException($"The pool read '{key}'. Its definition takes no configuration.");

        public Task<int?> ResolveVersionAsync(DateOnly asOf, CancellationToken ct = default)
            => throw new NotSupportedException("A stage does not resolve the store-wide config version.");

        public Task<int> RequireVersionAsync(DateOnly asOf, CancellationToken ct = default)
            => throw new NotSupportedException("A stage does not resolve the store-wide config version.");
    }

    /// <summary>
    /// The delisted symbol list, carrying this fixture's three names and nothing else,
    /// so the intersection is bounded by the fixture whatever `price_daily` holds.
    /// </summary>
    private sealed class DelistedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = string.Join(",", new[] { Inside, Before, NoBars }.Select(t => string.Format(
                CultureInfo.InvariantCulture,
                """{{"Code":"{0}","Name":"{0} Inc","Type":"Common Stock"}}""",
                t[..^3])));

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("[" + body + "]", System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
