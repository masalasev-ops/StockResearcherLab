using System.Globalization;
using Npgsql;
using StockResearcherLab.Core;
using StockResearcherLab.Core.Gates;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;
using StockResearcherLab.Pipeline;
using StockResearcherLab.Pipeline.Select;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Select;

/// <summary>
/// Checkpoint 4.8. C12 labels every active member with every reason it is unavailable,
/// or with none.
///
/// **The gate labels and never narrows.** Every active member gets exactly one row, which
/// makes INVARIANT 1 checkable here as well as at C13. A gate that wrote rows only for
/// the names it rejected would be a filter wearing a label's clothes, and nothing
/// downstream would be able to tell the difference between a name that passed and a name
/// the gate never looked at.
///
/// **All five reasons are exercised against fabricated rows.** Three of them are
/// structurally unevaluable over the backfill window: `position` and `trade_outcome` hold
/// no rows until phase 7 and earnings are deliberately not backfilled [D-117]. Leaving
/// them untested because the tables are empty would mean phase 7 is the first time
/// anybody finds out whether they work, on live rows.
/// </summary>
[Collection("database")]
public sealed class GateEngineTests
{
    private static readonly DateOnly RunDate = new(2021, 5, 3);

    private const string Bucket = "srltest-gates";
    private const string Sector = "srltest-gates";

    /// <summary>A name that trades normally and is blocked by nothing.</summary>
    private const string Clean = "SRLGATE.CLEAN";

    /// <summary>A second name, spoiled per test.</summary>
    private const string Spoiled = "SRLGATE.SPOILED";

    private const decimal PreviousClose = 100m;

    // ------------------------------------------------------- labels, never narrows ---

    /// <summary>
    /// **Every active member has exactly one row.** The count is compared against
    /// <see cref="Universe.MembersAsOf"/> rather than against a literal, which is
    /// INVARIANT 1 checked mechanically rather than argued.
    /// </summary>
    [Fact]
    public async Task EveryActiveMemberHasExactlyOneRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            // Blocked for three reasons and still a row, which is the labelling rule.
            await SeedEarningsAsync(Spoiled, RunDate, ct);
            await SeedPositionAsync(Spoiled, RunDate.AddDays(-10), closed: null, isOpen: true, ct);

            await RunAsync(ct);

            Assert.Equal(await MemberCountAsync(ct), await RowCountAsync(ct));
            Assert.True(await RowExistsAsync(Clean, ct));
            Assert.True(await RowExistsAsync(Spoiled, ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// **`passed` is the empty reason array and not a separately maintained flag**, so
    /// the two cannot disagree. Asserted in both directions on one run.
    /// </summary>
    [Fact]
    public async Task PassedIsTheEmptyReasonArray()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await SeedEarningsAsync(Spoiled, RunDate, ct);
            await RunAsync(ct);

            Assert.True(await PassedAsync(Clean, ct));
            Assert.Empty(await ReasonsAsync(Clean, ct));

            Assert.False(await PassedAsync(Spoiled, ct));
            Assert.NotEmpty(await ReasonsAsync(Spoiled, ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// **Every failing reason is recorded rather than the first, in a fixed order.** The
    /// three chosen here are the three that cannot fire over the backfill window, which
    /// makes this the same fixture as the phase-7 exercise below.
    ///
    /// Earnings, already held and cooldown rather than a gap or a halt, because those two
    /// are mutually exclusive on one name: a gap needs an opening price and a halt is the
    /// absence of a bar.
    /// </summary>
    [Fact]
    public async Task ANameFailingThreeReasonsCarriesThreeInAFixedOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await SeedEarningsAsync(Spoiled, RunDate.AddDays(2), ct);
            await SeedPositionAsync(Spoiled, RunDate.AddDays(-10), closed: null, isOpen: true, ct);
            await SeedExitAsync(Spoiled, RunDate.AddDays(-5), ct);

            await RunAsync(ct);

            Assert.Equal(
                ["earnings_blackout", "already_held", "cooldown"],
                await ReasonsAsync(Spoiled, ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    // ------------------------------------------------------- the five conditions ---

    /// <summary>
    /// The blackout reaches <c>gates.earnings_blackout_days_before</c> days forward and
    /// <c>gates.earnings_blackout_days_after</c> days back, and both bounds are asserted
    /// on the day they hold and the day after.
    ///
    /// The window's name is what it is measured on: five days **before** earnings is an
    /// earnings date up to five days ahead of tonight, which is the reading that is easy
    /// to get backwards and impossible to notice once it is.
    /// </summary>
    [Theory]
    [InlineData(5, true)]
    [InlineData(6, false)]
    [InlineData(-2, true)]
    [InlineData(-3, false)]
    public async Task TheEarningsBlackoutReachesForwardFiveAndBackTwo(int offset, bool gated)
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await SeedEarningsAsync(Spoiled, RunDate.AddDays(offset), ct);
            await RunAsync(ct);

            Assert.Equal(gated, (await ReasonsAsync(Spoiled, ct)).Contains("earnings_blackout"));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// **A gap above the threshold fires in either direction.** A name that gapped down
    /// eleven percent is as unenterable at tomorrow's open as one that gapped up, and §03
    /// names the reason without a sign.
    ///
    /// The bound is exclusive, so exactly eight percent does not fire. That is asserted
    /// rather than left to whichever comparison the statement happens to carry.
    /// </summary>
    [Theory]
    [InlineData(109.0, true)]
    [InlineData(91.0, true)]
    [InlineData(108.0, false)]
    [InlineData(92.0, false)]
    [InlineData(103.0, false)]
    public async Task AGapAboveTheThresholdFiresInEitherDirection(double open, bool gated)
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await SetOpenAsync(Spoiled, (decimal) open, ct);
            await RunAsync(ct);

            Assert.Equal(gated, (await ReasonsAsync(Spoiled, ct)).Contains("gap"));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// A halt is a name that did not trade this session, which `price_daily` says in one
    /// of two ways: no bar at all, or a bar with no volume.
    ///
    /// **This reading is unauthored** and is reported at 4.8 rather than decided. Nothing
    /// in the corpus defines the condition operationally; `CONFIG_REFERENCE.md` says only
    /// that it carries no threshold key because a name is halted or it is not, and §3
    /// gives C12 one price source.
    /// </summary>
    [Fact]
    public async Task AHaltIsANameThatDidNotTradeThisSession()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await RunAsync(ct);
            Assert.DoesNotContain("halt", await ReasonsAsync(Spoiled, ct));

            await ExecAsync(
                "UPDATE price_daily SET volume = 0 WHERE ticker = @t AND date = @d;",
                ct, ("t", Spoiled), ("d", RunDate));
            await RunAsync(ct);
            Assert.Contains("halt", await ReasonsAsync(Spoiled, ct));

            await ExecAsync(
                "DELETE FROM price_daily WHERE ticker = @t AND date = @d;",
                ct, ("t", Spoiled), ("d", RunDate));
            await RunAsync(ct);
            Assert.Contains("halt", await ReasonsAsync(Spoiled, ct));

            // And still a row, which is the labelling rule holding for the name with the
            // least data of any in the fixture.
            Assert.True(await RowExistsAsync(Spoiled, ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// **Already-held is read from the position's dates and not from `is_open`.**
    ///
    /// This is the assertion that matters and it is the one that would pass on every live
    /// night while being wrong on every historical one. `is_open` is a flag standing for
    /// today; asking it "was this name held on 2021-05-03" gets an answer about now. The
    /// fixture is a position opened before the date and closed after it, with `is_open`
    /// false, which is exactly what a closed historical holding looks like. It gates.
    ///
    /// This is D-92's defect, arriving in a second component.
    /// </summary>
    [Fact]
    public async Task AlreadyHeldIsReadFromTheDatesAndNotFromIsOpen()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await SeedPositionAsync(
                Spoiled, RunDate.AddDays(-20), closed: RunDate.AddDays(20), isOpen: false, ct);

            await RunAsync(ct);
            Assert.Contains("already_held", await ReasonsAsync(Spoiled, ct));

            // Closed before the date, so not held on it, whatever the flag says.
            await ExecAsync(
                "UPDATE \"position\" SET closed_date = @c, is_open = TRUE WHERE ticker = @t;",
                ct, ("c", RunDate.AddDays(-1)), ("t", Spoiled));

            await RunAsync(ct);
            Assert.DoesNotContain("already_held", await ReasonsAsync(Spoiled, ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// The cooldown counts calendar days back from the exit and its far bound is
    /// exclusive, so an exit thirty days ago no longer bites and one twenty-nine days ago
    /// still does.
    ///
    /// **Thirty calendar days is shorter than D-34's forty-trading-day time stop**, so a
    /// name can be re-surfaced before a position that ran its full stop would have
    /// closed. That was seen and accepted when the value was set.
    /// </summary>
    [Theory]
    [InlineData(-1, true)]
    [InlineData(-29, true)]
    [InlineData(-30, false)]
    [InlineData(-31, false)]
    public async Task TheCooldownCountsCalendarDaysFromTheExit(int offset, bool gated)
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await SeedExitAsync(Spoiled, RunDate.AddDays(offset), ct);
            await RunAsync(ct);

            Assert.Equal(gated, (await ReasonsAsync(Spoiled, ct)).Contains("cooldown"));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    // ---------------------------------------------------- the closed vocabulary ---

    /// <summary>
    /// **The vocabulary is closed by the database and not only by the enum.** A closed
    /// enum closes what this system writes and leaves the column able to hold a string no
    /// reader can interpret; `GateReasons.Parse` would then throw at read time, one night
    /// later and one component away from whatever wrote it.
    /// </summary>
    [Fact]
    public async Task AReasonOutsideTheVocabularyFailsTheInsert()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(true);

        await using var cmd = new NpgsqlCommand(
            "INSERT INTO gate_result (ticker, date, passed, reasons) " +
            "VALUES ('SRLGATE.BOGUS', DATE '2021-05-03', FALSE, ARRAY['not_a_reason']);", conn, tx);

        var thrown = await Assert.ThrowsAsync<PostgresException>(
            () => cmd.ExecuteNonQueryAsync(ct));

        Assert.Equal("gate_result_reasons_vocabulary", thrown.ConstraintName);

        await tx.RollbackAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// The constraint and the enum hold the same five names, read out of the catalogue
    /// rather than trusted. The list is duplicated deliberately, a migration built from a
    /// list in code being a migration whose recorded hash changes with a rebuild, so what
    /// stops the copy drifting is this.
    /// </summary>
    [Fact]
    public async Task TheConstraintAndTheEnumHoldTheSameVocabulary()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(
            "SELECT pg_get_constraintdef(oid) FROM pg_constraint " +
            "WHERE conname = 'gate_result_reasons_vocabulary';", conn);

        var definition = (string?) await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true);

        Assert.NotNull(definition);

        foreach (var name in GateReasons.Names)
        {
            Assert.Contains("'" + name + "'", definition, StringComparison.Ordinal);
        }

        // And nothing else: five quoted strings in the constraint, five in the enum.
        Assert.Equal(
            GateReasons.Names.Count,
            definition.Count(c => c == '\'') / 2);
    }

    /// <summary>Reading a reason outside the vocabulary fails closed rather than defaulting.</summary>
    [Fact]
    public void ParsingAReasonOutsideTheVocabularyThrows()
    {
        Assert.Throws<InvalidOperationException>(() => GateReasons.Parse("not_a_reason"));

        foreach (var name in GateReasons.Names)
        {
            Assert.Equal(name, GateReasons.Name(GateReasons.Parse(name)));
        }

        Assert.Equal(
            ["earnings_blackout", "gap", "halt", "already_held", "cooldown"],
            GateReasons.Names);
    }

    // ------------------------------------------------------------- the stage ---

    /// <summary>C12 is the sole declared writer of `gate_result` [INVARIANT 10].</summary>
    [Fact]
    public void C12IsTheSoleDeclaredWriterOfGateResult()
    {
        var writers = PipelineComposition
            .AllOwnersForConformance(TestDatabase.ConnectionString)
            .Where(o => o.WriteSet.Any(w => w.Table == "gate_result"))
            .Select(o => o.Name)
            .ToList();

        Assert.Equal(["GateEngine"], writers);
    }

    /// <summary>
    /// Two runs over one date write the same rows, which is what makes the night
    /// replayable [`CLAUDE.md` §6]. The statement is asserted byte-identical too, so a
    /// clock read or an unordered enumeration reaching it would fail here rather than in
    /// a diff of two nights' output.
    /// </summary>
    [Fact]
    public async Task TwoRunsOverOneDateAreIdentical()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await SeedEarningsAsync(Spoiled, RunDate.AddDays(1), ct);

            await RunAsync(ct);
            var first = await ReasonsAsync(Spoiled, ct);

            await RunAsync(ct);

            Assert.Equal(first, await ReasonsAsync(Spoiled, ct));
            Assert.Equal(await MemberCountAsync(ct), await RowCountAsync(ct));

            var thresholds = new GateEngine.GateThresholds(8, 5, 2, 30);
            Assert.Equal(GateEngine.Sql(RunDate, thresholds), GateEngine.Sql(RunDate, thresholds));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// The statement reads no screen store, and that absence is load-bearing [D-117].
    /// C13 does not read `gate_result` and C12 does not read `screen_score_daily`: a
    /// floor drawn over the ungated subset would move when a position opens, which makes
    /// a screen's floor a function of the portfolio.
    /// </summary>
    [Fact]
    public void TheStatementReadsNoScreenStore()
    {
        var sql = GateEngine.Sql(RunDate, new GateEngine.GateThresholds(8, 5, 2, 30));

        Assert.DoesNotContain("screen_score_daily", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("screen_history", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("candidate_set", sql, StringComparison.Ordinal);

        Assert.DoesNotContain("screen_score_daily", new GateEngine().ReadSet);
    }

    // ------------------------------------------------------------- plumbing ---

    private static async Task RunAsync(CancellationToken ct)
    {
        var stage = new GateEngine();

        var context = new StageContext(
            RunDate, 1,
            new StageData(TestDatabase.ConnectionString, new DeclaredAccess(stage)),
            new FrozenClock(RunDate),
            new ConfigStore(TestDatabase.ConnectionString));

        await stage.ExecuteAsync(context, ct).ConfigureAwait(true);
    }

    /// <summary>
    /// Two members, both trading normally: a bar on the run date opening at the previous
    /// session's close, and a previous session to compare it against.
    /// </summary>
    private static async Task SeedAsync(CancellationToken ct)
    {
        await ClearAsync(ct);
        await new ConfigSeeder(TestDatabase.ConnectionString).SeedAsync(ct).ConfigureAwait(true);

        foreach (var ticker in new[] { Clean, Spoiled })
        {
            await ExecAsync("""
                INSERT INTO security_daily (ticker, date, sector, size_bucket, market_cap, is_active)
                VALUES (@t, @d, @sec, @b, 3200000000, TRUE)
                ON CONFLICT (ticker, date) DO UPDATE SET is_active = TRUE;
                """, ct, ("t", ticker), ("d", RunDate), ("sec", Sector), ("b", Bucket));

            await InsertBarAsync(ticker, RunDate.AddDays(-1), PreviousClose, PreviousClose, ct);
            await InsertBarAsync(ticker, RunDate, PreviousClose, PreviousClose, ct);
        }
    }

    private static async Task InsertBarAsync(
        string ticker, DateOnly date, decimal open, decimal close, CancellationToken ct)
        => await ExecAsync("""
            INSERT INTO price_daily (ticker, date, open, high, low, close, adj_close, volume)
            VALUES (@t, @d, @o, @c, @o, @c, @c, 1000000)
            ON CONFLICT (ticker, date) DO UPDATE SET
                open = EXCLUDED.open, close = EXCLUDED.close, volume = EXCLUDED.volume;
            """, ct, ("t", ticker), ("d", date), ("o", open), ("c", close));

    private static async Task SetOpenAsync(string ticker, decimal open, CancellationToken ct)
        => await ExecAsync(
            "UPDATE price_daily SET open = @o WHERE ticker = @t AND date = @d;",
            ct, ("o", open), ("t", ticker), ("d", RunDate));

    private static async Task SeedEarningsAsync(string ticker, DateOnly on, CancellationToken ct)
        => await ExecAsync(
            "INSERT INTO events (ticker, event_type, event_date) VALUES (@t, 'earnings', @d);",
            ct, ("t", ticker), ("d", on));

    private static async Task SeedPositionAsync(
        string ticker, DateOnly opened, DateOnly? closed, bool isOpen, CancellationToken ct)
        => await ExecAsync("""
            INSERT INTO "position" (portfolio_id, ticker, opened_date, closed_date, quantity,
                entry_price, is_open)
            VALUES ('srltest', @t, @o, @c, 100, 100, @open);
            """, ct, ("t", ticker), ("o", opened),
            ("c", (object?) closed ?? DBNull.Value), ("open", isOpen));

    private static async Task SeedExitAsync(string ticker, DateOnly exit, CancellationToken ct)
        => await ExecAsync("""
            INSERT INTO trade_outcome (portfolio_id, ticker, entry_date, exit_date, pnl, exit_reason)
            VALUES ('srltest', @t, @e, @x, 0, 'srltest');
            """, ct, ("t", ticker), ("e", exit.AddDays(-10)), ("x", exit));

    private static async Task ClearAsync(CancellationToken ct)
    {
        var tickers = new[] { Clean, Spoiled };

        await ExecAsync("DELETE FROM gate_result WHERE date = @d;", ct, ("d", RunDate));
        await ExecAsync("DELETE FROM events WHERE ticker = ANY(@t);", ct, ("t", tickers));
        await ExecAsync("DELETE FROM \"position\" WHERE ticker = ANY(@t);", ct, ("t", tickers));
        await ExecAsync("DELETE FROM trade_outcome WHERE ticker = ANY(@t);", ct, ("t", tickers));
        await ExecAsync("DELETE FROM price_daily WHERE ticker = ANY(@t);", ct, ("t", tickers));
        await ExecAsync("DELETE FROM security_daily WHERE ticker = ANY(@t);", ct, ("t", tickers));
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

    private static async Task<IReadOnlyList<string>> ReasonsAsync(string ticker, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(
            "SELECT reasons FROM gate_result WHERE ticker = @t AND date = @d;", conn);

        cmd.Parameters.AddWithValue("t", ticker);
        cmd.Parameters.AddWithValue("d", RunDate);

        var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true);

        Assert.False(value is null, $"{ticker} has no gate_result row, and the gate labels every member.");

        // Read back through the vocabulary rather than as raw strings, so a stored
        // reason nothing recognises fails here rather than being compared as text.
        return [.. ((string[]) value!).Select(n => GateReasons.Name(GateReasons.Parse(n)))];
    }

    private static async Task<bool> PassedAsync(string ticker, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(
            "SELECT passed FROM gate_result WHERE ticker = @t AND date = @d;", conn);

        cmd.Parameters.AddWithValue("t", ticker);
        cmd.Parameters.AddWithValue("d", RunDate);

        return (bool) (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true))!;
    }

    private static async Task<bool> RowExistsAsync(string ticker, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM gate_result WHERE ticker = @t AND date = @d;", conn);

        cmd.Parameters.AddWithValue("t", ticker);
        cmd.Parameters.AddWithValue("d", RunDate);

        return (long) (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true))! == 1;
    }

    private static async Task<long> RowCountAsync(CancellationToken ct)
        => await ScalarAsync("SELECT count(*) FROM gate_result WHERE date = @d;", ct);

    private static async Task<long> MemberCountAsync(CancellationToken ct)
        => await ScalarAsync(
            $"SELECT count(*) FROM ({Universe.MembersAsOf(RunDate).TrimEnd(';')}) x;", ct);

    private static async Task<long> ScalarAsync(string sql, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(sql, conn);

        if (sql.Contains("@d", StringComparison.Ordinal))
        {
            cmd.Parameters.AddWithValue("d", RunDate);
        }

        return Convert.ToInt64(
            await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true), CultureInfo.InvariantCulture);
    }

    /// <summary>Per-file, as every other stage fixture in this suite keeps its own.</summary>
    private sealed class FrozenClock(DateOnly today) : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(today.Year, today.Month, today.Day, 21, 30, 0, TimeSpan.Zero);

        public DateOnly Today { get; } = today;
    }
}
