using Npgsql;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Pipeline;
using StockResearcherLab.Pipeline.Compute;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Stages;

/// <summary>
/// Checkpoint 2.11. A declared column set is a claim about what a stage writes, and
/// two things could make it fiction without anything failing.
///
/// **A stage writing through a statement of its own is not column-checked at the
/// call.** <see cref="IStageData.WriteAsync"/> checks the table and the operation and
/// cannot see what the SQL touches, so C34's and C11's declarations are held to their
/// statements here rather than at runtime. The staged path is different and needs no
/// such test: <see cref="DeclaredAccess.EnsureColumnsDeclared"/> refuses a column the
/// stage did not declare before a connection opens, which `BulkUpsertTests` already
/// exercises.
///
/// **Neither route checks that a declared column exists at all.** The declaration is
/// compared against the write and the write against the declaration, so a name wrong
/// in both is wrong consistently. That is not hypothetical: a column renamed by a
/// later migration leaves a declaration pointing at nothing, and `flow_daily` has
/// already had one renamed once [A1.a]. So every declared column is checked against
/// the live schema.
/// </summary>
[Collection("database")]
public sealed class StatementColumnConformanceTests
{
    /// <summary>
    /// The stages that write through a hand-written statement, with it.
    ///
    /// Two, and a third arrives the next time a stage writes without going through the
    /// staged bulk path. There is no marker in the registry that says which route a
    /// stage takes, so this list is stated rather than derived, and
    /// <see cref="EveryNamedStatementBelongsToARegisteredStage"/> is what keeps it from
    /// naming something that no longer exists.
    /// </summary>
    private static IEnumerable<(string Stage, string Table, string Sql)> Statements()
    {
        yield return ("FlowEngine", "flow_daily", FlowEngine.Sql);

        foreach (var source in PercentileEngine.Sources)
        {
            yield return ("PercentileEngine", source.Table, PercentileEngine.UpdateSql(
                source, new DateOnly(2026, 8, 7), 15));
        }
    }

    /// <summary>
    /// Every column a stage declares for a table appears in the statement that writes
    /// that table.
    /// </summary>
    [Fact]
    public void EveryDeclaredColumnAppearsInItsOwnStatement()
    {
        var declared = DeclaredWrites();

        foreach (var (stage, table, sql) in Statements())
        {
            var columns = declared
                .Where(w => string.Equals(w.Component, stage, StringComparison.Ordinal)
                            && string.Equals(w.Table, table, StringComparison.Ordinal))
                .SelectMany(w => w.Columns)
                .ToList();

            Assert.True(columns.Count > 0,
                $"{stage} declares no columns for {table}, so this assertion would pass over nothing.");

            Assert.Empty(Missing(columns, sql).Select(c => $"{stage} -> {table}.{c}"));
        }
    }

    /// <summary>
    /// The check discriminates. A conformance test that has never failed has not been
    /// tested, and a substring search over a long statement is exactly the kind that
    /// passes on everything.
    /// </summary>
    [Fact]
    public void TheCheckFailsWhenADeclaredColumnLeavesTheStatement()
    {
        var source = PercentileEngine.Sources[0];
        var sql = PercentileEngine.UpdateSql(source, new DateOnly(2026, 8, 7), 15);

        var dropped = source.PercentileColumns[0];
        var mutilated = sql.Replace(dropped, "some_other_column", StringComparison.Ordinal);

        Assert.Empty(Missing(source.PercentileColumns, sql));
        Assert.Equal([dropped], Missing(source.PercentileColumns, mutilated));
    }

    [Fact]
    public void EveryNamedStatementBelongsToARegisteredStage()
    {
        var registered = PipelineComposition
            .AllOwnersForConformance(TestDatabase.ConnectionString)
            .Select(o => o.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (stage, _, _) in Statements())
        {
            Assert.Contains(stage, registered);
        }
    }

    /// <summary>
    /// Every column any component declares is a column the database has.
    ///
    /// This covers the staged path as well, which the statement check above cannot
    /// reach, and it is the only thing standing between a declaration and a name that
    /// stopped existing. A COPY into a column that is not there fails loudly; a
    /// declaration that has drifted out of the schema does not, because nothing
    /// compares the two.
    /// </summary>
    [Fact]
    public async Task EveryDeclaredColumnExistsInTheLiveSchema()
    {
        var ct = TestContext.Current.CancellationToken;
        var actual = await ColumnsByTableAsync(ct).ConfigureAwait(true);

        var missing = DeclaredWrites()
            .SelectMany(w => w.Columns.Select(c => (w.Component, w.Table, Column: c)))
            .Where(x => !actual.TryGetValue(x.Table, out var columns) || !columns.Contains(x.Column))
            .Select(x => $"{x.Component} -> {x.Table}.{x.Column}")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "Component(s) declare a column the schema does not have: " + string.Join(", ", missing));

        // The declarations are not empty, so the assertion above is over something.
        // Phase 2 alone adds thirty percentile columns and forty-two metric ones.
        Assert.True(DeclaredWrites().Sum(w => w.Columns.Count) > 60,
            "The registry declares almost no columns, so the check above passed over nothing.");
    }

    // ---------------------------------------------------------------- helpers ---

    private static IReadOnlyList<(string Component, string Table, IReadOnlyList<string> Columns)>
        DeclaredWrites()
        => PipelineComposition.AllOwnersForConformance(TestDatabase.ConnectionString)
            .SelectMany(o => o.WriteSet.Select(w => (Component: o.Name, w.Table, w.Columns)))
            .Where(w => w.Columns.Count > 0)
            .ToList();

    /// <summary>
    /// Declared but absent from the statement, ordinal and in declared order so the
    /// failure names them the way the stage does.
    ///
    /// A plain substring, which is what makes the negative fixture above necessary: it
    /// would also match a longer name containing this one. That is acceptable here
    /// because it errs toward passing, and the direction that matters is a column
    /// leaving the statement entirely.
    /// </summary>
    private static IReadOnlyList<string> Missing(IReadOnlyList<string> columns, string sql)
        => columns.Where(c => !sql.Contains(c, StringComparison.Ordinal)).ToList();

    private static async Task<IReadOnlyDictionary<string, IReadOnlySet<string>>> ColumnsByTableAsync(
        CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT table_name, column_name
            FROM information_schema.columns
            WHERE table_schema = 'public'
            ORDER BY table_name, column_name;
            """, conn);

        var byTable = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var table = r.GetString(0);

            if (!byTable.TryGetValue(table, out var columns))
            {
                byTable[table] = columns = new HashSet<string>(StringComparer.Ordinal);
            }

            columns.Add(r.GetString(1));
        }

        return byTable.ToDictionary(
            kv => kv.Key, kv => (IReadOnlySet<string>) kv.Value, StringComparer.Ordinal);
    }
}
