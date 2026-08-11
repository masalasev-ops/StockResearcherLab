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
/// D-77's fifth done-when, carried from 2.3 to here because the engines it needs did
/// not exist then.
///
/// **The failure this arrangement can have is not a conflict the registry catches.**
/// Two components own one table, the metric engine inserting and C11 updating a
/// disjoint column set, which INVARIANT 10 as amended permits and the registry
/// therefore passes. What it cannot see is a metric engine re-running and blanking the
/// percentiles the other one wrote. D-68's idempotence is guaranteed per stage and
/// says nothing about the columns a stage does not write, and this is the first place
/// in the codebase where two stages share a table's rows rather than a table.
///
/// It is safe today by construction rather than by assertion, which is what these
/// tests change. Three of the four tables are written through the staged path, whose
/// staging table is built as <c>SELECT &lt;written columns&gt; ... WITH NO DATA</c>, so
/// the upsert's <c>SET</c> covers the written columns and nothing else. The fourth,
/// <c>flow_daily</c>, is written by a statement of its own and gets the same property
/// from its <c>DO UPDATE SET</c> list, which is a different mechanism and so is
/// checked by running it rather than by reasoning from the first.
///
/// **Each case is asserted in both directions.** A test that only shows the
/// percentiles surviving would pass just as well against a write that never touched
/// the row, so every case also shows the metric column being overwritten in the same
/// call, and the widened column set is exercised to show the property failing when the
/// staging table stops being narrow.
/// </summary>
[Collection("database")]
public sealed class PercentileSplitTests
{
    private static readonly DateOnly RunDate = new(2001, 5, 22);

    private const string Ticker = "SRLTEST.SPLIT";

    /// <summary>
    /// The three tables written through the staged bulk path, each with the column set
    /// its own engine declares. Read off the engines rather than restated, so a stage
    /// that widened its declaration would be tested as it now is rather than as it
    /// was.
    /// </summary>
    public static TheoryData<string, string[]> StagedTables() => new()
    {
        { "indicator_daily", IndicatorEngine.Columns },
        { "valuation_daily", ValuationEngine.Columns },
        { "sentiment_derived_daily", SentimentEngine.Columns },
    };

    /// <summary>
    /// The property itself. A metric engine re-running over a date C11 has already
    /// ranked leaves every percentile byte-identical.
    /// </summary>
    [Theory]
    [MemberData(nameof(StagedTables))]
    public async Task ReRunningAMetricEngineLeavesEveryPercentileByteIdentical(
        string table, string[] engineColumns)
    {
        var ct = TestContext.Current.CancellationToken;
        var source = SourceFor(table);

        await ClearAsync(table, ct).ConfigureAwait(true);
        await InsertAsync(table, engineColumns, ct).ConfigureAwait(true);

        // C11 has run: every percentile carries a distinct value, so a shuffle would
        // be caught as well as a blanking.
        await SetPercentilesAsync(table, source, ct).ConfigureAwait(true);

        // And the metric engine has something to overwrite, so the re-run below is
        // visibly a write rather than a call that did nothing.
        await SetMetricAsync(table, source.Metrics[0], 42, ct).ConfigureAwait(true);

        var before = await ReadPercentilesAsync(table, source, ct).ConfigureAwait(true);

        await InsertAsync(table, engineColumns, ct).ConfigureAwait(true);

        var after = await ReadPercentilesAsync(table, source, ct).ConfigureAwait(true);

        Assert.Equal(before, after);
        Assert.All(after, v => Assert.NotNull(v));

        // The same call did overwrite the column the engine owns, which is what makes
        // the equality above a statement about column ownership rather than about a
        // write that never happened.
        Assert.Null(await ReadMetricAsync(table, source.Metrics[0], ct).ConfigureAwait(true));

        await ClearAsync(table, ct).ConfigureAwait(true);
    }

    /// <summary>
    /// The change that would silently break it, made to fail on purpose.
    ///
    /// Widening the staging table to the full column set is a one-line edit that looks
    /// like completeness: the stage would then declare and write every column of the
    /// table, the registry would still pass because the operation is still
    /// <c>Insert</c>, and every percentile would go null on the next nightly run of a
    /// date already ranked. Nothing would error.
    /// </summary>
    [Theory]
    [MemberData(nameof(StagedTables))]
    public async Task WideningTheStagingTableToTheFullColumnSetBlanksThem(
        string table, string[] engineColumns)
    {
        var ct = TestContext.Current.CancellationToken;
        var source = SourceFor(table);

        await ClearAsync(table, ct).ConfigureAwait(true);
        await InsertAsync(table, engineColumns, ct).ConfigureAwait(true);
        await SetPercentilesAsync(table, source, ct).ConfigureAwait(true);

        Assert.All(await ReadPercentilesAsync(table, source, ct).ConfigureAwait(true),
            v => Assert.NotNull(v));

        var widened = engineColumns.Concat(source.PercentileColumns).ToArray();

        await InsertAsync(table, widened, ct).ConfigureAwait(true);

        Assert.All(await ReadPercentilesAsync(table, source, ct).ConfigureAwait(true),
            Assert.Null);

        await ClearAsync(table, ct).ConfigureAwait(true);
    }

    /// <summary>
    /// The cause, asserted where the effect above cannot point at it. The staging
    /// table names the written columns and no percentile column appears in it, which
    /// is the whole of why the upsert's <c>SET</c> leaves them alone.
    /// </summary>
    [Theory]
    [MemberData(nameof(StagedTables))]
    public void TheStagingTableNamesNoPercentileColumn(string table, string[] engineColumns)
    {
        var source = SourceFor(table);

        var staging = BulkUpsertSql.CreateStaging(
            BulkUpsertSql.StagingNameFor(table), table, engineColumns);

        var upsert = BulkUpsertSql.Upsert(
            table, BulkUpsertSql.StagingNameFor(table), engineColumns, ["ticker", "date"]);

        foreach (var column in source.PercentileColumns)
        {
            Assert.DoesNotContain(column, staging, StringComparison.Ordinal);
            Assert.DoesNotContain(column, upsert, StringComparison.Ordinal);
        }

        // And the metric columns are in both, so the assertions above are about the
        // percentile columns rather than about a statement that named nothing.
        Assert.Contains(source.Metrics[0], staging, StringComparison.Ordinal);
        Assert.Contains(source.Metrics[0], upsert, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>flow_daily</c> gets the same property from a different mechanism, so it is
    /// run rather than reasoned about.
    ///
    /// C34 writes one statement of its own with an explicit <c>DO UPDATE SET</c> list
    /// rather than going through the staged path, so nothing about the staging table
    /// protects it. What protects it is that list naming three columns, and a fourth
    /// added there would blank a percentile exactly as a widened staging table would.
    /// </summary>
    [Fact]
    public async Task ReRunningTheFlowEngineLeavesEveryPercentileByteIdentical()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = SourceFor("flow_daily");

        await ClearFlowAsync(ct).ConfigureAwait(true);
        await SeedOneInsiderPurchaseAsync(ct).ConfigureAwait(true);

        await RunFlowEngineAsync(ct).ConfigureAwait(true);

        var metric = await ReadMetricAsync("flow_daily", source.Metrics[0], ct).ConfigureAwait(true);
        Assert.NotNull(metric);

        await SetPercentilesAsync("flow_daily", source, ct).ConfigureAwait(true);
        var before = await ReadPercentilesAsync("flow_daily", source, ct).ConfigureAwait(true);

        await RunFlowEngineAsync(ct).ConfigureAwait(true);

        Assert.Equal(before, await ReadPercentilesAsync("flow_daily", source, ct).ConfigureAwait(true));

        // The re-run wrote the row rather than skipping it, so the equality is about
        // the columns C34 does not own.
        Assert.Equal(metric, await ReadMetricAsync("flow_daily", source.Metrics[0], ct).ConfigureAwait(true));

        await ClearFlowAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// The declaration-level form of the same statement, over all four tables at once.
    /// No metric engine claims a column C11 claims, and C11 claims nothing else.
    /// </summary>
    [Fact]
    public void NoMetricEngineDeclaresAColumnThePercentileEngineOwns()
    {
        var byTable = new (string Table, IReadOnlyList<string> Engine)[]
        {
            ("indicator_daily", IndicatorEngine.Columns),
            ("valuation_daily", ValuationEngine.Columns),
            ("flow_daily", FlowEngine.FlowColumns),
            ("sentiment_derived_daily", SentimentEngine.Columns),
        };

        foreach (var (table, engine) in byTable)
        {
            var owned = SourceFor(table).PercentileColumns;

            Assert.Empty(engine.Intersect(owned, StringComparer.Ordinal));

            // And the split is real rather than two components writing disjoint halves
            // of nothing: both sets are non-empty and both are on the same table.
            Assert.NotEmpty(owned);
            Assert.NotEmpty(engine);
        }
    }

    // --------------------------------------------------------------- rigging ---

    private static PercentileEngine.MetricTable SourceFor(string table)
        => PercentileEngine.Sources.Single(s => string.Equals(s.Table, table, StringComparison.Ordinal));

    /// <summary>
    /// One row through the staged path with the given column set, every column null
    /// except the key. Null rather than a value because the columns differ in type
    /// across the four tables and none of the assertions here is about what a metric
    /// engine computes.
    /// </summary>
    private static async Task InsertAsync(string table, IReadOnlyList<string> columns, CancellationToken ct)
    {
        var data = new StageData(
            TestDatabase.ConnectionString,
            new DeclaredAccess("TestMetricEngine", [],
                [new TableWrite(table, WriteOperation.Insert, columns)]));

        await data.BulkUpsertAsync(table, columns, ["ticker", "date"],
            async (w, c) =>
            {
                await w.StartRowAsync(c).ConfigureAwait(false);

                foreach (var column in columns)
                {
                    if (string.Equals(column, "ticker", StringComparison.Ordinal))
                    {
                        await w.WriteAsync(Ticker, c).ConfigureAwait(false);
                    }
                    else if (string.Equals(column, "date", StringComparison.Ordinal))
                    {
                        await w.WriteAsync(RunDate, c).ConfigureAwait(false);
                    }
                    else
                    {
                        await w.WriteAsync<object?>(null, c).ConfigureAwait(false);
                    }
                }
            }, ct).ConfigureAwait(false);
    }

    private static async Task SetPercentilesAsync(
        string table, PercentileEngine.MetricTable source, CancellationToken ct)
    {
        var sets = string.Join(", ", source.PercentileColumns.Select(
            (c, i) => $"{c} = {(i + 1).ToString(CultureInfo.InvariantCulture)}"));

        await ExecuteAsync($"UPDATE {table} SET {sets} WHERE ticker = @t AND date = @d;", ct)
            .ConfigureAwait(false);
    }

    private static async Task SetMetricAsync(string table, string metric, int value, CancellationToken ct)
        => await ExecuteAsync(
            $"UPDATE {table} SET {metric} = {value.ToString(CultureInfo.InvariantCulture)} " +
            "WHERE ticker = @t AND date = @d;", ct).ConfigureAwait(false);

    private static async Task<IReadOnlyList<double?>> ReadPercentilesAsync(
        string table, PercentileEngine.MetricTable source, CancellationToken ct)
    {
        var columns = string.Join(", ", source.PercentileColumns.Select(c => c + "::numeric"));

        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {columns} FROM {table} WHERE ticker = @t AND date = @d;", conn);
        cmd.Parameters.AddWithValue("t", Ticker);
        cmd.Parameters.AddWithValue("d", RunDate);

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        Assert.True(await r.ReadAsync(ct).ConfigureAwait(false),
            $"{table} has no row for {Ticker}, so there is nothing to assert over.");

        var values = new List<double?>(source.PercentileColumns.Count);

        for (var i = 0; i < source.PercentileColumns.Count; i++)
        {
            values.Add(await r.IsDBNullAsync(i, ct).ConfigureAwait(false)
                ? null
                : (double) r.GetDecimal(i));
        }

        return values;
    }

    private static async Task<double?> ReadMetricAsync(string table, string metric, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {metric}::numeric FROM {table} WHERE ticker = @t AND date = @d;", conn);
        cmd.Parameters.AddWithValue("t", Ticker);
        cmd.Parameters.AddWithValue("d", RunDate);

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        Assert.True(await r.ReadAsync(ct).ConfigureAwait(false), $"{table} has no row for {Ticker}.");

        return await r.IsDBNullAsync(0, ct).ConfigureAwait(false) ? null : (double) r.GetDecimal(0);
    }

    private static async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("t", Ticker);
        cmd.Parameters.AddWithValue("d", RunDate);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task ClearAsync(string table, CancellationToken ct)
        => await ExecuteAsync($"DELETE FROM {table} WHERE ticker = @t;", ct).ConfigureAwait(false);

    // ------------------------------------------------------------ flow_daily ---

    private static async Task RunFlowEngineAsync(CancellationToken ct)
    {
        var stage = new FlowEngine();

        var context = new StageContext(
            RunDate, 1,
            new StageData(TestDatabase.ConnectionString, new DeclaredAccess(stage)),
            new FrozenClock(RunDate),
            new StubConfig(45));

        await stage.ExecuteAsync(context, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// One priced, attributed, in-window open-market purchase, which is the minimum
    /// that makes C34 write a row with a non-null total.
    /// </summary>
    private static async Task SeedOneInsiderPurchaseAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO insider_transaction
                (ticker, accession_number, transaction_side, transaction_ordinal, filed_at,
                 transaction_date, reporting_owner_cik, transaction_code, shares_amount,
                 price_per_share, acquired_or_disposed)
            VALUES (@t, 'SPLIT-1', 'non_derivative', 0, @f, @x, '999', 'P', 1000, 25, 'A');
            """, conn);
        cmd.Parameters.AddWithValue("t", Ticker);
        cmd.Parameters.AddWithValue("f", RunDate.AddDays(-10));
        cmd.Parameters.AddWithValue("x", RunDate.AddDays(-11));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task ClearFlowAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);

        foreach (var table in new[] { "flow_daily", "insider_transaction" })
        {
            await using var cmd = new NpgsqlCommand(
                "DELETE FROM " + table + " WHERE ticker = @t;", conn);
            cmd.Parameters.AddWithValue("t", Ticker);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private sealed class FrozenClock(DateOnly today) : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(today.Year, today.Month, today.Day, 21, 30, 0, TimeSpan.Zero);

        public DateOnly Today { get; } = today;
    }

    private sealed class StubConfig(int value) : IConfigStore
    {
        public Task<ConfigRow?> ResolveAsync(string key, DateOnly asOf, CancellationToken ct = default)
            => Task.FromResult<ConfigRow?>(new ConfigRow(
                key, 1, value.ToString(CultureInfo.InvariantCulture), new DateOnly(2000, 1, 1)));

        public async Task<ConfigRow> RequireAsync(string key, DateOnly asOf, CancellationToken ct = default)
            => (await ResolveAsync(key, asOf, ct).ConfigureAwait(false))!;

        public Task<int?> ResolveVersionAsync(DateOnly asOf, CancellationToken ct = default)
            => throw new NotSupportedException("A stage does not resolve the store-wide config version.");

        public Task<int> RequireVersionAsync(DateOnly asOf, CancellationToken ct = default)
            => throw new NotSupportedException("A stage does not resolve the store-wide config version.");
    }
}
