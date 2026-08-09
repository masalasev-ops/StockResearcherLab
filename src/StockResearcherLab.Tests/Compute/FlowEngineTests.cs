using System.Globalization;
using Npgsql;
using StockResearcherLab.Core;
using StockResearcherLab.Core.Config;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;
using StockResearcherLab.Pipeline.Compute;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Compute;

/// <summary>
/// Checkpoint 1.8, the compute half. Two source tables at their own grain become
/// three metrics at ticker by day [D-61].
///
/// The fixture is hand-computed and the arithmetic is in the test, so a change to
/// the statement that alters a number fails here rather than passing with a
/// different answer.
/// </summary>
[Collection("database")]
public sealed class FlowEngineTests
{
    private const string Covered = "SRLTEST.FLOWA";
    private const string Unpriced = "SRLTEST.FLOWB";
    private const string HoldingsOnly = "SRLTEST.FLOWC";
    private const string NeverIngested = "SRLTEST.FLOWD";

    private static readonly string[] Fixture = [Covered, Unpriced, HoldingsOnly, NeverIngested];

    /// <summary>
    /// 2026-08-14 rather than the nearer date, because it is the first on which all
    /// three metrics are computable: the second institutional report is dated
    /// 2026-06-30 and the forty-five day filing lag makes it readable exactly then.
    /// A reference test on a date where one metric is null for a structural reason
    /// would assert less than it appears to.
    /// </summary>
    private static readonly DateOnly RunDate = new(2026, 8, 14);

    // ------------------------------------------------------- the arithmetic ---

    /// <summary>
    /// The reference. Six filings for one ticker, of which the statement must use
    /// exactly three.
    ///
    ///   in window, visible, open market
    ///     P  2026-06-15  filed 06-17   +120,000    owner CIK 111
    ///     P  2026-07-01  filed 07-02   + 45,000    owner CIK 222
    ///     S  2026-07-20  filed 07-21   - 30,000    owner CIK 111
    ///
    ///   excluded, each for a different reason
    ///     P  2026-03-01  filed 03-02   before the ninety day window
    ///     P  2026-08-06  filed 08-20   traded inside the window, public after it
    ///     M  2026-07-10  filed 07-11   an option exercise, not an open market trade
    ///
    /// So insider_net_90d_usd is 120,000 + 45,000 - 30,000 = 135,000 and
    /// distinct_buyer_count is 2, the two CIKs that bought. The M row would have
    /// added a third buyer and 900,000 to the total had the code filter not held,
    /// and the 08-06 row would have added a fourth and 500,000 had the filed_at
    /// filter not held.
    /// </summary>
    [Fact]
    public async Task TheThreeMetricsReproduceTheHandComputedReference()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);

        await RunAsync(RunDate, ct).ConfigureAwait(true);

        var row = await ReadAsync(Covered, RunDate, ct).ConfigureAwait(true);

        Assert.Equal(135_000m, row.Net);
        Assert.Equal(2, row.Buyers);

        // Two report dates, so a change exists: 1,300,000 against 1,000,000.
        Assert.NotNull(row.InstChange);
        Assert.Equal(0.30f, row.InstChange!.Value, 4);

        await ClearAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// A P or S row with no dollar value cannot be added, and dropping it from the
    /// sum is adding zero. Zero is a real value here, meaning insiders traded and
    /// netted out, so the ticker carries null instead [CLAUDE.md section 6].
    /// </summary>
    [Fact]
    public async Task ATickerWithAnUnpricedOpenMarketRowCarriesNullRatherThanAnUnderstatedTotal()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);

        await RunAsync(RunDate, ct).ConfigureAwait(true);

        var row = await ReadAsync(Unpriced, RunDate, ct).ConfigureAwait(true);

        Assert.Equal(1, row.Rows);
        Assert.Null(row.Net);

        // The buyer count survives: the unpriced row is a sale, so nothing about
        // it is unattributed. The two nulls are independent.
        Assert.Equal(0, row.Buyers);

        await ClearAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// The distinction the whole row set turns on. A ticker that has never been
    /// through the rotation and a ticker with genuinely no insider buying are
    /// different facts, and a zero cannot tell them apart, so absence of a row is
    /// what carries "unknown" [CLAUDE.md section 1].
    /// </summary>
    [Fact]
    public async Task ATickerWithNoIngestedFlowGetsNoRowRatherThanZeros()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);

        await RunAsync(RunDate, ct).ConfigureAwait(true);

        Assert.Equal(0, (await ReadAsync(NeverIngested, RunDate, ct).ConfigureAwait(true)).Rows);

        // And a ticker with holdings but no filings gets a row whose insider
        // metrics are null rather than zero, for the same reason.
        var holdings = await ReadAsync(HoldingsOnly, RunDate, ct).ConfigureAwait(true);
        Assert.Equal(1, holdings.Rows);
        Assert.Null(holdings.Net);
        Assert.Null(holdings.Buyers);

        await ClearAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// `institutional_holding.report_date` is a period end and a 13F is due within
    /// forty-five days of it. Reading on `report_date &lt;= date` is the mistake
    /// INVARIANT 12 names for fundamentals arriving through the other table with
    /// the same shape, so the newer report is invisible until the lag has passed.
    /// </summary>
    [Fact]
    public async Task AnInstitutionalReportIsNotReadableUntilTheFilingLagHasPassed()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);

        // The second report is dated 2026-06-30. At forty-five days it becomes
        // readable on 2026-08-14, so on the 13th only the first report is visible
        // and there is no second date to compute a change against.
        await RunAsync(new DateOnly(2026, 8, 13), ct).ConfigureAwait(true);
        Assert.Null((await ReadAsync(Covered, new DateOnly(2026, 8, 13), ct).ConfigureAwait(true)).InstChange);

        await RunAsync(new DateOnly(2026, 8, 14), ct).ConfigureAwait(true);
        Assert.NotNull((await ReadAsync(Covered, new DateOnly(2026, 8, 14), ct).ConfigureAwait(true)).InstChange);

        await ClearAsync(ct).ConfigureAwait(true);
    }

    /// <summary>D-68 on the table's own grain, which for flow_daily is (ticker, date).</summary>
    [Fact]
    public async Task RunningTheSameDateTwiceLeavesTheRowCountUnchangedAndEveryValueEqual()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);

        await RunAsync(RunDate, ct).ConfigureAwait(true);
        var first = await ReadAsync(Covered, RunDate, ct).ConfigureAwait(true);

        await RunAsync(RunDate, ct).ConfigureAwait(true);
        var second = await ReadAsync(Covered, RunDate, ct).ConfigureAwait(true);

        Assert.Equal(first, second);
        Assert.Equal(1, second.Rows);

        await ClearAsync(ct).ConfigureAwait(true);
    }

    // ------------------------------------------------------------ declared ---

    [Fact]
    public void TheDeclaredSetsAreTheTwoSourcesAndTheDerivedTable()
    {
        var stage = new FlowEngine();

        Assert.Equal(["insider_transaction", "institutional_holding"], stage.ReadSet);

        var write = Assert.Single(stage.WriteSet);
        Assert.Equal("flow_daily", write.Table);
        Assert.Equal(FlowEngine.FlowColumns, write.Columns);

        // The statement writes exactly the declared columns. WriteAsync checks the
        // table and the operation but not the column list, so a set-based stage
        // has to be checked against its own SQL rather than at the call.
        foreach (var column in FlowEngine.FlowColumns)
        {
            Assert.Contains(column, FlowEngine.Sql, StringComparison.Ordinal);
        }

        // The name in ARCHITECTURE.html, not SCHEMA.md's superseded one [A1.a].
        Assert.DoesNotContain("insider_net_usd_90d", FlowEngine.Sql, StringComparison.Ordinal);
    }

    // --------------------------------------------------------------- rigging ---

    private static async Task RunAsync(DateOnly date, CancellationToken ct)
    {
        var stage = new FlowEngine();
        var context = new StageContext(
            date, 1,
            new StageData(TestDatabase.ConnectionString, new DeclaredAccess(stage)),
            new FrozenClock(date),
            new StubConfig(45));

        await stage.ExecuteAsync(context, ct).ConfigureAwait(false);
    }

    private readonly record struct Row(long Rows, decimal? Net, int? Buyers, float? InstChange);

    private static async Task<Row> ReadAsync(string ticker, DateOnly date, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT count(*), max(insider_net_90d_usd), max(distinct_buyer_count), max(inst_ownership_change)
            FROM flow_daily WHERE ticker = @t AND date = @d;
            """, conn);
        cmd.Parameters.AddWithValue("t", ticker);
        cmd.Parameters.AddWithValue("d", date);

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await r.ReadAsync(ct).ConfigureAwait(false);

        return new Row(
            r.GetInt64(0),
            await r.IsDBNullAsync(1, ct).ConfigureAwait(false) ? null : r.GetDecimal(1),
            await r.IsDBNullAsync(2, ct).ConfigureAwait(false) ? null : r.GetInt32(2),
            await r.IsDBNullAsync(3, ct).ConfigureAwait(false) ? null : r.GetFloat(3));
    }

    private static async Task SeedAsync(CancellationToken ct)
    {
        await ClearAsync(ct).ConfigureAwait(false);

        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);

        // ticker, accession, side, ordinal, filed_at, transaction_date, cik, code, shares, price, a/d
        var insider = new (string T, string Acc, string Side, int Ord, string Filed, string Txn,
                           string? Cik, string Code, decimal? Shares, decimal? Price, string Ad)[]
        {
            (Covered, "F-1", "non_derivative", 0, "2026-06-17", "2026-06-15", "111", "P", 4_000m, 30m, "A"),
            (Covered, "F-2", "non_derivative", 0, "2026-07-02", "2026-07-01", "222", "P", 1_500m, 30m, "A"),
            (Covered, "F-3", "non_derivative", 0, "2026-07-21", "2026-07-20", "111", "S", 1_000m, 30m, "D"),
            (Covered, "F-4", "non_derivative", 0, "2026-03-02", "2026-03-01", "333", "P", 5_000m, 30m, "A"),
            (Covered, "F-5", "non_derivative", 0, "2026-08-20", "2026-08-06", "444", "P", 10_000m, 50m, "A"),
            (Covered, "F-6", "derivative",     0, "2026-07-11", "2026-07-10", "555", "M", 30_000m, 30m, "A"),

            // One sale with no price and no total, which is what nulls the sum.
            (Unpriced, "G-1", "non_derivative", 0, "2026-07-02", "2026-07-01", "111", "S", 1_000m, null, "D"),
        };

        foreach (var i in insider)
        {
            await using var cmd = new NpgsqlCommand(
                """
                INSERT INTO insider_transaction
                    (ticker, accession_number, transaction_side, transaction_ordinal, filed_at,
                     transaction_date, reporting_owner_cik, transaction_code, shares_amount,
                     price_per_share, acquired_or_disposed)
                VALUES (@t, @a, @s, @o, @f, @x, @c, @k, @sh, @p, @ad);
                """, conn);
            cmd.Parameters.AddWithValue("t", i.T);
            cmd.Parameters.AddWithValue("a", i.Acc);
            cmd.Parameters.AddWithValue("s", i.Side);
            cmd.Parameters.AddWithValue("o", i.Ord);
            cmd.Parameters.AddWithValue("f", DateOnly.Parse(i.Filed, CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("x", DateOnly.Parse(i.Txn, CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("c", (object?) i.Cik ?? DBNull.Value);
            cmd.Parameters.AddWithValue("k", i.Code);
            cmd.Parameters.AddWithValue("sh", (object?) i.Shares ?? DBNull.Value);
            cmd.Parameters.AddWithValue("p", (object?) i.Price ?? DBNull.Value);
            cmd.Parameters.AddWithValue("ad", i.Ad);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Two report dates for the covered name, one for the holdings-only name.
        var holdings = new (string T, string Date, string Holder, decimal Shares)[]
        {
            (Covered, "2026-03-31", "Alpha Capital", 600_000m),
            (Covered, "2026-03-31", "Beta Partners", 400_000m),
            (Covered, "2026-06-30", "Alpha Capital", 800_000m),
            (Covered, "2026-06-30", "Beta Partners", 500_000m),
            (HoldingsOnly, "2026-03-31", "Alpha Capital", 100_000m),
        };

        foreach (var h in holdings)
        {
            await using var cmd = new NpgsqlCommand(
                """
                INSERT INTO institutional_holding (ticker, report_date, holder_name, shares)
                VALUES (@t, @d, @h, @s);
                """, conn);
            cmd.Parameters.AddWithValue("t", h.T);
            cmd.Parameters.AddWithValue("d", DateOnly.Parse(h.Date, CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("h", h.Holder);
            cmd.Parameters.AddWithValue("s", h.Shares);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Enumerated tickers rather than a LIKE, which does not use the index under
    /// this collation and timed out against a populated table [1.5].
    /// </summary>
    private static async Task ClearAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);

        foreach (var table in new[] { "flow_daily", "insider_transaction", "institutional_holding" })
        {
            await using var cmd = new NpgsqlCommand(
                "DELETE FROM " + table + " WHERE ticker = ANY(@t);", conn);
            cmd.Parameters.AddWithValue("t", Fixture);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private sealed class FrozenClock(DateOnly today) : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(today.Year, today.Month, today.Day, 21, 30, 0, TimeSpan.Zero);

        public DateOnly Today { get; } = today;
    }

    private sealed class StubConfig(int lagDays) : IConfigStore
    {
        public Task<ConfigRow?> ResolveAsync(string key, DateOnly asOf, CancellationToken ct = default)
            => Task.FromResult<ConfigRow?>(new ConfigRow(
                key, 1, lagDays.ToString(CultureInfo.InvariantCulture), new DateOnly(2020, 1, 1)));

        public async Task<ConfigRow> RequireAsync(string key, DateOnly asOf, CancellationToken ct = default)
            => (await ResolveAsync(key, asOf, ct).ConfigureAwait(false))!;
    }
}
