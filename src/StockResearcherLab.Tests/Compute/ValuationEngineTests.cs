using StockResearcherLab.Pipeline.Compute;
using Xunit;

namespace StockResearcherLab.Tests.Compute;

/// <summary>
/// C09's reference. Phase 2's definition of done is that a known ticker's metrics match
/// a hand-computed reference, and these are those for the valuation columns.
///
/// **The references are closed forms rather than a second implementation.** Every
/// figure below divides into a round number, so each expectation is arithmetic written
/// out at its assertion rather than the engine's own formula restated. Reproducing the
/// formula in the test would assert only that two copies of it agree, which is what a
/// reference is supposed to rule out.
///
/// The synthetic company reports the same eight quarters twice over: four recent and
/// four a year older, each internally constant, so every four-quarter change has an
/// exact answer and every trailing twelve month sum is four times one quarter.
/// </summary>
public sealed class ValuationEngineTests
{
    private static readonly DateOnly AsOf = new(2026, 8, 10);

    /// <summary>The raw close on the date being computed. Shares are 1,000, so the market capitalisation is 2,000.</summary>
    private const decimal Close = 2m;

    private const int OwnHistoryMinPoints = 24;

    /// <summary>
    /// Ten of the twelve computed columns, each read off the fixture without running
    /// anything.
    /// </summary>
    [Fact]
    public void TheSteadyCompanyReproducesItsClosedForm()
    {
        var row = ValuationEngine.Compute(
            "SRLTEST.VAL", AsOf, EightQuarters(), Close, [], OwnHistoryMinPoints);

        // Cash from operating is 100 a quarter and capital expenditure 25, so trailing
        // twelve month free cash flow is 400 - 100 = 300 against a market
        // capitalisation of 1,000 shares at 2.
        Assert.Equal(0.15f, row.FcfYield!.Value, 6);

        // Market capitalisation 2,000 plus net debt 500, over a trailing EBIT of 200.
        Assert.Equal(12.5f, row.EvEbit!.Value, 5);

        // NOPAT is 200 x (1 - 10/40) = 150. Invested capital is 2,000 - 300 - 100 - 100
        // = 1,500, goodwill and intangibles excluded.
        Assert.Equal(0.10f, row.Roic!.Value, 6);

        // A year earlier the same company earned 40 a quarter, so NOPAT was 120 over
        // the same 1,500 and ROIC was 0.08.
        Assert.Equal(0.02f, row.Roic4QChange!.Value, 6);

        // (500 - 300) / 500 against (400 - 280) / 400 a year earlier.
        Assert.Equal(0.10f, row.GrossMargin4QChange!.Value, 6);

        // Net debt 500 over a trailing EBITDA of 400.
        Assert.Equal(1.25f, row.NetDebtEbitda!.Value, 6);

        // Trailing net income 120 less trailing cash from operating 400, over average
        // total assets of 2,000. Negative is earnings backed by more cash than they
        // report, which is the good direction.
        Assert.Equal(-0.14f, row.Accruals!.Value, 6);

        // 1,000 shares against 800 four quarters back.
        Assert.Equal(0.25f, row.ShareCountChange!.Value, 6);

        // Revenue is 500 against 400 at every one of the four points, so the growth
        // rate is 0.25 four times over and its slope is zero. The column is the trend
        // in growth rather than growth itself.
        Assert.Equal(0f, row.RevenueGrowth4QTrend!.Value, 6);

        // 150 of cash and equivalents plus 50 of short-term investments.
        Assert.Equal(200m, row.CashOnHand);

        // Cash from operating 100 and investing -40 nets to +60, so the company burned
        // nothing. Zero rather than null, because that is a fact rather than an
        // absence.
        Assert.Equal(0m, row.QuarterlyBurnRate);
    }

    /// <summary>
    /// The one that would pass silently if it were wrong.
    ///
    /// D-79 left the sign convention open and `METRICS.md` §3 required 2.7 to confirm
    /// it against real rows before the formula was fixed. The provider sends capital
    /// expenditure as a positive magnitude, so it is subtracted. Read as a negative
    /// outflow and added, the same fixture yields 500 of free cash flow rather than
    /// 300, and a yield of 0.25 rather than 0.15: free cash flow doubles rather than
    /// halves and nothing downstream errors.
    /// </summary>
    [Fact]
    public void CapitalExpenditureIsSubtractedAsAPositiveMagnitude()
    {
        var readable = ValuationEngine.ReadableAt(EightQuarters(), AsOf);

        Assert.Equal(300m, ValuationEngine.FreeCashFlow(readable));

        // The wrong reading, stated so the number that would appear is on the page.
        Assert.NotEqual(500m, ValuationEngine.FreeCashFlow(readable));
    }

    /// <summary>
    /// INVARIANT 12. No fundamental value reaches a valuation row before its effective
    /// filing date.
    ///
    /// The fixture's newest quarter is filed the day after the date being computed and
    /// carries an EBIT twenty times the others, so a leak is not a rounding difference:
    /// including it would put trailing EBIT at 5,150 and EV/EBIT under 0.5, where the
    /// readable answer is 12.5.
    /// </summary>
    [Fact]
    public void AQuarterFiledAfterTheDateIsNotReadable()
    {
        var quarters = EightQuarters().ToList();

        quarters.Insert(0, quarters[0] with
        {
            PeriodEnd = new DateOnly(2026, 9, 30),
            FilingDateEffective = AsOf.AddDays(1),
            Ebit = 5_000m,
        });

        var row = ValuationEngine.Compute(
            "SRLTEST.LATE", AsOf, quarters, Close, [], OwnHistoryMinPoints);

        Assert.Equal(12.5f, row.EvEbit!.Value, 5);

        // And the same fixture with that filing date brought forward does move, so the
        // assertion above is about the filter rather than about the period being
        // ignored for some other reason.
        quarters[0] = quarters[0] with { FilingDateEffective = AsOf.AddDays(-1) };

        var leaked = ValuationEngine.Compute(
            "SRLTEST.LEAK", AsOf, quarters, Close, [], OwnHistoryMinPoints);

        Assert.True(leaked.EvEbit!.Value < 1f);
    }

    /// <summary>
    /// A row with no effective filing date is excluded rather than assumed public.
    /// D-62 made the column nullable precisely because a ticker with no clean gap has
    /// no date to substitute from, and null there means no usable filing date and none
    /// derivable.
    /// </summary>
    [Fact]
    public void AQuarterWithNoEffectiveFilingDateIsNotReadable()
    {
        var quarters = EightQuarters().ToList();
        quarters[0] = quarters[0] with { FilingDateEffective = null };

        var readable = ValuationEngine.ReadableAt(quarters, AsOf);

        Assert.Equal(7, readable.Count);
        Assert.DoesNotContain(readable, p => p.PeriodEnd == quarters[0].PeriodEnd);
    }

    /// <summary>
    /// A trailing twelve month sum is null unless all four quarters are present and
    /// readable. Summing three and calling it a year understates every ratio built on
    /// it, on exactly the names whose filing history is thinnest.
    /// </summary>
    [Fact]
    public void ThreeQuartersIsNotAYear()
    {
        var row = ValuationEngine.Compute(
            "SRLTEST.SHORT", AsOf, EightQuarters().Take(3).ToList(), Close, [], OwnHistoryMinPoints);

        Assert.Null(row.FcfYield);
        Assert.Null(row.EvEbit);
        Assert.Null(row.Roic);
        Assert.Null(row.NetDebtEbitda);
        Assert.Null(row.Accruals);

        // The two that need no trailing sum are computable from the latest quarter and
        // are, which is what makes the nulls above a rule rather than an empty fixture.
        Assert.Equal(200m, row.CashOnHand);
        Assert.Equal(0m, row.QuarterlyBurnRate);
    }

    /// <summary>
    /// A quarter missing one field nulls only what that field feeds. Null propagates
    /// through the metric that reads it and no further.
    /// </summary>
    [Fact]
    public void AnAbsentFieldNullsOnlyTheMetricsThatReadIt()
    {
        var quarters = EightQuarters().ToList();
        quarters[0] = quarters[0] with { CapitalExpenditures = null };

        var row = ValuationEngine.Compute(
            "SRLTEST.GAP", AsOf, quarters, Close, [], OwnHistoryMinPoints);

        Assert.Null(row.FcfYield);
        Assert.Equal(12.5f, row.EvEbit!.Value, 5);
        Assert.Equal(0.10f, row.Roic!.Value, 6);
    }

    /// <summary>
    /// EV/EBIT is null rather than negative when trailing EBIT is at or below zero.
    /// A negative multiple is not a cheap company, and ranking it alongside positive
    /// ones puts loss-makers at the top of a value screen.
    /// </summary>
    [Fact]
    public void ALossMakerHasNoMultipleRatherThanANegativeOne()
    {
        var quarters = EightQuarters()
            .Select(q => q with { Ebit = -10m, Ebitda = -5m, IncomeBeforeTax = -20m })
            .ToList();

        var row = ValuationEngine.Compute(
            "SRLTEST.LOSS", AsOf, quarters, Close, [], OwnHistoryMinPoints);

        Assert.Null(row.EvEbit);
        Assert.Null(row.NetDebtEbitda);
        Assert.Null(row.Roic);
    }

    /// <summary>
    /// The company that is actually burning. Cash from operating -30 and investing -20
    /// nets to -50, and the column carries that as a positive 50 because it is a rate
    /// rather than a signed flow.
    /// </summary>
    [Fact]
    public void ACompanyConsumingCashCarriesTheRateRatherThanASign()
    {
        var quarters = EightQuarters().ToList();
        quarters[0] = quarters[0] with { CashFromOperating = -30m, CashFromInvesting = -20m };

        var row = ValuationEngine.Compute(
            "SRLTEST.BURN", AsOf, quarters, Close, [], OwnHistoryMinPoints);

        Assert.Equal(50m, row.QuarterlyBurnRate);
    }

    /// <summary>
    /// Growth improving quarter on quarter, as an exact slope.
    ///
    /// The four year-over-year growth rates are 0.10, 0.20, 0.30 and 0.40 oldest first.
    /// Against a quarter index of 0 to 3 the deviations are -1.5, -0.5, 0.5 and 1.5,
    /// their squares sum to 5, and the cross products sum to 0.5, so the slope is
    /// exactly 0.1 per quarter. A sign error in the index direction gives -0.1.
    /// </summary>
    [Fact]
    public void ImprovingGrowthHasAPositiveTrendWithAnExactSlope()
    {
        var quarters = EightQuarters().ToList();

        // Oldest four at 100 of revenue, newest four at 110, 120, 130 and 140 going
        // forward, so each growth rate is exactly its own tenth.
        decimal[] recent = [140m, 130m, 120m, 110m];

        for (var i = 0; i < 4; i++)
        {
            quarters[i] = quarters[i] with { TotalRevenue = recent[i] };
            quarters[i + 4] = quarters[i + 4] with { TotalRevenue = 100m };
        }

        var row = ValuationEngine.Compute(
            "SRLTEST.GROW", AsOf, quarters, Close, [], OwnHistoryMinPoints);

        Assert.Equal(0.1f, row.RevenueGrowth4QTrend!.Value, 6);
    }

    /// <summary>
    /// Today's multiple against the median of its own history, and the floor below
    /// which it is null rather than computed over less.
    ///
    /// Every quarter in this fixture is identical, so EV/EBIT at a sample date is a
    /// function of the close alone: at 1 it is (1,000 + 500) / 200 = 7.5 and today at
    /// 2 it is 12.5. The median of twenty-four identical points is 7.5 and the column
    /// is 12.5 / 7.5 - 1 = 2/3.
    /// </summary>
    [Fact]
    public void OwnHistoryIsAMedianAndIsNullBelowTheConfiguredFloor()
    {
        var quarters = ThirtyQuarters();

        var justUnder = ValuationEngine.Compute(
            "SRLTEST.OWN", AsOf, quarters, Close, MonthEnds(OwnHistoryMinPoints - 1, 1m),
            OwnHistoryMinPoints);

        Assert.Null(justUnder.EvEbitVsOwn5Y);

        var atTheFloor = ValuationEngine.Compute(
            "SRLTEST.OWN", AsOf, quarters, Close, MonthEnds(OwnHistoryMinPoints, 1m),
            OwnHistoryMinPoints);

        Assert.Equal((float) (2.0 / 3.0), atTheFloor.EvEbitVsOwn5Y!.Value, 5);
    }

    /// <summary>
    /// The own-history samples resolve their filings as of their own date, not as of
    /// the run date.
    ///
    /// Every quarter here is filed a year after its period end, so at a sample date
    /// only the quarters filed by then are readable. Applying the run date's readable
    /// set to a past date is the lookahead INVARIANT 12 exists to prevent, arriving
    /// through the one column that looks backwards, and it would leave every sample
    /// evaluable where in truth the earliest ones are not.
    /// </summary>
    [Fact]
    public void EachOwnHistorySampleResolvesFilingsAsOfItsOwnDate()
    {
        var quarters = ThirtyQuarters()
            .Select(q => q with { FilingDateEffective = q.PeriodEnd.AddYears(1) })
            .ToList();

        var sampled = MonthEnds(60, 1m);

        var readableAtTheOldest = ValuationEngine.ReadableAt(quarters, sampled[0].Date);
        var readableToday = ValuationEngine.ReadableAt(quarters, AsOf);

        Assert.True(readableAtTheOldest.Count < readableToday.Count,
            "A sample five years back must see fewer filings than the run date does, or the " +
            "as-of date is not reaching the filter.");

        // And every one it does see was filed on or before that sample date.
        Assert.All(readableAtTheOldest, p =>
            Assert.True(p.FilingDateEffective <= sampled[0].Date));
    }

    // ---------------------------------------------------------------- fixtures ---

    /// <summary>
    /// Four recent quarters and four a year older, each group internally constant.
    /// Index 0 is the newest, which is the order <see cref="ValuationEngine.ReadableAt"/>
    /// produces.
    /// </summary>
    private static IReadOnlyList<ValuationEngine.Period> EightQuarters()
    {
        var quarters = new List<ValuationEngine.Period>(8);

        for (var i = 0; i < 8; i++)
        {
            var q = Quarter(new DateOnly(2026, 6, 30).AddMonths(-3 * i));

            quarters.Add(i < 4
                ? q
                : q with
                {
                    Ebit = 40m,
                    TotalRevenue = 400m,
                    CostOfRevenue = 280m,
                    SharesOutstanding = 800m,
                });
        }

        return quarters;
    }

    /// <summary>Seven and a half years of identical quarters, so a five-year sample window is fully covered.</summary>
    private static IReadOnlyList<ValuationEngine.Period> ThirtyQuarters()
        => Enumerable.Range(0, 30)
            .Select(i => Quarter(new DateOnly(2026, 6, 30).AddMonths(-3 * i)))
            .ToList();

    /// <summary>
    /// One quarter, at values that divide into round numbers.
    ///
    /// Gross profit is deliberately absent so the margin comes through the revenue
    /// less cost of revenue fallback, which is the branch most of the universe takes.
    /// </summary>
    private static ValuationEngine.Period Quarter(DateOnly periodEnd)
        => new(
            PeriodEnd: periodEnd,
            FilingDateEffective: periodEnd.AddDays(30),
            TotalAssets: 2_000m,
            TotalCurrentLiabilities: 300m,
            Goodwill: 100m,
            IntangibleAssets: 100m,
            Cash: null,
            CashAndEquivalents: 150m,
            ShortTermInvestments: 50m,
            NetDebt: 500m,
            ShortLongTermDebtTotal: null,
            TotalRevenue: 500m,
            CostOfRevenue: 300m,
            GrossProfit: null,
            Ebit: 50m,
            Ebitda: 100m,
            NetIncome: 30m,
            IncomeBeforeTax: 40m,
            IncomeTaxExpense: 10m,
            CashFromOperating: 100m,
            CashFromInvesting: -40m,
            CapitalExpenditures: 25m,
            SharesOutstanding: 1_000m);

    /// <summary>
    /// <paramref name="count"/> monthly sample points ending the month before the date
    /// being computed, ascending, each at the same close.
    /// </summary>
    private static IReadOnlyList<(DateOnly Date, decimal Close)> MonthEnds(int count, decimal close)
        => Enumerable.Range(1, count)
            .Select(i => (Date: LastDayOfMonth(AsOf.AddMonths(-i)), Close: close))
            .OrderBy(x => x.Date)
            .ToList();

    private static DateOnly LastDayOfMonth(DateOnly d)
        => new(d.Year, d.Month, DateTime.DaysInMonth(d.Year, d.Month));
}
