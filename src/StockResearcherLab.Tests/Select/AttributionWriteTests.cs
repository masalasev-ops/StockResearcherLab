using System.Text.Json;
using Npgsql;
using StockResearcherLab.Core;
using StockResearcherLab.Core.Config;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;
using StockResearcherLab.Pipeline;
using StockResearcherLab.Pipeline.Select;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Select;

/// <summary>
/// Checkpoint 4.10. The attribution write: one row per name any registered screen
/// surfaced, scores and ranks frozen as they stand, and the return columns empty.
///
/// **These rows are written at shortlist time and never reconstructed** [INVARIANT 4,
/// D-40]. Screen definitions and slot allocations drift, so a reconstruction applies
/// today's definitions to a past date and produces a record that looks exactly like the
/// one it replaced.
///
/// **A candidate row carrying a shadow screen's id in `screens_surfacing` is the ordinary
/// case** [`SCREEN_LIFECYCLE.md` §4.6]. Two filters do different work: `surfaced_as`
/// selects the population a reader means and the screen ids select the grouping a
/// per-screen report means. Conflating them is the trap that section names, and it is
/// asserted here rather than described.
/// </summary>
[Collection("database")]
public sealed class AttributionWriteTests
{
    private static readonly DateOnly RunDate = new(2021, 7, 1);

    private const string Sector = "srltest-attr";
    /// <summary>
    /// One of the three the `market_context_daily` CHECK admits, so the fixture cannot
    /// use a marked test value here as it does for sector and bucket [0005].
    /// </summary>
    private const string Regime = "risk_off";

    private const string Live = "SRLATTRL";
    private const string Shadow = "SRLATTRS";

    private const string OneMetric =
        """[{"metric":"adx14","direction":"high","weight":1}]""";

    // ------------------------------------------------------- the population ---

    /// <summary>
    /// **One row per name any registered screen surfaced, and none for a name none
    /// did.** The third name here clears no floor at all, so it is a member with a score
    /// row and no attribution row.
    /// </summary>
    [Fact]
    public async Task OneRowPerNameSurfacedAndNoneForANameNoScreenSurfaced()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await RankAsync(Live, "SRLT.A", "mid", 1, score: 90, ct);
            await RankAsync(Live, "SRLT.B", "mid", 2, score: 80, ct);
            await RankAsync(Live, "SRLT.C", "mid", rank: null, score: 10, ct);

            await RunAsync(ct);

            Assert.Equal(["SRLT.A", "SRLT.B"], await AttributedAsync(ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// **A gated name has no attribution row at all**, which is why `gate_state` carries
    /// `passed` or `passed_partial` and never `gated`: there is no row for a gated name
    /// to carry it [D-117].
    /// </summary>
    [Fact]
    public async Task AGatedNameHasNoAttributionRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await RankAsync(Live, "SRLT.A", "mid", 1, score: 90, ct);
            await RankAsync(Live, "SRLT.B", "mid", 2, score: 80, ct);
            await GateAsync("SRLT.A", ct);

            await RunAsync(ct);

            Assert.Equal(["SRLT.B"], await AttributedAsync(ct));

            Assert.DoesNotContain(
                "gated",
                await StatesAsync(ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// The scores and ranks are frozen as they stand tonight, in `score_per_screen`'s
    /// object shape: screen id to an object of score and rank
    /// [`SCREEN_LIFECYCLE.md` §4.4].
    /// </summary>
    [Fact]
    public async Task ScoresAndRanksAreFrozenInTheObjectShape()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await RankAsync(Live, "SRLT.A", "mid", 3, score: 87.5, ct);

            await RunAsync(ct);

            using var doc = JsonDocument.Parse(await ScorePerScreenAsync("SRLT.A", ct));
            var entry = doc.RootElement.GetProperty(Live);

            Assert.Equal(JsonValueKind.Object, entry.ValueKind);
            Assert.Equal(87.5, entry.GetProperty("score").GetDouble(), 4);
            Assert.Equal(3, entry.GetProperty("rank").GetInt32());
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// **The flat shape is refused by the constraint rather than by the writer** [D-110].
    /// A writer-side test closes what this system writes and leaves the column able to
    /// hold a shape no reader can interpret.
    /// </summary>
    [Fact]
    public async Task TheFlatShapeIsRefusedByTheConstraint()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(true);

        await using var cmd = new NpgsqlCommand(
            "INSERT INTO attribution (ticker, date, screens_surfacing, score_per_screen, " +
            "surfaced_as, config_version) VALUES ('SRLT.FLAT', DATE '2021-07-01', " +
            "ARRAY['S1'], '{\"S1\": 87.4}'::jsonb, 'candidate', 1);", conn, tx);

        var thrown = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync(ct));

        Assert.Equal("attribution_score_per_screen_object_map", thrown.ConstraintName);

        await tx.RollbackAsync(ct).ConfigureAwait(true);
    }

    /// <summary>The nine return columns start empty and are C21's to fill [`SCHEMA.md`].</summary>
    [Fact]
    public async Task TheNineReturnColumnsAreNull()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await RankAsync(Live, "SRLT.A", "mid", 1, score: 90, ct);
            await RunAsync(ct);

            await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);

            foreach (var window in new[] { "5d", "21d", "63d" })
            {
                foreach (var kind in new[] { "raw", "vs_spy", "vs_peers" })
                {
                    var column = $"return_{window}_{kind}";

                    await using var cmd = new NpgsqlCommand(
                        $"SELECT count(*) FROM attribution WHERE date = @d AND {column} IS NOT NULL;", conn);

                    cmd.Parameters.AddWithValue("d", RunDate);

                    Assert.Equal(
                        0L,
                        (long) (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true))!);
                }
            }
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    // ------------------------------------------------- the shadow half ---

    /// <summary>
    /// **A shadow screen writes attribution rows labelled shadow and no `candidate_set`
    /// row** [D-85, D-119]. This is the mechanism D-119 asks to be proved against a
    /// fixture screen rather than by registering a family member.
    /// </summary>
    [Fact]
    public async Task AShadowScreenWritesAttributionAndNoCandidateRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await RankAsync(Live, "SRLT.A", "mid", 1, score: 90, ct);
            await RankAsync(Shadow, "SRLT.Z", "mid", 1, score: 95, ct);
            await SetStateAsync(Shadow, "shadow", ct);

            await RunAsync(ct);

            Assert.Equal(["SRLT.A", "SRLT.Z"], await AttributedAsync(ct));
            Assert.Equal("candidate", await SurfacedAsAsync("SRLT.A", ct));
            Assert.Equal("shadow", await SurfacedAsAsync("SRLT.Z", ct));

            Assert.Equal(["SRLT.A"], await CandidatesAsync(ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// **`SCREEN_LIFECYCLE.md` §4.6's trap, asserted.** A name surfaced by a live screen
    /// and a shadow is one candidate row carrying both ids. A per-screen report that
    /// grouped by every id in `screens_surfacing` would open a bucket for the shadow, and
    /// that bucket would render as a screen with no verdicts rather than as a screen that
    /// was not judged.
    /// </summary>
    [Fact]
    public async Task ACandidateRowCarryingAShadowIdIsTheOrdinaryCase()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await RankAsync(Live, "SRLT.A", "mid", 1, score: 90, ct);
            await RankAsync(Shadow, "SRLT.A", "mid", 1, score: 70, ct);
            await SetStateAsync(Shadow, "shadow", ct);

            await RunAsync(ct);

            Assert.Equal("candidate", await SurfacedAsAsync("SRLT.A", ct));
            Assert.Equal([Live, Shadow], await SurfacingAsync("SRLT.A", ct));

            // And the candidate row carries only the live screen, candidate_set being
            // written from the live screens alone.
            Assert.Equal([Live], await CandidateSurfacingAsync("SRLT.A", ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// **Removing the shadow leaves the live screens unchanged**, which is what makes the
    /// mechanism separable from the registration decision D-119 defers to sign-off.
    /// </summary>
    [Fact]
    public async Task RemovingTheShadowLeavesTheLiveScreensUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await RankAsync(Live, "SRLT.A", "mid", 1, score: 90, ct);
            await RankAsync(Shadow, "SRLT.Z", "mid", 1, score: 95, ct);
            await SetStateAsync(Shadow, "shadow", ct);

            await RunAsync(ct);
            var withShadow = await CandidatesAsync(ct);

            await ExecAsync("DELETE FROM attribution WHERE date = @d;", ct, ("d", RunDate));
            await ExecAsync("DELETE FROM config_rows WHERE key LIKE @k;", ct, ("k", $"screens.{Shadow}.%"));
            await ExecAsync("DELETE FROM screen_score_daily WHERE date = @d AND screen_id = @s;",
                ct, ("d", RunDate), ("s", Shadow));

            await RunAsync(ct);

            Assert.Equal(withShadow, await CandidatesAsync(ct));
            Assert.Equal(["SRLT.A"], await AttributedAsync(ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    // ------------------------------------------------------------- the view ---

    /// <summary>
    /// `candidate_attribution` returns exactly the candidate rows [D-110]. Every reader
    /// meaning candidate reads the view, so reading the table is a deliberate act rather
    /// than the default [`SCREEN_LIFECYCLE.md` §4.7].
    /// </summary>
    [Fact]
    public async Task TheViewReturnsExactlyTheCandidateRows()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await RankAsync(Live, "SRLT.A", "mid", 1, score: 90, ct);
            await RankAsync(Shadow, "SRLT.Z", "mid", 1, score: 95, ct);
            await SetStateAsync(Shadow, "shadow", ct);

            await RunAsync(ct);

            Assert.Equal(["SRLT.A", "SRLT.Z"], await AttributedAsync(ct));
            Assert.Equal(
                ["SRLT.A"],
                await TickersAsync(
                    "SELECT ticker FROM candidate_attribution WHERE date = @d ORDER BY ticker COLLATE \"C\";",
                    ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    // -------------------------------------------------------- write ownership ---

    /// <summary>
    /// **The allocator declares an Insert on `attribution` and no Update**, so an
    /// attempted update throws through <see cref="DeclaredAccess"/> before a connection
    /// opens [INVARIANT 10 as amended].
    ///
    /// The guard is exercised rather than the declaration inspected: a declaration that
    /// happened to be right while the guard read a different one would pass an inspection
    /// and fail nothing.
    /// </summary>
    [Fact]
    public async Task TheAllocatorAttemptingAnUpdateThrowsBeforeAConnectionOpens()
    {
        var ct = TestContext.Current.CancellationToken;
        var stage = new CandidateAllocator();

        Assert.DoesNotContain(
            stage.WriteSet,
            w => w.Table == "attribution" && w.Operation == WriteOperation.Update);

        // A connection string that cannot connect, so a guard that let this through
        // would fail with a connection error rather than passing.
        var data = new StageData(
            "Host=srl-no-such-host;Database=none;Username=none;Password=none",
            new DeclaredAccess(stage));

        await Assert.ThrowsAsync<UndeclaredTableAccessException>(
            () => data.WriteAsync(
                "attribution", WriteOperation.Update,
                "UPDATE attribution SET regime = 'x';", parameters: null, ct));
    }

    /// <summary>
    /// Two components write `attribution` and one operation each. C21 does not exist yet,
    /// so what is asserted now is that C14 is the only declared writer and that it claims
    /// the insert alone [INVARIANT 10, `SCHEMA.md`].
    /// </summary>
    [Fact]
    public void C14ClaimsTheInsertAloneAndNoOtherComponentClaimsTheTable()
    {
        var writes = PipelineComposition
            .AllOwnersForConformance(TestDatabase.ConnectionString)
            .SelectMany(o => o.WriteSet.Where(w => w.Table == "attribution").Select(w => (o.Name, w.Operation)))
            .ToList();

        Assert.Equal([("CandidateAllocator", WriteOperation.Insert)], writes);
    }

    /// <summary>
    /// **A row once written is never rewritten.** The scores are changed underneath and a
    /// second run leaves the row as it stood, which is INVARIANT 4 holding in the one
    /// place it can be checked mechanically.
    /// </summary>
    [Fact]
    public async Task ARowOnceWrittenIsNeverRewritten()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await RankAsync(Live, "SRLT.A", "mid", 1, score: 90, ct);
            await RunAsync(ct);

            var first = await ScorePerScreenAsync("SRLT.A", ct);

            await RankAsync(Live, "SRLT.A", "mid", 7, score: 12, ct);
            await RunAsync(ct);

            Assert.Equal(first, await ScorePerScreenAsync("SRLT.A", ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// The row carries the config version in force as of the date and the regime of the
    /// night [`CLAUDE.md` §8, INVARIANT 13]. Without the version a year of data becomes
    /// uninterpretable the first time the tuner runs.
    /// </summary>
    [Fact]
    public async Task TheRowCarriesTheConfigVersionAndTheRegime()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await RankAsync(Live, "SRLT.A", "mid", 1, score: 90, ct);
            await RunAsync(ct);

            await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
            await using var cmd = new NpgsqlCommand(
                "SELECT config_version, regime, sector, size_bucket, gate_state " +
                "FROM attribution WHERE ticker = 'SRLT.A' AND date = @d;", conn);

            cmd.Parameters.AddWithValue("d", RunDate);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(true);
            Assert.True(await reader.ReadAsync(ct).ConfigureAwait(true));

            Assert.Equal(1, reader.GetInt32(0));
            Assert.Equal(Regime, reader.GetString(1));
            Assert.Equal(Sector, reader.GetString(2));
            Assert.Equal("mid", reader.GetString(3));
            Assert.Equal("passed_partial", reader.GetString(4));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// **`gate_state` is `passed_partial` over the backfill window and `passed` when
    /// every reason could fire** [D-117]. The three phase-7 and calendar-blind reasons
    /// are given something to read, and the label changes.
    ///
    /// This is what stops a backfilled night reading identically to a live one when it is
    /// not, which is the D-58 and D-69 pattern met a third time.
    /// </summary>
    [Fact]
    public async Task GateStateIsPartialUntilEveryReasonCanFire()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await RankAsync(Live, "SRLT.A", "mid", 1, score: 90, ct);
            await RunGateAsync(ct);
            await RunAsync(ct);

            Assert.Equal("passed_partial", await SurfacedGateStateAsync("SRLT.A", ct));

            // One earnings date somewhere in the universe, one open position and one
            // recent exit, none of them this name's, so every reason has something to
            // read on this date.
            await ExecAsync(
                "INSERT INTO events (ticker, event_type, event_date) VALUES ('SRLT.OTHER', 'earnings', @d);",
                ct, ("d", RunDate.AddDays(1)));

            await ExecAsync("""
                INSERT INTO "position" (portfolio_id, ticker, opened_date, quantity, entry_price, is_open)
                VALUES ('srltest-attr', 'SRLT.OTHER', @d, 1, 1, TRUE);
                """, ct, ("d", RunDate.AddDays(-5)));

            await ExecAsync("""
                INSERT INTO trade_outcome (portfolio_id, ticker, entry_date, exit_date, pnl, exit_reason)
                VALUES ('srltest-attr', 'SRLT.OTHER', @e, @x, 0, 'srltest');
                """, ct, ("e", RunDate.AddDays(-20)), ("x", RunDate.AddDays(-3)));

            await ExecAsync("DELETE FROM attribution WHERE date = @d;", ct, ("d", RunDate));
            await RunGateAsync(ct);
            await RunAsync(ct);

            Assert.Equal("passed", await SurfacedGateStateAsync("SRLT.A", ct));
        }
        finally
        {
            await ExecAsync("DELETE FROM events WHERE ticker = 'SRLT.OTHER';", ct);
            await ExecAsync("DELETE FROM \"position\" WHERE ticker = 'SRLT.OTHER';", ct);
            await ExecAsync("DELETE FROM trade_outcome WHERE ticker = 'SRLT.OTHER';", ct);
            await ClearAsync(ct);
        }
    }

    // ------------------------------------------------------------- plumbing ---

    private static async Task RunAsync(CancellationToken ct)
        => await RunStageAsync(new CandidateAllocator(), ct);

    private static async Task RunGateAsync(CancellationToken ct)
    {
        // The fixture writes its own gate_result rows for the names it ranks, so running
        // C12 here would relabel them. It is run only where gate_state is the subject,
        // and the fixture rows are removed first so the stage's own labels stand.
        await ExecAsync("DELETE FROM gate_result WHERE date = @d;", ct, ("d", RunDate));
        await RunStageAsync(new GateEngine(), ct);
    }

    private static async Task RunStageAsync(IStage stage, CancellationToken ct)
    {
        var context = new StageContext(
            RunDate, 1,
            new StageData(TestDatabase.ConnectionString, new DeclaredAccess(stage)),
            new FrozenClock(RunDate),
            new ConfigStore(TestDatabase.ConnectionString));

        await stage.ExecuteAsync(context, ct).ConfigureAwait(true);
    }

    private static async Task SeedAsync(CancellationToken ct)
    {
        await ClearAsync(ct);
        await new ConfigSeeder(TestDatabase.ConnectionString).SeedAsync(ct).ConfigureAwait(true);

        foreach (var id in new[] { Live, Shadow })
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

        foreach (var id in new[] { "S1", "S2", "S3", "S4", "S5" })
        {
            await ExecAsync("""
                INSERT INTO config_rows (key, version, value, set_at, set_by)
                VALUES (@k, 2, '"retired"'::jsonb, TIMESTAMPTZ '2020-06-01 12:00:00Z', 'test')
                ON CONFLICT (key, version) DO UPDATE SET value = EXCLUDED.value;
                """, ct, ("k", $"screens.{id}.state"));
        }

        await ExecAsync("""
            INSERT INTO market_context_daily (date, regime_label) VALUES (@d, @r)
            ON CONFLICT (date) DO UPDATE SET regime_label = EXCLUDED.regime_label;
            """, ct, ("d", RunDate), ("r", Regime));
    }

    private static async Task RankAsync(
        string screenId, string ticker, string bucket, int? rank, double score, CancellationToken ct)
    {
        await ExecAsync("""
            INSERT INTO security_daily (ticker, date, sector, size_bucket, market_cap, is_active)
            VALUES (@t, @d, @sec, @b, 1000000000, TRUE)
            ON CONFLICT (ticker, date) DO UPDATE SET size_bucket = EXCLUDED.size_bucket, is_active = TRUE;
            """, ct, ("t", ticker), ("d", RunDate), ("sec", Sector), ("b", bucket));

        // A bar on the date and the session before it, at one price, so the name passes
        // C12's real gate where a test runs it: no halt and no gap. Every other fixture
        // here writes its own gate_result row and never runs C12, so the bars are inert
        // there.
        foreach (var on in new[] { RunDate.AddDays(-1), RunDate })
        {
            await ExecAsync("""
                INSERT INTO price_daily (ticker, date, open, high, low, close, adj_close, volume)
                VALUES (@t, @d, 100, 100, 100, 100, 100, 1000000)
                ON CONFLICT (ticker, date) DO NOTHING;
                """, ct, ("t", ticker), ("d", on));
        }

        await ExecAsync("""
            INSERT INTO gate_result (ticker, date, passed, reasons, gate_state)
            VALUES (@t, @d, TRUE, '{}', 'passed_partial')
            ON CONFLICT (ticker, date) DO UPDATE SET passed = TRUE, reasons = '{}';
            """, ct, ("t", ticker), ("d", RunDate));

        await ExecAsync("""
            INSERT INTO screen_score_daily (date, screen_id, ticker, score, rank_within_screen, config_version)
            VALUES (@d, @s, @t, @sc, @r, 1)
            ON CONFLICT (date, screen_id, ticker) DO UPDATE SET
                score = EXCLUDED.score,
                rank_within_screen = EXCLUDED.rank_within_screen;
            """, ct, ("d", RunDate), ("s", screenId), ("t", ticker),
            ("sc", (float) score), ("r", (object?) rank ?? DBNull.Value));
    }

    private static async Task GateAsync(string ticker, CancellationToken ct)
        => await ExecAsync(
            "UPDATE gate_result SET passed = FALSE, reasons = ARRAY['halt'] " +
            "WHERE ticker = @t AND date = @d;",
            ct, ("t", ticker), ("d", RunDate));

    private static async Task SetStateAsync(string screenId, string state, CancellationToken ct)
        => await ExecAsync(
            "UPDATE config_rows SET value = @v::jsonb WHERE key = @k AND version = 1;",
            ct, ("v", "\"" + state + "\""), ("k", $"screens.{screenId}.state"));

    private static async Task ClearAsync(CancellationToken ct)
    {
        await ExecAsync("DELETE FROM attribution WHERE date = @d;", ct, ("d", RunDate));
        await ExecAsync("DELETE FROM candidate_set WHERE date = @d;", ct, ("d", RunDate));
        await ExecAsync("DELETE FROM screen_score_daily WHERE date = @d;", ct, ("d", RunDate));
        await ExecAsync("DELETE FROM screen_history WHERE date = @d;", ct, ("d", RunDate));
        await ExecAsync("DELETE FROM gate_result WHERE date = @d;", ct, ("d", RunDate));
        await ExecAsync("DELETE FROM market_context_daily WHERE date = @d;", ct, ("d", RunDate));
        await ExecAsync("DELETE FROM price_daily WHERE ticker LIKE 'SRLT.%';", ct);
        await ExecAsync("DELETE FROM security_daily WHERE ticker LIKE 'SRLT.%';", ct);
        await ExecAsync("DELETE FROM config_rows WHERE key LIKE @a OR key LIKE @b;", ct,
            ("a", $"screens.{Live}.%"), ("b", $"screens.{Shadow}.%"));
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

    private static async Task<IReadOnlyList<string>> AttributedAsync(CancellationToken ct)
        => await TickersAsync(
            "SELECT ticker FROM attribution WHERE date = @d ORDER BY ticker COLLATE \"C\";", ct);

    private static async Task<IReadOnlyList<string>> CandidatesAsync(CancellationToken ct)
        => await TickersAsync(
            "SELECT ticker FROM candidate_set WHERE date = @d ORDER BY ticker COLLATE \"C\";", ct);

    private static async Task<IReadOnlyList<string>> StatesAsync(CancellationToken ct)
        => await TickersAsync("SELECT DISTINCT gate_state FROM attribution WHERE date = @d;", ct);

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

    private static async Task<string> ScorePerScreenAsync(string ticker, CancellationToken ct)
        => (string) (await ScalarAsync(
            "SELECT score_per_screen::text FROM attribution WHERE ticker = @t AND date = @d;", ticker, ct))!;

    private static async Task<string> SurfacedAsAsync(string ticker, CancellationToken ct)
        => (string) (await ScalarAsync(
            "SELECT surfaced_as FROM attribution WHERE ticker = @t AND date = @d;", ticker, ct))!;

    private static async Task<string> SurfacedGateStateAsync(string ticker, CancellationToken ct)
        => (string) (await ScalarAsync(
            "SELECT gate_state FROM attribution WHERE ticker = @t AND date = @d;", ticker, ct))!;

    private static async Task<IReadOnlyList<string>> SurfacingAsync(string ticker, CancellationToken ct)
        => (string[]) (await ScalarAsync(
            "SELECT screens_surfacing FROM attribution WHERE ticker = @t AND date = @d;", ticker, ct))!;

    private static async Task<IReadOnlyList<string>> CandidateSurfacingAsync(string ticker, CancellationToken ct)
        => (string[]) (await ScalarAsync(
            "SELECT screens_surfacing FROM candidate_set WHERE ticker = @t AND date = @d;", ticker, ct))!;

    private static async Task<object?> ScalarAsync(string sql, string ticker, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(sql, conn);

        cmd.Parameters.AddWithValue("t", ticker);
        cmd.Parameters.AddWithValue("d", RunDate);

        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true);
    }

    /// <summary>Per-file, as every other stage fixture in this suite keeps its own.</summary>
    private sealed class FrozenClock(DateOnly today) : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(today.Year, today.Month, today.Day, 21, 30, 0, TimeSpan.Zero);

        public DateOnly Today { get; } = today;
    }

}
