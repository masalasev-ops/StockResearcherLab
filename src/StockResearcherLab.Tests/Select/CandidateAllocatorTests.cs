using System.Globalization;
using Npgsql;
using StockResearcherLab.Core;
using StockResearcherLab.Core.Config;
using StockResearcherLab.Core.Screens;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;
using StockResearcherLab.Pipeline;
using StockResearcherLab.Pipeline.Select;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Select;

/// <summary>
/// Checkpoint 4.9. C14's live half: the proportion, the ceiling, the empty slot, the
/// gated name's slot, and dedup across screens.
///
/// **The empty slot and the gated name's slot are separate fixtures because they are
/// separate cases** [INVARIANT 3, D-8, D-117]. D-8 forbids backfilling from a larger
/// bucket, which is the case where nothing of that size cleared the floor. A name that
/// cleared the floor and is unavailable tonight is not that case, and conflating the two
/// either leaves a hole D-8 does not ask for or leaks the guarantee D-8 exists to hold.
/// The two are asserted side by side so the difference is visible rather than argued.
/// </summary>
[Collection("database")]
public sealed class CandidateAllocatorTests
{
    private static readonly DateOnly RunDate = new(2021, 6, 1);

    private const string Sector = "srltest-alloc";

    /// <summary>
    /// The screens these fixtures allocate from, fabricated so the seeded five are never
    /// live on this date and cannot contribute a candidate of their own.
    /// </summary>
    private const string ScreenA = "SRLALLOCA";

    private const string ScreenB = "SRLALLOCB";

    /// <summary>
    /// A screen ranking on one metric, so a fixture sets a rank by setting one
    /// percentile. Direction high, so a higher percentile is a better rank.
    /// </summary>
    private const string OneMetric =
        """[{"metric":"adx14","direction":"high","weight":1}]""";

    // ------------------------------------------------- the proportion itself ---

    /// <summary>
    /// **`SCREEN_LIFECYCLE.md` §8.1's nine-row table, reproduced exactly at every count
    /// from four to twelve**, and 2/3/3 at eight.
    ///
    /// The figures are the document's, transcribed, rather than recomputed from the
    /// formula the code uses. A test that recomputed them would agree with itself
    /// whatever either did.
    /// </summary>
    [Theory]
    [InlineData(4, 1, 1, 2)]
    [InlineData(5, 1, 2, 2)]
    [InlineData(6, 1, 2, 3)]
    [InlineData(7, 1, 3, 3)]
    [InlineData(8, 2, 3, 3)]
    [InlineData(9, 2, 3, 4)]
    [InlineData(10, 2, 4, 4)]
    [InlineData(11, 2, 4, 5)]
    [InlineData(12, 3, 4, 5)]
    public void TheSlotTableReproducesExactly(int slots, int large, int mid, int small)
    {
        var quota = SlotQuota.For(slots);

        Assert.Equal(new SlotQuota(large, mid, small), quota);
        Assert.Equal(slots, quota.Total);
    }

    /// <summary>
    /// **D-7's megacap bound holds at every live-screen count, not only at five screens
    /// of eight.** The large share never exceeds a quarter at any count from four to
    /// twelve, so the sum of the large seats over any allocation summing to forty is at
    /// most ten [`SCREEN_LIFECYCLE.md` §8.1].
    /// </summary>
    [Fact]
    public void TheLargeShareNeverExceedsAQuarterAtAnyCount()
    {
        for (var slots = 4; slots <= 12; slots++)
        {
            var quota = SlotQuota.For(slots);

            Assert.True(
                quota.Large * 4 <= slots,
                $"{slots} slots gives {quota} and a large share above a quarter, which breaks D-7's " +
                "megacap bound of ten of forty at that count.");
        }
    }

    /// <summary>
    /// **D-7's small-cap floor of fifteen is a floor across the whole reachable space**
    /// rather than a figure that happens to hold today. `small/slots` is at its lowest at
    /// exactly eight, so five screens of eight gives fifteen and every other feasible
    /// allocation gives more.
    /// </summary>
    [Fact]
    public void TheSmallShareIsLowestAtEight()
    {
        var atEight = SlotQuota.For(8).Small / 8d;

        for (var slots = 4; slots <= 12; slots++)
        {
            Assert.True(
                SlotQuota.For(slots).Small / (double) slots >= atEight,
                $"{slots} slots gives a smaller small-cap share than eight does, so D-7's floor of " +
                "fifteen would not be a floor across the reachable space.");
        }

        Assert.Equal(15, SlotQuota.For(8).Small * 5);
    }

    /// <summary>A bucket name outside the three fails rather than reading as no seats.</summary>
    [Fact]
    public void AnUnknownBucketFailsRatherThanReturningZeroSeats()
        => Assert.Throws<InvalidOperationException>(() => SlotQuota.For(8).Seats("nano"));

    // ------------------------------------------------------- the allocation ---

    /// <summary>
    /// **The slot count is a ceiling and never a target.** Three names clear the floor
    /// against eight slots, and the screen returns three.
    /// </summary>
    [Fact]
    public async Task AScreenWithThreeNamesClearingItsFloorReturnsThree()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await RankAsync(ScreenA, "SRLA.L1", "large", 1, ct);
            await RankAsync(ScreenA, "SRLA.M1", "mid", 2, ct);
            await RankAsync(ScreenA, "SRLA.S1", "small", 3, ct);

            await RunAsync(ct);

            Assert.Equal(["SRLA.L1", "SRLA.M1", "SRLA.S1"], await CandidatesAsync(ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// The quota bites per bucket: three larges clear the floor against two large seats,
    /// and the third is not taken.
    /// </summary>
    [Fact]
    public async Task TheQuotaCutsEachBucketAtItsOwnSeatCount()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await RankAsync(ScreenA, "SRLA.L1", "large", 1, ct);
            await RankAsync(ScreenA, "SRLA.L2", "large", 2, ct);
            await RankAsync(ScreenA, "SRLA.L3", "large", 3, ct);

            await RunAsync(ct);

            Assert.Equal(["SRLA.L1", "SRLA.L2"], await CandidatesAsync(ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// **INVARIANT 3's fixture. A screen with no small name clearing its floor sends
    /// fewer names, and no larger name is promoted into the small slot.**
    ///
    /// Six mid names against three mid seats and three empty small seats. If a mid name
    /// were promoted the count would be six; it is five, being two large and three mid,
    /// and the three small seats stay empty. The empty slot is the diversity guarantee
    /// working rather than a bug to fix.
    /// </summary>
    [Fact]
    public async Task AnUnfillableSmallSlotStaysEmptyAndNoLargerNameIsPromoted()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await RankAsync(ScreenA, "SRLA.L1", "large", 1, ct);
            await RankAsync(ScreenA, "SRLA.L2", "large", 2, ct);

            for (var i = 1; i <= 6; i++)
            {
                await RankAsync(ScreenA, $"SRLA.M{i.ToString(CultureInfo.InvariantCulture)}", "mid", 10 + i, ct);
            }

            await RunAsync(ct);

            var candidates = await CandidatesAsync(ct);

            Assert.Equal(
                ["SRLA.L1", "SRLA.L2", "SRLA.M1", "SRLA.M2", "SRLA.M3"],
                candidates);

            Assert.Empty(await CandidatesInBucketAsync("small", ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// **A gated name's slot passes to the next name of its own size, and it is a
    /// different case from the one above** [D-117].
    ///
    /// Four small names clear the floor against three small seats. The top one is gated,
    /// so the fourth takes the freed seat: three small names, not two. Beside the fixture
    /// above, that is the whole distinction. Nothing is promoted from another bucket in
    /// either case.
    /// </summary>
    [Fact]
    public async Task AGatedNamesSlotPassesToTheNextNameOfItsOwnSize()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await RankAsync(ScreenA, "SRLA.S1", "small", 1, ct);
            await RankAsync(ScreenA, "SRLA.S2", "small", 2, ct);
            await RankAsync(ScreenA, "SRLA.S3", "small", 3, ct);
            await RankAsync(ScreenA, "SRLA.S4", "small", 4, ct);

            await RunAsync(ct);
            Assert.Equal(["SRLA.S1", "SRLA.S2", "SRLA.S3"], await CandidatesAsync(ct));

            await GateAsync("SRLA.S1", ct);
            await RunAsync(ct);

            // Three small names still, and the fourth is the one that moved up. A slot
            // left empty here would be D-8 applied to a case D-8 is not about.
            Assert.Equal(["SRLA.S2", "SRLA.S3", "SRLA.S4"], await CandidatesAsync(ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// **A name surfaced by two screens is one row carrying both ids**, ordinal by id so
    /// two runs write the same array [`CLAUDE.md` §6].
    /// </summary>
    [Fact]
    public async Task ANameSurfacedByTwoScreensIsOneRowCarryingBoth()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await RankAsync(ScreenB, "SRLA.M1", "mid", 1, ct);
            await RankAsync(ScreenA, "SRLA.M1", "mid", 1, ct);
            await RankAsync(ScreenA, "SRLA.M2", "mid", 2, ct);

            await RunAsync(ct);

            Assert.Equal(["SRLA.M1", "SRLA.M2"], await CandidatesAsync(ct));
            Assert.Equal([ScreenA, ScreenB], await SurfacingAsync("SRLA.M1", ct));
            Assert.Equal([ScreenA], await SurfacingAsync("SRLA.M2", ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// A name below its screen's floor carries a null rank and is not a candidate,
    /// which is what makes §06's "floors already applied" literally true: this component
    /// has no floor knowledge and could not apply one differently if it wanted to.
    /// </summary>
    [Fact]
    public async Task ANameBelowItsScreensFloorIsNotACandidate()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await RankAsync(ScreenA, "SRLA.M1", "mid", 1, ct);
            await RankAsync(ScreenA, "SRLA.M2", "mid", rank: null, ct);

            await RunAsync(ct);

            Assert.Equal(["SRLA.M1"], await CandidatesAsync(ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// A shadow screen is scored and allocated nothing [D-84, D-85]. The shadow half of
    /// the record is 4.10's; here it simply does not reach the quota.
    /// </summary>
    [Fact]
    public async Task AShadowScreenIsAllocatedNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await RankAsync(ScreenB, "SRLA.M2", "mid", 1, ct);
            await RankAsync(ScreenA, "SRLA.M1", "mid", 1, ct);

            await SetStateAsync(ScreenB, "shadow", ct);
            await RunAsync(ct);

            Assert.Equal(["SRLA.M1"], await CandidatesAsync(ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    // ------------------------------------------------------- failing closed ---

    /// <summary>
    /// **A slot count outside the tuner's floor and cap fails the stage closed rather
    /// than clamping.** Clamping would run the night under an allocation nobody wrote
    /// while every row it produced looked ordinary.
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(13)]
    public async Task ASlotCountOutsideTheTunersRangeFailsTheStage(int slots)
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await SetSlotsAsync(ScreenA, slots, ct);

            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(ct));

            Assert.Contains(ScreenA, thrown.Message, StringComparison.Ordinal);
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// **A missing gate halts rather than writing no candidate.** The allocation joins
    /// `gate_result`, so a night where C12 did not run would drop every name and write
    /// nothing, which is indistinguishable on the page from a night where nothing cleared
    /// a floor [`CLAUDE.md` §1].
    /// </summary>
    [Fact]
    public async Task AMissingGateHaltsRatherThanWritingNoCandidate()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await RankAsync(ScreenA, "SRLA.M1", "mid", 1, ct);
            await ExecAsync("DELETE FROM gate_result WHERE date = @d;", ct, ("d", RunDate));

            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(ct));

            Assert.Contains("gate_result", thrown.Message, StringComparison.Ordinal);
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    // ------------------------------------------------------------ the stage ---

    /// <summary>
    /// C14 is the sole declared writer of `candidate_set` and declares an Insert alone.
    /// The Update it must not perform is 4.10's assertion for `attribution`; here the
    /// point is that no second component claims this table [INVARIANT 10].
    /// </summary>
    [Fact]
    public void C14IsTheSoleDeclaredWriterOfCandidateSet()
    {
        var writers = PipelineComposition
            .AllOwnersForConformance(TestDatabase.ConnectionString)
            .Where(o => o.WriteSet.Any(w => w.Table == "candidate_set"))
            .Select(o => o.Name)
            .ToList();

        Assert.Equal(["CandidateAllocator"], writers);
    }

    /// <summary>
    /// **This component has no floor knowledge and no screen definition in its
    /// statement.** The floors are C13's and the ranked list arrives with them applied,
    /// so a floor cannot be applied differently here [§06, D-115].
    /// </summary>
    [Fact]
    public void TheStatementCarriesNoFloorAndNoScore()
    {
        var sql = CandidateAllocator.Sql([(ScreenA, SlotQuota.For(8))], RunDate);

        Assert.DoesNotContain("floor", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("screen_history", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("s.score", sql, StringComparison.Ordinal);

        Assert.DoesNotContain("screen_history", new CandidateAllocator().ReadSet);
    }

    /// <summary>Two runs over one date emit the same statement, byte for byte.</summary>
    [Fact]
    public void TwoBuildsOfTheStatementAreIdentical()
    {
        var quotas = new[] { (ScreenB, SlotQuota.For(8)), (ScreenA, SlotQuota.For(8)) };

        Assert.Equal(
            CandidateAllocator.Sql(quotas, RunDate),
            CandidateAllocator.Sql(quotas, RunDate));
    }

    // ------------------------------------------------------------- plumbing ---

    private static async Task RunAsync(CancellationToken ct)
    {
        var stage = new CandidateAllocator();

        var context = new StageContext(
            RunDate, 1,
            new StageData(TestDatabase.ConnectionString, new DeclaredAccess(stage)),
            new FrozenClock(RunDate),
            new ConfigStore(TestDatabase.ConnectionString));

        await stage.ExecuteAsync(context, ct).ConfigureAwait(true);
    }

    /// <summary>
    /// Two fabricated live screens and the seeded five retired on this date, so nothing
    /// but this fixture's ranks can produce a candidate.
    /// </summary>
    private static async Task SeedAsync(CancellationToken ct)
    {
        await ClearAsync(ct);
        await new ConfigSeeder(TestDatabase.ConnectionString).SeedAsync(ct).ConfigureAwait(true);

        foreach (var id in new[] { ScreenA, ScreenB })
        {
            await ExecAsync("""
                INSERT INTO config_rows (key, version, value, set_at, set_by) VALUES
                  (@m, 1, @mv::jsonb, TIMESTAMPTZ '2020-01-01 12:00:00Z', 'test'),
                  (@i, 1, '1'::jsonb, TIMESTAMPTZ '2020-01-01 12:00:00Z', 'test'),
                  (@s, 1, '"live"'::jsonb, TIMESTAMPTZ '2020-01-01 12:00:00Z', 'test'),
                  (@l, 1, '8'::jsonb, TIMESTAMPTZ '2020-01-01 12:00:00Z', 'test')
                ON CONFLICT (key, version) DO UPDATE SET value = EXCLUDED.value;
                """, ct,
                ("m", $"screens.{id}.metrics"), ("mv", OneMetric),
                ("i", $"screens.{id}.min_inputs"),
                ("s", $"screens.{id}.state"),
                ("l", $"screens.{id}.slots"));
        }

        // The seeded five are retired on this date only, at a later config version, so
        // the rows the seeder owns are untouched and every other fixture still sees them
        // live [config is append-only, CLAUDE.md section 8].
        foreach (var id in new[] { "S1", "S2", "S3", "S4", "S5" })
        {
            await ExecAsync("""
                INSERT INTO config_rows (key, version, value, set_at, set_by)
                VALUES (@k, 2, '"retired"'::jsonb, TIMESTAMPTZ '2020-06-01 12:00:00Z', 'test')
                ON CONFLICT (key, version) DO UPDATE SET value = EXCLUDED.value;
                """, ct, ("k", $"screens.{id}.state"));
        }
    }

    /// <summary>
    /// One name, a member in the bucket named, with a rank in one screen and a passing
    /// gate row. The rank is written straight into `screen_score_daily`, C13's own work
    /// not being what this checkpoint is about.
    /// </summary>
    private static async Task RankAsync(
        string screenId, string ticker, string bucket, int? rank, CancellationToken ct)
    {
        await ExecAsync("""
            INSERT INTO security_daily (ticker, date, sector, size_bucket, market_cap, is_active)
            VALUES (@t, @d, @sec, @b, 1000000000, TRUE)
            ON CONFLICT (ticker, date) DO UPDATE SET size_bucket = EXCLUDED.size_bucket, is_active = TRUE;
            """, ct, ("t", ticker), ("d", RunDate), ("sec", Sector), ("b", bucket));

        await ExecAsync("""
            INSERT INTO gate_result (ticker, date, passed, reasons, gate_state)
            VALUES (@t, @d, TRUE, '{}', 'passed')
            ON CONFLICT (ticker, date) DO UPDATE SET passed = TRUE, reasons = '{}';
            """, ct, ("t", ticker), ("d", RunDate));

        await ExecAsync("""
            INSERT INTO screen_score_daily (date, screen_id, ticker, score, rank_within_screen, config_version)
            VALUES (@d, @s, @t, 90, @r, 1)
            ON CONFLICT (date, screen_id, ticker) DO UPDATE SET
                rank_within_screen = EXCLUDED.rank_within_screen;
            """, ct, ("d", RunDate), ("s", screenId), ("t", ticker),
            ("r", (object?) rank ?? DBNull.Value));
    }

    private static async Task GateAsync(string ticker, CancellationToken ct)
        => await ExecAsync(
            "UPDATE gate_result SET passed = FALSE, reasons = ARRAY['halt'] " +
            "WHERE ticker = @t AND date = @d;",
            ct, ("t", ticker), ("d", RunDate));

    private static async Task SetSlotsAsync(string screenId, int slots, CancellationToken ct)
        => await ExecAsync(
            "UPDATE config_rows SET value = @v::jsonb WHERE key = @k AND version = 1;",
            ct, ("v", slots.ToString(CultureInfo.InvariantCulture)), ("k", $"screens.{screenId}.slots"));

    private static async Task SetStateAsync(string screenId, string state, CancellationToken ct)
        => await ExecAsync(
            "UPDATE config_rows SET value = @v::jsonb WHERE key = @k AND version = 1;",
            ct, ("v", "\"" + state + "\""), ("k", $"screens.{screenId}.state"));

    private static async Task ClearAsync(CancellationToken ct)
    {
        await ExecAsync("DELETE FROM candidate_set WHERE date = @d;", ct, ("d", RunDate));

        // attribution too, from 4.10. Not cleaning it left nine rows behind and broke
        // 4.1's zero-row assertion from a different file, which is the cross-test leak
        // ScreenEngineTests already records twice.
        await ExecAsync("DELETE FROM attribution WHERE date = @d;", ct, ("d", RunDate));
        await ExecAsync("DELETE FROM screen_score_daily WHERE date = @d;", ct, ("d", RunDate));
        await ExecAsync("DELETE FROM screen_history WHERE date = @d;", ct, ("d", RunDate));
        await ExecAsync("DELETE FROM gate_result WHERE date = @d;", ct, ("d", RunDate));
        await ExecAsync("DELETE FROM security_daily WHERE ticker LIKE 'SRLA.%';", ct);
        await ExecAsync("DELETE FROM config_rows WHERE key LIKE @a OR key LIKE @b;", ct,
            ("a", $"screens.{ScreenA}.%"), ("b", $"screens.{ScreenB}.%"));
        await ExecAsync("DELETE FROM config_rows WHERE key LIKE 'screens.S%.state' AND version = 2;", ct);
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

    private static async Task<IReadOnlyList<string>> CandidatesAsync(CancellationToken ct)
        => await TickersAsync(
            "SELECT ticker FROM candidate_set WHERE date = @d ORDER BY ticker COLLATE \"C\";", ct);

    private static async Task<IReadOnlyList<string>> CandidatesInBucketAsync(
        string bucket, CancellationToken ct)
        => await TickersAsync(
            "SELECT ticker FROM candidate_set WHERE date = @d AND size_bucket = '" + bucket +
            "' ORDER BY ticker COLLATE \"C\";", ct);

    private static async Task<IReadOnlyList<string>> TickersAsync(string sql, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(sql, conn);

        cmd.Parameters.AddWithValue("d", RunDate);

        var found = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(true);

        while (await reader.ReadAsync(ct).ConfigureAwait(true))
        {
            found.Add(reader.GetString(0));
        }

        return found;
    }

    private static async Task<IReadOnlyList<string>> SurfacingAsync(string ticker, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(
            "SELECT screens_surfacing FROM candidate_set WHERE ticker = @t AND date = @d;", conn);

        cmd.Parameters.AddWithValue("t", ticker);
        cmd.Parameters.AddWithValue("d", RunDate);

        return (string[]) (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true))!;
    }

    /// <summary>Per-file, as every other stage fixture in this suite keeps its own.</summary>
    private sealed class FrozenClock(DateOnly today) : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(today.Year, today.Month, today.Day, 21, 30, 0, TimeSpan.Zero);

        public DateOnly Today { get; } = today;
    }
}
