using System.Globalization;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data.Eodhd;

namespace StockResearcherLab.Pipeline.Ingest;

/// <summary>
/// The half of a backfill pool that every sweep able to reach delisted names shares
/// [D-101].
///
/// **Stated once because three pools with two definitions is the shape this phase has
/// found every silent hole in.** C03's pool was widened at 3.7 and C04's and C06's at
/// 3.8 and 3.10, and a rule that has to be looked up per component is one a later
/// session gets wrong. The live half differs legitimately between them, C03 taking the
/// candidate pool and the other two the universe; the delisted half is one set and is
/// derived here.
///
/// **C05 cannot use this and that is not an omission.** `sec-filings` answers 404 for a
/// delisted ticker against the same string `eod/{t}` and `fundamentals/{t}` return
/// series for, so the bias there is a fact about the provider rather than a choice
/// [open item 12].
/// </summary>
public static class BackfillPool
{
    /// <summary>
    /// Every admitted delisted common stock carrying a <c>price_daily</c> bar at or
    /// after <paramref name="windowStart"/>.
    ///
    /// Names that stopped trading before the window are excluded and cost nothing. The
    /// symbol list carries no delisting date, so the last bar is the only date there is
    /// and 3.6 having loaded prices first is what makes the set computable at all.
    ///
    /// **The caller declares `price_daily`**, which is what puts the read in its §3
    /// Reads cell rather than hiding it behind a helper [D-101].
    ///
    /// **The statement carries the pool bound, and it did not when this helper was
    /// extracted** [D-102]. C03 gives the identical statement
    /// `universe.pool_statement_timeout_seconds` and records at its own call site that
    /// leaving it on the connection string's `Command Timeout` had already failed a sweep
    /// once. Extracting the shared half brought the SQL across and left the bound behind,
    /// so the two diverged again: 3.8's first day ran it warm at 3.1s and passed, and its
    /// second day timed out at 300 on a cold cache before a ticker was dispatched. One
    /// statement, one bound, resolved from the one key.
    /// </summary>
    public static async Task<IReadOnlySet<string>> DelistedWithBarsInWindowAsync(
        EodhdClient client, StageContext context, DateOnly windowStart, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var statementTimeout = await StatementTimeoutAsync(context, ct).ConfigureAwait(false);

        var delisted = await SymbolList.AdmittedDelistedAsync(client, ct).ConfigureAwait(false);

        var from = windowStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var traded = await context.Data.ReadAsync(
            "price_daily",
            $"""
             SELECT DISTINCT ticker FROM price_daily
             WHERE date >= DATE '{from}'
             ORDER BY ticker;
             """,
            ct, statementTimeout).ConfigureAwait(false);

        var inWindow = traded.Select(r => (string) r[0]!).ToHashSet(StringComparer.Ordinal);

        return delisted.Keys.Where(inWindow.Contains).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The precondition every range pool sourcing its live half from `security_daily`
    /// carries: that table has a row the range can read.
    ///
    /// **It throws rather than halting, and the difference is what the operator does
    /// next.** A halt means the allowance ran out and tomorrow's budget resolves it, so
    /// the sweep is re-invoked unchanged. A missing precondition needs another checkpoint
    /// to run first and tomorrow changes nothing, so re-invoking is the one thing that
    /// cannot help. The three exit codes already carry that distinction: 2 is the gate, 1
    /// is a fault, and this is a fault.
    ///
    /// **What it is written against.** 3.8's second day swept a pool of 16,861 where its
    /// first day read 19,706, reported Completed, and left 678 universe members with no
    /// attempt row. `security_daily` held no rows, C01 having stopped writing
    /// `security.is_active` at 3.11 [D-92] while 3.12 moved every reader onto the new
    /// table and the checkpoint that fills it had not run. Nothing errored, which is the
    /// shape `CLAUDE.md` §1 describes rather than an ordinary bug.
    ///
    /// **The condition is a row at or before the range end**, which is exactly when the
    /// live half can be non-empty, and no more than that. A stronger form would have to
    /// assert how far the cadence may lag the range, and 3.11's own first date is
    /// 2021-01-10 against a window opening 2021-01-04, so "a row at or before the range
    /// start" would fail on the data this is written to accept.
    /// </summary>
    public static async Task RequireUniverseCoverageAsync(
        StageContext context, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var rows = await context.Data.ReadAsync(
            "security_daily",
            $"""
             SELECT count(*), coalesce(min(date)::text, '-'), coalesce(max(date)::text, '-')
             FROM security_daily
             WHERE date <= DATE '{Iso(to)}';
             """,
            ct).ConfigureAwait(false);

        var covered = Convert.ToInt64(rows[0][0], CultureInfo.InvariantCulture);

        if (covered > 0)
        {
            return;
        }

        // The span is reported even though it is empty on this side of the branch,
        // because the whole-table span is what tells an operator whether the table is
        // unfilled or merely filled later than the range asked for.
        var spanRows = await context.Data.ReadAsync(
            "security_daily",
            "SELECT count(*), coalesce(min(date)::text, '-'), coalesce(max(date)::text, '-') FROM security_daily;",
            ct).ConfigureAwait(false);

        throw new InvalidOperationException(
            $"security_daily carries no row at or before {Iso(to)}, so a range pool over " +
            $"{Iso(from)}..{Iso(to)} would take an empty live half and sweep the delisted names alone. " +
            $"The table holds {Convert.ToInt64(spanRows[0][0], CultureInfo.InvariantCulture)} row(s) " +
            $"spanning {spanRows[0][1]}..{spanRows[0][2]}. " +
            "UniverseBuilder's range mode is what fills it [checkpoint 3.11]. This throws rather than " +
            "halting: a halt is resolved by tomorrow's allowance and this is not.");
    }

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// Resolved as of the context's date rather than as of now, which is what every other
    /// reader of this key does [INVARIANT 13].
    /// </summary>
    private static async Task<int> StatementTimeoutAsync(StageContext context, CancellationToken ct)
    {
        const string Key = "universe.pool_statement_timeout_seconds";

        var row = await context.Config.RequireAsync(Key, context.Date, ct).ConfigureAwait(false);

        return int.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new InvalidOperationException($"{Key} resolved to '{row.Value}', which is not a whole number.");
    }
}
