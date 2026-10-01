using Npgsql;
using StockResearcherLab.Core;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;
using StockResearcherLab.Pipeline.Select;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Select;

/// <summary>
/// Checkpoint 4.13. The two-pass range run, and §5's pre-registered persistence measure.
///
/// **Pass two refuses to run unless pass one covered the calendar's every session.** A
/// floor is the 98th percentile of a screen's own trailing distribution [D-9, D-115], so
/// a floor drawn over a short score table comes from a population that does not exist and
/// looks entirely normal doing it: a number in the right range, on every date, against a
/// table with the right columns. Nothing downstream can tell the difference, which is why
/// the refusal has to be here.
/// </summary>
[Collection("database")]
public sealed class ScreenRangeRunTests
{
    /// <summary>
    /// A short window inside 2022, so the fixture's dates fall in one partition and no
    /// other Select fixture's by-date cleanup reaches them.
    /// </summary>
    private static readonly DateOnly From = new(2022, 3, 1);

    private static readonly DateOnly To = new(2022, 3, 10);

    private const string Screen = "SRLRANGE";
    private const string Sector = "srltest-range";
    private const string Name = "SRLR.A";

    private const string OneMetric =
        """[{"metric":"adx14","direction":"high","weight":1}]""";

    // ------------------------------------------------------------- the passes ---

    /// <summary>
    /// Pass one scores every session in the range and ranks nothing. `rank_within_screen`
    /// null everywhere afterwards is 4.13's own done-when.
    /// </summary>
    [Fact]
    public async Task PassOneScoresEverySessionAndRanksNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            var one = await Run().ScoreAsync(From, To, ct);

            Assert.Equal(await SessionCountAsync(ct), one.Dates);
            Assert.Equal(one.Dates, await ScoredDatesAsync(ct));
            Assert.Equal(0, await RankedRowsAsync(ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// **An interrupted pass one blocks pass two rather than producing floors over a short
    /// table.** One session removed is enough, which is what makes this an assertion about
    /// the comparison rather than about an obviously empty table.
    /// </summary>
    [Fact]
    public async Task AnInterruptedPassOneBlocksPassTwo()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await Run().ScoreAsync(From, To, ct);

            var dropped = await OneScoredDateAsync(ct);
            await ExecAsync("DELETE FROM screen_score_daily WHERE date = @d;", ct, ("d", dropped));

            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => Run().FloorAsync(From, To, ct));

            Assert.Contains("will not run", thrown.Message, StringComparison.Ordinal);

            // And nothing was written by the refusal, so a caller that ignored the
            // exception would still find no floor rather than a partial one.
            Assert.Equal(0, await HistoryRowsAsync(ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// Pass two runs when pass one is complete, and writes a `screen_history` row per
    /// screen per session whether or not a floor exists on it.
    /// </summary>
    [Fact]
    public async Task PassTwoRunsWhenPassOneIsCompleteAndRecordsEverySession()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            var one = await Run().ScoreAsync(From, To, ct);
            var two = await Run().FloorAsync(From, To, ct);

            Assert.Equal(one.Dates, two.Dates);
            Assert.Equal(one.Dates, await HistoryRowsAsync(ct));

            // Below the lookback there is no floor and nothing is ranked, which over a
            // ten-session fixture is every session [D-115].
            Assert.Equal(0, await RankedRowsAsync(ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// **A pass restricted to a screen touches no other screen's rows** [Q.7].
    ///
    /// This is the property the shadow registration rests on. Pass one upserts
    /// `rank_within_screen` along with the score and always writes it null, so an
    /// unrestricted pass one over a store that is already floored clears every rank in
    /// it. Restricting the pass is what makes registering a screen after the range run a
    /// cheap operation rather than a full rebuild.
    ///
    /// The fixture ranks a second screen by hand rather than running pass two, because
    /// what is under test is whether the restricted pass leaves those ranks alone and not
    /// how they came to be there.
    /// </summary>
    [Fact]
    public async Task APassRestrictedToOneScreenLeavesAnothersRanksAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await RegisterSecondScreenAsync(ct);

            await Run().ScoreAsync(From, To, ct);

            // Rank the second screen's rows by hand, which is what pass two would have
            // left behind on a store that had run far enough to have a floor.
            await ExecAsync(
                "UPDATE screen_score_daily SET rank_within_screen = 1 WHERE screen_id = @s;",
                ct, ("s", Second));

            var before = await RankedRowsForAsync(Second, ct);
            Assert.True(before > 0, "the fixture ranked nothing, so this asserts over an empty set");

            // Pass one again, restricted to the first screen alone.
            var again = await Run().ScoreAsync(From, To, new[] { Screen }, ct);

            Assert.Equal(before, await RankedRowsForAsync(Second, ct));
            Assert.Contains(Screen, again.Detail, StringComparison.Ordinal);

            // And the unrestricted pass is what would have cleared them, which is the
            // half that makes the assertion above mean something rather than describing
            // a pass that never writes.
            await Run().ScoreAsync(From, To, ct);

            Assert.Equal(0, await RankedRowsForAsync(Second, ct));
        }
        finally
        {
            await ClearSecondScreenAsync(ct);
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// **The refusal is per screen in scope, not per range** [Q.7]. It counted distinct
    /// dates over the whole range, which one screen covering the range satisfies however
    /// short another is. A pass one that completed for one screen and died on the second
    /// is exactly the interruption the guard exists for, and it used to pass.
    /// </summary>
    [Fact]
    public async Task APassOneShortForOneScreenBlocksPassTwoForThatScreen()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await RegisterSecondScreenAsync(ct);

            await Run().ScoreAsync(From, To, ct);

            // One session removed from the second screen alone. Every date in the range
            // still carries rows, so a count of distinct dates over the range is
            // unchanged and the old guard saw nothing.
            var dropped = await OneScoredDateAsync(ct);
            await ExecAsync(
                "DELETE FROM screen_score_daily WHERE screen_id = @s AND date = @d;",
                ct, ("s", Second), ("d", dropped));

            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => Run().FloorAsync(From, To, ct));

            Assert.Contains(Second, thrown.Message, StringComparison.Ordinal);
            Assert.Contains("will not run", thrown.Message, StringComparison.Ordinal);
            Assert.Equal(0, await HistoryRowsAsync(ct));

            // The screen that is complete still floors when it is the only one in scope,
            // so the guard refuses the short screen rather than the range.
            var two = await Run().FloorAsync(From, To, new[] { Screen }, ct);
            Assert.Equal(await SessionCountAsync(ct), two.Dates);
        }
        finally
        {
            await ClearSecondScreenAsync(ct);
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// **A screen id nothing matches fails rather than running over nothing.** A pass
    /// restricted to a mistyped name would write no row and report success, which is
    /// indistinguishable from a screen that scored nothing [`CLAUDE.md` §1].
    /// </summary>
    [Fact]
    public async Task ARestrictionNamingNoRegisteredScreenFails()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => Run().ScoreAsync(From, To, new[] { "SRLRANGE-TYPO" }, ct));

            Assert.Contains("SRLRANGE-TYPO", thrown.Message, StringComparison.Ordinal);

            // The message names what is registered, so the caller can see the typo rather
            // than only that there was one.
            Assert.Contains(Screen, thrown.Message, StringComparison.Ordinal);

            Assert.Equal(0, await ScoredDatesAsync(ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// **No rank survives a floor it does not clear, after `screens.floor_percentile`
    /// moves and the floor pass alone is re-run** [5.5.5, §06].
    ///
    /// Twenty-one names over ten sessions, the lookback lowered to five so a floor exists,
    /// floored at the 50th percentile and then at the 98th with no score written in
    /// between. The count is §06's "floors already applied" stated as a query: a ranked
    /// row on a date with no floor, or below its date's floor. Until 5.5.5 the rank
    /// statement updated only the rows that cleared, so every name between the two floors
    /// kept the rank the lower one gave it.
    /// </summary>
    [Fact]
    public async Task ARaisedFloorLeavesNoRankBelowItAcrossTheRange()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await SeedSpreadAsync(ct);
            await SetFloorAsync("screens.floor_lookback_days", 2, "5", "2020-06-01", ct);
            await SetFloorAsync("screens.floor_percentile", 2, "50", "2020-06-01", ct);

            await Run().ScoreAsync(From, To, ct);
            await Run().FloorAsync(From, To, ct);

            var lower = await RankedRowsAsync(ct);
            Assert.True(lower > 0, "the lower floor ranked nothing, so the raise has nothing to clear");

            await SetFloorAsync("screens.floor_percentile", 3, "98", "2020-07-01", ct);
            await Run().FloorAsync(From, To, ct);

            Assert.Equal(0L, await StaleRanksAsync(ct));
            Assert.True(await RankedRowsAsync(ct) < lower, "the raised floor ranks no fewer names than the lower one");
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// **A date that loses its floor loses its ranks** [5.5.5]. The lookback raised past the
    /// fixture's ten sessions after it has been floored, so every date's floor is null
    /// when the floor pass is re-run. Until 5.5.5 the null-floor path returned before
    /// touching a rank, so every rank the earlier floor gave stood on a date with none.
    /// </summary>
    [Fact]
    public async Task ADateThatLosesItsFloorLosesItsRanks()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await SeedSpreadAsync(ct);
            await SetFloorAsync("screens.floor_lookback_days", 2, "5", "2020-06-01", ct);
            await SetFloorAsync("screens.floor_percentile", 2, "50", "2020-06-01", ct);

            await Run().ScoreAsync(From, To, ct);
            await Run().FloorAsync(From, To, ct);
            Assert.True(await RankedRowsAsync(ct) > 0, "nothing was ranked, so nothing can be shown to clear");

            await SetFloorAsync("screens.floor_lookback_days", 3, "50", "2020-07-01", ct);
            await Run().FloorAsync(From, To, ct);

            Assert.Equal(0, await RankedRowsAsync(ct));
            Assert.Equal(0L, await StaleRanksAsync(ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// **The floor pass resolves the floor keys as of each session, not as of the range's
    /// end** [INVARIANT 13, D-43, 5.5.6].
    ///
    /// `screens.floor_percentile` drops to 50 by a version stamped 2022-03-07, inside the
    /// range. A nightly C13 run over 2022-03-06 resolves the 98 in force that day; until
    /// 5.5.6 the range pass resolved both keys once, at the range's end, so every session
    /// took the 50, including the ones before the stamp. Both sides of the stamp are
    /// compared with what the nightly stage writes for the same date, because that is the
    /// one reading of "the floor on that date" that cannot be argued with.
    /// </summary>
    [Fact]
    public async Task TheFloorPassResolvesItsKeysAsOfEachSession()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await SeedSpreadAsync(ct);
            await SetFloorAsync("screens.floor_lookback_days", 2, "5", "2020-06-01", ct);
            await SetFloorAsync("screens.floor_percentile", 2, "50", "2022-03-07", ct);

            await Run().ScoreAsync(From, To, ct);
            await Run().FloorAsync(From, To, ct);

            var before = new DateOnly(2022, 3, 6);
            var after = new DateOnly(2022, 3, 8);

            var rangeBefore = await FloorOnAsync(before, ct);
            var rangeAfter = await FloorOnAsync(after, ct);

            Assert.NotNull(rangeBefore);
            Assert.NotNull(rangeAfter);

            await RunNightlyAsync(before, ct);
            await RunNightlyAsync(after, ct);

            Assert.Equal(await FloorOnAsync(before, ct), rangeBefore);
            Assert.Equal(await FloorOnAsync(after, ct), rangeAfter);

            // And the two sides of the stamp differ, so the equalities above are not two
            // readings of one value.
            Assert.NotEqual(rangeBefore, rangeAfter);
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// **A lag with no pair is unknown, not zero** [5.5.7, `CLAUDE.md` §6].
    ///
    /// Ten sessions, one name ranked on every one. D-1 has nine pairs and an overlap of 1.
    /// D-21 has none: no session in the range has one twenty-one sessions before it. Until
    /// 5.5.7 the null the statement returned for that lag was read as 0 and printed as
    /// 0.0000, which reads as a screen whose ranked set is entirely replaced within a month,
    /// a finding, when nothing was measured.
    /// </summary>
    [Fact]
    public async Task ALagWithNoPairIsUnknownRatherThanZero()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await Run().ScoreAsync(From, To, ct);

            await ExecAsync(
                "UPDATE screen_score_daily SET rank_within_screen = 1 " +
                "WHERE date BETWEEN @f AND @t AND score IS NOT NULL;",
                ct, ("f", From), ("t", To));

            var screen = Assert.Single(await PersistenceMeasure
                .MeasureAsync(TestDatabase.ConnectionString, From, To, ct));

            Assert.Equal((double?) 1d, screen.Lag1);
            Assert.Null(screen.Lag21);
            Assert.Equal(0L, screen.Pairs21);
            Assert.Contains("| 1.0000 | ", PersistenceMeasure.TableRow(screen), StringComparison.Ordinal);
            Assert.Contains("| " + PersistenceMeasure.NoPair + " |", PersistenceMeasure.TableRow(screen), StringComparison.Ordinal);
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>Re-running one date reproduces it byte-identically.</summary>
    [Fact]
    public async Task ReRunningOneDateReproducesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await Run().ScoreAsync(From, To, ct);
            var first = await ScoreDigestAsync(ct);

            await Run().ScoreAsync(From, To, ct);

            Assert.Equal(first, await ScoreDigestAsync(ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// A range holding no session fails rather than reporting success over nothing, which
    /// is the silent zero `CLAUDE.md` §1 describes.
    /// </summary>
    [Fact]
    public async Task ARangeWithNoSessionFails()
    {
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Run().ScoreAsync(new DateOnly(1990, 1, 1), new DateOnly(1990, 1, 5), ct));
    }

    // ------------------------------------------------- the persistence measure ---

    /// <summary>
    /// **The chance baseline is the closed form and not a simulation**, so it carries no
    /// seed and reproduces exactly. Two independent draws of k from n have an expected
    /// intersection of k²/n and an expected union of 2k - k²/n, so the Jaccard ratio is
    /// k / (2n - k).
    /// </summary>
    [Theory]
    [InlineData(50d, 2500d, 50d / 4950d)]
    [InlineData(1d, 1d, 1d)]
    [InlineData(0d, 100d, 0d)]
    [InlineData(10d, 0d, 0d)]
    public void TheChanceBaselineIsTheClosedForm(double ranked, double scored, double expected)
        => Assert.Equal(expected, PersistenceMeasure.ChanceOverlap(ranked, scored), 10);

    /// <summary>
    /// The Jaccard function is the database's and is exercised rather than assumed. It is
    /// called from inside an aggregate over twenty million rows, so it is schema rather
    /// than code [`0020`].
    /// </summary>
    [Theory]
    [InlineData("{A,B,C}", "{A,B,C}", 1.0)]
    [InlineData("{A,B,C}", "{D,E,F}", 0.0)]
    [InlineData("{A,B}", "{B,C}", 1.0 / 3.0)]
    [InlineData("{A,B,C,D}", "{C,D}", 0.5)]
    public async Task TheJaccardFunctionAgreesWithTheDefinition(string a, string b, double expected)
    {
        var ct = TestContext.Current.CancellationToken;

        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand("SELECT jaccard(@a::text[], @b::text[]);", conn);

        cmd.Parameters.AddWithValue("a", a);
        cmd.Parameters.AddWithValue("b", b);

        Assert.Equal(
            expected,
            Convert.ToDouble(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true),
                System.Globalization.CultureInfo.InvariantCulture),
            6);
    }

    /// <summary>
    /// **Two empty sets have no overlap rather than an overlap of zero**, and a null
    /// argument likewise. A date with no prior ranked set is filtered out of the measure
    /// rather than averaged in as a zero, which over the first 250 sessions of the window
    /// would drag every screen's figure toward zero and read as near-chance persistence on
    /// all five.
    /// </summary>
    [Fact]
    public async Task AnAbsentSetIsNullRatherThanZero()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);

        await using (var empty = new NpgsqlCommand(
            "SELECT jaccard('{}'::text[], '{}'::text[]);", conn))
        {
            Assert.True(await empty.ExecuteScalarAsync(ct).ConfigureAwait(true) is DBNull);
        }

        await using (var missing = new NpgsqlCommand(
            "SELECT jaccard(NULL::text[], '{A}'::text[]);", conn))
        {
            Assert.True(await missing.ExecuteScalarAsync(ct).ConfigureAwait(true) is DBNull);
        }
    }

    /// <summary>
    /// **The measure reads no forward return and no attribution row**, so it does not
    /// touch `CLAUDE.md` §11's prohibition on tuning screens on forward returns before
    /// anything has judged them. Asserted against the statement rather than argued.
    /// </summary>
    [Fact]
    public void TheMeasureReadsNoReturnAndNoAttribution()
    {
        var sql = PersistenceMeasure.Sql(From, To);

        Assert.DoesNotContain("attribution", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("return_", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("vs_peers", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("vs_spy", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("candidate_set", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// The three lags are §5's three and are stated in the code as one list, so a fourth
    /// arriving would arrive in the statement too.
    /// </summary>
    [Fact]
    public void TheThreeLagsAreTheOnesStatedInAdvance()
        => Assert.Equal([1, 5, 21], PersistenceMeasure.Lags);

    /// <summary>
    /// The measure over a fixture whose ranked set is known: one name ranked on every
    /// session, so the overlap is 1 at every lag and the figures come back per screen.
    /// A degenerate fixture, and its job is to prove the statement runs and groups rather
    /// than to say anything about a screen.
    /// </summary>
    [Fact]
    public async Task TheMeasureReturnsARowPerScreenWithItsOwnBaseline()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await Run().ScoreAsync(From, To, ct);

            // Rank the one name on every session, the fixture window being far too short
            // for a floor to exist on its own.
            await ExecAsync(
                "UPDATE screen_score_daily SET rank_within_screen = 1 " +
                "WHERE date BETWEEN @f AND @t AND score IS NOT NULL;",
                ct, ("f", From), ("t", To));

            var measured = await PersistenceMeasure
                .MeasureAsync(TestDatabase.ConnectionString, From, To, ct);

            var screen = Assert.Single(measured);

            Assert.Equal(Screen, screen.ScreenId);
            Assert.True(screen.Pairs > 0);
            Assert.Equal(1d, screen.Lag1!.Value, 6);
            Assert.Equal(1d, screen.MeanRankedSize, 6);
            Assert.Equal(
                PersistenceMeasure.ChanceOverlap(screen.MeanRankedSize, screen.MeanScoredSize),
                screen.Chance,
                10);
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    // ------------------------------------------------------------- plumbing ---

    private static ScreenRangeRun Run() => new(TestDatabase.ConnectionString);

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

        // A session list, which the range run reads out of price_daily rather than off a
        // calendar: a calendar walk would produce a row of nulls on a day the exchange did
        // not trade [TradingCalendar].
        for (var day = From; day <= To; day = day.AddDays(1))
        {
            await ExecAsync("""
                INSERT INTO price_daily (ticker, date, open, high, low, close, adj_close, volume)
                VALUES (@t, @d, 100, 100, 100, 100, 100, 1000000)
                ON CONFLICT (ticker, date) DO NOTHING;
                """, ct, ("t", Name), ("d", day));

            await ExecAsync("""
                INSERT INTO security_daily (ticker, date, sector, size_bucket, market_cap, is_active)
                VALUES (@t, @d, @sec, 'mid', 1000000000, TRUE)
                ON CONFLICT (ticker, date) DO UPDATE SET is_active = TRUE;
                """, ct, ("t", Name), ("d", day), ("sec", Sector));

            await ExecAsync("""
                INSERT INTO indicator_daily (ticker, date, adx14_pctile) VALUES (@t, @d, 60)
                ON CONFLICT (ticker, date) DO UPDATE SET adx14_pctile = 60;
                """, ct, ("t", Name), ("d", day));
        }
    }

    private static async Task ClearAsync(CancellationToken ct)
    {
        await ExecAsync("DELETE FROM screen_score_daily WHERE date BETWEEN @f AND @t;",
            ct, ("f", From), ("t", To));
        await ExecAsync("DELETE FROM screen_history WHERE date BETWEEN @f AND @t;",
            ct, ("f", From), ("t", To));
        await ExecAsync("DELETE FROM price_daily WHERE ticker LIKE 'SRLR.%';", ct);
        await ExecAsync("DELETE FROM indicator_daily WHERE ticker LIKE 'SRLR.%';", ct);
        await ExecAsync("DELETE FROM security_daily WHERE ticker LIKE 'SRLR.%';", ct);
        await ExecAsync("DELETE FROM config_rows WHERE key LIKE @k;", ct, ("k", $"screens.{Screen}.%"));
        await ExecAsync("DELETE FROM config_rows WHERE key LIKE '" + SeededScreens.AnyStateVersionTwo +
            "' AND version = 2;", ct);

        // The two shared floor keys are read by every screen fixture, so a version this
        // file writes must not outlive it [5.5.5].
        await ExecAsync(
            "DELETE FROM config_rows WHERE key IN ('screens.floor_percentile', 'screens.floor_lookback_days') " +
            "AND version > 1;", ct);
    }

    /// <summary>
    /// Twenty names beside `SRLR.A`, at `adx14_pctile` 5 to 100 on every session, so a
    /// floor separates them.
    /// </summary>
    private static async Task SeedSpreadAsync(CancellationToken ct)
    {
        for (var n = 1; n <= 20; n++)
        {
            var ticker = $"SRLR.B{n:00}";

            for (var day = From; day <= To; day = day.AddDays(1))
            {
                await ExecAsync("""
                    INSERT INTO security_daily (ticker, date, sector, size_bucket, market_cap, is_active)
                    VALUES (@t, @d, @sec, 'mid', 1000000000, TRUE)
                    ON CONFLICT (ticker, date) DO UPDATE SET is_active = TRUE;
                    """, ct, ("t", ticker), ("d", day), ("sec", Sector));

                await ExecAsync("""
                    INSERT INTO indicator_daily (ticker, date, adx14_pctile) VALUES (@t, @d, @p)
                    ON CONFLICT (ticker, date) DO UPDATE SET adx14_pctile = EXCLUDED.adx14_pctile;
                    """, ct, ("t", ticker), ("d", day), ("p", (float) (5 * n)));
            }
        }
    }

    private static async Task<double?> FloorOnAsync(DateOnly date, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(
            "SELECT floor_score FROM screen_history WHERE screen_id = @s AND date = @d;", conn);

        cmd.Parameters.AddWithValue("s", Screen);
        cmd.Parameters.AddWithValue("d", date);

        var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true);

        return value is null or DBNull ? null : Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>C13 as the evening sequence runs it, on one date, against the same store.</summary>
    private static async Task RunNightlyAsync(DateOnly date, CancellationToken ct)
    {
        var stage = new ScreenEngine();
        var config = new ConfigStore(TestDatabase.ConnectionString);

        var context = new StageContext(
            date, await config.RequireVersionAsync(date, ct).ConfigureAwait(true),
            new StageData(TestDatabase.ConnectionString, new DeclaredAccess(stage)),
            new FrozenClock(date),
            config);

        await stage.ExecuteAsync(context, ct).ConfigureAwait(true);
    }

    /// <summary>Per-file, as every other stage fixture in this suite keeps its own.</summary>
    private sealed class FrozenClock(DateOnly today) : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(today.Year, today.Month, today.Day, 21, 30, 0, TimeSpan.Zero);

        public DateOnly Today { get; } = today;
    }
    private static async Task SetFloorAsync(
        string key, int version, string value, string setAt, CancellationToken ct)
        => await ExecAsync(
            "INSERT INTO config_rows (key, version, value, set_at, set_by) " +
            "VALUES (@k, @v, @val::jsonb, @at::timestamptz, 'test') " +
            "ON CONFLICT (key, version) DO UPDATE SET value = EXCLUDED.value, set_at = EXCLUDED.set_at;",
            ct, ("k", key), ("v", version), ("val", value), ("at", setAt + " 12:00:00Z"));

    /// <summary>
    /// §06's "floors already applied" as a query, over the fixture's range: a ranked row on
    /// a date with no floor, or below its date's floor. The same statement is run once
    /// against the live store [5.5.5].
    /// </summary>
    private static async Task<long> StaleRanksAsync(CancellationToken ct)
        => await ScalarAsync(
            "SELECT count(*)::bigint FROM screen_score_daily s JOIN screen_history h USING (screen_id, date) " +
            "WHERE s.date BETWEEN @f AND @t AND s.rank_within_screen IS NOT NULL " +
            "AND (h.floor_score IS NULL OR s.score < h.floor_score);", ct);

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

    private static async Task<int> SessionCountAsync(CancellationToken ct)
        => (int) await ScalarAsync(
            "SELECT count(DISTINCT date)::bigint FROM price_daily WHERE date BETWEEN @f AND @t;", ct);

    private static async Task<int> ScoredDatesAsync(CancellationToken ct)
        => (int) await ScalarAsync(
            "SELECT count(DISTINCT date)::bigint FROM screen_score_daily WHERE date BETWEEN @f AND @t;", ct);

    private static async Task<int> RankedRowsAsync(CancellationToken ct)
        => (int) await ScalarAsync(
            "SELECT count(*)::bigint FROM screen_score_daily " +
            "WHERE date BETWEEN @f AND @t AND rank_within_screen IS NOT NULL;", ct);

    /// <summary>A second live screen, so a restriction has something to leave alone.</summary>
    private const string Second = "SRLRANGE2";

    private static async Task RegisterSecondScreenAsync(CancellationToken ct)
        => await ExecAsync("""
            INSERT INTO config_rows (key, version, value, set_at, set_by) VALUES
              (@m, 1, @mv::jsonb, TIMESTAMPTZ '2020-01-01 12:00:00Z', 'test'),
              (@i, 1, '1'::jsonb, TIMESTAMPTZ '2020-01-01 12:00:00Z', 'test'),
              (@s, 1, '"live"'::jsonb, TIMESTAMPTZ '2020-01-01 12:00:00Z', 'test'),
              (@l, 1, '8'::jsonb, TIMESTAMPTZ '2020-01-01 12:00:00Z', 'test')
            ON CONFLICT (key, version) DO UPDATE SET value = EXCLUDED.value;
            """, ct,
            ("m", $"screens.{Second}.metrics"), ("mv", OneMetric),
            ("i", $"screens.{Second}.min_inputs"),
            ("s", $"screens.{Second}.state"),
            ("l", $"screens.{Second}.slots"));

    private static async Task ClearSecondScreenAsync(CancellationToken ct)
        => await ExecAsync(
            "DELETE FROM config_rows WHERE key LIKE 'screens.' || @s || '.%';", ct, ("s", Second));

    private static async Task<int> RankedRowsForAsync(string screenId, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*)::bigint FROM screen_score_daily " +
            "WHERE screen_id = @s AND date BETWEEN @f AND @t AND rank_within_screen IS NOT NULL;",
            conn);

        cmd.Parameters.AddWithValue("s", screenId);
        cmd.Parameters.AddWithValue("f", From);
        cmd.Parameters.AddWithValue("t", To);

        return (int) (long) (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true))!;
    }

    private static async Task<int> HistoryRowsAsync(CancellationToken ct)
        => (int) await ScalarAsync(
            "SELECT count(*)::bigint FROM screen_history WHERE date BETWEEN @f AND @t;", ct);

    private static async Task<long> ScalarAsync(string sql, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(sql, conn);

        cmd.Parameters.AddWithValue("f", From);
        cmd.Parameters.AddWithValue("t", To);

        return (long) (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true))!;
    }

    private static async Task<DateOnly> OneScoredDateAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(
            "SELECT max(date) FROM screen_score_daily WHERE date BETWEEN @f AND @t;", conn);

        cmd.Parameters.AddWithValue("f", From);
        cmd.Parameters.AddWithValue("t", To);

        return DateOnly.FromDateTime(
            (DateTime) (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true))!);
    }

    /// <summary>
    /// Every score in the range as one ordered string, so "reproduces byte-identically" is
    /// a comparison of the whole table rather than of a count.
    /// </summary>
    private static async Task<string> ScoreDigestAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(
            "SELECT string_agg(x, '|' ORDER BY x COLLATE \"C\") FROM (" +
            "  SELECT date::text || ' ' || screen_id || ' ' || ticker || ' ' || " +
            "         coalesce(score::text, 'null') || ' ' || coalesce(rank_within_screen::text, 'null') AS x" +
            "  FROM screen_score_daily WHERE date BETWEEN @f AND @t) s;", conn);

        cmd.Parameters.AddWithValue("f", From);
        cmd.Parameters.AddWithValue("t", To);

        return (string) (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true))!;
    }
}
