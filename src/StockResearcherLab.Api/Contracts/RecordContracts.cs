namespace StockResearcherLab.Api.Contracts;

/// <summary>
/// What the store holds for one ticker on one date, panel by panel [D-109].
///
/// **Every field here is a stored value or an absence, never a derived one.** Where a
/// figure would have to be computed the contract carries the inputs and a line saying
/// so, which is the phase's first rule expressed in the type rather than promised in a
/// review. A second implementation of a metric is the defect phase 2 and phase 3 kept
/// finding, and a viewer is where one creeps in unnoticed because its wrong number
/// reads as a display bug.
///
/// **Absence is a state and not an empty value.** A null here means the store holds
/// nothing, which is a different fact from zero and from not-yet-populated, and the
/// panels render the three differently [`CLAUDE.md` §6].
/// </summary>
public sealed record RecordView(
    string Ticker,
    DateOnly Date,
    MembershipPanel Membership,
    MetricsPanel Metrics);

/// <summary>
/// Every ranked metric with three figures beside it: the raw value, the percentile, and
/// the size of the cell the percentile was ranked in [D-107].
///
/// **The third is the one that makes the other two readable.** A percentile over three
/// members and one over eighty are the same number without it, and the fifteen-member
/// fallback is theoretical until a reader can see the cell it fired on.
/// </summary>
/// <param name="Cell">
/// The cell this name belonged to on this date, being its size bucket and sector from
/// the `security_daily` row in force. Null when the name had no row, in which case it
/// belonged to no cell and every metric reads as unranked.
/// </param>
/// <param name="Populated">
/// Whether the populating pass has reached this date. False means the cell figures are
/// not written yet, which is a different state from a cell that does not exist and
/// renders differently [D-106, D-107].
/// </param>
/// <param name="CoveredFrom">The earliest date the pass has reached, null before it has run at all.</param>
public sealed record MetricsPanel(
    MetricCell? Cell,
    bool Populated,
    DateOnly? CoveredFrom,
    DateOnly? CoveredTo,
    IReadOnlyList<MetricRow> Metrics);

/// <summary>The cell a name belonged to, which is the pair C11 partitions on [D-10].</summary>
public sealed record MetricCell(string? SizeBucket, string? Sector);

/// <param name="Value">
/// The raw value as stored. Null means the metric could not be computed for this name on
/// this date, which is a different fact from zero.
/// </param>
/// <param name="Percentile">Null where nothing ranked it, which the scope says the reason for.</param>
/// <param name="CellMembers">
/// The non-null population of this metric in this name's `(size_bucket, sector)` cell.
/// Null where the name has no sector and so formed no cell.
/// </param>
/// <param name="RankedScope">
/// `cell`, `bucket` or `none`, stored by C11 rather than derived here. The page does not
/// compare a count against a floor and label the answer.
/// </param>
public sealed record MetricRow(
    string Table,
    string Metric,
    decimal? Value,
    double? Percentile,
    int? CellMembers,
    int? BucketMembers,
    int? MinMembers,
    string? RankedScope);

/// <summary>
/// Was this name a member on that date, and if not, which criterion stopped it.
///
/// The rejected case is the one this panel exists for: "why is this obvious company not
/// in my universe" is the question that took a query nobody could write, three of the
/// nine criteria having no persisted input to re-derive a verdict from [D-108].
/// </summary>
/// <param name="Identity">
/// From `security`, which carries identity and lifespan and nothing that varies by
/// date. Null when the store has never seen the ticker at all.
/// </param>
/// <param name="InForce">
/// The `security_daily` row in force on the date, being the most recent at or before it
/// and never the newest outright [D-92]. Null when no evaluation had produced a row for
/// this ticker by that date.
/// </param>
/// <param name="Rejection">
/// The `universe_rejection` row in force on the date, read the same as-of way. Null
/// when the name was admitted, or when it was not evaluated at all.
/// </param>
/// <param name="Thresholds">
/// The criteria's values as they stood on the viewed date, never as they stand now
/// [D-43, INVARIANT 13]. A page showing today's floors beside a 2022 verdict would
/// answer a different question and look right doing it.
/// </param>
public sealed record MembershipPanel(
    SecurityIdentity? Identity,
    MembershipRow? InForce,
    RejectionRow? Rejection,
    IReadOnlyList<ThresholdInForce> Thresholds);

/// <param name="FirstSeen">
/// The earliest bar the store holds, not the earliest that existed. C01 recomputes both
/// columns from `price_daily` on every run, so a prune moves them forward [`SCHEMA.md`,
/// item 51].
/// </param>
public sealed record SecurityIdentity(
    string? Name,
    DateOnly? FirstSeen,
    DateOnly? LastSeen,
    DateOnly? DelistedDate);

/// <param name="EvaluationDate">
/// The date the row in force was written on, which is not the date being viewed. C01
/// evaluates weekly, so a Wednesday reads the Sunday before it, and showing which one
/// is what stops a reader taking the row for a daily fact.
/// </param>
/// <param name="IsActive">
/// False on a departure row, which C01 writes when a name that was a member is not one
/// now. A departure is a stored fact rather than an absence, and the panel says so.
/// </param>
public sealed record MembershipRow(
    DateOnly EvaluationDate,
    string? Sector,
    string? SizeBucket,
    decimal? MarketCap,
    bool IsActive);

/// <param name="Criterion">
/// The criterion the evaluation stopped on, not the only one the name failed. D-4 is a
/// conjunction tested in a fixed order, so a name below the market cap floor that is
/// also thinly traded records the thin trading [D-108].
/// </param>
public sealed record RejectionRow(DateOnly EvaluationDate, string Criterion);

/// <summary>One configuration value as it stood on the viewed date, with the version that says which one it was.</summary>
public sealed record ThresholdInForce(string Key, string? Value, int? Version, DateOnly? SetOn);
