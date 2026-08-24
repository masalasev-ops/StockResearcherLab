using Npgsql;
using StockResearcherLab.Core;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;
using StockResearcherLab.Pipeline;
using StockResearcherLab.Pipeline.Select;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Select;

/// <summary>
/// Checkpoint 4.12. **Zero candidates on the warm-up night is correct, and it must not
/// read as the data fault §18 halts on.**
///
/// Those two look identical downstream, both being an empty candidate set, and §18 gives
/// them opposite behaviour: "a screen returns zero names, expected, not an error" sits two
/// rows above "every live screen returns zero, halt before the model is called, alert,
/// since this indicates a data fault rather than a quiet market".
///
/// **What separates them is whether a floor exists.** Below
/// `screens.floor_lookback_days` observations a screen has no floor and ranks nothing
/// [D-115], so the first 250 sessions of the window carry scores and no candidates. That
/// is the floor working. Floors that exist and rank nothing anywhere is the fault.
/// </summary>
[Collection("database")]
public sealed class WarmUpNightTests
{
    /// <summary>
    /// Late in 2021 rather than early, because the trailing fixture writes 250 calendar
    /// days behind this date and `screen_score_daily` is range-partitioned by year with
    /// no default partition [0017]. From September the window reaches into 2020 and the
    /// insert has nowhere to land.
    /// </summary>
    private static readonly DateOnly RunDate = new(2021, 12, 1);

    private const string Screen = "SRLWARM";
    private const string Sector = "srltest-warm";
    private const string Name = "SRLW.A";

    private const string OneMetric =
        """[{"metric":"adx14","direction":"high","weight":1}]""";

    // ---------------------------------------------------- the two zero cases ---

    /// <summary>
    /// **The warm-up. No screen has a floor, nothing is ranked, no candidate is written,
    /// and the run does not halt.**
    ///
    /// This is what the real night at 4.12 produced against the populated store: five
    /// screens each reporting "no floor at 1 of 250 days", 14,095 scores and zero
    /// candidates.
    /// </summary>
    [Fact]
    public async Task NoFloorMeansNoCandidateAndTheStageSaysItsZeroWasExpected()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            var scored = await RunAsync(new ScreenEngine(), ct);
            Assert.Contains("no floor", scored.Detail!, StringComparison.Ordinal);

            var allocated = await RunAsync(new CandidateAllocator(), ct);

            Assert.Equal(0, allocated.RowsWritten);
            Assert.True(allocated.ZeroRowsExpected);
            Assert.Contains("warm-up", allocated.Detail!, StringComparison.Ordinal);
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// **Every live screen with a floor ranking nothing halts the run**, which is §18's
    /// own behaviour for that row. A floor is the 98th percentile of a screen's own
    /// trailing distribution, so roughly two percent of the scored population clears it in
    /// the ordinary course; nothing clearing it anywhere is a data fault rather than a
    /// quiet market [D-84].
    ///
    /// The fixture gives the screen a full lookback of history at a level no name reaches
    /// tonight, so a floor exists and nothing is above it.
    /// </summary>
    [Fact]
    public async Task EveryLiveScreenWithAFloorRankingNothingHaltsTheRun()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await FillTrailingAsync(score: 99, ct);
            await SetPercentileAsync(Name, 1, ct);

            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => RunAsync(new ScreenEngine(), ct));

            Assert.Contains("data fault", thrown.Message, StringComparison.Ordinal);
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// **And a screen still below its lookback is not counted**, so a night where one
    /// screen has a floor and ranks something runs even though the others do not.
    /// </summary>
    [Fact]
    public async Task AScreenWithAFloorRankingSomethingDoesNotHalt()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await FillTrailingAsync(score: 10, ct);
            await SetPercentileAsync(Name, 99, ct);

            var result = await RunAsync(new ScreenEngine(), ct);

            Assert.Equal("ok", result.Status);
            Assert.Contains("ranked", result.Detail!, StringComparison.Ordinal);
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// **A night that writes no candidate against floors that exist is not the warm-up**
    /// and does not claim to be. Here the screen has a floor and ranks a name, and the
    /// name is gated, so the candidate set is empty for a reason the warm-up flag must not
    /// cover.
    /// </summary>
    [Fact]
    public async Task ZeroCandidatesAgainstAFloorThatExistsIsNotTheWarmUp()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await FillTrailingAsync(score: 10, ct);
            await SetPercentileAsync(Name, 99, ct);
            await RunAsync(new ScreenEngine(), ct);

            await ExecAsync(
                "UPDATE gate_result SET passed = FALSE, reasons = ARRAY['halt'] WHERE date = @d;",
                ct, ("d", RunDate));

            var allocated = await RunAsync(new CandidateAllocator(), ct);

            Assert.Equal(0, allocated.RowsWritten);
            Assert.False(allocated.ZeroRowsExpected);
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    // ------------------------------------------------------- the halt itself ---

    /// <summary>
    /// The runner halts on a writing stage that wrote nothing, and does not when the
    /// stage said its own zero was expected. Both against the same double, so the only
    /// difference is the flag.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task TheZeroRowHaltHonoursTheStagesOwnAnswer(bool expected, bool completes)
    {
        var ct = TestContext.Current.CancellationToken;
        var name = "SRLWarm-" + (expected ? "expected" : "silent");

        var registry = new StageRegistry(
            [new QuietWriter(name, expected), new RunLog(TestDatabase.ConnectionString)]);

        var night = new NightlyRun(
            registry,
            new StageRunner(registry, new RunLog(TestDatabase.ConnectionString),
                new FrozenClock(RunDate), TestDatabase.ConnectionString));

        var result = await night.ExecuteAsync(RunDate, 1, [name], ct).ConfigureAwait(true);

        Assert.Equal(completes, result.Completed);
    }

    /// <summary>
    /// A stage that declares a write, writes nothing, and says so. The table it names is
    /// real so the guard admits it; nothing is written either way.
    /// </summary>
    private sealed class QuietWriter(string name, bool expected) : IStage
    {
        public string Name { get; } = name;

        public IReadOnlyList<string> ReadSet { get; } = [];

        public IReadOnlyList<TableWrite> WriteSet { get; } =
            [new("alert", WriteOperation.Insert, ["date"])];

        public Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
            => Task.FromResult(new StageResult(0, "ok", "nothing tonight", ZeroRowsExpected: expected));
    }

    // ------------------------------------------------------------- plumbing ---

    private static async Task<StageResult> RunAsync(IStage stage, CancellationToken ct)
    {
        var context = new StageContext(
            RunDate, 1,
            new StageData(TestDatabase.ConnectionString, new DeclaredAccess(stage)),
            new FrozenClock(RunDate),
            new ConfigStore(TestDatabase.ConnectionString));

        return await stage.ExecuteAsync(context, ct).ConfigureAwait(true);
    }

    private static async Task SeedAsync(CancellationToken ct)
    {
        await ClearAsync(ct);
        await new ConfigSeeder(TestDatabase.ConnectionString).SeedAsync(ct).ConfigureAwait(true);

        await ExecAsync("""
            INSERT INTO config_rows (key, version, value, set_at, set_by) VALUES
              (@m, 1, @mv::jsonb, TIMESTAMPTZ '2020-01-01 12:00:00Z', 'test'),
              (@i, 1, '1'::jsonb, TIMESTAMPTZ '2020-01-01 12:00:00Z', 'test'),
              (@s, 1, '"live"'::jsonb, TIMESTAMPTZ '2020-01-01 12:00:00Z', 'test'),
              (@l, 1, '8'::jsonb, TIMESTAMPTZ '2020-01-01 12:00:00Z', 'test')
            ON CONFLICT (key, version) DO UPDATE SET value = EXCLUDED.value;
            """, ct,
            ("m", $"screens.{Screen}.metrics"), ("mv", OneMetric),
            ("i", $"screens.{Screen}.min_inputs"),
            ("s", $"screens.{Screen}.state"),
            ("l", $"screens.{Screen}.slots"));

        // Every seeded screen, read off the seeder. This was a literal S1 to S5 until
        // Q.7 registered three shadows, at which point this fixture scored eight screens
        // where it asserts over one [SeededScreens].
        foreach (var id in SeededScreens.Ids())
        {
            await ExecAsync("""
                INSERT INTO config_rows (key, version, value, set_at, set_by)
                VALUES (@k, 2, '"retired"'::jsonb, TIMESTAMPTZ '2020-06-01 12:00:00Z', 'test')
                ON CONFLICT (key, version) DO UPDATE SET value = EXCLUDED.value;
                """, ct, ("k", $"screens.{id}.state"));
        }

        await ExecAsync("""
            INSERT INTO security_daily (ticker, date, sector, size_bucket, market_cap, is_active)
            VALUES (@t, @d, @sec, 'mid', 1000000000, TRUE)
            ON CONFLICT (ticker, date) DO UPDATE SET is_active = TRUE;
            """, ct, ("t", Name), ("d", RunDate), ("sec", Sector));

        await ExecAsync("""
            INSERT INTO gate_result (ticker, date, passed, reasons, gate_state)
            VALUES (@t, @d, TRUE, '{}', 'passed_partial')
            ON CONFLICT (ticker, date) DO UPDATE SET passed = TRUE, reasons = '{}';
            """, ct, ("t", Name), ("d", RunDate));

        await SetPercentileAsync(Name, 50, ct);
    }

    /// <summary>
    /// A full lookback of trailing scores at one level, so a floor exists tonight and its
    /// height is that level. Written straight into `screen_score_daily` rather than run
    /// through C13, the trailing distribution being an input here rather than the subject.
    /// </summary>
    private static async Task FillTrailingAsync(double score, CancellationToken ct)
    {
        var lookback = 250;

        for (var day = 1; day <= lookback; day++)
        {
            await ExecAsync("""
                INSERT INTO screen_score_daily (date, screen_id, ticker, score, rank_within_screen,
                    config_version)
                VALUES (@d, @s, @t, @sc, NULL, 1)
                ON CONFLICT (date, screen_id, ticker) DO UPDATE SET score = EXCLUDED.score;
                """, ct, ("d", RunDate.AddDays(-day)), ("s", Screen), ("t", Name), ("sc", (float) score));
        }
    }

    private static async Task SetPercentileAsync(string ticker, double percentile, CancellationToken ct)
        => await ExecAsync("""
            INSERT INTO indicator_daily (ticker, date, adx14_pctile) VALUES (@t, @d, @p)
            ON CONFLICT (ticker, date) DO UPDATE SET adx14_pctile = EXCLUDED.adx14_pctile;
            """, ct, ("t", ticker), ("d", RunDate), ("p", (float) percentile));

    private static async Task ClearAsync(CancellationToken ct)
    {
        await ExecAsync(
            "DELETE FROM screen_score_daily WHERE date BETWEEN @from AND @d;",
            ct, ("from", RunDate.AddDays(-400)), ("d", RunDate));

        await ExecAsync(
            "DELETE FROM screen_history WHERE date BETWEEN @from AND @d;",
            ct, ("from", RunDate.AddDays(-400)), ("d", RunDate));

        await ExecAsync("DELETE FROM candidate_set WHERE date = @d;", ct, ("d", RunDate));
        await ExecAsync("DELETE FROM attribution WHERE date = @d;", ct, ("d", RunDate));
        await ExecAsync("DELETE FROM alert WHERE date = @d;", ct, ("d", RunDate));
        await ExecAsync("DELETE FROM gate_result WHERE date = @d;", ct, ("d", RunDate));
        await ExecAsync("DELETE FROM indicator_daily WHERE ticker LIKE 'SRLW.%';", ct);
        await ExecAsync("DELETE FROM security_daily WHERE ticker LIKE 'SRLW.%';", ct);
        await ExecAsync("DELETE FROM config_rows WHERE key LIKE @k;", ct, ("k", $"screens.{Screen}.%"));
        await ExecAsync("DELETE FROM config_rows WHERE key LIKE '" + SeededScreens.AnyStateVersionTwo +
            "' AND version = 2;", ct);
    }

    private static async Task ExecAsync(
        string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(sql, conn);

        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(true);
    }

    /// <summary>Per-file, as every other stage fixture in this suite keeps its own.</summary>
    private sealed class FrozenClock(DateOnly today) : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(today.Year, today.Month, today.Day, 21, 30, 0, TimeSpan.Zero);

        public DateOnly Today { get; } = today;
    }

}
