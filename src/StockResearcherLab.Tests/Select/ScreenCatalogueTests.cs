using System.Globalization;
using Npgsql;
using StockResearcherLab.Core;
using StockResearcherLab.Core.Config;
using StockResearcherLab.Core.Screens;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;
using StockResearcherLab.Pipeline.Compute;
using StockResearcherLab.Pipeline.Select;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Select;

/// <summary>
/// Checkpoint 4.6. S2, S3 and S4 are configuration and reach no new code path, and a
/// fabricated sixth screen scores with nothing in the source naming it.
///
/// **The screens are held against `ARCHITECTURE.html` §05 rather than against a list
/// written here.** A seeded metric list is data, so nothing but a reader who remembers
/// §05 stands between `screens.S3.metrics` losing an input to a bad edit and every
/// downstream number staying plausible. That is this system's characteristic failure
/// rather than an unlikely one [`CLAUDE.md` §1].
///
/// **Where the seeded rows and §05 differ, the difference is a row in
/// <see cref="Divergences"/> carrying the decision that authorised it**, and a second
/// test asserts every one of them is still true of the document. A divergence the
/// document catches up with fails its own entry rather than sitting in the list for
/// ever.
/// </summary>
[Collection("database")]
public sealed class ScreenCatalogueTests
{
    /// <summary>
    /// The date these fixtures score on. Distinct from every other Select fixture's, so
    /// the by-date cleanup each of them does cannot reach this one's rows.
    /// </summary>
    private static readonly DateOnly RunDate = new(2021, 3, 1);

    private const string Bucket = "srltest-catalogue";
    private const string Sector = "srltest-catalogue";

    /// <summary>A name carrying every input of whichever screen is under test.</summary>
    private const string Whole = "SRLCAT.WHOLE";

    /// <summary>
    /// The percentile every `high` input is given, and the score a screen whose inputs
    /// all sit at one adjusted percentile must produce.
    ///
    /// **This is a property of a weighted mean rather than a restatement of the stage's
    /// expression.** Every ranked input adjusted to the same value means the mean is
    /// that value at any weights and any input count, so the expected figure is a
    /// constant here and is not recomputed from the metric list.
    /// </summary>
    private const double High = 60d;

    /// <summary>
    /// What a `low` input is given so that it adjusts to <see cref="High"/>, direction
    /// `low` being 100 minus the stored percentile [D-113].
    ///
    /// This is what makes the constant above a direction test as well as an arithmetic
    /// one: one metric read in the wrong direction contributes 40 where its neighbours
    /// contribute 60, and the mean moves off the constant.
    /// </summary>
    private const double Low = 100d - High;

    // ------------------------------------------------- the catalogue itself ---

    /// <summary>
    /// A metric §05 names that the seeded configuration does not carry under that name.
    /// </summary>
    /// <param name="ScreenId">The screen whose Ranks-on cell carries the token.</param>
    /// <param name="DocumentToken">The token as §05 writes it.</param>
    /// <param name="ConfigMetric">
    /// What configuration carries instead, or null where the input is gone from the
    /// screen entirely.
    /// </param>
    /// <param name="Why">The decision that authorised the difference.</param>
    private sealed record Divergence(string ScreenId, string DocumentToken, string? ConfigMetric, string Why);

    private static readonly IReadOnlyList<Divergence> Divergences =
    [
        new("S1", "net_debt_ebitda_inv", "net_debt_ebitda",
            "D-113. Direction is a word on the metric, not a second stored column. An _inv column " +
            "is a second copy of one fact and doubles the percentile write, which is the shape " +
            "D-76, D-77 and D-83 each removed from this corpus."),
        new("S1", "accruals_inv", "accruals", "D-113, as above."),
        new("S1", "share_count_change_inv", "share_count_change", "D-113, as above."),

        // The one entry here that is a report rather than a record. D-118 is ACTIVE and
        // section 05 has not been amended to it, so the document and the register
        // disagree. Configuration follows the register, being the later and more
        // specific statement, and the amendment is reported rather than taken:
        // ARCHITECTURE.html is human-edited [CLAUDE.md section 13].
        new("S4", "inst_ownership_change", null,
            "D-118 drops it and runs S4 on its two insider inputs. Section 05's Ranks-on cell " +
            "still names it and needs the amendment; this entry is what keeps that visible " +
            "rather than letting the seeded two-input list read as the design."),
    ];

    /// <summary>
    /// S5 ranks on a clause rather than a metric list, so its Ranks-on cell holds no
    /// metric token at all and this check cannot speak to it. 4.7 is where S5's own
    /// composites and gate conditions are held against the two tables §05 puts below the
    /// screen table.
    /// </summary>
    private const string GatedScreen = "S5";

    [Fact]
    public async Task EveryScreenTheCatalogueNamesIsRegisteredInConfig()
    {
        var ct = TestContext.Current.CancellationToken;
        await new ConfigSeeder(TestDatabase.ConnectionString).SeedAsync(ct).ConfigureAwait(true);

        var registered = await ScreenRegistry
            .IdsAsync(new ConfigStore(TestDatabase.ConnectionString), RunDate, ct).ConfigureAwait(true);

        foreach (var row in ArchitectureDocument.ScreenTable())
        {
            Assert.Contains(row.Id, registered);
        }
    }

    /// <summary>
    /// **Each screen ranks on what §05 says it ranks on**, the recorded divergences
    /// applied. This is checkpoint 4.6's own assertion: S2, S3 and S4 arrived as rows at
    /// 4.3 and nothing verified them against the design until this ran.
    /// </summary>
    [Fact]
    public async Task EveryScreenRanksOnWhatTheCatalogueNames()
    {
        var ct = TestContext.Current.CancellationToken;
        await new ConfigSeeder(TestDatabase.ConnectionString).SeedAsync(ct).ConfigureAwait(true);

        var config = new ConfigStore(TestDatabase.ConnectionString);
        var checkedRows = 0;

        foreach (var row in ArchitectureDocument.ScreenTable())
        {
            if (row.Id == GatedScreen)
            {
                Assert.Empty(row.Metrics);
                continue;
            }

            var expected = row.Metrics
                .Select(t => Mapped(row.Id, t))
                .Where(t => t is not null)
                .Select(t => t!)
                .OrderBy(t => t, StringComparer.Ordinal);

            var definition = await ScreenRegistry.LoadOneAsync(config, row.Id, RunDate, ct).ConfigureAwait(true);

            var actual = definition.Metrics
                .Select(m => m.Metric)
                .OrderBy(t => t, StringComparer.Ordinal);

            Assert.Equal(expected, actual);
            checkedRows++;
        }

        // Not a silent zero: a parse that matched nothing would otherwise report a pass
        // over an empty loop, which is the failure this whole class exists to prevent.
        Assert.Equal(4, checkedRows);
    }

    /// <summary>
    /// **Every recorded divergence is still a divergence.** Without this the list is a
    /// place where an entry outlives the difference it records, and the next reader
    /// takes it for a statement about the document that is no longer true. It is also
    /// what will fail when §05's S4 cell is amended to D-118, which is the point.
    /// </summary>
    [Fact]
    public void EveryRecordedDivergenceIsStillTrueOfTheDocument()
    {
        var table = ArchitectureDocument.ScreenTable()
            .ToDictionary(r => r.Id, r => r.Metrics, StringComparer.Ordinal);

        foreach (var divergence in Divergences)
        {
            Assert.True(
                table[divergence.ScreenId].Contains(divergence.DocumentToken),
                $"ARCHITECTURE.html section 05 no longer names '{divergence.DocumentToken}' under " +
                $"{divergence.ScreenId}, so this divergence is spent and its row should go. {divergence.Why}");
        }
    }

    /// <summary>
    /// The parser fails on a document it cannot read rather than reporting an empty
    /// table, which would pass every check above vacuously. A conformance check that has
    /// never failed has not been tested, and the only way to fail this one is to hand it
    /// a different document.
    /// </summary>
    [Fact]
    public void AnUnreadableScreenTableFailsRatherThanReadingEmpty()
        => Assert.Throws<InvalidOperationException>(
            () => ArchitectureDocument.ScreenTableIn(
                "<table><tbody><tr><td>nothing here</td></tr></tbody></table>"));

    /// <summary>
    /// **S2 is the only screen carrying a bonus** [D-114]. `base_breakout_flag` is the
    /// one column with a named reader that is not percentiled, so it enters in score
    /// points outside the mean, and a second screen quietly gaining one would change
    /// what a score means without changing anything visible.
    /// </summary>
    [Fact]
    public async Task S2IsTheOnlyScreenCarryingABonus()
    {
        var ct = TestContext.Current.CancellationToken;
        await new ConfigSeeder(TestDatabase.ConnectionString).SeedAsync(ct).ConfigureAwait(true);

        var config = new ConfigStore(TestDatabase.ConnectionString);

        foreach (var row in ArchitectureDocument.ScreenTable())
        {
            var definition = await ScreenRegistry.LoadOneAsync(config, row.Id, RunDate, ct).ConfigureAwait(true);

            if (row.Id == "S2")
            {
                var bonus = Assert.Single(definition.Bonuses);
                Assert.Equal("base_breakout_flag", bonus.Metric);
                Assert.Equal(10d, bonus.BonusPoints);
            }
            else
            {
                Assert.Empty(definition.Bonuses);
            }
        }
    }

    /// <summary>
    /// **S4 runs on two inputs and both come from one store** [D-118]. The decision
    /// records that as a cost rather than glossing it: the screen was designed with four
    /// inputs from three sources and this is the second removal, so a single provider
    /// change now takes the whole screen. The assertion is here because the cost is only
    /// visible by counting the stores its metric list touches.
    /// </summary>
    [Fact]
    public async Task S4RunsOnTwoInsiderInputsFromOneStore()
    {
        var ct = TestContext.Current.CancellationToken;
        await new ConfigSeeder(TestDatabase.ConnectionString).SeedAsync(ct).ConfigureAwait(true);

        var definition = await ScreenRegistry
            .LoadOneAsync(new ConfigStore(TestDatabase.ConnectionString), "S4", RunDate, ct).ConfigureAwait(true);

        Assert.Equal(2, definition.RankedMetrics.Count);
        Assert.Equal(2, definition.MinInputs);
        Assert.DoesNotContain(definition.Metrics, m => m.Metric == "inst_ownership_change");

        Assert.Equal(["flow_daily"], definition.Metrics.Select(m => TableOf(m.Metric)).Distinct());
    }

    // -------------------------------------------------------- and they score ---

    /// <summary>
    /// **S2, S3 and S4 score through the statement 4.4 built, with nothing added.** The
    /// name carries every input of the screen at one adjusted percentile, so the score is
    /// that percentile whatever the weights and the input count are, and any input read
    /// in the wrong direction moves it.
    ///
    /// S1 is here too. It was never scored against its own seeded list either: 4.4 ran on
    /// a fabricated screen, so the seeded five have been configuration that nothing
    /// executed until now.
    /// </summary>
    [Theory]
    [InlineData("S1")]
    [InlineData("S2")]
    [InlineData("S3")]
    [InlineData("S4")]
    public async Task ARegisteredScreenScoresAtThePercentileItsInputsSitAt(string screenId)
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedForAsync(screenId, ct);

        try
        {
            await RunAsync(ct);

            Assert.Equal(High, (await ScoreAsync(screenId, Whole, ct))!.Value, 4);
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// **The bonus lands outside the mean and a null flag contributes what false
    /// contributes** [D-114], asserted on S2's own seeded list rather than on 4.4's
    /// fabricated one. Ten points on a mean of sixty is seventy, which is the magnitude
    /// stated in configuration rather than falling out of the arithmetic.
    /// </summary>
    [Fact]
    public async Task S2sBonusIsTenPointsOnTopOfItsMean()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedForAsync("S2", ct);

        try
        {
            await SetFlagAsync(Whole, true, ct);
            await RunAsync(ct);
            Assert.Equal(High + 10d, (await ScoreAsync("S2", Whole, ct))!.Value, 4);

            await SetFlagAsync(Whole, null, ct);
            await RunAsync(ct);
            Assert.Equal(High, (await ScoreAsync("S2", Whole, ct))!.Value, 4);

            await SetFlagAsync(Whole, false, ct);
            await RunAsync(ct);
            Assert.Equal(High, (await ScoreAsync("S2", Whole, ct))!.Value, 4);
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    // ------------------------------------------------- the fabricated sixth ---

    /// <summary>
    /// A screen id nothing in the source names. The assertion below reads the source tree
    /// to prove that rather than claiming it in prose.
    /// </summary>
    private const string Sixth = "SRLSIXTH";

    /// <summary>
    /// Two metrics on two different stores, so the fabricated screen exercises the join
    /// derivation as well as the score, and direction `low` on one of them, so it is not
    /// a special case of every metric pointing one way.
    /// </summary>
    private const string SixthMetrics =
        """[{"metric":"adx14","direction":"high","weight":1},{"metric":"fcf_yield","direction":"low","weight":3}]""";

    /// <summary>
    /// **A sixth screen is an insert, not a deployment.** That is what `CLAUDE.md` §5
    /// means by a screen being a configuration row, and it has been a claim about the
    /// design until this ran: four config rows, no rebuild, and the screen scores.
    /// </summary>
    [Fact]
    public async Task AFabricatedSixthScreenScoresWithNoCodeChange()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedSixthAsync(ct);

        try
        {
            await RunAsync(ct);

            // adx14 at 60 high, and fcf_yield at 40 adjusting to 60 at weight 3. Both at
            // the same adjusted percentile, so the mean is that percentile.
            Assert.Equal(High, (await ScoreAsync(Sixth, Whole, ct))!.Value, 4);
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// **The proof that the screen above needed no code**: its id occurs in this file and
    /// nowhere else under `src`. Asserting that it scored proves the config path works;
    /// only this proves nothing in the source knew about it.
    ///
    /// The scan reads every `.cs` and `.sql` file rather than a chosen few, and it fails
    /// if it finds too few files to be the source tree, so a wrong root cannot pass by
    /// reading nothing.
    /// </summary>
    [Fact]
    public void NothingUnderSrcNamesTheFabricatedScreen()
    {
        var root = System.IO.Path.Combine(SchemaDocument.RepositoryRoot, "src");
        var mine = System.IO.Path.GetFullPath(ThisFile());

        var scanned = 0;
        var naming = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
            if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                && !file.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // bin and obj hold copies of committed source and of the built artefact, so
            // scanning them reports this very file back under another path.
            if (IsUnder(file, "bin") || IsUnder(file, "obj"))
            {
                continue;
            }

            scanned++;

            if (System.IO.Path.GetFullPath(file) == mine)
            {
                continue;
            }

            if (File.ReadAllText(file).Contains(Sixth, StringComparison.Ordinal))
            {
                naming.Add(file);
            }
        }

        Assert.True(
            scanned > 50,
            "the source scan read " + scanned.ToString(CultureInfo.InvariantCulture) +
            " files, which is too few to be the source tree. A scan over the wrong root reports a " +
            "pass by reading nothing.");

        Assert.Empty(naming);
    }

    private static bool IsUnder(string file, string folder)
    {
        var separator = System.IO.Path.DirectorySeparatorChar;

        return file.Contains(separator + folder + separator, StringComparison.Ordinal);
    }

    private static string ThisFile()
        => System.IO.Path.Combine(
            SchemaDocument.RepositoryRoot, "src", "StockResearcherLab.Tests", "Select", "ScreenCatalogueTests.cs");

    // ------------------------------------------------------------- plumbing ---

    private static string? Mapped(string screenId, string token)
    {
        var divergence = Divergences.SingleOrDefault(
            d => d.ScreenId == screenId && d.DocumentToken == token);

        return divergence is null ? token : divergence.ConfigMetric;
    }

    /// <summary>
    /// Which store a metric is percentiled on, from <see cref="PercentileEngine.Sources"/>
    /// rather than from a list here, for the reason `ScreenEngine` derives its own.
    /// </summary>
    private static string TableOf(string metric)
        => PercentileEngine.Sources.Single(s => s.Metrics.Contains(metric)).Table;

    private static async Task RunAsync(CancellationToken ct)
    {
        var stage = new ScreenEngine();

        var context = new StageContext(
            RunDate, 1,
            new StageData(TestDatabase.ConnectionString, new DeclaredAccess(stage)),
            new FrozenClock(RunDate),
            new ConfigStore(TestDatabase.ConnectionString));

        await stage.ExecuteAsync(context, ct).ConfigureAwait(true);
    }

    /// <summary>
    /// One name, a member on the date, carrying every ranked input of the named screen at
    /// the percentile that adjusts to <see cref="High"/>.
    /// </summary>
    private static async Task<ScreenDefinition> SeedForAsync(string screenId, CancellationToken ct)
    {
        await ClearAsync(ct);
        await new ConfigSeeder(TestDatabase.ConnectionString).SeedAsync(ct).ConfigureAwait(true);

        var definition = await ScreenRegistry
            .LoadOneAsync(new ConfigStore(TestDatabase.ConnectionString), screenId, RunDate, ct).ConfigureAwait(true);

        await SeedMemberAsync(ct);
        await SeedPercentilesAsync(definition.RankedMetrics, ct);

        return definition;
    }

    private static async Task SeedSixthAsync(CancellationToken ct)
    {
        await ClearAsync(ct);
        await new ConfigSeeder(TestDatabase.ConnectionString).SeedAsync(ct).ConfigureAwait(true);

        await using (var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true))
        {
            await ExecAsync(conn, """
                INSERT INTO config_rows (key, version, value, set_at, set_by) VALUES
                  (@m, 1, @mv::jsonb, TIMESTAMPTZ '2020-01-01 12:00:00Z', 'test'),
                  (@i, 1, '2'::jsonb, TIMESTAMPTZ '2020-01-01 12:00:00Z', 'test'),
                  (@s, 1, '"live"'::jsonb, TIMESTAMPTZ '2020-01-01 12:00:00Z', 'test'),
                  (@l, 1, '8'::jsonb, TIMESTAMPTZ '2020-01-01 12:00:00Z', 'test')
                ON CONFLICT (key, version) DO UPDATE SET value = EXCLUDED.value;
                """, ct,
                ("m", $"screens.{Sixth}.metrics"), ("mv", SixthMetrics),
                ("i", $"screens.{Sixth}.min_inputs"),
                ("s", $"screens.{Sixth}.state"),
                ("l", $"screens.{Sixth}.slots"));
        }

        var definition = await ScreenRegistry
            .LoadOneAsync(new ConfigStore(TestDatabase.ConnectionString), Sixth, RunDate, ct).ConfigureAwait(true);

        await SeedMemberAsync(ct);
        await SeedPercentilesAsync(definition.RankedMetrics, ct);
    }

    private static async Task SeedMemberAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);

        await ExecAsync(conn, """
            INSERT INTO security_daily (ticker, date, sector, size_bucket, market_cap, is_active)
            VALUES (@t, @d, @sec, @b, 1000000000, TRUE)
            ON CONFLICT (ticker, date) DO UPDATE SET is_active = TRUE;
            """, ct, ("t", Whole), ("d", RunDate), ("sec", Sector), ("b", Bucket));
    }

    /// <summary>
    /// Each ranked metric written to whichever store percentiles it, at the value its own
    /// direction turns into <see cref="High"/>. One statement per store, the assignments
    /// built from the metric list, so a screen gaining an input needs no change here.
    /// </summary>
    private static async Task SeedPercentilesAsync(IReadOnlyList<ScreenMetric> metrics, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);

        foreach (var group in metrics.GroupBy(m => TableOf(m.Metric)).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var ordered = group
                .OrderBy(m => m.Metric, StringComparer.Ordinal)
                .ToList();

            var columns = ordered.Select(m => m.Metric + PercentileEngine.Suffix).ToList();

            var values = ordered.Select(m => Num(m.Direction == MetricDirection.High ? High : Low));

            var assignments = columns.Select(c => c + " = EXCLUDED." + c);

            await ExecAsync(conn,
                "INSERT INTO " + group.Key + " (ticker, date, " + string.Join(", ", columns) + ")\n" +
                "VALUES (@t, @d, " + string.Join(", ", values) + ")\n" +
                "ON CONFLICT (ticker, date) DO UPDATE SET " + string.Join(", ", assignments) + ";",
                ct, ("t", Whole), ("d", RunDate));
        }
    }

    private static async Task SetFlagAsync(string ticker, bool? value, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);

        await ExecAsync(conn,
            "UPDATE indicator_daily SET base_breakout_flag = @v WHERE ticker = @t AND date = @d;",
            ct, ("v", (object?) value ?? DBNull.Value), ("t", ticker), ("d", RunDate));
    }

    /// <summary>
    /// By date rather than by screen, for the reason `ScreenEngineTests` records: a run
    /// here scores every registered screen, so a screen-scoped delete leaves the others
    /// behind and breaks an assertion in a different file.
    /// </summary>
    private static async Task ClearAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);

        await ExecAsync(conn, "DELETE FROM screen_score_daily WHERE date = @d;", ct, ("d", RunDate));
        await ExecAsync(conn, "DELETE FROM screen_history WHERE date = @d;", ct, ("d", RunDate));

        foreach (var source in PercentileEngine.Sources)
        {
            await ExecAsync(conn, "DELETE FROM " + source.Table + " WHERE ticker = @t;", ct, ("t", Whole));
        }

        await ExecAsync(conn, "DELETE FROM security_daily WHERE ticker = @t;", ct, ("t", Whole));
        await ExecAsync(conn, "DELETE FROM config_rows WHERE key LIKE @k;", ct, ("k", "screens." + Sixth + ".%"));
    }

    private static string Num(double value)
        => value.ToString("0.############################", CultureInfo.InvariantCulture);

    private static async Task ExecAsync(
        NpgsqlConnection conn, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);

        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(true);
    }

    private static async Task<double?> ScoreAsync(string screenId, string ticker, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(
            "SELECT score FROM screen_score_daily WHERE screen_id = @s AND ticker = @t AND date = @d;", conn);

        cmd.Parameters.AddWithValue("s", screenId);
        cmd.Parameters.AddWithValue("t", ticker);
        cmd.Parameters.AddWithValue("d", RunDate);

        var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true);

        return value is null or DBNull ? null : Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }

    /// <summary>Per-file, as every other stage fixture in this suite keeps its own.</summary>
    private sealed class FrozenClock(DateOnly today) : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(today.Year, today.Month, today.Day, 21, 30, 0, TimeSpan.Zero);

        public DateOnly Today { get; } = today;
    }
}
