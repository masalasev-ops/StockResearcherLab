using System.Text.RegularExpressions;
using Npgsql;
using StockResearcherLab.Core.Gates;
using StockResearcherLab.Core.Screens;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;
using StockResearcherLab.Pipeline;
using StockResearcherLab.Pipeline.Compute;
using StockResearcherLab.Pipeline.Ingest;
using StockResearcherLab.Pipeline.Select;
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
    private static IEnumerable<(string Stage, string Table, WriteOperation Operation, string Sql)> Statements()
    {
        var date = new DateOnly(2026, 8, 7);
        const WriteOperation insert = WriteOperation.Insert;
        const WriteOperation update = WriteOperation.Update;

        yield return ("FlowEngine", "flow_daily", insert, FlowEngine.Sql);

        foreach (var source in PercentileEngine.Sources)
        {
            yield return ("PercentileEngine", source.Table, update, PercentileEngine.UpdateSql(source, date, 15));
            yield return ("PercentileEngine", "percentile_cell_daily", insert, PercentileEngine.CellSql(source, date, 15));
            yield return ("PercentileEngine", "percentile_cell_coverage", insert, PercentileEngine.CoverageSql(source.Table, date));
        }

        // The six statement writers 5.5.10 adds, and CostLedger, which the plan's count of
        // six did not name. Each statement is built by the component's own builder, or for
        // the three whose rows are assembled at run time, from the fixed head the builder
        // itself uses.
        var screen = new ScreenDefinition(
            "S2", [ScreenMetric.Ranked("adx14", MetricDirection.High)], 1, ScreenState.Live, 8);

        yield return ("ScreenEngine", "screen_score_daily", insert, ScreenEngine.ScoreSql(screen, date, 1));
        yield return ("ScreenEngine", "screen_score_daily", update, ScreenEngine.RankSql("S2", date, 0.5));
        yield return ("ScreenEngine", "screen_score_daily", update, ScreenEngine.ClearRanksSql("S2", date));
        yield return ("ScreenEngine", "screen_history", insert, ScreenEngine.HistorySql("S2", date, 0.9, 0.9, 250));

        yield return ("GateEngine", "gate_result", insert, GateEngine.Sql(
            date, new GateEngine.GateThresholds(8, 5, 2, 30), GateReasons.All.ToHashSet()));

        yield return ("CandidateAllocator", "candidate_set", insert,
            CandidateAllocator.Sql([("S2", SlotQuota.For(8))], date));
        yield return ("CandidateAllocator", "attribution", insert,
            CandidateAllocator.AttributionSql([("S2", SlotQuota.For(8))], [("X-FM", SlotQuota.For(8))], date, 1));

        yield return ("ConcentrationMonitor", "alert", insert, ConcentrationMonitor.AlertInsertHead);
        yield return ("HeadlineIngestor", "headline", insert, HeadlineIngestor.InsertHead);
        yield return ("NewsDigester", "news_digest", insert, NewsDigester.InsertHead);
        yield return ("CostLedger", "cost_ledger", insert, CostLedger.InsertSql);
    }

    /// <summary>
    /// Every column a stage declares for a table appears in the statement that writes
    /// that table.
    /// </summary>
    [Fact]
    public void EveryDeclaredColumnAppearsInItsOwnStatement()
    {
        var declared = DeclaredWrites();

        foreach (var (stage, table, operation, sql) in Statements())
        {
            // Matched on the operation as well [5.5.10]. A component declaring an insert
            // and an update on one table declares two column sets, and the update's
            // statement is held to its own rather than to the union.
            var columns = declared
                .Where(w => string.Equals(w.Component, stage, StringComparison.Ordinal)
                            && string.Equals(w.Table, table, StringComparison.Ordinal)
                            && w.Operation == operation)
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

        // **A partial declaration is discriminated as well as a percentile one** [5.5.10].
        // C13's rank update declares one column of a table it also inserts into, and C14's
        // attribution insert declares the columns C21 does not own. Each statement is
        // checked against its own set, and a column taken out of either is named.
        var date = new DateOnly(2026, 8, 7);

        var rank = ScreenEngine.RankSql("S2", date, 0.5);
        Assert.Empty(Missing(ScreenEngine.RankColumns, rank));
        Assert.Equal(
            ["rank_within_screen"],
            Missing(ScreenEngine.RankColumns, rank.Replace("rank_within_screen", "some_other_column", StringComparison.Ordinal)));

        var attribution = CandidateAllocator.AttributionSql(
            [("S2", SlotQuota.For(8))], [("X-FM", SlotQuota.For(8))], date, 1);
        Assert.Empty(Missing(CandidateAllocator.AttributionColumns, attribution));
        Assert.Equal(
            ["gate_state"],
            Missing(CandidateAllocator.AttributionColumns, attribution.Replace("gate_state", "some_other_column", StringComparison.Ordinal)));
    }

    /// <summary>
    /// The write direction discriminates too. C13's rank update with a second assignment
    /// added, to <c>score</c>, which the update does not declare and the insert does, is
    /// reported: the update reaching into a column its declaration does not give it [5.5.10].
    /// </summary>
    [Fact]
    public void TheWriteCheckFailsWhenAStatementWritesPastItsDeclaration()
    {
        var rank = ScreenEngine.RankSql("S2", new DateOnly(2026, 8, 7), 0.5);
        const string assignment = "SET rank_within_screen = r.rk";

        Assert.Contains(assignment, rank, StringComparison.Ordinal);

        var widened = rank.Replace(assignment, assignment + ", score = 0", StringComparison.Ordinal);

        Assert.Equal(["rank_within_screen"], Written(rank, "screen_score_daily", WriteOperation.Update));
        Assert.Contains("score", Written(widened, "screen_score_daily", WriteOperation.Update));
        Assert.DoesNotContain("score", ScreenEngine.RankColumns);
    }

    [Fact]
    public void EveryNamedStatementBelongsToARegisteredStage()
    {
        var registered = PipelineComposition
            .AllOwnersForConformance(TestDatabase.ConnectionString)
            .Select(o => o.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (stage, _, _, _) in Statements())
        {
            Assert.Contains(stage, registered);
        }
    }

    /// <summary>
    /// **Every component that declares the columns it inserts or updates has those columns
    /// checked on one route or the other** [5.5.10].
    ///
    /// The staged bulk route checks at the call, <see cref="DeclaredAccess.EnsureColumnsDeclared"/>
    /// refusing an undeclared column before a connection opens. The statement route is
    /// checked here, by <see cref="Statements"/>, and only for what that list names. Until
    /// 5.5.10 it named two components, and `BUILD_PLAN.md` carried column ownership as closed
    /// at 1.12 while every other statement writer declared columns nothing compared with
    /// anything. This walks the registry so that cannot recur: a declared insert or update
    /// is either a statement on the list or a bulk write naming that table in the component's
    /// own source, and anything else is named here.
    ///
    /// Deletes are not walked. A delete writes no column, so a column list declared on one
    /// is held to nothing either way.
    /// </summary>
    [Fact]
    public void EveryComponentDeclaringWriteColumnsIsCheckedOnOneRoute()
    {
        var files = SourceTree.Files();
        var stated = Statements().Select(s => (s.Stage, s.Table, s.Operation)).ToHashSet();

        var uncovered = new List<string>();

        foreach (var owner in PipelineComposition.AllOwnersForConformance(TestDatabase.ConnectionString))
        {
            var bulk = BulkTables(SourceTree.Of(files, owner.GetType().Name));

            foreach (var write in owner.WriteSet.Where(w => w.Columns.Count > 0
                         && w.Operation is WriteOperation.Insert or WriteOperation.Update))
            {
                if (stated.Contains((owner.Name, write.Table, write.Operation))
                    || (write.Operation == WriteOperation.Insert && bulk.Contains(write.Table)))
                {
                    continue;
                }

                uncovered.Add($"{owner.Name} -> {write.Operation} {write.Table}");
            }
        }

        uncovered.Sort(StringComparer.Ordinal);

        Assert.True(uncovered.Count == 0,
            "Declared write columns no route checks. Each needs its statement on Statements(), or a " +
            "bulk write naming the table:\n  " + string.Join("\n  ", uncovered));
    }

    /// <summary>
    /// **No statement writes a column its component does not declare for that operation**
    /// [INVARIANT 10, 5.5.10]. The direction above asks that a declaration is not fiction;
    /// this one asks that a statement does not write past it, which is what column
    /// ownership means: two components sharing a table each own a part of it, and a
    /// statement reaching into the other's part is the breach the bulk route refuses at the
    /// call. Read off the statement's own column list for an insert and its <c>SET</c>
    /// assignments for an update, and kept to the columns the table has, so a parse that
    /// picked up an alias names nothing rather than everything.
    /// </summary>
    [Fact]
    public async Task EveryColumnAStatementWritesIsDeclared()
    {
        var actual = await ColumnsByTableAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        var declared = DeclaredWrites();

        var undeclared = new List<string>();

        foreach (var (stage, table, operation, sql) in Statements())
        {
            var columns = declared
                .Where(w => w.Component == stage && w.Table == table && w.Operation == operation)
                .SelectMany(w => w.Columns)
                .ToHashSet(StringComparer.Ordinal);

            var written = Written(sql, table, operation).Where(actual[table].Contains).ToList();

            Assert.True(written.Count > 0, $"{stage}'s {operation} on {table} parsed as writing nothing, so this check passed over it.");

            undeclared.AddRange(written.Where(c => !columns.Contains(c)).Select(c => $"{stage} -> {operation} {table}.{c}"));
        }

        Assert.True(undeclared.Count == 0,
            "Statements write columns their component does not declare: " +
            string.Join(", ", undeclared.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal)));
    }

    /// <summary>
    /// The columns a statement writes into one table: an insert's column list and every
    /// <c>SET</c> assignment an update on that table makes, an <c>ON CONFLICT ... DO UPDATE</c>
    /// included.
    /// </summary>
    private static List<string> Written(string sql, string table, WriteOperation operation)
    {
        var written = new List<string>();
        var name = Regex.Escape(table);

        if (operation == WriteOperation.Insert)
        {
            foreach (Match m in Regex.Matches(sql, $@"INSERT\s+INTO\s+""?{name}""?\s*\((?<c>[^)]*)\)"))
            {
                written.AddRange(m.Groups["c"].Value.Split(',').Select(c => c.Trim().Trim('"')));
            }
        }

        foreach (Match m in Regex.Matches(sql,
                     $@"UPDATE\s+""?{name}""?(?:\s+(?:AS\s+)?[a-z]\w*)?\s+SET\s+(?<s>.+?)(?=\s(?:FROM|WHERE|RETURNING)\b|;|$)",
                     RegexOptions.Singleline))
        {
            written.AddRange(Assignments(m.Groups["s"].Value));
        }

        foreach (Match m in Regex.Matches(sql, @"DO\s+UPDATE\s+SET\s+(?<s>.+?)(?=\s(?:WHERE|RETURNING)\b|;|$)", RegexOptions.Singleline))
        {
            written.AddRange(Assignments(m.Groups["s"].Value));
        }

        return [.. written.Where(c => c.Length > 0).Distinct(StringComparer.Ordinal)];

        static IEnumerable<string> Assignments(string set)
            => Regex.Matches(set, @"(?:^|,)\s*(?:[a-z]\w*\.)?(?<c>[a-z_][a-z0-9_]*)\s*=")
                .Select(a => a.Groups["c"].Value);
    }

    /// <summary>The tables a component's source passes literally to <c>BulkUpsertAsync</c>.</summary>
    private static HashSet<string> BulkTables(string source)
        => [.. Regex.Matches(source, @"BulkUpsertAsync\(\s*""(?<t>[a-z_][a-z0-9_]*)""")
            .Select(m => m.Groups["t"].Value)];

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

    private static IReadOnlyList<(string Component, string Table, WriteOperation Operation, IReadOnlyList<string> Columns)>
        DeclaredWrites()
        => PipelineComposition.AllOwnersForConformance(TestDatabase.ConnectionString)
            .SelectMany(o => o.WriteSet.Select(w => (Component: o.Name, w.Table, w.Operation, w.Columns)))
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
