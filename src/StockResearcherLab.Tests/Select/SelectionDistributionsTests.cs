using System.Text.RegularExpressions;
using StockResearcherLab.Core.Screens;
using StockResearcherLab.Pipeline.Select;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Select;

/// <summary>
/// The six done-when lines are six, and each is a clause `BUILD_PLAN.md` actually states
/// [4.14, phase 4 sign-off].
///
/// **A definition of done that can quietly lose a line is not one.** The measure runs
/// against a populated store this suite does not have, so what is asserted here is its
/// shape: that six lines come back, that each names a clause of the plan's own done-when
/// sentence, and that the size target is D-89's proportion rather than a number typed
/// twice.
///
/// **The lines are held against `BUILD_PLAN.md` and not against a list in this file, and
/// that is the correction this class exists in its present form for.** It carried a copy
/// of the six until the phase 4 sign-off review. Q.9 amended the plan's overlap clause and
/// the measure and this copy both kept the retired wording and the retired 10-to-20 bound,
/// so the measure went on scoring a line the plan no longer stated and nothing failed,
/// because the only thing it was checked against was the copy. Reading the plan is what
/// makes the drift impossible rather than unlikely.
///
/// The figures themselves are in `PROGRESS.md` and are reproduced by re-running
/// `distributions` over the same range, which is what makes them traceable rather than
/// asserted [D-67].
/// </summary>
[Collection("database")]
public sealed class SelectionDistributionsTests
{
    /// <summary>
    /// The five live screens, which is what a range over the frozen record resolves. The
    /// measure takes the live set as an argument rather than inferring it from the id, so
    /// these fixtures pass it too.
    /// </summary>
    private static readonly string[] Live = ["S1", "S2", "S3", "S4", "S5"];

    /// <summary>`monitor.megacap_share_max`'s seeded value, passed in as the run passes it.</summary>
    private const double MegacapShareMax = 0.333d;

    [Fact]
    public async Task TheMeasureYieldsSixLinesAndThePlanStatesSixClauses()
    {
        var ct = TestContext.Current.CancellationToken;

        // An empty range in the test database. Every query returns a row of nulls or
        // zeroes, which is enough to assert the shape and is deliberately not enough to
        // assert a figure: the figures belong to the populated store.
        var measured = await SelectionDistributions.MeasureAsync(
            TestDatabase.ConnectionString,
            new DateOnly(2021, 2, 1), new DateOnly(2021, 2, 5), Live, MegacapShareMax, ct)
            .ConfigureAwait(true);

        var clauses = DoneWhenClauses();

        // **The count is asserted on both sides and against each other**, because the
        // likely repair when the text disagrees is deleting from whichever failed, and a
        // measure and a plan that had each quietly lost the same line would still match.
        Assert.Equal(6, measured.Count);
        Assert.Equal(6, clauses.Count);
        Assert.Equal(clauses.Count, measured.Count);

        // Every line reports something, so a query that returned nothing cannot pass as a
        // line that held.
        Assert.All(measured, m => Assert.False(string.IsNullOrWhiteSpace(m.Measured)));
    }

    /// <summary>
    /// **Each line, in order, is a clause of the plan's done-when sentence.** Containment
    /// rather than equality, so the plan may say more about a clause than the label needs
    /// to carry; what it cannot do is say something the label does not.
    /// </summary>
    [Fact]
    public async Task EveryLineNamesTheClauseThePlanStatesInThatPosition()
    {
        var ct = TestContext.Current.CancellationToken;

        var measured = await SelectionDistributions.MeasureAsync(
            TestDatabase.ConnectionString,
            new DateOnly(2021, 2, 1), new DateOnly(2021, 2, 5), Live, MegacapShareMax, ct)
            .ConfigureAwait(true);

        var clauses = DoneWhenClauses();

        for (var i = 0; i < measured.Count; i++)
        {
            Assert.True(
                clauses[i].Contains(measured[i].Line, StringComparison.Ordinal),
                $"Done-when line {i + 1} reads\n  {measured[i].Line}\nand BUILD_PLAN.md's clause " +
                $"in that position reads\n  {clauses[i]}\nThe measure and the plan have drifted. " +
                "Amend the measure to the plan; the plan is what was asked for.");
        }
    }

    /// <summary>
    /// **The overlap line carries no verdict** [Q.9, phase 4 sign-off]. Its clause
    /// describes what near-independence looks like rather than stating a bound the result
    /// has to clear, and setting one now would be a bar fitted to the measurement that
    /// produced it [`CLAUDE.md` §11].
    /// </summary>
    [Fact]
    public async Task TheOverlapLineReportsBothReadingsAndNeitherPassesNorFails()
    {
        var ct = TestContext.Current.CancellationToken;

        var measured = await SelectionDistributions.MeasureAsync(
            TestDatabase.ConnectionString,
            new DateOnly(2021, 2, 1), new DateOnly(2021, 2, 5), Live, MegacapShareMax, ct)
            .ConfigureAwait(true);

        var overlap = Assert.Single(measured, m => m.Line.StartsWith("overlap", StringComparison.Ordinal));

        Assert.Null(overlap.Holds);
        Assert.Contains("over allocated candidates", overlap.Measured, StringComparison.Ordinal);
        Assert.Contains("over ranked sets", overlap.Measured, StringComparison.Ordinal);
    }

    /// <summary>
    /// **The megacap line scores against `monitor.megacap_share_max` and not a literal**
    /// [phase 4 sign-off]. C28 alerts on that key and this line scores the same guarantee,
    /// so a literal here would be one bound stated twice [`CLAUDE.md` §8].
    /// </summary>
    [Fact]
    public async Task TheMegacapLineScoresAgainstTheConfiguredBound()
    {
        var ct = TestContext.Current.CancellationToken;

        // A bound of zero cannot be met by any share at all and a bound of one cannot be
        // missed, so the verdict flips between them. A literal third would return the same
        // verdict twice and this would fail.
        var strict = Line(await MeasureWithAsync(0d, ct));
        var loose = Line(await MeasureWithAsync(1d, ct));

        Assert.False(strict.Holds);
        Assert.True(loose.Holds);

        // And the bound in force is printed beside the figure, so a reader can see which
        // number the verdict was taken against rather than inferring it.
        Assert.Contains("monitor.megacap_share_max at 0.0 percent", strict.Measured, StringComparison.Ordinal);
        Assert.Contains("monitor.megacap_share_max at 100.0 percent", loose.Measured, StringComparison.Ordinal);

        static DoneWhenLine Line(IReadOnlyList<DoneWhenLine> lines)
            => Assert.Single(lines, m => m.Line.StartsWith("megacap", StringComparison.Ordinal));
    }

    /// <summary>
    /// **The ranked reading refuses an empty live set rather than reporting nothing.** A
    /// range with no live screen would print 0.0 percent of 0 names, which reads exactly
    /// like five screens that never agree [`CLAUDE.md` §1].
    /// </summary>
    [Fact]
    public void AnEmptyLiveSetFailsRatherThanReportingNoOverlap()
        => Assert.Throws<InvalidOperationException>(
            () => SelectionDistributions.SqlFor(
                "overlap between screens is read on both of its readings, allocated candidates and ranked sets",
                new DateOnly(2021, 1, 11), new DateOnly(2026, 8, 12), [], MegacapShareMax));

    /// <summary>
    /// **The size target is D-89's proportion at eight slots and is derived, not typed.**
    /// D-116 retired `screens.quota_large`, `quota_mid` and `quota_small` precisely
    /// because three numbers that must sum to the slot count can be set so they do not,
    /// and a target restated here would be that defect returning in a test.
    /// </summary>
    [Fact]
    public void TheSizeTargetIsTheProportionAtEightSlots()
    {
        var quota = SlotQuota.For(8);

        Assert.Equal(2, quota.Large);
        Assert.Equal(3, quota.Mid);
        Assert.Equal(3, quota.Small);

        var target = SelectionDistributions.SizeTarget.ToDictionary(
            x => x.Bucket, x => x.Target, StringComparer.Ordinal);

        Assert.Equal(quota.Large / 8d, target["large"]);
        Assert.Equal(quota.Mid / 8d, target["mid"]);
        Assert.Equal(quota.Small / 8d, target["small"]);

        Assert.Equal(1d, target.Values.Sum(), 10);
    }

    /// <summary>
    /// **The drawdown segment reads `regime_label` and not a date window.** A window
    /// chosen by the build session is a window that can be chosen to suit the answer, and
    /// `market_context_daily.regime_label` is authored and closed to three values by
    /// migration `0005` [D-80].
    /// </summary>
    [Fact]
    public void TheDrawdownSegmentReadsTheRegimeLabel()
    {
        var sql = SelectionDistributions.SqlFor(
            "megacap share sits under a third including inside the 2022 drawdown",
            new DateOnly(2021, 1, 11), new DateOnly(2026, 8, 12), Live, MegacapShareMax);

        Assert.Contains("regime_label = 'risk_off'", sql, StringComparison.Ordinal);
        Assert.Contains("market_context_daily", sql, StringComparison.Ordinal);

        // And no literal year, which is what an invented window would look like.
        Assert.DoesNotContain("2022-", sql, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- plumbing ---

    private static async Task<IReadOnlyList<DoneWhenLine>> MeasureWithAsync(double max, CancellationToken ct)
        => await SelectionDistributions.MeasureAsync(
            TestDatabase.ConnectionString,
            new DateOnly(2021, 2, 1), new DateOnly(2021, 2, 5), Live, max, ct).ConfigureAwait(true);

    /// <summary>
    /// `BUILD_PLAN.md`'s phase 4 done-when sentence, split into its clauses.
    ///
    /// **Whitespace-tolerant, because this document is hard-wrapped** and a clause that
    /// breaks across a line would not match a pattern anchored to one [`CLAUDE.md` §7].
    /// The sentence is bounded by the `**Done when:**` marker and the blank line that ends
    /// its paragraph, and it fails rather than returning an empty list if either is
    /// missing: a parse that matched nothing would satisfy every assertion over it.
    /// </summary>
    private static IReadOnlyList<string> DoneWhenClauses()
    {
        var path = System.IO.Path.Combine(SchemaDocument.RepositoryRoot, "docs", "BUILD_PLAN.md");
        var text = File.ReadAllText(path);

        var phase = text.IndexOf("## Phase 4 —", StringComparison.Ordinal);
        Assert.True(phase >= 0, "BUILD_PLAN.md has no '## Phase 4 —' heading.");

        var next = text.IndexOf("\n## Phase 5", phase, StringComparison.Ordinal);
        Assert.True(next > phase, "BUILD_PLAN.md has no '## Phase 5' heading after phase 4's.");

        var section = text[phase..next];

        var marker = section.IndexOf("**Done when:**", StringComparison.Ordinal);
        Assert.True(marker >= 0, "Phase 4 has no '**Done when:**' paragraph.");

        var body = section[(marker + "**Done when:**".Length)..];
        var end = body.IndexOf("\n\n", StringComparison.Ordinal);
        Assert.True(end > 0, "Phase 4's done-when paragraph does not end in a blank line.");

        var sentence = Whitespace.Replace(body[..end], " ").Trim().TrimEnd('.');

        var clauses = sentence
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        Assert.True(
            clauses.Count > 1,
            "Phase 4's done-when paragraph parsed as one clause. It is a semicolon-separated " +
            "list and this read it as prose, so the markup or the punctuation has moved: " +
            sentence);

        return clauses;
    }

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
}
