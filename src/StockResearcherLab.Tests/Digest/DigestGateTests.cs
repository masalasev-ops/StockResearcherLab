using System.Globalization;
using Npgsql;
using StockResearcherLab.Core;
using StockResearcherLab.Core.Digest;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;
using StockResearcherLab.Pipeline;
using StockResearcherLab.Pipeline.Select;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Digest;

/// <summary>
/// Checkpoint 5.11. **The gate: no healthy link halts the run at C33** [INVARIANT 15,
/// D-139].
///
/// **The observable is narrower than the invariant states it, and that is D-139's whole
/// point.** INVARIANT 15 and §18 both say the halt in terms of the researcher and of
/// orders, neither of which exists in phase 5. What can be observed now is that C33 fails,
/// `NightlyRun` returns not completed, no stage after C33 executes, and no `news_digest`
/// row is written. The line as `BUILD_PLAN.md` states it is re-asked at phase 7.
///
/// **The halt has been seen once outside a test**, on 2026-08-25, when a digest run started
/// against a local model that had gone non-resident: the probe exceeded
/// `digest.health_timeout_ms`, the second link was unavailable, and the run stopped with the
/// night's existing rows untouched. These tests are that night made repeatable.
/// </summary>
[Collection("database")]
public sealed class DigestGateTests : IAsyncLifetime
{
    private static readonly DateOnly Date = new(2026, 8, 12);

    private const string Marker = "ZGATE";

    private static string Candidate => Marker + "ONE.US";

    /// <summary>
    /// The chain's two rows, seeded here rather than relied on, which is
    /// `NewsDigesterTests`' rule and the same reason: `TestDatabase` seeds the config keys
    /// and not `local_model_config` [D-136], so on a fresh database this class fails
    /// wherever it happens to run first.
    /// </summary>
    public async ValueTask InitializeAsync()
        => await new ConfigSeeder(TestDatabase.ConnectionString)
            .SeedChainAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    public async ValueTask DisposeAsync()
        => await ClearAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    // ------------------------------------------------------- the halt itself ---

    /// <summary>
    /// **Both links unhealthy: C33 fails and writes nothing.**
    ///
    /// The failure names the invariant, because a halt that reads as an ordinary error is
    /// one an operator restarts past. What is asserted beside it is the table: a night that
    /// halts must not leave a partial digest set, which downstream cannot tell from a real
    /// one [`CLAUDE.md` §6].
    /// </summary>
    [Fact]
    public async Task NoHealthyLinkFailsTheDigestStepAndWritesNoRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClearAsync(ct);
        await SeedNightAsync(ct);

        var stage = Stage(Dead(DigestProvider.Local), Dead(DigestProvider.Haiku));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => stage.ExecuteAsync(Context(stage), ct)).ConfigureAwait(true);

        Assert.Contains("INVARIANT 15", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("D-139", thrown.Message, StringComparison.Ordinal);

        // Both links are named with their reason, which is what separates a machine that is
        // down from a model that answers with nothing [D-144].
        Assert.Contains("local", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("haiku", thrown.Message, StringComparison.Ordinal);

        Assert.Equal(0, await DigestCountAsync(ct).ConfigureAwait(true));
    }

    /// <summary>
    /// **A halted night leaves the rows an earlier run wrote exactly as they were.**
    ///
    /// The delete and the insert both sit after the probe, so the halt happens before the
    /// table is touched. This is the case that was seen live: a re-run started against a
    /// cold model, stopped at the gate, and the 32 rows already there were still there.
    /// Asserted because the natural shape of a re-run is delete-then-write, and a delete
    /// that ran before the probe would empty a night on the way to failing.
    /// </summary>
    [Fact]
    public async Task AHaltedRunLeavesAnEarlierRunsRowsUntouched()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClearAsync(ct);
        await SeedNightAsync(ct);

        var healthy = Stage(Answering(DigestProvider.Local), Dead(DigestProvider.Haiku));
        await healthy.ExecuteAsync(Context(healthy), ct).ConfigureAwait(true);

        Assert.Equal(1, await DigestCountAsync(ct).ConfigureAwait(true));
        var before = await DigestTextAsync(ct).ConfigureAwait(true);

        var halted = Stage(Dead(DigestProvider.Local), Dead(DigestProvider.Haiku));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => halted.ExecuteAsync(Context(halted), ct)).ConfigureAwait(true);

        Assert.Equal(1, await DigestCountAsync(ct).ConfigureAwait(true));
        Assert.Equal(before, await DigestTextAsync(ct).ConfigureAwait(true));
    }

    /// <summary>
    /// **No stage after C33 executes, and the run says it did not complete.**
    ///
    /// Run through `NightlyRun` rather than through the stage, because "nothing after it
    /// ran" is a property of the sequence and not of the component. The order is passed
    /// explicitly: C29 and C33 are not in `EveningOrder` until 5.12, and this assertion is
    /// about what the halt does to whatever follows it rather than about where they sit.
    /// </summary>
    [Fact]
    public async Task AHaltedNightRunsNoStageAfterTheDigestStep()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClearAsync(ct);
        await SeedNightAsync(ct);

        var stage = Stage(Dead(DigestProvider.Local), Dead(DigestProvider.Haiku));
        var after = new Sentinel("GateSentinel");

        var registry = new StageRegistry([stage, after, new RunLog(TestDatabase.ConnectionString)]);
        var clock = new FixedClock(new DateTimeOffset(2026, 8, 12, 22, 33, 0, TimeSpan.Zero), Date);
        var runner = new StageRunner(
            registry, new RunLog(TestDatabase.ConnectionString), clock, TestDatabase.ConnectionString);

        var result = await new NightlyRun(registry, runner)
            .ExecuteAsync(Date, 1, ["NewsDigester", after.Name], ct).ConfigureAwait(true);

        Assert.False(result.Completed);
        Assert.Equal(0, after.Calls);

        var step = Assert.Single(result.Steps);
        Assert.Equal("NewsDigester", step.Stage);
        Assert.Equal("failed", step.Outcome);
        Assert.Equal(0, await DigestCountAsync(ct).ConfigureAwait(true));
    }

    // --------------------------------------------- D-139's ordering consequence ---

    /// <summary>
    /// **C28 runs before the digest stages, so a halted night still raises its concentration
    /// alerts** [D-139].
    ///
    /// §04 puts C28 at 19:00, after C33 at 18:33, and that 19:00 is a clock time rather than
    /// a dependency: it reads `candidate_set`, `position` and `security_daily` and nothing
    /// the digest step writes. On a night that halts the candidate set exists and is exactly
    /// as concentrated as it is, and losing its alert on the nights something else is already
    /// wrong is the wrong direction.
    ///
    /// **The rule is asserted against the real order and proved against a counter-example**,
    /// because until 5.12 puts C29 and C33 into `EveningOrder` the first assertion alone
    /// holds by their absence and would pass over a check that tested nothing.
    /// </summary>
    [Fact]
    public void ConcentrationRunsBeforeTheDigestStagesWhereverTheyAppear()
    {
        Assert.True(Precedes(NightlyRun.EveningOrder), "C28 must precede C29 and C33 [D-139].");

        Assert.True(Precedes(["ConcentrationMonitor", "HeadlineIngestor", "NewsDigester"]));
        Assert.False(Precedes(["HeadlineIngestor", "NewsDigester", "ConcentrationMonitor"]));
        Assert.False(Precedes(["NewsDigester", "ConcentrationMonitor"]));
    }

    /// <summary>
    /// Whether C28 precedes both digest stages in an order, taking their absence as
    /// satisfied: an order that does not run them cannot run them too early.
    /// </summary>
    private static bool Precedes(IReadOnlyList<string> order)
    {
        var monitor = order.ToList().IndexOf("ConcentrationMonitor");

        foreach (var name in (string[])["HeadlineIngestor", "NewsDigester"])
        {
            var at = order.ToList().IndexOf(name);

            if (at >= 0 && (monitor < 0 || monitor > at))
            {
                return false;
            }
        }

        return true;
    }

    // ------------------------------------------------------------- plumbing ---

    private static IDigestLink Dead(DigestProvider provider) => new StubLink(provider, healthy: false);

    private static IDigestLink Answering(DigestProvider provider) => new StubLink(provider, healthy: true);

    private static NewsDigester Stage(IDigestLink local, IDigestLink secondary)
        => new(
            (context, ct) => DigestChain.BuildAsync(
                new StageData(TestDatabase.ConnectionString, LocalModelClient.Access()),
                context.Config,
                context.Date,
                new Dictionary<DigestProvider, IDigestLink>
                {
                    [local.Provider] = local,
                    [secondary.Provider] = secondary,
                },
                ct),
            () => new DigestInstruction.Instruction("Summarise.", "hash"));

    private static StageContext Context(NewsDigester stage)
        => new(
            Date, 1,
            new StageData(TestDatabase.ConnectionString, new DeclaredAccess(stage)),
            new FixedClock(new DateTimeOffset(2026, 8, 12, 22, 33, 0, TimeSpan.Zero), Date),
            new ConfigStore(TestDatabase.ConnectionString));

    /// <summary>
    /// A link that is healthy or is not, and answers the same way either way. Health and
    /// answering are separate here because they are separate in the chain: `ReadyAsync`
    /// stops at the first healthy link, so a link that fails only its probe is still asked
    /// nothing rather than asked and refused.
    /// </summary>
    private sealed class StubLink(DigestProvider provider, bool healthy) : IDigestLink
    {
        public DigestProvider Provider { get; } = provider;

        public Task<LinkHealth> HealthAsync(CancellationToken ct = default)
            => Task.FromResult(new LinkHealth(
                Provider, healthy, healthy ? "stub-model" : null, 1,
                healthy ? "stub answered" : "stub answered with no content"));

        public Task<DigestAnswer> DigestAsync(DigestRequest request, CancellationToken ct = default)
            => Task.FromResult(new DigestAnswer("A digest.", "stub-model", 3_000, 40));
    }

    /// <summary>
    /// A stage that records whether it ran. It declares a write on a real table so the
    /// guard admits it, and writes nothing: what is asserted is that it is never reached.
    /// </summary>
    private sealed class Sentinel(string name) : IStage
    {
        public string Name { get; } = name;

        public int Calls { get; private set; }

        public IReadOnlyList<string> ReadSet { get; } = [];

        public IReadOnlyList<TableWrite> WriteSet { get; } =
            [new("alert", WriteOperation.Insert, ["date"])];

        public Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new StageResult(0, "ok", "reached", ZeroRowsExpected: true));
        }
    }

    private static async Task SeedNightAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);

        await using (var candidate = new NpgsqlCommand(
            "INSERT INTO candidate_set (ticker, date, screens_surfacing, size_bucket, slot_filled) " +
            "VALUES (@t, @d, ARRAY['S1'], 'small', TRUE) ON CONFLICT DO NOTHING;", conn))
        {
            candidate.Parameters.AddWithValue("t", Candidate);
            candidate.Parameters.AddWithValue("d", Date);
            await candidate.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using var article = new NpgsqlCommand(
            "INSERT INTO headline (ticker, date, published_at, title, source, url, content) " +
            "VALUES (@t, @d, @p, 'A title', NULL, 'http://example.invalid/gate', 'Something happened.');", conn);

        article.Parameters.AddWithValue("t", Candidate);
        article.Parameters.AddWithValue("d", Date);
        article.Parameters.AddWithValue("p", new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero));
        await article.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<long> DigestCountAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM news_digest WHERE ticker LIKE @m;", conn);
        cmd.Parameters.AddWithValue("m", Marker + "%");

        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task<string?> DigestTextAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "SELECT digest_text FROM news_digest WHERE ticker LIKE @m;", conn);
        cmd.Parameters.AddWithValue("m", Marker + "%");

        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    private static async Task ClearAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM news_digest WHERE ticker LIKE @m; " +
            "DELETE FROM headline WHERE ticker LIKE @m; " +
            "DELETE FROM candidate_set WHERE ticker LIKE @m;", conn);
        cmd.Parameters.AddWithValue("m", Marker + "%");
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
