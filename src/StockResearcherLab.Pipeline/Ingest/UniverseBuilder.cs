using System.Globalization;
using System.Text.Json;
using StockResearcherLab.Core.Config;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data.Eodhd;

namespace StockResearcherLab.Pipeline.Ingest;

/// <summary>
/// C01. D-4's six absolute criteria, and the only place in this system anything is
/// filtered out.
///
/// **No component downstream narrows by rank, score or count** [D-5, INVARIANT 1].
/// Ranking at ingest decides what can be discovered before anything has had a chance
/// to be discovered, and the sentiment screen was once made structurally unable to
/// find what it exists to find by exactly that.
///
/// **The clean filing-gap criterion is computed for the date being built, never read
/// from a stored total** [D-62, M.1]. A ticker has more clean gaps now than it had
/// three years ago, so a stored value read during backfill would admit names a live
/// system on that date would have excluded, and backfilled screen scores would sit
/// on a different population than live ones.
/// </summary>
public sealed class UniverseBuilder : IStage, IBackfillStage
{
    /// <summary>
    /// Identity and lifespan, and nothing that varies by date [D-92, `SCHEMA.md`].
    ///
    /// **`sector`, `size_bucket`, `market_cap` and `is_active` left this list at 3.11**
    /// and moved to <see cref="DailyColumns"/>. The four columns are still physically on
    /// `security` because 0007 dropped nothing, so from here they hold whatever the last
    /// run before this change put there: nothing writes them after 3.11 and nothing reads
    /// them after 3.12, which is open item 14 and is a window rather than a state.
    /// </summary>
    public static readonly string[] Columns =
    [
        "ticker", "name", "first_seen", "last_seen", "delisted_date",
    ];

    /// <summary>
    /// Membership on a date [D-92, 0007].
    ///
    /// **`is_active` is written rather than defaulted.** 0007 gives the column no
    /// default precisely so a writer that has not decided fails at the insert instead of
    /// recording `true`, which is what let C01 have no deactivation path and no error to
    /// show for it [`PROGRESS.md`, 2026-08-09].
    /// </summary>
    public static readonly string[] DailyColumns =
    [
        "ticker", "date", "sector", "size_bucket", "market_cap", "is_active",
    ];

    private static readonly string[] ConflictTarget = ["ticker"];

    private static readonly string[] DailyConflictTarget = ["ticker", "date"];

    private readonly EodhdClient _client;

    public UniverseBuilder(EodhdClient client) => _client = client;

    public string Name => "UniverseBuilder";

    public IReadOnlyList<string> ReadSet { get; } =
        ["price_daily", "fundamental_snapshot", "security_daily"];

    public IReadOnlyList<TableWrite> WriteSet { get; } =
    [
        new TableWrite("security", WriteOperation.Insert, Columns),
        new TableWrite("security_daily", WriteOperation.Insert, DailyColumns),
    ];

    public async Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
    {
        var settings = await SettingsAsync(context, ct).ConfigureAwait(false);
        var listing = await ListingAsync(context, ct).ConfigureAwait(false);

        var day = await MembershipAsync(context, settings, listing, context.Date, ct).ConfigureAwait(false);

        var written = await WriteIdentityAsync(context, day.Members, listing, ct).ConfigureAwait(false);

        // **The nightly path writes departures too, from 3.12.** It reads the membership
        // in force before this run rather than carrying it in memory, which is the one
        // thing the range path can do and this cannot. Without it a name that left the
        // universe keeps its last `true` row and is inherited into every later cell,
        // because readers take the most recent row at or before the date [D-92]. Opened
        // as a gap at 3.11, when `security_daily` was not yet in this component's read
        // set, and closed here with the §3 Reads cell.
        var previous = await PreviousMembersAsync(context, context.Date, ct).ConfigureAwait(false);

        written += await WriteDailyAsync(
            context, context.Date, day.Members, previous, ct).ConfigureAwait(false);

        return new StageResult(written, "ok", day.Detail);
    }

    /// <summary>
    /// The same component over a range, which is the whole of what D-93 permits: this
    /// evaluates membership on each weekly date rather than reimplementing anything.
    ///
    /// **The cadence is the live one and that is D-92 rather than convenience.**
    /// `ARCHITECTURE.html` §3 runs C01 weekly on Sunday and C11 reads the most recent
    /// `security_daily` row at or before the date, so a backfilled cell sits on the
    /// identical population rule as a live one. A denser grain would put a population the
    /// live system does not share underneath a floor [D-58].
    ///
    /// **Config resolves per evaluation date, and unlike C03's and C04's sweeps this is
    /// exactly the case D-93 governs.** Those two are ticker-partitioned: no date is
    /// being computed and no configured value reaches a row they write, so resolving once
    /// for the range is sound. This one computes a date. `universe.min_market_cap` and
    /// the two bucket floors decide what a row says, so a 2021 date must resolve the
    /// version in force in 2021 or the backfilled universe is built to today's criteria
    /// [INVARIANT 13, D-43]. `ForDateAsync` is the only route to a `StageContext` here
    /// for that reason.
    ///
    /// **No allowance gate and no attempt record.** Both exist for sweeps that spend per
    /// ticker; this spends two symbol lists for the whole range however many dates it
    /// covers, so there is nothing to meter and nothing to resume by. An interruption is
    /// re-run, which D-68's per-grain idempotence makes free.
    ///
    /// **The per-date wall clock is reported.** `PROGRESS.md` records this checkpoint's
    /// cost as a bracket spanning a factor of twenty-three, cold against warm, with
    /// nothing on record covering the case that decides it: many dates inside one
    /// process. The run is therefore built to answer that question as it goes rather than
    /// leaving it to a separate measurement.
    /// </summary>
    public async Task<BackfillResult> ExecuteRangeAsync(BackfillContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var dates = EvaluationDates(context.From, context.To);
        if (dates.Count == 0)
        {
            return BackfillResult.Completed(0, context.To, RangeDetail(dates, [], [], 0));
        }

        // One fetch for the range. The symbol lists are not configuration and not
        // date-parametric: this provider carries no listing history, which is the same
        // limitation D-92 records against sector [item 35].
        var listing = await ListingAsync(
            await context.ForDateAsync(dates[0], ct).ConfigureAwait(false), ct).ConfigureAwait(false);

        // Keyed on the resolved version rather than on the date, so a range whose config
        // never moved reads the keys once and one that moved reads them again at the
        // boundary. Correct either way; the cache is what stops 293 dates being 2,344
        // config round trips.
        var byVersion = new Dictionary<int, Settings>();

        long written = 0;

        // Identity is a whole-history fact rather than a per-date one, so it accumulates
        // across the evaluated dates and is written once at the end. Accumulated rather
        // than re-queried: every member already carries the `min` and `max` its own date's
        // statement computed, so a second pass over `price_daily` would ask a question
        // already answered.
        var everMember = new SortedDictionary<string, Identity>(StringComparer.Ordinal);

        // Seeded from the store rather than empty, so the first evaluated date measures
        // departures against whatever was in force before it. An empty seed would make a
        // resumed range silently unable to retire anything on its first date [3.12].
        var previous = new HashSet<string>(
            await PreviousMembersAsync(
                await context.ForDateAsync(dates[0], ct).ConfigureAwait(false), dates[0], ct)
                .ConfigureAwait(false),
            StringComparer.Ordinal);
        var counts = new List<int>(dates.Count);
        var elapsed = new List<long>(dates.Count);
        var lastCovered = context.From;

        foreach (var date in dates)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var stage = await context.ForDateAsync(date, ct).ConfigureAwait(false);

            if (!byVersion.TryGetValue(stage.ConfigVersion, out var settings))
            {
                settings = await SettingsAsync(stage, ct).ConfigureAwait(false);
                byVersion[stage.ConfigVersion] = settings;
            }

            var day = await MembershipAsync(stage, settings, listing, date, ct).ConfigureAwait(false);

            written += await WriteDailyAsync(stage, date, day.Members, previous, ct).ConfigureAwait(false);

            previous.Clear();
            foreach (var m in day.Members)
            {
                previous.Add(m.Ticker);

                everMember[m.Ticker] = everMember.TryGetValue(m.Ticker, out var seen)
                    ? seen with
                    {
                        FirstSeen = m.FirstSeen < seen.FirstSeen ? m.FirstSeen : seen.FirstSeen,
                        LastSeen = m.LastSeen > seen.LastSeen ? m.LastSeen : seen.LastSeen,
                    }
                    : new Identity(m.Name, m.FirstSeen, m.LastSeen);
            }

            counts.Add(day.Members.Count);
            elapsed.Add((long) System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            lastCovered = date;
        }

        var last = await context.ForDateAsync(dates[^1], ct).ConfigureAwait(false);
        written += await WriteIdentityAsync(last, everMember, listing, ct).ConfigureAwait(false);

        return BackfillResult.Completed(
            written, lastCovered, RangeDetail(dates, counts, elapsed, everMember.Count));
    }

    /// <summary>
    /// Sundays, which is the live cadence [D-92, `ARCHITECTURE.html` §3].
    ///
    /// The first evaluation date is the first Sunday at or after the range start rather
    /// than the range start itself, so a range whose start is a Monday does not get an
    /// off-cadence date at the front that no live run would ever have produced.
    /// </summary>
    public static IReadOnlyList<DateOnly> EvaluationDates(DateOnly from, DateOnly to)
    {
        var dates = new List<DateOnly>();
        var offset = ((int) DayOfWeek.Sunday - (int) from.DayOfWeek + 7) % 7;

        for (var d = from.AddDays(offset); d <= to; d = d.AddDays(7))
        {
            dates.Add(d);
        }

        return dates;
    }

    private static string RangeDetail(
        IReadOnlyList<DateOnly> dates, IReadOnlyList<int> counts, IReadOnlyList<long> elapsed, int everMember)
    {
        if (dates.Count == 0)
        {
            return "no weekly evaluation date falls inside this range, so nothing was evaluated.";
        }

        var ordered = elapsed.OrderBy(static x => x).ToList();

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:N0} weekly evaluation date(s) from {1} to {2}. Membership {3:N0} to {4:N0}, " +
            "{5:N0} distinct ticker(s) a member on at least one of them. " +
            "Per date: first {6:N0} ms, median {7:N0} ms, last {8:N0} ms, total {9:N0} ms. " +
            "**The first against the median is what says whether the working set survives " +
            "between dates inside one process**, which is the question the cost bracket in " +
            "`PROGRESS.md` turns on and which nothing measured before this run.",
            dates.Count, Iso(dates[0]), Iso(dates[^1]), counts.Min(), counts.Max(), everMember,
            elapsed[0], ordered[ordered.Count / 2], elapsed[^1], elapsed.Sum());
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// The size bucket, which is total: every market capitalisation takes one of three
    /// values and none takes null.
    ///
    /// Public so the test that C11's fallback rests on reads this rule rather than a
    /// copy of it [2.10]. `METRICS.md` §6.4 says a null <c>size_bucket</c> cannot
    /// arise, and that claim is this function being total plus a member having cleared
    /// D-4's market capitalisation floor to be here at all. Without it a reader cannot
    /// tell whether the bucket hole was reasoned about or missed, and the percentile
    /// fallback would need a third step for a case that does not exist.
    /// </summary>
    public static string Bucket(decimal marketCap, decimal largeFloor, decimal midFloor)
        => marketCap >= largeFloor ? "large" : marketCap >= midFloor ? "mid" : "small";

    private readonly record struct Member(
        string Ticker, string Name, string? Sector, decimal MarketCap, string Bucket,
        DateOnly FirstSeen, DateOnly LastSeen);

    /// <summary>Identity accumulated across evaluated dates rather than re-queried.</summary>
    private sealed record Identity(string Name, DateOnly FirstSeen, DateOnly LastSeen);

    private sealed record Settings(
        decimal MinPrice, decimal MinAdv, int MinHistory, decimal MinMarketCap,
        decimal LargeFloor, decimal MidFloor, int MinCleanGaps, int StatementTimeout,
        DateOnly WindowStart);

    /// <summary>
    /// The provider's two symbol lists and, for the delisted half, the date each stopped
    /// trading.
    ///
    /// **Both lists, which is what makes a historical date answerable.** C01 took the
    /// live list alone until 3.11. Evaluated at a 2022 date that rejects every name
    /// delisted since as `rejectedType`, so the reconstructed universe would hold only
    /// survivors, which is the bias this system exists to measure rather than to
    /// introduce [D-101, phase 3 done-when 2].
    ///
    /// **The symbol list carries no delisting date, so the last bar is the only date
    /// there is** [3.1, §2]. It is bounded to names trading inside the window because
    /// names that stopped before it can never be a member on any evaluated date.
    /// </summary>
    private sealed record Listing(
        IReadOnlyDictionary<string, string> Names, IReadOnlyDictionary<string, DateOnly> DelistedOn);

    private sealed record Day(List<Member> Members, string Detail);

    private static async Task<Settings> SettingsAsync(StageContext context, CancellationToken ct)
        => new(
            await DecimalAsync(context, "universe.min_price", ct).ConfigureAwait(false),
            await DecimalAsync(context, "universe.min_adv_20d", ct).ConfigureAwait(false),
            (int) await LongAsync(context, "universe.min_history_days", ct).ConfigureAwait(false),
            await DecimalAsync(context, "universe.min_market_cap", ct).ConfigureAwait(false),
            await DecimalAsync(context, "universe.bucket_large_floor", ct).ConfigureAwait(false),
            await DecimalAsync(context, "universe.bucket_mid_floor", ct).ConfigureAwait(false),
            (int) await LongAsync(context, "fundamentals.min_clean_gaps_for_substitution", ct).ConfigureAwait(false),
            (int) await LongAsync(context, "universe.pool_statement_timeout_seconds", ct).ConfigureAwait(false),
            ConfigValue.Date(await context.Config
                .RequireAsync("backfill.window_start", context.Date, ct).ConfigureAwait(false)));

    private async Task<Listing> ListingAsync(StageContext context, CancellationToken ct)
    {
        var live = await SymbolList.AdmittedAsync(_client, ct).ConfigureAwait(false);
        var delisted = await SymbolList.AdmittedDelistedAsync(_client, ct).ConfigureAwait(false);

        var names = new Dictionary<string, string>(live, StringComparer.Ordinal);
        foreach (var (ticker, name) in delisted)
        {
            names[ticker] = name;
        }

        var windowStart = ConfigValue.Date(await context.Config
            .RequireAsync("backfill.window_start", context.Date, ct).ConfigureAwait(false));

        // A seek per ticker rather than a group-by over the heap, which is the shape
        // D-102 settled: `max` on the `(ticker, date)` primary key is a one-row descent.
        var rows = await context.Data.ReadAsync(
            "price_daily",
            $"""
             WITH traded AS (
                 SELECT DISTINCT ticker FROM price_daily
                 WHERE date >= DATE '{Iso(windowStart)}'
             )
             SELECT t.ticker,
                    (SELECT max(p.date) FROM price_daily p WHERE p.ticker = t.ticker) AS last_bar
             FROM traded t
             ORDER BY t.ticker;
             """,
            ct).ConfigureAwait(false);

        var delistedOn = new Dictionary<string, DateOnly>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            var ticker = (string) r[0]!;
            if (delisted.ContainsKey(ticker) && r[1] is DateTime lastBar)
            {
                delistedOn[ticker] = DateOnly.FromDateTime(lastBar);
            }
        }

        return new Listing(names, delistedOn);
    }

    /// <summary>
    /// D-4's six absolute criteria as of one date, which is the whole of what changed at
    /// 3.11: the criteria are untouched and the date is now a parameter rather than
    /// today.
    /// </summary>
    private static async Task<Day> MembershipAsync(
        StageContext context, Settings settings, Listing listing, DateOnly asOf, CancellationToken ct)
    {
        var liquid = await LiquidAsync(context, asOf, settings, ct).ConfigureAwait(false);
        var fundamentals = await FundamentalsAsync(context, asOf, ct).ConfigureAwait(false);
        var sectors = await SectorsAsync(context, asOf, ct).ConfigureAwait(false);

        // Every rejection counted, because "the universe builds to roughly 2,000
        // names" is not an answer on its own: what was excluded and by which
        // criterion is what makes the number readable [phase 1 done-when].
        var rejectedType = 0;
        var rejectedDelisted = 0;
        var rejectedNoFundamentals = 0;
        var rejectedCleanGaps = 0;
        var rejectedNoShares = 0;
        var rejectedMarketCap = 0;

        var members = new List<Member>();

        foreach (var p in liquid)
        {
            if (!listing.Names.TryGetValue(p.Ticker, out var name))
            {
                rejectedType++;
                continue;
            }

            // **Counted apart from the type rejection**, because a name that had not
            // stopped trading yet on this date and one the provider never admitted are
            // different facts, and the second is constant across the window while the
            // first moves with it. A name is a member up to its last bar and not after.
            if (listing.DelistedOn.TryGetValue(p.Ticker, out var stopped) && asOf > stopped)
            {
                rejectedDelisted++;
                continue;
            }

            // Counted apart from the thin ones, because they are not the same
            // fact and the pooled number is unreadable [1.8]. A ticker C03 has
            // never fetched has zero clean gaps by absence; a ticker it has
            // fetched has however many its filings actually carry. Both fail the
            // floor and only one of them is about the data.
            if (!fundamentals.TryGetValue(p.Ticker, out var f))
            {
                rejectedNoFundamentals++;
                continue;
            }

            if (f.CleanGaps < settings.MinCleanGaps)
            {
                rejectedCleanGaps++;
                continue;
            }

            if (f.SharesOutstanding is not decimal shares || shares <= 0)
            {
                // No readable share count means no market capitalisation, and D-4's
                // floor cannot be applied. Absent is not zero and not a pass.
                rejectedNoShares++;
                continue;
            }

            var marketCap = shares * p.Close;
            if (marketCap < settings.MinMarketCap)
            {
                rejectedMarketCap++;
                continue;
            }

            members.Add(new Member(
                p.Ticker, name, sectors.GetValueOrDefault(p.Ticker), marketCap,
                Bucket(marketCap, settings.LargeFloor, settings.MidFloor),
                p.FirstSeen, p.LastSeen));
        }

        // Ordinal, so two runs over the same data write in the same order.
        members.Sort(static (a, b) => string.CompareOrdinal(a.Ticker, b.Ticker));

        var detail = string.Format(
            CultureInfo.InvariantCulture,
            "{0:N0} names on {1}. Rejected: {2:N0} not common stock, {3:N0} already delisted on this " +
            "date, {4:N0} with no fundamentals fetched yet, {5:N0} fetched but below {6} clean filing " +
            "gaps, {7:N0} with no readable share count, {8:N0} below the market cap floor. " +
            "Candidates passing price, liquidity and history: {9:N0}",
            members.Count, Iso(asOf), rejectedType, rejectedDelisted, rejectedNoFundamentals,
            rejectedCleanGaps, settings.MinCleanGaps, rejectedNoShares, rejectedMarketCap, liquid.Count);

        return new Day(members, detail);
    }

    /// <summary>
    /// One row per member, plus one `is_active = false` row for every ticker that was a
    /// member on the previous evaluation date and is not one now.
    ///
    /// **The departure row is what makes C11's join correct.** Readers take the most
    /// recent `security_daily` row at or before the date [D-92], so without it a name
    /// that left the universe in 2023 resolves to its last `true` row for every date
    /// after, and is a member of every subsequent cell. The three per-date columns are
    /// null on a departure because they describe a membership that has ended, and null
    /// means unknown rather than zero [`CLAUDE.md` §6].
    /// </summary>
    private static async Task<long> WriteDailyAsync(
        StageContext context, DateOnly date, List<Member> members,
        IReadOnlySet<string> previous, CancellationToken ct)
    {
        var departed = Departures(previous, members.Select(static m => m.Ticker));

        return await context.Data.BulkUpsertAsync(
            "security_daily", DailyColumns, DailyConflictTarget,
            async (w, c) =>
            {
                foreach (var m in members)
                {
                    await w.StartRowAsync(c).ConfigureAwait(false);
                    await w.WriteAsync(m.Ticker, c).ConfigureAwait(false);
                    await w.WriteAsync(date, c).ConfigureAwait(false);
                    await w.WriteAsync(m.Sector, c).ConfigureAwait(false);
                    await w.WriteAsync(m.Bucket, c).ConfigureAwait(false);
                    await w.WriteAsync(m.MarketCap, c).ConfigureAwait(false);
                    await w.WriteAsync(true, c).ConfigureAwait(false);
                }

                foreach (var ticker in departed)
                {
                    await w.StartRowAsync(c).ConfigureAwait(false);
                    await w.WriteAsync(ticker, c).ConfigureAwait(false);
                    await w.WriteAsync(date, c).ConfigureAwait(false);
                    await w.WriteAsync((string?) null, c).ConfigureAwait(false);
                    await w.WriteAsync((string?) null, c).ConfigureAwait(false);
                    await w.WriteAsync((decimal?) null, c).ConfigureAwait(false);
                    await w.WriteAsync(false, c).ConfigureAwait(false);
                }
            }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Who was a member before and is not one now, ordered [3.12].
    ///
    /// **One rule, and both paths call it.** The nightly path takes
    /// <paramref name="previous"/> from <see cref="PreviousMembersAsync"/> and the range
    /// path from the date it evaluated last, and that is the only difference between
    /// them. Public so the property can be asserted against the rule rather than against
    /// a copy of it: a component whose two entry points retire members by two pieces of
    /// similar-looking code is one where they diverge and nothing fails.
    /// </summary>
    public static IReadOnlyList<string> Departures(IReadOnlySet<string> previous, IEnumerable<string> current)
    {
        var held = current.ToHashSet(StringComparer.Ordinal);

        return previous
            .Where(t => !held.Contains(t))
            .OrderBy(static t => t, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Who was a member immediately before <paramref name="date"/>, which is the set a
    /// departure is measured against [3.12].
    ///
    /// **Strictly before, not at or before.** A re-run of the same date would otherwise
    /// read the rows it is about to replace and find every member of that date already
    /// present, so nothing would ever depart. Bounded to the previous date, the answer is
    /// the same on a first run and a re-run, which is what D-68's per-grain idempotence
    /// requires of a stage that can be run twice.
    /// </summary>
    private static async Task<IReadOnlySet<string>> PreviousMembersAsync(
        StageContext context, DateOnly date, CancellationToken ct)
    {
        var rows = await context.Data.ReadAsync(
            "security_daily", Universe.MembersAsOf(date.AddDays(-1)), ct).ConfigureAwait(false);

        return rows.Select(r => (string) r[0]!).ToHashSet(StringComparer.Ordinal);
    }

    private static Task<long> WriteIdentityAsync(
        StageContext context, IReadOnlyList<Member> members, Listing listing, CancellationToken ct)
        => WriteIdentityAsync(
            context,
            members.ToDictionary(
                static m => m.Ticker,
                static m => new Identity(m.Name, m.FirstSeen, m.LastSeen),
                StringComparer.Ordinal),
            listing, ct);

    private static async Task<long> WriteIdentityAsync(
        StageContext context, IReadOnlyDictionary<string, Identity> identity,
        Listing listing, CancellationToken ct)
    {
        var ordered = identity.OrderBy(static kv => kv.Key, StringComparer.Ordinal).ToList();

        return await context.Data.BulkUpsertAsync(
            "security", Columns, ConflictTarget,
            async (w, c) =>
            {
                foreach (var (ticker, id) in ordered)
                {
                    await w.StartRowAsync(c).ConfigureAwait(false);
                    await w.WriteAsync(ticker, c).ConfigureAwait(false);
                    await w.WriteAsync(id.Name, c).ConfigureAwait(false);
                    await w.WriteAsync(id.FirstSeen, c).ConfigureAwait(false);
                    await w.WriteAsync(id.LastSeen, c).ConfigureAwait(false);
                    await w.WriteAsync(
                        listing.DelistedOn.TryGetValue(ticker, out var d) ? d : (DateOnly?) null,
                        c).ConfigureAwait(false);
                }
            }, ct).ConfigureAwait(false);
    }

    private readonly record struct Liquid(string Ticker, decimal Close, DateOnly FirstSeen, DateOnly LastSeen);

    private readonly record struct Fund(int CleanGaps, decimal? SharesOutstanding);

    /// <summary>
    /// Price, liquidity and history, as of the date being built.
    ///
    /// **The 20-day median dollar volume is computed here from `price_daily`, never
    /// taken from a provider average** [1.5]. The probe's own sample selection used
    /// `avgvol_50d * adjusted_close` and understated for any name that split inside
    /// the window, because average volume is unadjusted while adjusted close is
    /// adjusted. A median rather than a mean, and computed from the same bars every
    /// other stage reads.
    ///
    /// **Rewritten at D-102 and D-4's criteria are unchanged.** The set is the same;
    /// what changed is how it is derived. Measured against the 109,787,541 row store:
    /// the old statement took 871.1s and this one is proved to return the identical
    /// ticker set, compared set against set rather than by count [`PROGRESS.md`].
    ///
    /// **The old shape built a hundred and nine million rows to keep fifty-eight
    /// thousand.** `row_number() OVER (PARTITION BY ticker ORDER BY date DESC)` across
    /// every row matching the date bound materialised a 5.4 GB on-disk CTE and sorted
    /// it externally at about 4.5 GB across three workers, and every node above it
    /// re-read that spill: 19 GB of temporary I/O for 8,610 rows. The whole table was
    /// then scanned a second time for `span`.
    ///
    /// **What replaces it is a seek per ticker rather than a scan**, which is the same
    /// change item 24 made to C03's pool and is why D-102 covers both. The distinct
    /// tickers come off the `(ticker, date)` primary key, a LATERAL takes each ticker's
    /// twenty most recent bars by index however far back they are, which is exactly
    /// what `rn <= 20` meant, and the history test is asked last of the few thousand
    /// names that already cleared price and liquidity rather than of all 88,341.
    ///
    /// **The history test is an `EXISTS ... OFFSET` and not a `count(*)`, and that is
    /// worth 2.2x on its own.** A first attempt used `count(*) >= 250` inside a LATERAL,
    /// which reads every bar a ticker has, and measured 948.7s against the old shape's
    /// 2,090.8s. `EXISTS ... OFFSET 249 LIMIT 1` stops at the 250th index entry, which
    /// is the same question asked in a way that can stop early, and is exactly what C03
    /// already does. `first_seen` and `last_seen` come from `min` and `max` on the same
    /// index, each of which Postgres answers as a one-row seek rather than an aggregate.
    ///
    /// `count(*) >= min_history_days` is preserved as an offset rather than replaced by
    /// a calendar test: "has at least N bars" and "has a bar N days ago" are different
    /// questions and the second admits a ticker with ten bars spread over a year.
    ///
    /// **It stays date-parametric and that is load-bearing for 3.11**, which runs this
    /// per weekly evaluation date across the window. Every bound is `<= asOf`, so the
    /// statement answers for a historical date exactly as it answers for today, which
    /// is the property a summary of current state could not have given it [D-102].
    /// </summary>
    private static async Task<IReadOnlyList<Liquid>> LiquidAsync(
        StageContext context, DateOnly evaluationDate, Settings settings, CancellationToken ct)
    {
        var (minPrice, minAdv, minHistory, statementTimeout) =
            (settings.MinPrice, settings.MinAdv, settings.MinHistory, settings.StatementTimeout);

        var asOf = evaluationDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var sql = $"""
            WITH tickers AS (
                SELECT DISTINCT ticker FROM price_daily WHERE date <= DATE '{asOf}'
            ),
            recent AS (
                SELECT t.ticker, b.close, b.volume, b.rn
                FROM tickers t
                CROSS JOIN LATERAL (
                    SELECT close, volume, row_number() OVER (ORDER BY date DESC) AS rn
                    FROM price_daily p
                    WHERE p.ticker = t.ticker AND p.date <= DATE '{asOf}'
                    ORDER BY p.date DESC
                    LIMIT {DollarVolume.WindowBars.ToString(CultureInfo.InvariantCulture)}
                ) b
            ),
            latest AS (SELECT ticker, close FROM recent WHERE rn = 1),
            mdv AS (
                SELECT ticker,
                       {DollarVolume.MedianExpression} AS median_dollar_volume
                FROM recent
                WHERE {DollarVolume.RowFilter}
                GROUP BY ticker
            ),
            liquid AS (
                SELECT l.ticker, l.close
                FROM latest l
                JOIN mdv m ON m.ticker = l.ticker
                WHERE l.close >= {minPrice.ToString(CultureInfo.InvariantCulture)}
                  AND m.median_dollar_volume >= {minAdv.ToString(CultureInfo.InvariantCulture)}
            )
            SELECT q.ticker, q.close,
                   (SELECT min(f.date) FROM price_daily f
                     WHERE f.ticker = q.ticker AND f.date <= DATE '{asOf}') AS first_seen,
                   (SELECT max(g.date) FROM price_daily g
                     WHERE g.ticker = q.ticker AND g.date <= DATE '{asOf}') AS last_seen
            FROM liquid q
            WHERE EXISTS (
                SELECT 1 FROM price_daily h
                WHERE h.ticker = q.ticker AND h.date <= DATE '{asOf}'
                OFFSET {(minHistory - 1).ToString(CultureInfo.InvariantCulture)} LIMIT 1
            )
            ORDER BY q.ticker;
            """;

        var rows = await context.Data
            .ReadAsync("price_daily", sql, ct, statementTimeout).ConfigureAwait(false);

        return rows.Select(r => new Liquid(
            (string) r[0]!,
            (decimal) r[1]!,
            DateOnly.FromDateTime((DateTime) r[2]!),
            DateOnly.FromDateTime((DateTime) r[3]!))).ToList();
    }

    /// <summary>
    /// The clean gap count and the newest readable share count, both as of the date
    /// being built.
    ///
    /// The gap count filters on `filing_date_effective <= date`, so it is what a
    /// live system on that date would have seen rather than what is known now
    /// [M.1]. The share count filters the same way, because a market capitalisation
    /// built from a filing nobody had yet is the lookahead INVARIANT 12 exists to
    /// prevent arriving through a different door.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, Fund>> FundamentalsAsync(
        StageContext context, DateOnly evaluationDate, CancellationToken ct)
    {
        var asOf = evaluationDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var sql = $"""
            WITH readable AS (
                SELECT * FROM fundamental_snapshot
                WHERE filing_date_effective IS NOT NULL AND filing_date_effective <= DATE '{asOf}'
            ),
            gaps AS (
                SELECT ticker, count(*) AS clean_gaps FROM readable
                WHERE filing_date_unknown_reason = 'none' GROUP BY ticker
            ),
            shares AS (
                SELECT DISTINCT ON (ticker) ticker, shares_outstanding
                FROM readable WHERE shares_outstanding IS NOT NULL
                ORDER BY ticker, period_end DESC
            )
            SELECT coalesce(g.ticker, s.ticker), coalesce(g.clean_gaps, 0), s.shares_outstanding
            FROM gaps g FULL OUTER JOIN shares s ON s.ticker = g.ticker;
            """;

        var rows = await context.Data.ReadAsync("fundamental_snapshot", sql, ct).ConfigureAwait(false);

        var map = new Dictionary<string, Fund>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            map[(string) r[0]!] = new Fund(
                Convert.ToInt32(r[1], CultureInfo.InvariantCulture),
                r[2] is null ? null : (decimal) r[2]!);
        }

        return map;
    }

    /// <summary>
    /// Sector, read from the most recent filing at or before the date being built
    /// [D-97].
    ///
    /// **This was a call per member and is now a read.** At 10 units a name over 2,840
    /// members that was 28,401 units a week, buying what `fundamentals/{t}` already
    /// carries for nothing in a call C03 makes anyway. Removing it is what makes a
    /// per-date C01 affordable at all.
    ///
    /// **As of the date being built, not as of now**, which the call could never be.
    /// A backfilled 2021 date reads the sector on the newest filing readable then,
    /// where the call read today's. Sector as of a filing is still not sector as of a
    /// date, and that limitation is recorded rather than proxied [D-97].
    ///
    /// Null where the ticker has no readable filing carrying one, which resolves to the
    /// percentile engine's existing bucket-only fallback rather than to a new case.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, string?>> SectorsAsync(
        StageContext context, DateOnly evaluationDate, CancellationToken ct)
    {
        var asOf = evaluationDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var sql = $"""
            SELECT DISTINCT ON (ticker) ticker, sector
            FROM fundamental_snapshot
            WHERE sector IS NOT NULL
              AND filing_date_effective IS NOT NULL
              AND filing_date_effective <= DATE '{asOf}'
            ORDER BY ticker, filing_date_effective DESC, period_end DESC;
            """;

        var rows = await context.Data.ReadAsync("fundamental_snapshot", sql, ct).ConfigureAwait(false);

        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            map[(string) r[0]!] = r[1] as string;
        }

        return map;
    }

    private static async Task<long> LongAsync(StageContext context, string key, CancellationToken ct)
    {
        var row = await context.Config.RequireAsync(key, context.Date, ct).ConfigureAwait(false);
        return long.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new InvalidOperationException($"{key} resolved to '{row.Value}', which is not a whole number.");
    }

    private static async Task<decimal> DecimalAsync(StageContext context, string key, CancellationToken ct)
    {
        var row = await context.Config.RequireAsync(key, context.Date, ct).ConfigureAwait(false);
        return decimal.TryParse(row.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new InvalidOperationException($"{key} resolved to '{row.Value}', which is not a number.");
    }
}
