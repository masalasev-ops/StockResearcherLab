using Npgsql;
using StockResearcherLab.Pipeline.Ingest;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Ingest;

/// <summary>
/// D-62 and INVARIANT 12. No fundamental value is readable before its effective
/// filing date.
///
/// Period end is the natural-looking key and hands you quarterly numbers weeks
/// before they were public. The quality and mean reversion screens would look
/// excellent in backfill and ordinary live, which is the silent-failure class this
/// system is built around rather than an error anyone would see.
/// </summary>
public sealed class FilingDateRuleTests
{
    private static RawPeriod P(string end, string? filed)
        => new(DateOnly.Parse(end), filed is null ? null : DateOnly.Parse(filed));

    // ------------------------------------------------------ the four states ---

    [Fact]
    public void AFilingDateAfterItsPeriodEndIsUsedAsItStands()
    {
        var r = FilingDateRule.Resolve([P("2026-03-31", "2026-05-05")]);

        var p = Assert.Single(r.Periods);
        Assert.Equal(FilingDateReason.None, p.Reason);
        Assert.Equal(new DateOnly(2026, 5, 5), p.Effective);
        Assert.Equal(35, p.CleanGapDays);
    }

    [Fact]
    public void ANullFilingDateIsUnknownAndSubstituted()
    {
        // Three clean gaps of 30, 40 and 50 give a widest of 50.
        var r = FilingDateRule.Resolve([
            P("2025-03-31", "2025-04-30"),
            P("2025-06-30", "2025-08-09"),
            P("2025-09-30", "2025-11-19"),
            P("2025-12-31", null),
        ]);

        var unknown = r.Periods.Single(p => p.PeriodEnd == new DateOnly(2025, 12, 31));
        Assert.Equal(FilingDateReason.Null, unknown.Reason);
        Assert.Equal(new DateOnly(2025, 12, 31).AddDays(50), unknown.Effective);
        Assert.Null(unknown.CleanGapDays);
    }

    /// <summary>
    /// The case that motivated D-57 and then D-62. The field is populated, so
    /// nothing errors, and reading it hands you the quarter's numbers on the day the
    /// quarter closed. RJET.US returned this in 35 of 73 periods.
    /// </summary>
    [Fact]
    public void AFilingDateEqualToItsPeriodEndIsUnknownRatherThanUsable()
    {
        var r = FilingDateRule.Resolve([
            P("2025-03-31", "2025-05-10"),
            P("2025-06-30", "2025-06-30"),
        ]);

        var equal = r.Periods.Single(p => p.PeriodEnd == new DateOnly(2025, 6, 30));
        Assert.Equal(FilingDateReason.Equal, equal.Reason);
        Assert.NotEqual(new DateOnly(2025, 6, 30), equal.Effective);
        Assert.Equal(new DateOnly(2025, 6, 30).AddDays(40), equal.Effective);
    }

    /// <summary>
    /// A filing date before its own period end is impossible in fact, so it is
    /// unknown rather than early. The probe found gaps to -16 days.
    /// </summary>
    [Fact]
    public void AFilingDateBeforeItsPeriodEndIsUnknown()
    {
        var r = FilingDateRule.Resolve([
            P("2025-03-31", "2025-05-10"),
            P("2025-06-30", "2025-06-14"),
        ]);

        var negative = r.Periods.Single(p => p.PeriodEnd == new DateOnly(2025, 6, 30));
        Assert.Equal(FilingDateReason.Negative, negative.Reason);
        Assert.True(negative.Effective > negative.PeriodEnd);
    }

    // --------------------------------------------------- no read before due ---

    /// <summary>
    /// INVARIANT 12 stated as the property it is: for every period, whatever the
    /// state, the effective date is never at or before the period end. A read
    /// filtering effective <= date therefore cannot return a period on the day it
    /// closed.
    /// </summary>
    [Fact]
    public void NoPeriodIsEverReadableOnOrBeforeItsOwnPeriodEnd()
    {
        var r = FilingDateRule.Resolve([
            P("2025-03-31", "2025-05-10"),
            P("2025-06-30", "2025-06-30"),
            P("2025-09-30", null),
            P("2025-12-31", "2025-12-01"),
            P("2026-03-31", "2026-05-20"),
        ]);

        foreach (var p in r.Periods)
        {
            Assert.True(p.Effective is null || p.Effective > p.PeriodEnd,
                $"{p.PeriodEnd:yyyy-MM-dd} resolved to {p.Effective:yyyy-MM-dd}, which is not after " +
                "its own period end. That is the quarter readable on the day it closed.");
        }
    }

    // ---------------------------------------------------------- the widest ---

    [Fact]
    public void TheWidestIsTakenFromCleanGapsOnly()
    {
        // A 200-day apparent gap that is actually a negative-then-null pair must not
        // become the widest. Building a gap list before the classification is one of
        // the five probe patterns this phase must not inherit.
        var r = FilingDateRule.Resolve([
            P("2025-03-31", "2025-05-10"),   // clean, 40
            P("2025-06-30", "2025-06-30"),   // equal
            P("2025-09-30", null),           // null
            P("2025-12-31", "2026-02-09"),   // clean, 40
        ]);

        Assert.Equal(40, r.WidestCleanGapDays);
        Assert.Equal(2, r.CleanGapCount);
        Assert.Equal(2, r.SubstitutedCount);
    }

    /// <summary>
    /// The zero-clean-gaps case, and the reason `filing_date_effective` is nullable.
    ///
    /// There is no widest to substitute from. `period_end` would make the row
    /// readable immediately, which is the lookahead D-62 exists to prevent, and a
    /// universal constant is what D-62 rejected. Null means no usable filing date
    /// and none derivable, and every read filters effective <= date so the row is
    /// unreadable by construction.
    /// </summary>
    [Fact]
    public void ATickerWithNoCleanGapGetsNoEffectiveDateAtAll()
    {
        var r = FilingDateRule.Resolve([
            P("2025-03-31", "2025-03-31"),
            P("2025-06-30", null),
            P("2025-09-30", "2025-09-01"),
        ]);

        Assert.Null(r.WidestCleanGapDays);
        Assert.Equal(0, r.CleanGapCount);
        Assert.All(r.Periods, p => Assert.Null(p.Effective));
        Assert.All(r.Periods, p => Assert.NotEqual(FilingDateReason.None, p.Reason));
    }

    [Fact]
    public void ResponseOrderDoesNotChangeTheAnswer()
    {
        RawPeriod[] forwards = [
            P("2025-03-31", "2025-05-10"), P("2025-06-30", null), P("2025-09-30", "2025-11-29"),
        ];

        var a = FilingDateRule.Resolve(forwards);
        var b = FilingDateRule.Resolve(forwards.Reverse().ToArray());

        Assert.Equal(a.WidestCleanGapDays, b.WidestCleanGapDays);
        Assert.Equal(
            a.Periods.Select(p => (p.PeriodEnd, p.Reason, p.Effective)),
            b.Periods.Select(p => (p.PeriodEnd, p.Reason, p.Effective)));
    }

    // ----------------------------------------------- the database agrees ------

    /// <summary>
    /// The rule and the schema must agree. A row whose reason is a substitution and
    /// whose effective date is null is exactly what the zero-clean-gap ticker
    /// produces, and the check added at 1.4 must accept it while refusing the
    /// undated row with no stated reason.
    /// </summary>
    [Fact]
    public async Task TheSchemaAcceptsWhatTheRuleProducesAndRefusesWhatItCannot()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var conn = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(true);

        async Task<bool> Accepts(string reason, string effective)
        {
            try
            {
                await using var i = new NpgsqlCommand(
                    "INSERT INTO fundamental_snapshot (ticker, period_end, period_type, " +
                    "filing_date_unknown_reason, filing_date_effective) VALUES " +
                    $"('SRLRULE.US', DATE '2020-03-31', 'quarterly', {reason}, {effective});", conn);
                await i.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                await using var d = new NpgsqlCommand(
                    "DELETE FROM fundamental_snapshot WHERE ticker = 'SRLRULE.US';", conn);
                await d.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return true;
            }
            catch (PostgresException)
            {
                return false;
            }
        }

        Assert.True(await Accepts("'null'", "NULL").ConfigureAwait(true),
            "A zero-clean-gap row is what the rule produces and the schema must hold it.");
        Assert.True(await Accepts("'none'", "DATE '2020-05-10'").ConfigureAwait(true));

        Assert.False(await Accepts("'none'", "NULL").ConfigureAwait(true),
            "An undated row with no stated reason is the case NOT NULL was pointing at.");
        Assert.False(await Accepts("NULL", "NULL").ConfigureAwait(true),
            "The reason column is NOT NULL, so null carries one meaning rather than two.");
        Assert.False(await Accepts("'bogus'", "NULL").ConfigureAwait(true),
            "Four states, named in the schema rather than only in code.");
    }
}
