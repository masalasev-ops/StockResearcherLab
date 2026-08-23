using System.Globalization;
using System.Net;
using System.Text;
using Npgsql;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;
using StockResearcherLab.Data.Eodhd;
using StockResearcherLab.Pipeline.Ingest;
using StockResearcherLab.Tests.Compute;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Ingest;

/// <summary>
/// D-108's nine criteria, each against a name built to fail exactly that one, and the
/// admitted control that makes the nine mean something.
///
/// **The control is not decoration.** Nine rejections with nothing admitted would also
/// be produced by a bug that rejects everything, and every assertion would pass. So one
/// name clears all nine and is asserted to be a member with no rejection row, which is
/// the only way the nine below say what they claim to say.
///
/// **This is also the test that the projection did not change membership.** The three
/// pre-pass criteria used to be filtered away inside one statement and now arrive as
/// rows carrying a verdict, so a loop testing them anywhere but first would admit names
/// D-4 excludes. `NoPrePassFailureIsAdmitted` is that assertion: the three are absent
/// from `security_daily` as members, not merely present in `universe_rejection`.
///
/// **It runs C01 whole rather than a piece of it.** The classification lives inside one
/// SQL statement and the ordering lives inside the loop that consumes it, and a test of
/// either alone would pass while the pair disagreed. The cost is that the run evaluates
/// every ticker the shared test database holds, so the fixture cleans up by evaluation
/// date rather than by ticker.
/// </summary>
[Collection("database")]
public sealed class UniverseRejectionTests : IAsyncLifetime
{
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Runs after every test in this class, passing or failing, which clearing first
    /// cannot do: the last run's rows would otherwise outlive the class.
    ///
    /// **That is not tidiness.** C01 writes a `security_daily` row for every member it
    /// finds and a departure for every name that left, over whatever the shared database
    /// holds, and `SentimentIngestorTests` counts active members across the whole table.
    /// Rows left behind here fail a test in another class, which is exactly what
    /// happened before this was added.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await ClearAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        _ran = false;
        _rows = null;
    }

    /// <summary>
    /// A Sunday, which is C01's cadence, and after `backfill.window_start` so the names
    /// below are inside the population D-108 records. A date before that start would
    /// produce no rejection row for anything and every assertion here would fail for a
    /// reason that has nothing to do with the criteria.
    /// </summary>
    private static readonly DateOnly Evaluated = new(2021, 2, 7);

    /// <summary>The prefix every fixture ticker carries, so cleanup can find them and nothing else can collide.</summary>
    private const string Prefix = "ZZR";

    /// <summary>
    /// The nine criteria against the name built to fail each, in C01's own test order.
    ///
    /// Reading down this list is reading the order the evaluation applies, which is what
    /// makes `criterion` mean "the one it stopped on" rather than "the only one it
    /// failed" [D-108, `SCHEMA.md`].
    /// </summary>
    public static TheoryData<string, string> Criteria => new()
    {
        { Prefix + "PRICE.US", "below_min_price" },
        { Prefix + "VOL.US", "below_min_dollar_volume" },
        { Prefix + "HIST.US", "insufficient_history" },
        { Prefix + "TYPE.US", "not_common_stock" },
        { Prefix + "GONE.US", "delisted_on_date" },
        { Prefix + "NOFUN.US", "no_fundamentals" },
        { Prefix + "GAPS.US", "below_clean_gaps" },
        { Prefix + "SHARE.US", "no_share_count" },
        { Prefix + "CAP.US", "below_market_cap" },
    };

    private const string Admitted = Prefix + "OK.US";

    [Theory]
    [MemberData(nameof(Criteria))]
    public async Task EachCriterionIsRecordedAgainstTheNameBuiltToFailIt(string ticker, string criterion)
    {
        var ct = TestContext.Current.CancellationToken;
        var rows = await RunAsync(ct).ConfigureAwait(true);

        Assert.True(rows.TryGetValue(ticker, out var recorded),
            $"{ticker} was built to fail {criterion} and carries no universe_rejection row at all. " +
            "Recorded criteria: " + string.Join(", ", rows.Select(r => r.Key + "=" + r.Value)));

        Assert.Equal(criterion, recorded);
    }

    /// <summary>
    /// The control. Without it, a C01 that rejected everything would satisfy every
    /// assertion above.
    /// </summary>
    [Fact]
    public async Task TheNameThatClearsEveryCriterionIsAMemberAndCarriesNoRejection()
    {
        var ct = TestContext.Current.CancellationToken;
        var rows = await RunAsync(ct).ConfigureAwait(true);

        Assert.False(rows.ContainsKey(Admitted),
            $"{Admitted} clears all nine criteria and was recorded as rejected on " +
            rows.GetValueOrDefault(Admitted));

        Assert.True(await IsActiveMemberAsync(Admitted, ct).ConfigureAwait(true),
            $"{Admitted} clears all nine criteria and is not an active security_daily row. " +
            "Nine rejections with nothing admitted is also what a C01 that rejects everything " +
            "produces, and this is the assertion that separates the two.");
    }

    /// <summary>
    /// **The assertion that stands between an addition and a membership change** [D-108].
    ///
    /// The three pre-pass criteria are now projected rather than filtered, so the loop
    /// sees rows it never used to see. Tested anywhere but first, price, dollar volume
    /// and history would be evaluated by nothing and a name failing one of them would be
    /// admitted if it happened to clear the other six.
    ///
    /// `ZZRPRICE.US` is built to make that failure visible rather than theoretical: it
    /// clears every one of the other eight criteria and fails on price alone, so a loop
    /// that tested the verdict last would admit it.
    /// </summary>
    [Fact]
    public async Task NoPrePassFailureIsAdmitted()
    {
        var ct = TestContext.Current.CancellationToken;
        await RunAsync(ct).ConfigureAwait(true);

        foreach (var ticker in new[] { Prefix + "PRICE.US", Prefix + "VOL.US", Prefix + "HIST.US" })
        {
            Assert.False(await IsActiveMemberAsync(ticker, ct).ConfigureAwait(true),
                $"{ticker} fails a pre-pass criterion and is an active member on {Evaluated:yyyy-MM-dd}. " +
                "The statement classifies where it used to filter, so the loop has to reject on the " +
                "verdict before every other criterion or the admitted set widens silently.");
        }
    }

    /// <summary>
    /// A member has no row and a rejected name has exactly one [`SCHEMA.md`]. Asserted
    /// over the fixture's own names rather than the whole table, the run having
    /// evaluated whatever else the shared database holds.
    /// </summary>
    [Fact]
    public async Task ARejectedNameCarriesExactlyOneRowAndAMemberCarriesNone()
    {
        var ct = TestContext.Current.CancellationToken;
        await RunAsync(ct).ConfigureAwait(true);

        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT ticker, count(*)
            FROM universe_rejection
            WHERE date = @d AND ticker LIKE @p
            GROUP BY ticker HAVING count(*) <> 1;
            """, conn);
        cmd.Parameters.AddWithValue("d", Evaluated);
        cmd.Parameters.AddWithValue("p", Prefix + "%");

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(true);
        Assert.False(await r.ReadAsync(ct).ConfigureAwait(true),
            "A ticker carries more than one rejection row for one evaluation date, which the " +
            "primary key should make impossible.");
    }

    /// <summary>
    /// A re-run of one evaluation date produces identical rows [D-68, D-108]. The date
    /// is recomputed whole rather than merged, so this is what proves the delete is
    /// there: an upsert alone would leave a stale row the moment a verdict moved.
    /// </summary>
    [Fact]
    public async Task ARerunOfOneEvaluationDateProducesIdenticalRows()
    {
        var ct = TestContext.Current.CancellationToken;

        var first = await RunAsync(ct).ConfigureAwait(true);
        var second = await RunAsync(ct, force: true).ConfigureAwait(true);

        Assert.Equal(
            first.OrderBy(x => x.Key, StringComparer.Ordinal).ToList(),
            second.OrderBy(x => x.Key, StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// A name admitted on an earlier run and rejected on a later one keeps no stale row,
    /// and the reverse. This is the delete's own assertion: the upsert cannot remove a
    /// row, so without it a name that becomes a member stays listed as rejected forever.
    /// </summary>
    [Fact]
    public async Task ANameThatStopsBeingRejectedLosesItsRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await RunAsync(ct).ConfigureAwait(true);

        // A row for a ticker the fixture never produces, planted at the evaluation date.
        // The next run recomputes the date whole and it has to be gone.
        await ExecuteAsync(
            """
            INSERT INTO universe_rejection (ticker, date, criterion)
            VALUES (@t, @d, 'no_fundamentals')
            ON CONFLICT (ticker, date) DO UPDATE SET criterion = excluded.criterion;
            """,
            c =>
            {
                c.Parameters.AddWithValue("t", Prefix + "STALE.US");
                c.Parameters.AddWithValue("d", Evaluated);
            }, ct).ConfigureAwait(true);

        var after = await RunAsync(ct, force: true).ConfigureAwait(true);

        Assert.False(after.ContainsKey(Prefix + "STALE.US"),
            "A rejection row for a name the evaluation no longer produces survived a re-run. " +
            "The date is recomputed whole precisely so it cannot.");
    }

    // ---------------------------------------------------------------- harness ---

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _ran;
    private static Dictionary<string, string>? _rows;

    /// <summary>
    /// Seeds, runs C01 once for the evaluation date, and returns the fixture's own
    /// rejection rows. Cached across the class's tests because the run evaluates every
    /// ticker in the database and there is nothing per-test about it.
    /// </summary>
    private static async Task<Dictionary<string, string>> RunAsync(
        CancellationToken ct, bool force = false)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ran && !force && _rows is not null)
            {
                return _rows;
            }

            await SeedAsync(ct).ConfigureAwait(false);

            var stage = new UniverseBuilder(SymbolListClient());
            var config = new ConfigStore(TestDatabase.ConnectionString);

            var context = new StageContext(
                Evaluated,
                await config.RequireVersionAsync(Evaluated, ct).ConfigureAwait(false),
                new StageData(TestDatabase.ConnectionString, new DeclaredAccess(stage)),
                new Seam.FrozenClock(Evaluated),
                config);

            await stage.ExecuteAsync(context, ct).ConfigureAwait(false);

            _rows = await RejectionsAsync(ct).ConfigureAwait(false);
            _ran = true;
            return _rows;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<Dictionary<string, string>> RejectionsAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "SELECT ticker, criterion FROM universe_rejection WHERE date = @d AND ticker LIKE @p ORDER BY ticker;",
            conn);
        cmd.Parameters.AddWithValue("d", Evaluated);
        cmd.Parameters.AddWithValue("p", Prefix + "%");

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            map[r.GetString(0)] = r.GetString(1);
        }

        return map;
    }

    private static async Task<bool> IsActiveMemberAsync(string ticker, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "SELECT is_active FROM security_daily WHERE ticker = @t AND date = @d;", conn);
        cmd.Parameters.AddWithValue("t", ticker);
        cmd.Parameters.AddWithValue("d", Evaluated);

        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is true;
    }

    /// <summary>
    /// Ten names, nine of them failing exactly one criterion and one clearing all nine.
    ///
    /// Every one of the nine clears every criterion the evaluation reaches before the one
    /// it is built for, which is what makes the recorded value a statement about order
    /// rather than a coincidence.
    /// </summary>
    private static async Task SeedAsync(CancellationToken ct)
    {
        await ClearAsync(ct).ConfigureAwait(false);

        // 400 daily bars to 2021-02-05, which clears the 250 the history criterion wants.
        // Bars on weekends are irrelevant here: price_daily is rows and the criterion
        // counts them.
        await BarsAsync(Prefix + "OK.US", 400, 50m, 400_000, new DateOnly(2021, 2, 5), ct).ConfigureAwait(false);
        await BarsAsync(Prefix + "TYPE.US", 400, 50m, 400_000, new DateOnly(2021, 2, 5), ct).ConfigureAwait(false);
        await BarsAsync(Prefix + "NOFUN.US", 400, 50m, 400_000, new DateOnly(2021, 2, 5), ct).ConfigureAwait(false);
        await BarsAsync(Prefix + "GAPS.US", 400, 50m, 400_000, new DateOnly(2021, 2, 5), ct).ConfigureAwait(false);
        await BarsAsync(Prefix + "SHARE.US", 400, 50m, 400_000, new DateOnly(2021, 2, 5), ct).ConfigureAwait(false);
        await BarsAsync(Prefix + "CAP.US", 400, 50m, 400_000, new DateOnly(2021, 2, 5), ct).ConfigureAwait(false);

        // Fails price alone: the dollar volume still clears, so the order is what decides.
        await BarsAsync(Prefix + "PRICE.US", 400, 2m, 20_000_000, new DateOnly(2021, 2, 5), ct).ConfigureAwait(false);

        // Fails dollar volume alone: the price clears comfortably.
        await BarsAsync(Prefix + "VOL.US", 400, 50m, 100, new DateOnly(2021, 2, 5), ct).ConfigureAwait(false);

        // Fails history alone: price and dollar volume both clear.
        await BarsAsync(Prefix + "HIST.US", 100, 50m, 400_000, new DateOnly(2021, 2, 5), ct).ConfigureAwait(false);

        // Stopped trading inside the window and before the evaluation date, so it is in
        // the delisted list with a last bar the evaluation is past.
        await BarsAsync(Prefix + "GONE.US", 400, 50m, 400_000, new DateOnly(2021, 1, 20), ct).ConfigureAwait(false);

        // Fundamentals. Four clean gaps is the floor, so four is what a name needs.
        foreach (var t in new[] { Prefix + "OK.US", Prefix + "TYPE.US", Prefix + "GONE.US" })
        {
            await FilingsAsync(t, clean: 4, shares: 40_000_000m, ct).ConfigureAwait(false);
        }

        // Three clean gaps against a floor of four, and a share count that would have
        // cleared, so the recorded criterion is the gap count and not what follows it.
        await FilingsAsync(Prefix + "GAPS.US", clean: 3, shares: 40_000_000m, ct).ConfigureAwait(false);

        // Enough gaps, no readable share count. Absent is not zero and not a pass.
        await FilingsAsync(Prefix + "SHARE.US", clean: 4, shares: null, ct).ConfigureAwait(false);

        // Enough gaps and a share count, and 1,000,000 x 50 is 50m against a floor of
        // 300m, so this one reaches the last criterion and fails it.
        await FilingsAsync(Prefix + "CAP.US", clean: 4, shares: 1_000_000m, ct).ConfigureAwait(false);

        // The three pre-pass names carry full fundamentals deliberately: each clears
        // every later criterion, so a loop testing the verdict late would admit them.
        foreach (var t in new[] { Prefix + "PRICE.US", Prefix + "VOL.US", Prefix + "HIST.US" })
        {
            await FilingsAsync(t, clean: 4, shares: 40_000_000m, ct).ConfigureAwait(false);
        }
    }

    private static Task BarsAsync(
        string ticker, int bars, decimal close, long volume, DateOnly last, CancellationToken ct)
        => ExecuteAsync(
            """
            INSERT INTO price_daily (ticker, date, open, high, low, close, adj_close, volume)
            SELECT @t, d::date, @c, @c, @c, @c, @c, @v
            FROM generate_series(@last::date - (@bars - 1), @last::date, INTERVAL '1 day') AS d
            ON CONFLICT (ticker, date) DO UPDATE
                SET close = excluded.close, adj_close = excluded.adj_close, volume = excluded.volume;
            """,
            c =>
            {
                c.Parameters.AddWithValue("t", ticker);
                c.Parameters.AddWithValue("c", close);
                c.Parameters.AddWithValue("v", volume);
                c.Parameters.AddWithValue("bars", bars);
                c.Parameters.AddWithValue("last", last);
            }, ct);

    /// <summary>
    /// <paramref name="clean"/> quarters carrying `none`, which is what the clean gap
    /// count counts, plus one carrying `null` so the row set is not uniform and the
    /// count is a count rather than a row total.
    /// </summary>
    private static async Task FilingsAsync(string ticker, int clean, decimal? shares, CancellationToken ct)
    {
        for (var i = 0; i < clean + 1; i++)
        {
            var periodEnd = new DateOnly(2019, 3, 31).AddMonths(3 * i);
            var reason = i < clean ? "none" : "null";

            await ExecuteAsync(
                """
                INSERT INTO fundamental_snapshot
                    (ticker, period_end, period_type, filing_date, filing_date_effective,
                     filing_date_unknown_reason, shares_outstanding)
                VALUES (@t, @p, 'quarterly', @f, @f, @r, @s)
                ON CONFLICT (ticker, period_end, period_type) DO UPDATE
                    SET filing_date_unknown_reason = excluded.filing_date_unknown_reason,
                        shares_outstanding = excluded.shares_outstanding;
                """,
                c =>
                {
                    c.Parameters.AddWithValue("t", ticker);
                    c.Parameters.AddWithValue("p", periodEnd);
                    c.Parameters.AddWithValue("f", periodEnd.AddDays(40));
                    c.Parameters.AddWithValue("r", reason);
                    c.Parameters.AddWithValue("s", (object?) shares ?? DBNull.Value);
                }, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// **Cleanup is by evaluation date, not by ticker**, because the run evaluates every
    /// ticker the shared database holds and writes a `security_daily` row for each
    /// member and a departure for each name that left. Another class counting active
    /// members across the whole table would otherwise see this fixture's run.
    /// </summary>
    private static async Task ClearAsync(CancellationToken ct)
    {
        await ExecuteAsync("DELETE FROM universe_rejection WHERE date = @d;",
            c => c.Parameters.AddWithValue("d", Evaluated), ct).ConfigureAwait(false);

        await ExecuteAsync("DELETE FROM security_daily WHERE date = @d;",
            c => c.Parameters.AddWithValue("d", Evaluated), ct).ConfigureAwait(false);

        await ExecuteAsync("DELETE FROM security WHERE ticker LIKE @p;",
            c => c.Parameters.AddWithValue("p", Prefix + "%"), ct).ConfigureAwait(false);

        await ExecuteAsync("DELETE FROM price_daily WHERE ticker LIKE @p;",
            c => c.Parameters.AddWithValue("p", Prefix + "%"), ct).ConfigureAwait(false);

        await ExecuteAsync("DELETE FROM fundamental_snapshot WHERE ticker LIKE @p;",
            c => c.Parameters.AddWithValue("p", Prefix + "%"), ct).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(string sql, Action<NpgsqlCommand> bind, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        bind(cmd);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The two symbol lists, and nothing else. `ZZRTYPE.US` is absent from both, which is
    /// what `not_common_stock` means: the provider never typed it as one.
    /// </summary>
    private static EodhdClient SymbolListClient()
        => new(
            new HttpClient(new SymbolListHandler()) { BaseAddress = new Uri(EodhdUrl.BaseAddress) },
            "test-token",
            new Seam.FrozenClock(Evaluated));

    private sealed class SymbolListHandler : HttpMessageHandler
    {
        private static readonly string[] Live =
        [
            Prefix + "OK", Prefix + "NOFUN", Prefix + "GAPS", Prefix + "SHARE", Prefix + "CAP",
            Prefix + "PRICE", Prefix + "VOL", Prefix + "HIST",
        ];

        private static readonly string[] Delisted = [Prefix + "GONE"];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!.ToString();

            if (!uri.Contains("exchange-symbol-list/US", StringComparison.Ordinal))
            {
                throw new NotSupportedException(
                    "C01 reached an endpoint other than the symbol list: " + uri +
                    ". The fixture serves the two lists and nothing else, so a new call is a change " +
                    "in what this component fetches rather than a gap in the double.");
            }

            var codes = uri.Contains("delisted=1", StringComparison.Ordinal) ? Delisted : Live;

            var body = new StringBuilder("[");
            for (var i = 0; i < codes.Length; i++)
            {
                if (i > 0)
                {
                    body.Append(',');
                }

                body.Append(CultureInfo.InvariantCulture,
                    $$"""{"Code":"{{codes[i]}}","Name":"{{codes[i]}} Inc","Type":"Common Stock"}""");
            }

            body.Append(']');

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json"),
            });
        }
    }
}
