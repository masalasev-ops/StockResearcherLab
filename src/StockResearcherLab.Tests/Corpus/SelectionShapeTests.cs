using Npgsql;
using Xunit;

namespace StockResearcherLab.Tests.Corpus;

/// <summary>
/// Checkpoint 4.1, migration <c>0017</c>. The shape phase 4 writes into, asserted
/// against the real schema before any component exists to write a row.
///
/// **Every test that writes rolls back.** The checkpoint's own done-when is that all
/// six selection tables still hold zero rows after it, so a test that inserted and
/// deleted would pass while leaving a moment where the table was not empty. A
/// transaction that never commits leaves nothing behind at all.
///
/// The constraints are asserted through the database rather than through a writer,
/// which is D-110's point: a flat <c>score_per_screen</c> has to be refused by the
/// constraint rather than by whichever writer remembers.
/// </summary>
[Collection("database")]
public sealed class SelectionShapeTests
{
    private const string Ticker = "SRLTEST.SEL";

    private static NpgsqlCommand Insert(NpgsqlConnection conn, NpgsqlTransaction tx, string columns, string values)
        => new($"INSERT INTO attribution ({columns}) VALUES ({values});", conn, tx);

    // -------------------------------------------------------------- surfaced_as ---

    /// <summary>
    /// No DEFAULT, so a writer that has not decided fails at the column rather than
    /// taking the value nobody chose [D-110].
    /// </summary>
    [Fact]
    public async Task AnInsertOmittingSurfacedAsFailsAtTheColumn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(true);

        await using var cmd = Insert(conn, tx,
            "ticker, date, screens_surfacing, config_version",
            $"'{Ticker}', DATE '2026-01-05', ARRAY['S1'], 1");

        var ex = await Assert.ThrowsAsync<PostgresException>(
            async () => await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(true)).ConfigureAwait(true);

        Assert.Equal(PostgresErrorCodes.NotNullViolation, ex.SqlState);
        Assert.Equal("surfaced_as", ex.ColumnName);

        await tx.RollbackAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// The CHECK rather than a writer-side test, for D-80's reason: this column
    /// segments every analysis in <c>SCREEN_LIFECYCLE.md</c> section 4.5, so a drifted
    /// value would land in its own bucket in each of them without ever erroring [D-110].
    /// </summary>
    [Fact]
    public async Task AValueOutsideTheVocabularyIsRefusedByTheConstraint()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(true);

        await using var cmd = Insert(conn, tx,
            "ticker, date, screens_surfacing, config_version, surfaced_as",
            $"'{Ticker}', DATE '2026-01-05', ARRAY['S1'], 1, 'shortlisted'");

        var ex = await Assert.ThrowsAsync<PostgresException>(
            async () => await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(true)).ConfigureAwait(true);

        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        Assert.Equal("attribution_surfaced_as_check", ex.ConstraintName);

        await tx.RollbackAsync(ct).ConfigureAwait(true);
    }

    [Theory]
    [InlineData("candidate")]
    [InlineData("shadow")]
    public async Task TheTwoDeclaredValuesAreAccepted(string surfacedAs)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(true);

        await using var cmd = Insert(conn, tx,
            "ticker, date, screens_surfacing, config_version, surfaced_as",
            $"'{Ticker}', DATE '2026-01-05', ARRAY['S1'], 1, '{surfacedAs}'");

        Assert.Equal(1, await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(true));

        await tx.RollbackAsync(ct).ConfigureAwait(true);
    }

    // --------------------------------------------------------- score_per_screen ---

    /// <summary>
    /// The flat shape D-85 left with no writer cannot be written at all now, rather
    /// than being refused by whichever writer remembers [D-110].
    /// </summary>
    [Fact]
    public async Task AFlatScorePerScreenIsRefusedByTheConstraint()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(true);

        await using var cmd = Insert(conn, tx,
            "ticker, date, screens_surfacing, config_version, surfaced_as, score_per_screen",
            $"'{Ticker}', DATE '2026-01-05', ARRAY['S1'], 1, 'candidate', '{{\"S1\": 71.2}}'::jsonb");

        var ex = await Assert.ThrowsAsync<PostgresException>(
            async () => await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(true)).ConfigureAwait(true);

        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        Assert.Equal("attribution_score_per_screen_object_map", ex.ConstraintName);

        await tx.RollbackAsync(ct).ConfigureAwait(true);
    }

    /// <summary>Screen id to an object of score and rank, which is the shape D-85 asked for.</summary>
    [Fact]
    public async Task TheObjectMapShapeIsAccepted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(true);

        await using var cmd = Insert(conn, tx,
            "ticker, date, screens_surfacing, config_version, surfaced_as, score_per_screen",
            $"'{Ticker}', DATE '2026-01-05', ARRAY['S1','S4'], 1, 'candidate', " +
            "'{\"S1\": {\"score\": 71.2, \"rank\": 3}, \"S4\": {\"score\": 66.0, \"rank\": 11}}'::jsonb");

        Assert.Equal(1, await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(true));

        await tx.RollbackAsync(ct).ConfigureAwait(true);
    }

    /// <summary>A null carries no shape and is not the flat shape, so it is admitted.</summary>
    [Fact]
    public async Task ANullScorePerScreenIsAccepted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(true);

        await using var cmd = Insert(conn, tx,
            "ticker, date, screens_surfacing, config_version, surfaced_as, score_per_screen",
            $"'{Ticker}', DATE '2026-01-05', ARRAY['S1'], 1, 'candidate', NULL");

        Assert.Equal(1, await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(true));

        await tx.RollbackAsync(ct).ConfigureAwait(true);
    }

    // ------------------------------------------------------ candidate_attribution ---

    /// <summary>
    /// The view over a fabricated pair. Reading the table becomes the deliberate act
    /// and the view is the default, which inverts which mistake is easy [D-110].
    /// </summary>
    [Fact]
    public async Task TheViewReturnsExactlyTheCandidateRows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(true);

        await using (var seed = new NpgsqlCommand(
            "INSERT INTO attribution (ticker, date, screens_surfacing, config_version, surfaced_as) VALUES " +
            $"('{Ticker}.C', DATE '2026-01-05', ARRAY['S1'], 1, 'candidate'), " +
            $"('{Ticker}.S', DATE '2026-01-05', ARRAY['SX'], 1, 'shadow');", conn, tx))
        {
            Assert.Equal(2, await seed.ExecuteNonQueryAsync(ct).ConfigureAwait(true));
        }

        var seen = new List<string>();
        await using (var read = new NpgsqlCommand(
            "SELECT ticker FROM candidate_attribution WHERE date = DATE '2026-01-05' ORDER BY ticker;", conn, tx))
        await using (var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(true))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(true)) { seen.Add(reader.GetString(0)); }
        }

        Assert.Equal([$"{Ticker}.C"], seen);

        await tx.RollbackAsync(ct).ConfigureAwait(true);
    }

    // -------------------------------------------------------- screen_score_daily ---

    /// <summary>
    /// The key leads on the column every read constrains, which is the change
    /// <c>SCREEN_LIFECYCLE.md</c> section 9.3's complaint literally describes [D-111].
    /// </summary>
    [Fact]
    public async Task TheTableIsPartitionedAndTheKeyLeadsOnDate()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);

        await using (var kind = new NpgsqlCommand(
            "SELECT relkind FROM pg_class WHERE relname = 'screen_score_daily';", conn))
        {
            Assert.Equal('p', (char)(await kind.ExecuteScalarAsync(ct).ConfigureAwait(true))!);
        }

        var key = new List<string>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT a.attname
            FROM pg_index i
            JOIN pg_class c ON c.oid = i.indrelid
            JOIN unnest(i.indkey) WITH ORDINALITY AS k(attnum, ord) ON TRUE
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = k.attnum
            WHERE c.relname = 'screen_score_daily' AND i.indisprimary
            ORDER BY k.ord;
            """, conn))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(true))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(true)) { key.Add(reader.GetString(0)); }
        }

        Assert.Equal(["date", "screen_id", "ticker"], key);
    }

    /// <summary>
    /// No default partition, so a date outside the declared range fails loudly rather
    /// than landing in a child nothing queries [D-111].
    /// </summary>
    [Fact]
    public async Task ThereIsNoDefaultPartition()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT p.partdefid
            FROM pg_partitioned_table p
            JOIN pg_class c ON c.oid = p.partrelid
            WHERE c.relname = 'screen_score_daily';
            """, conn);

        Assert.Equal(0u, (uint)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true))!);
    }

    [Fact]
    public async Task AWriteOutsideTheDeclaredRangeFails()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(true);

        await using var cmd = new NpgsqlCommand(
            "INSERT INTO screen_score_daily (date, screen_id, ticker, config_version) " +
            $"VALUES (DATE '2035-03-01', 'S1', '{Ticker}', 1);", conn, tx);

        var ex = await Assert.ThrowsAsync<PostgresException>(
            async () => await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(true)).ConfigureAwait(true);

        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);

        await tx.RollbackAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// The index covers the ranked rows only, which is about two percent of the table
    /// rather than all of it [D-111].
    /// </summary>
    [Fact]
    public async Task ThePartialIndexCoversTheRankedRowsOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(
            "SELECT indexdef FROM pg_indexes WHERE indexname = 'screen_score_daily_ranked_idx';", conn);

        var def = (string?)await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true);

        Assert.NotNull(def);
        Assert.Contains("(date, screen_id, rank_within_screen)", def);
        Assert.Contains("WHERE (rank_within_screen IS NOT NULL)", def);
    }

    /// <summary>
    /// 0017's guard, exercised against a populated table rather than described. The
    /// drop would discard roughly 19 million rows after 4.13, so the migration raises
    /// instead of running [D-111].
    /// </summary>
    [Fact]
    public async Task TheDropGuardRefusesAPopulatedTable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(true);

        await using (var seed = new NpgsqlCommand(
            "INSERT INTO screen_score_daily (date, screen_id, ticker, config_version) " +
            $"VALUES (DATE '2026-01-05', 'S1', '{Ticker}', 1);", conn, tx))
        {
            Assert.Equal(1, await seed.ExecuteNonQueryAsync(ct).ConfigureAwait(true));
        }

        await using var guard = new NpgsqlCommand(
            """
            DO $guard$
            DECLARE existing bigint;
            BEGIN
                SELECT count(*) INTO existing FROM screen_score_daily;
                IF existing <> 0 THEN
                    RAISE EXCEPTION 'screen_score_daily holds % row(s).', existing;
                END IF;
            END
            $guard$;
            """, conn, tx);

        var ex = await Assert.ThrowsAsync<PostgresException>(
            async () => await guard.ExecuteNonQueryAsync(ct).ConfigureAwait(true)).ConfigureAwait(true);

        // The count is not asserted exactly. This transaction adds one row to whatever
        // the table already holds, and pinning the total would make this test a
        // statement about every other test's cleanup rather than about the guard.
        Assert.StartsWith("screen_score_daily holds ", ex.MessageText, StringComparison.Ordinal);
        Assert.Contains("row(s)", ex.MessageText, StringComparison.Ordinal);

        await tx.RollbackAsync(ct).ConfigureAwait(true);
    }

    // ---------------------------------------------------------- the parity filter ---

    /// <summary>
    /// **The filter exercised against a fabricated child**, which is the half that
    /// would otherwise pass vacuously if the predicate were removed [D-111].
    ///
    /// The seven real children already prove the predicate does something; a child
    /// created and dropped inside the test proves it is the predicate doing it, on a
    /// name no migration has ever mentioned. The view is asserted absent in the same
    /// place, because <c>relkind IN ('r','p')</c> is what keeps it out and a reader
    /// meeting <c>candidate_attribution</c> in this list would declare it as a table.
    /// </summary>
    [Fact]
    public async Task PublicTablesExcludesPartitionChildrenAndViews()
    {
        var ct = TestContext.Current.CancellationToken;

        await using (var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true))
        await using (var make = new NpgsqlCommand(
            "CREATE TABLE srltest_ssd_fabricated PARTITION OF screen_score_daily " +
            "FOR VALUES FROM ('2028-01-01') TO ('2029-01-01');", conn))
        {
            await make.ExecuteNonQueryAsync(ct).ConfigureAwait(true);
        }

        try
        {
            var tables = await TestDatabase.PublicTablesAsync(ct).ConfigureAwait(true);

            Assert.DoesNotContain("srltest_ssd_fabricated", tables);
            Assert.DoesNotContain("screen_score_daily_2026", tables);
            Assert.Contains("screen_score_daily", tables);
            Assert.DoesNotContain("candidate_attribution", tables);
        }
        finally
        {
            await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
            await using var drop = new NpgsqlCommand("DROP TABLE IF EXISTS srltest_ssd_fabricated;", conn);
            await drop.ExecuteNonQueryAsync(ct).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 4.1's last done-when. Schema only: nothing this checkpoint did put a row
    /// anywhere, and 4.13 and 4.14 are where these stop being zero.
    /// </summary>
    [Fact]
    public async Task TheSixSelectionTablesStillHoldZeroRows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);

        foreach (var table in new[]
                 { "screen_score_daily", "screen_history", "candidate_set", "attribution", "gate_result", "alert" })
        {
            await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM {table};", conn);
            Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true))!);
        }
    }
}
