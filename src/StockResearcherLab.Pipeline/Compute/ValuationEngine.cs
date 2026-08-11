using System.Globalization;
using StockResearcherLab.Core.Stages;

namespace StockResearcherLab.Pipeline.Compute;

/// <summary>
/// C09. The valuation and quality ratios, recomputed daily because price moves
/// [`SCHEMA.md`].
///
/// **Every fundamental input is read on <c>filing_date_effective</c> and never on
/// <c>period_end</c>** [INVARIANT 12, D-46, D-62]. Period end is the natural-looking
/// key and reading it hands you quarterly numbers weeks before they were public,
/// which makes the quality screens look excellent in backfill and ordinary live.
/// <see cref="ReadableAt"/> is the one place that filter lives, and it takes the
/// as-of date as an argument rather than closing over the run date, because
/// <c>ev_ebit_vs_own_5y</c> evaluates the same ratio at sixty past dates and each of
/// them has its own answer to what was public.
///
/// **Ticker-partitioned, so this reads once, computes in C# and writes once**
/// [`ARCHITECTURE.html` §19]. Every ratio here is a function of one ticker's own
/// filings and its own price, so no worker needs to see another's data. C08 is the
/// precedent and C11 is the opposite case.
///
/// **A TTM sum is null unless all four quarters are present and readable.** Summing
/// three and calling it a year understates every ratio built on it, and it
/// understates them most on the names whose filing history is thinnest
/// [`METRICS.md` §1.5].
///
/// **A row is written for every ticker with a readable quarterly filing, which is a
/// wider set than the universe.** `ARCHITECTURE.html` §3 gives this component two
/// source tables and <c>security</c> is not one of them, so the universe is not
/// available here to narrow by and narrowing is not this component's to do
/// [INVARIANT 1]. C11 joins <c>security</c> itself, which it declares, so the extra
/// rows are never ranked. The cost is storage rather than correctness and the count
/// is recorded in `PROGRESS.md`.
///
/// Formulas, windows and null rules for every column are in `METRICS.md` §3.
/// </summary>
public sealed class ValuationEngine : IStage
{
    /// <summary>
    /// The columns this stage writes. The percentile columns on this table belong to
    /// C11 and are absent here deliberately [D-77].
    ///
    /// <c>last_two_earnings_surprises</c> is written and is written null, which is
    /// the treatment <c>market_context_daily.vix</c> already gets: the store carries
    /// no EPS actual and no estimate, nothing in any document says where a surprise
    /// comes from, and a column left out of the write would keep whatever a previous
    /// run happened to put there [`METRICS.md` §3, carried to phase 3].
    /// </summary>
    public static readonly string[] Columns =
    [
        "ticker", "date",
        "fcf_yield", "ev_ebit", "ev_ebit_vs_own_5y", "roic", "roic_4q_change",
        "gross_margin_4q_change", "net_debt_ebitda", "accruals", "share_count_change",
        "revenue_growth_4q_trend", "cash_on_hand", "quarterly_burn_rate",
        "last_two_earnings_surprises",
    ];

    private static readonly string[] ConflictTarget = ["ticker", "date"];

    /// <summary>Quarters in a trailing twelve months, which is what "TTM" means here.</summary>
    public const int TtmQuarters = 4;

    /// <summary>
    /// How far back <c>ev_ebit_vs_own_5y</c> samples, in years. Named in the column,
    /// so it is a constant rather than a config key [`FlowEngine.WindowDays`].
    /// </summary>
    public const int OwnHistoryYears = 5;

    /// <summary>
    /// Filings fetched behind the date, in years. Five years of month-end samples,
    /// plus the trailing twelve months the earliest of them needs, plus a year of
    /// margin for a company whose period ends move.
    /// </summary>
    public const int FilingHistoryYears = 7;

    /// <summary>
    /// The effective tax rate is clamped to this range. A quarter carrying a one-off
    /// tax item produces rates outside any real one, and an unclamped rate can flip
    /// NOPAT's sign [`METRICS.md` §3].
    /// </summary>
    public const double MinTaxRate = 0.0;

    /// <summary>See <see cref="MinTaxRate"/>.</summary>
    public const double MaxTaxRate = 0.5;

    public string Name => "ValuationEngine";

    public IReadOnlyList<string> ReadSet { get; } = ["price_daily", "fundamental_snapshot"];

    public IReadOnlyList<TableWrite> WriteSet { get; } =
        [new TableWrite("valuation_daily", WriteOperation.Insert, Columns)];

    public async Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
    {
        var minPoints = (int) await LongAsync(context, "valuation.own_history_min_points", ct)
            .ConfigureAwait(false);

        var filings = await FilingsAsync(context, ct).ConfigureAwait(false);
        var closes = await ClosesAsync(context, ct).ConfigureAwait(false);
        var monthEnds = await MonthEndClosesAsync(context, ct).ConfigureAwait(false);

        var rows = new List<Row>(filings.Count);

        foreach (var (ticker, periods) in filings)
        {
            rows.Add(Compute(
                ticker, context.Date,
                periods,
                closes.GetValueOrDefault(ticker),
                monthEnds.GetValueOrDefault(ticker, []),
                minPoints));
        }

        // Ordinal, and load-bearing rather than tidy: the loop above walks a
        // Dictionary, whose enumeration order is unspecified and can differ between
        // runs of the same binary, and write order reaches output [CLAUDE.md
        // section 6].
        rows.Sort(static (a, b) => string.CompareOrdinal(a.Ticker, b.Ticker));

        var written = await context.Data.BulkUpsertAsync(
            "valuation_daily", Columns, ConflictTarget,
            async (w, c) =>
            {
                foreach (var r in rows)
                {
                    await w.StartRowAsync(c).ConfigureAwait(false);
                    await w.WriteAsync(r.Ticker, c).ConfigureAwait(false);
                    await w.WriteAsync(r.Date, c).ConfigureAwait(false);
                    await w.WriteAsync(r.FcfYield, c).ConfigureAwait(false);
                    await w.WriteAsync(r.EvEbit, c).ConfigureAwait(false);
                    await w.WriteAsync(r.EvEbitVsOwn5Y, c).ConfigureAwait(false);
                    await w.WriteAsync(r.Roic, c).ConfigureAwait(false);
                    await w.WriteAsync(r.Roic4QChange, c).ConfigureAwait(false);
                    await w.WriteAsync(r.GrossMargin4QChange, c).ConfigureAwait(false);
                    await w.WriteAsync(r.NetDebtEbitda, c).ConfigureAwait(false);
                    await w.WriteAsync(r.Accruals, c).ConfigureAwait(false);
                    await w.WriteAsync(r.ShareCountChange, c).ConfigureAwait(false);
                    await w.WriteAsync(r.RevenueGrowth4QTrend, c).ConfigureAwait(false);
                    await w.WriteAsync(r.CashOnHand, c).ConfigureAwait(false);
                    await w.WriteAsync(r.QuarterlyBurnRate, c).ConfigureAwait(false);

                    // No EPS actual and no estimate anywhere in the store, and no
                    // document says where a surprise comes from. Null rather than a
                    // proxy, because the field is one of the five section 7 calls
                    // out as able to flip a verdict.
                    await w.WriteAsync<float[]?>(null, c).ConfigureAwait(false);
                }
            }, ct).ConfigureAwait(false);

        return new StageResult(written, "ok", Detail(rows));
    }

    /// <summary>
    /// What the run log says about the columns that carry null, read off the rows
    /// rather than inferred. A metric that is null for a reason nobody counted is a
    /// metric nobody notices going null [C34's precedent].
    /// </summary>
    private static string Detail(IReadOnlyList<Row> rows)
        => string.Format(
            CultureInfo.InvariantCulture,
            "{0:N0} names. Null: {1:N0} fcf_yield, {2:N0} ev_ebit, {3:N0} ev_ebit_vs_own_5y, " +
            "{4:N0} roic, {5:N0} roic_4q_change, {6:N0} gross_margin_4q_change, " +
            "{7:N0} net_debt_ebitda, {8:N0} accruals, {9:N0} share_count_change, " +
            "{10:N0} revenue_growth_4q_trend, {11:N0} cash_on_hand, {12:N0} quarterly_burn_rate. " +
            "last_two_earnings_surprises is null for every row and has no input column [phase 3]",
            rows.Count,
            rows.Count(r => r.FcfYield is null),
            rows.Count(r => r.EvEbit is null),
            rows.Count(r => r.EvEbitVsOwn5Y is null),
            rows.Count(r => r.Roic is null),
            rows.Count(r => r.Roic4QChange is null),
            rows.Count(r => r.GrossMargin4QChange is null),
            rows.Count(r => r.NetDebtEbitda is null),
            rows.Count(r => r.Accruals is null),
            rows.Count(r => r.ShareCountChange is null),
            rows.Count(r => r.RevenueGrowth4QTrend is null),
            rows.Count(r => r.CashOnHand is null),
            rows.Count(r => r.QuarterlyBurnRate is null));

    /// <summary>
    /// Every column for one ticker. Public so the reference test computes through the
    /// same path the stage does rather than through a copy of it.
    /// </summary>
    /// <param name="filings">
    /// Every quarterly period in the fetched window, in any order and **not**
    /// pre-filtered on filing date. The point-in-time filter is applied here, once
    /// per as-of date, because the own-history column has sixty of them.
    /// </param>
    /// <param name="close">The raw close on <paramref name="date"/>, or null if the name did not trade.</param>
    /// <param name="monthEnds">The last trading date of each month in the five years before <paramref name="date"/>, ascending.</param>
    public static Row Compute(
        string ticker, DateOnly date,
        IReadOnlyList<Period> filings,
        decimal? close,
        IReadOnlyList<(DateOnly Date, decimal Close)> monthEnds,
        int ownHistoryMinPoints)
    {
        var readable = ReadableAt(filings, date);

        var latest = At(readable, 0);
        var fourthBack = At(readable, TtmQuarters);

        var marketCap = MarketCap(readable, close);
        var evEbit = EvEbit(readable, close);

        var ttmEbitda = Ttm(readable, 0, static p => p.Ebitda);
        var ttmNetIncome = Ttm(readable, 0, static p => p.NetIncome);
        var ttmCashFromOperating = Ttm(readable, 0, static p => p.CashFromOperating);

        var netDebt = latest is null ? null : NetDebt(latest.Value);

        return new Row(
            ticker, date,
            FcfYield: (float?) Ratio(FreeCashFlow(readable), marketCap),
            EvEbit: (float?) evEbit,
            EvEbitVsOwn5Y: (float?) EvEbitVsOwnHistory(
                filings, monthEnds, evEbit, ownHistoryMinPoints),
            Roic: (float?) Roic(readable, 0),
            Roic4QChange: (float?) Difference(Roic(readable, 0), Roic(readable, TtmQuarters)),
            GrossMargin4QChange: (float?) Difference(GrossMargin(latest), GrossMargin(fourthBack)),
            NetDebtEbitda: (float?) (ttmEbitda is > 0 ? Ratio(netDebt, ttmEbitda) : null),
            Accruals: (float?) Accruals(readable, ttmNetIncome, ttmCashFromOperating),
            ShareCountChange: (float?) ShareCountChange(latest, fourthBack),
            RevenueGrowth4QTrend: (float?) RevenueGrowthTrend(readable),
            CashOnHand: CashOnHand(latest),
            QuarterlyBurnRate: QuarterlyBurnRate(latest));
    }

    /// <summary>
    /// The periods a reader on <paramref name="asOf"/> could have seen, newest period
    /// end first.
    ///
    /// **This is INVARIANT 12 in one function.** A row with no effective filing date
    /// is excluded rather than assumed public, which is the direction D-62 already
    /// chose when it made the column nullable: null means no usable filing date and
    /// none derivable, and every read filters it out by construction.
    ///
    /// Ordered by period end rather than by filing date. The primary key makes period
    /// end unique per ticker within <c>quarterly</c>, so the order is total and two
    /// runs agree; filing dates can tie across a restatement and would not.
    /// </summary>
    public static IReadOnlyList<Period> ReadableAt(IReadOnlyList<Period> filings, DateOnly asOf)
        => filings
            .Where(p => p.FilingDateEffective is { } f && f <= asOf)
            .OrderByDescending(p => p.PeriodEnd)
            .ToList();

    private static Period? At(IReadOnlyList<Period> readable, int index)
        => index < readable.Count ? readable[index] : null;

    /// <summary>
    /// A trailing twelve month sum of one field, ending at
    /// <paramref name="from"/> and running four quarters back.
    ///
    /// Null unless all four are present and readable. Summing three and calling it a
    /// year understates every ratio built on it [`METRICS.md` §1.5].
    /// </summary>
    public static decimal? Ttm(
        IReadOnlyList<Period> readable, int from, Func<Period, decimal?> field)
    {
        if (from + TtmQuarters > readable.Count)
        {
            return null;
        }

        decimal sum = 0;

        for (var i = from; i < from + TtmQuarters; i++)
        {
            if (field(readable[i]) is not { } v)
            {
                return null;
            }

            sum += v;
        }

        return sum;
    }

    /// <summary>
    /// Free cash flow, trailing twelve months.
    ///
    /// **Capital expenditure on its own line, never total investing cash flow**
    /// [D-79]. Cash from operating plus cash from investing reads an acquisition as
    /// capital expenditure and an asset sale as free cash flow, and both errors land
    /// hardest on exactly the names S1 exists to find.
    ///
    /// **The provider reports capital expenditure as a positive magnitude**, so it is
    /// subtracted. That was the open item D-79 left and `METRICS.md` §3 flagged: the
    /// sign is not derivable from anything in this repository, providers differ, and
    /// getting it backwards doubles free cash flow rather than halving it while
    /// nothing errors.
    ///
    /// Confirmed at 2.7 against real rows rather than assumed. Over 41,651 populated
    /// rows the field is positive 38,613 times, zero 3,038 times and negative never;
    /// and on the 31,362 of them whose <c>cash_from_investing</c> is an outflow, the
    /// capital expenditure sitting inside that outflow is positive in every case. The
    /// counts are in `PROGRESS.md`.
    /// </summary>
    public static decimal? FreeCashFlow(IReadOnlyList<Period> readable)
    {
        var operating = Ttm(readable, 0, static p => p.CashFromOperating);
        var capex = Ttm(readable, 0, static p => p.CapitalExpenditures);

        return operating is null || capex is null ? null : operating.Value - capex.Value;
    }

    /// <summary>
    /// Net debt as of the period, taken from the reported figure where the provider
    /// sends one and reconstructed where it does not.
    ///
    /// The reconstruction propagates null rather than reading an absent cash line as
    /// zero. <c>cash_on_hand</c> is the one place in this layer that coalesces to
    /// zero and it says why; here an absent short-term investments line would
    /// overstate net debt, which is the direction that makes a leveraged name look
    /// worse and a screen reject it [`METRICS.md` §1.3].
    /// </summary>
    public static decimal? NetDebt(Period p)
    {
        if (p.NetDebt is { } reported)
        {
            return reported;
        }

        return p.ShortLongTermDebtTotal is { } debt
               && p.CashAndEquivalents is { } cash
               && p.ShortTermInvestments is { } sti
            ? debt - (cash + sti)
            : null;
    }

    /// <summary>Shares outstanding as last reported, times the raw close. A level today, so the raw close [`METRICS.md` §3].</summary>
    public static decimal? MarketCap(IReadOnlyList<Period> readable, decimal? close)
        => At(readable, 0) is { SharesOutstanding: { } shares } && close is { } c && shares > 0 && c > 0
            ? shares * c
            : null;

    /// <summary>
    /// Enterprise value over trailing twelve month EBIT.
    ///
    /// Null when TTM EBIT is at or below zero. A negative EV/EBIT is not a cheap
    /// company, and ranking it alongside positive ones puts loss-makers at the top of
    /// a value screen [`METRICS.md` §3].
    /// </summary>
    public static double? EvEbit(IReadOnlyList<Period> readable, decimal? close)
    {
        var cap = MarketCap(readable, close);
        var net = At(readable, 0) is { } latest ? NetDebt(latest) : null;
        var ebit = Ttm(readable, 0, static p => p.Ebit);

        return cap is null || net is null || ebit is not > 0 ? null : Ratio(cap.Value + net.Value, ebit);
    }

    /// <summary>
    /// Today's EV/EBIT against the median of its own five-year history.
    ///
    /// **Computed rather than read back.** Each historical point is evaluated from
    /// the filings and the price as they stood at that date, so the column is a pure
    /// function of the date. Reading C09's own prior <c>valuation_daily</c> rows would
    /// be cheaper and is permitted, and it would make the column a function of what
    /// previous runs happened to write: a backfill would have to run in date order to
    /// be correct and a gap in history would propagate silently [`METRICS.md` §3].
    ///
    /// **Month-end sampling rather than daily**, sixty points over five years rather
    /// than 1,250, for the whole universe every night. The median of a five-year
    /// distribution is not moved materially by the sampling frequency.
    ///
    /// Null below <paramref name="minPoints"/> evaluated points, which on a cold
    /// database is every name until phase 3 has backfilled.
    /// </summary>
    public static double? EvEbitVsOwnHistory(
        IReadOnlyList<Period> filings,
        IReadOnlyList<(DateOnly Date, decimal Close)> monthEnds,
        double? today,
        int minPoints)
    {
        if (today is null)
        {
            return null;
        }

        var points = new List<double>(monthEnds.Count);

        foreach (var (sampleDate, close) in monthEnds)
        {
            // Its own as-of date, not the run date. A filing that had not been made
            // by this sample date was not public at it, and applying the run date's
            // readable set to a past date is the lookahead INVARIANT 12 exists to
            // prevent, arriving through the one column that looks backwards.
            if (EvEbit(ReadableAt(filings, sampleDate), close) is { } point)
            {
                points.Add(point);
            }
        }

        if (points.Count < minPoints)
        {
            return null;
        }

        var median = Median(points);

        return median <= 0 ? null : (today.Value / median) - 1;
    }

    /// <summary>
    /// Return on invested capital, anchored at one period so
    /// <c>roic_4q_change</c> can be a difference of two of them.
    ///
    /// **Invested capital excludes goodwill and intangibles.** For a serial acquirer
    /// goodwill is the price paid for past acquisitions, so including it makes this
    /// report what was paid rather than what the operating assets earn, and a company
    /// that overpaid looks worse at the same operating performance. Both readings are
    /// standard and the choice is a rule, so `METRICS.md` §3 flags it as a proposal.
    ///
    /// The effective tax rate is one quarter's rather than the trailing year's,
    /// because the clamp exists for the quarter carrying a one-off item.
    /// </summary>
    public static double? Roic(IReadOnlyList<Period> readable, int from)
    {
        if (At(readable, from) is not { } p
            || Ttm(readable, from, static x => x.Ebit) is not { } ebit
            || p.IncomeBeforeTax is not { } beforeTax
            || p.IncomeTaxExpense is not { } tax
            || p.TotalAssets is not { } assets
            || p.TotalCurrentLiabilities is not { } currentLiabilities
            || p.Goodwill is not { } goodwill
            || p.IntangibleAssets is not { } intangibles
            || beforeTax <= 0)
        {
            return null;
        }

        var investedCapital = assets - currentLiabilities - goodwill - intangibles;

        if (investedCapital <= 0)
        {
            return null;
        }

        var taxRate = Math.Clamp((double) (tax / beforeTax), MinTaxRate, MaxTaxRate);

        return (double) ebit * (1 - taxRate) / (double) investedCapital;
    }

    /// <summary>
    /// Gross margin for one period, from the reported gross profit where there is one
    /// and from revenue less cost of revenue where there is not.
    /// </summary>
    public static double? GrossMargin(Period? period)
    {
        if (period is not { } p || p.TotalRevenue is not { } revenue || revenue <= 0)
        {
            return null;
        }

        var grossProfit = p.GrossProfit
                          ?? (p.CostOfRevenue is { } cost ? revenue - cost : null);

        return grossProfit is null ? null : (double) grossProfit.Value / (double) revenue;
    }

    /// <summary>
    /// Sloan's accrual ratio. Higher is worse, being earnings not backed by cash,
    /// which is why S1 ranks on the inverse and why the S1 rubric disqualifies the
    /// top quintile outright.
    /// </summary>
    public static double? Accruals(
        IReadOnlyList<Period> readable, decimal? ttmNetIncome, decimal? ttmCashFromOperating)
    {
        if (ttmNetIncome is not { } income || ttmCashFromOperating is not { } operating
            || At(readable, 0) is not { TotalAssets: { } now }
            || At(readable, TtmQuarters) is not { TotalAssets: { } then })
        {
            return null;
        }

        var average = (now + then) / 2;

        return average <= 0 ? null : (double) (income - operating) / (double) average;
    }

    /// <summary>Positive is dilution, which is why the S1 rubric disqualifies above five percent a year.</summary>
    public static double? ShareCountChange(Period? latest, Period? fourthBack)
        => latest is { SharesOutstanding: { } now } && fourthBack is { SharesOutstanding: { } then } && then > 0
            ? ((double) now / (double) then) - 1
            : null;

    /// <summary>
    /// The slope of the last four year-over-year revenue growth rates against quarter
    /// index, which is the direction growth is moving rather than its level.
    ///
    /// **PROPOSAL in `METRICS.md` §3.** The name says trend, not change, and §07
    /// explains the field as separating "improving margins on shrinking revenue" from
    /// "improving margins on growing revenue". The simpler reading, the latest growth
    /// rate less the fourth-back one, is a two-point estimate of the same thing and is
    /// far noisier on quarterly data.
    ///
    /// Eight qualifying periods, because each growth rate is itself year over year.
    /// </summary>
    public static double? RevenueGrowthTrend(IReadOnlyList<Period> readable)
    {
        var growth = new double[TtmQuarters];

        for (var k = 0; k < TtmQuarters; k++)
        {
            // Chronological, so index 0 is the oldest of the four and a positive
            // slope means growth improving.
            var from = TtmQuarters - 1 - k;

            if (At(readable, from) is not { TotalRevenue: { } now }
                || At(readable, from + TtmQuarters) is not { TotalRevenue: { } then }
                || then <= 0)
            {
                return null;
            }

            growth[k] = ((double) now / (double) then) - 1;
        }

        return Slope(growth);
    }

    /// <summary>
    /// Cash and short-term investments, falling back to the aggregate the provider
    /// sends where it sends no parts.
    ///
    /// **The coalesce to zero here is deliberate and is the one place in this layer
    /// that does it.** A company reporting cash and equivalents but no short-term
    /// investments line has no short-term investments, which is zero rather than
    /// unknown, and treating it as unknown would null the field for most of the
    /// universe [`METRICS.md` §3].
    /// </summary>
    public static decimal? CashOnHand(Period? period)
    {
        if (period is not { } p)
        {
            return null;
        }

        if (p.CashAndEquivalents is null && p.ShortTermInvestments is null)
        {
            return p.Cash;
        }

        return (p.CashAndEquivalents ?? 0) + (p.ShortTermInvestments ?? 0);
    }

    /// <summary>
    /// The latest quarter's cash burn, as a positive number, and zero where the
    /// company generated cash.
    ///
    /// **Zero rather than null when it generated cash**, because a company that
    /// generates cash burns nothing and that is a fact rather than an absence
    /// [`METRICS.md` §1.3]. The latest quarter rather than the trailing year, because
    /// §07's purpose is runway against <c>cash_on_hand</c> and runway is computed from
    /// the current rate.
    /// </summary>
    public static decimal? QuarterlyBurnRate(Period? period)
        => period is { CashFromOperating: { } operating, CashFromInvesting: { } investing }
            ? Math.Max(0, -(operating + investing))
            : null;

    private static double? Ratio(decimal? numerator, decimal? denominator)
        => numerator is { } n && denominator is { } d && d != 0 ? (double) n / (double) d : null;

    private static double? Difference(double? now, double? then)
        => now is { } a && then is { } b ? a - b : null;

    /// <summary>Ordinary least squares slope against the index, unnormalised. The unit is the series' own [`METRICS.md` §3].</summary>
    private static double Slope(double[] y)
    {
        var meanIndex = (y.Length - 1) / 2.0;
        var meanY = y.Average();

        double num = 0;
        double den = 0;

        for (var i = 0; i < y.Length; i++)
        {
            var di = i - meanIndex;
            num += di * (y[i] - meanY);
            den += di * di;
        }

        return num / den;
    }

    /// <summary>The middle value, or the mean of the two middle ones. Sorted here rather than assumed sorted.</summary>
    private static double Median(List<double> values)
    {
        values.Sort();

        var mid = values.Count / 2;

        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2;
    }

    // ------------------------------------------------------------------ reads ---

    /// <summary>
    /// Every quarterly filing in the window, in one statement rather than a query per
    /// ticker.
    ///
    /// **The <c>filing_date_effective &lt;= date</c> filter here is a bound on the
    /// fetch and not the point-in-time rule.** That rule is
    /// <see cref="ReadableAt"/>, which runs once per as-of date. The filter is safe
    /// because a period unreadable on the run date is unreadable on every earlier
    /// sample date too, so it can remove rows the answer never needed and cannot
    /// remove one it did.
    ///
    /// Only <c>quarterly</c> is ingested [`FundamentalsIngestor.cs:198`], and the
    /// filter is stated anyway so that a later period type arriving does not silently
    /// enter a trailing twelve month sum four annual periods wide.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<Period>>> FilingsAsync(
        StageContext context, CancellationToken ct)
    {
        var sql = $"""
            SELECT f.ticker, f.period_end, f.filing_date_effective,
                   f.total_assets, f.total_current_liabilities, f.goodwill, f.intangible_assets,
                   f.cash, f.cash_and_equivalents, f.short_term_investments,
                   f.net_debt, f.short_long_term_debt_total,
                   f.total_revenue, f.cost_of_revenue, f.gross_profit,
                   f.ebit, f.ebitda, f.net_income,
                   f.income_before_tax, f.income_tax_expense,
                   f.cash_from_operating, f.cash_from_investing, f.capital_expenditures,
                   f.shares_outstanding
            FROM fundamental_snapshot f
            WHERE f.period_type = 'quarterly'
              AND f.filing_date_effective IS NOT NULL
              AND f.filing_date_effective <= {Literal(context.Date)}
              AND f.period_end > {Literal(context.Date.AddYears(-FilingHistoryYears))}
            ORDER BY f.ticker, f.period_end DESC;
            """;

        var rows = await context.Data.ReadAsync("fundamental_snapshot", sql, ct).ConfigureAwait(false);

        var byTicker = new Dictionary<string, IReadOnlyList<Period>>(StringComparer.Ordinal);
        var current = new List<Period>();
        string? ticker = null;

        foreach (var r in rows)
        {
            var t = (string) r[0]!;

            if (ticker is not null && !string.Equals(t, ticker, StringComparison.Ordinal))
            {
                byTicker[ticker] = current;
                current = [];
            }

            ticker = t;

            current.Add(new Period(
                PeriodEnd: DateOnly.FromDateTime((DateTime) r[1]!),
                FilingDateEffective: r[2] is null ? null : DateOnly.FromDateTime((DateTime) r[2]!),
                TotalAssets: (decimal?) r[3],
                TotalCurrentLiabilities: (decimal?) r[4],
                Goodwill: (decimal?) r[5],
                IntangibleAssets: (decimal?) r[6],
                Cash: (decimal?) r[7],
                CashAndEquivalents: (decimal?) r[8],
                ShortTermInvestments: (decimal?) r[9],
                NetDebt: (decimal?) r[10],
                ShortLongTermDebtTotal: (decimal?) r[11],
                TotalRevenue: (decimal?) r[12],
                CostOfRevenue: (decimal?) r[13],
                GrossProfit: (decimal?) r[14],
                Ebit: (decimal?) r[15],
                Ebitda: (decimal?) r[16],
                NetIncome: (decimal?) r[17],
                IncomeBeforeTax: (decimal?) r[18],
                IncomeTaxExpense: (decimal?) r[19],
                CashFromOperating: (decimal?) r[20],
                CashFromInvesting: (decimal?) r[21],
                CapitalExpenditures: (decimal?) r[22],
                SharesOutstanding: (decimal?) r[23]));
        }

        if (ticker is not null)
        {
            byTicker[ticker] = current;
        }

        return byTicker;
    }

    /// <summary>
    /// The raw close on the date being computed, which is what a market
    /// capitalisation is built from: a level today rather than a comparison across
    /// dates [`METRICS.md` §1.1].
    ///
    /// A name that did not trade on the date has no close and therefore no market
    /// capitalisation. Carried forward from a neighbouring date it would be a price
    /// the market did not make.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, decimal>> ClosesAsync(
        StageContext context, CancellationToken ct)
    {
        var sql = $"""
            WITH covered AS ({CoveredTickers(context.Date)})
            SELECT p.ticker, p.close
            FROM price_daily p
            JOIN covered c ON c.ticker = p.ticker
            WHERE p.date = {Literal(context.Date)} AND p.close IS NOT NULL
            ORDER BY p.ticker;
            """;

        var rows = await context.Data.ReadAsync("price_daily", sql, ct).ConfigureAwait(false);
        var map = new Dictionary<string, decimal>(StringComparer.Ordinal);

        foreach (var r in rows)
        {
            map[(string) r[0]!] = (decimal) r[1]!;
        }

        return map;
    }

    /// <summary>
    /// The last trading date of each month in the five years before the date, per
    /// ticker, with its raw close.
    ///
    /// Sixty rows a name rather than 1,250, which is what makes the own-history
    /// column cheap enough to run nightly over the whole universe. Strictly before
    /// the date being computed, so today is compared against a history that does not
    /// contain it.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<(DateOnly Date, decimal Close)>>>
        MonthEndClosesAsync(StageContext context, CancellationToken ct)
    {
        var sql = $"""
            WITH covered AS ({CoveredTickers(context.Date)}),
            windowed AS (
                SELECT p.ticker, p.date, p.close,
                       row_number() OVER (
                           PARTITION BY p.ticker, date_trunc('month', p.date)
                           ORDER BY p.date DESC) AS rn
                FROM price_daily p
                JOIN covered c ON c.ticker = p.ticker
                WHERE p.date < {Literal(context.Date)}
                  AND p.date >= {Literal(context.Date.AddYears(-OwnHistoryYears))}
                  AND p.close IS NOT NULL AND p.close > 0
            )
            SELECT ticker, date, close
            FROM windowed
            WHERE rn = 1
            ORDER BY ticker, date;
            """;

        var rows = await context.Data.ReadAsync("price_daily", sql, ct).ConfigureAwait(false);

        var byTicker = new Dictionary<string, IReadOnlyList<(DateOnly, decimal)>>(StringComparer.Ordinal);
        var current = new List<(DateOnly, decimal)>();
        string? ticker = null;

        foreach (var r in rows)
        {
            var t = (string) r[0]!;

            if (ticker is not null && !string.Equals(t, ticker, StringComparison.Ordinal))
            {
                byTicker[ticker] = current;
                current = [];
            }

            ticker = t;
            current.Add((DateOnly.FromDateTime((DateTime) r[1]!), (decimal) r[2]!));
        }

        if (ticker is not null)
        {
            byTicker[ticker] = current;
        }

        return byTicker;
    }

    /// <summary>
    /// The tickers this stage produces a row for: those with at least one readable
    /// quarterly filing inside the window.
    ///
    /// It bounds the two price reads, which would otherwise materialise every ticker
    /// the bulk feed carries. That is roughly fifty thousand a day against the four
    /// thousand this stage can say anything about, and the month-end read spans five
    /// years of them.
    ///
    /// Both tables are in this stage's declared read set, which is what makes the
    /// join here checkable rather than a way around the check [C08's `SeriesAsync`
    /// is the same shape].
    /// </summary>
    private static string CoveredTickers(DateOnly date)
        => $"""
            SELECT DISTINCT ticker FROM fundamental_snapshot
                WHERE period_type = 'quarterly'
                  AND filing_date_effective IS NOT NULL
                  AND filing_date_effective <= {Literal(date)}
                  AND period_end > {Literal(date.AddYears(-FilingHistoryYears))}
            """;

    private static string Literal(DateOnly d)
        => "DATE '" + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "'";

    private static async Task<long> LongAsync(StageContext context, string key, CancellationToken ct)
    {
        var row = await context.Config.RequireAsync(key, context.Date, ct).ConfigureAwait(false);

        return long.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new InvalidOperationException($"{key} resolved to '{row.Value}', which is not a whole number.");
    }

    /// <summary>One quarterly filing, at the fields `valuation_daily` needs.</summary>
    public readonly record struct Period(
        DateOnly PeriodEnd, DateOnly? FilingDateEffective,
        decimal? TotalAssets, decimal? TotalCurrentLiabilities, decimal? Goodwill,
        decimal? IntangibleAssets, decimal? Cash, decimal? CashAndEquivalents,
        decimal? ShortTermInvestments, decimal? NetDebt, decimal? ShortLongTermDebtTotal,
        decimal? TotalRevenue, decimal? CostOfRevenue, decimal? GrossProfit,
        decimal? Ebit, decimal? Ebitda, decimal? NetIncome,
        decimal? IncomeBeforeTax, decimal? IncomeTaxExpense,
        decimal? CashFromOperating, decimal? CashFromInvesting, decimal? CapitalExpenditures,
        decimal? SharesOutstanding);

    /// <summary>One <c>valuation_daily</c> row, in the column order the write uses.</summary>
    public readonly record struct Row(
        string Ticker, DateOnly Date,
        float? FcfYield, float? EvEbit, float? EvEbitVsOwn5Y, float? Roic, float? Roic4QChange,
        float? GrossMargin4QChange, float? NetDebtEbitda, float? Accruals, float? ShareCountChange,
        float? RevenueGrowth4QTrend, decimal? CashOnHand, decimal? QuarterlyBurnRate);
}
