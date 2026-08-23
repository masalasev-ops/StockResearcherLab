using System.Globalization;
using StockResearcherLab.Api.Contracts;
using StockResearcherLab.Core;
using StockResearcherLab.Core.Config;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;

namespace StockResearcherLab.Api;

/// <summary>
/// C36. What the store holds for one ticker on one date, and where each number came
/// from [D-109].
///
/// **A reader, not a stage.** It takes no date-and-config-version, nothing schedules it
/// and it produces nothing to replay. What it shares with a stage is the only thing
/// that matters here: it declares what it reads as data, and `ARCHITECTURE.html` §3
/// carries that declaration where the conformance test can hold it against this class
/// in both directions [D-74]. A reader outside the catalogue is a reader nothing
/// checks, and D-74's record says what that cost the last time.
///
/// **Nothing here computes anything.** Every figure is read from a store; where one
/// would have to be derived, the panel carries the inputs and says so. Selecting,
/// ordering and filtering rows is reading, including taking the most recent row at or
/// before a date, which is the shape every stage already uses. Arithmetic over stored
/// values is computing, and so is a comparison whose answer this then labels.
///
/// **The read-only guarantee is structural rather than promised.** Data arrives through
/// the same <see cref="IStageData"/> route a stage uses, behind a
/// <see cref="DeclaredAccess"/> built from <see cref="ReadSet"/> and an empty write set,
/// so an undeclared table throws before a connection opens and any write at all throws
/// as undeclared. That is the guard `StageRunnerTests` proves with the trespassing-stage
/// fixture, reused rather than rebuilt.
///
/// **It lives in the Api, which never references Pipeline** [`CLAUDE.md` §4], so no page
/// can invoke a stage. `Universe.AsOf` moved to `Core` at 3.5.1 for exactly this: the
/// membership read is one statement with one copy rather than a second one here.
/// </summary>
public sealed class RecordInspector : IReadOwner
{
    private readonly IStageData _data;
    private readonly IConfigStore _config;

    public RecordInspector(string connectionString)
        : this(new StageData(connectionString, Access()), new ConfigStore(connectionString))
    {
    }

    /// <summary>
    /// The declared access the production route runs behind: this read set, and an
    /// empty write set.
    ///
    /// **Public so the test asserts over the object the constructor uses rather than
    /// over a reconstruction of it.** A test that rebuilt the same arguments would pass
    /// while the constructor passed different ones, which is the shape of every
    /// declaration this repository has found disagreeing with its code.
    /// </summary>
    public static DeclaredAccess Access() => new(ComponentName, Tables, []);

    /// <summary>
    /// The seam the tests use. It takes the two routes rather than a connection string
    /// so a test can exercise the panel logic without a store, and so the empty write
    /// set stays this class's own statement rather than something a caller supplies.
    /// </summary>
    public RecordInspector(IStageData data, IConfigStore config)
    {
        _data = data;
        _config = config;
    }

    private const string ComponentName = "RecordInspector";

    /// <summary>
    /// Declared as data and held against §3's Reads cell in both directions.
    ///
    /// It grows one checkpoint at a time rather than being declared whole in advance: a
    /// declaration naming a table nothing reads yet is a claim the conformance test
    /// would pass over, and the point of the check is that the cell and the code say the
    /// same thing at every commit.
    /// </summary>
    private static readonly string[] Tables = ["security", "security_daily", "universe_rejection"];

    /// <summary>
    /// D-4's criteria, in C01's own test order, so the panel lists them the way the
    /// evaluation reaches them.
    ///
    /// **Named here and resolved as of the viewed date**, never as of now [D-43,
    /// INVARIANT 13]. `universe.pool_statement_timeout_seconds` is deliberately absent:
    /// it bounds a statement rather than admitting or rejecting a name.
    /// </summary>
    public static readonly string[] CriterionKeys =
    [
        "universe.min_price",
        "universe.min_adv_20d",
        "universe.min_history_days",
        "universe.min_market_cap",
        "fundamentals.min_clean_gaps_for_substitution",
        "universe.bucket_large_floor",
        "universe.bucket_mid_floor",
    ];

    public string Name => ComponentName;

    public IReadOnlyList<string> ReadSet => Tables;

    /// <summary>The whole record for one name on one date. Panels are added a checkpoint at a time.</summary>
    public async Task<RecordView> ReadAsync(string ticker, DateOnly date, CancellationToken ct = default)
        => new(ticker, date, await MembershipAsync(ticker, date, ct).ConfigureAwait(false));

    /// <summary>
    /// Membership, and for a name that is not a member the criterion C01 stopped on.
    ///
    /// **Four reads and no arithmetic.** Identity, the `security_daily` row in force,
    /// the `universe_rejection` row in force, and the criteria as they stood. The panel
    /// does not decide whether the name is a member: presence of an active row says
    /// admitted, presence of a rejection says rejected, and absence of both says the
    /// name was not evaluated on that date, which is a third state and not a blank
    /// [`SCHEMA.md`, `universe_rejection`].
    /// </summary>
    public async Task<MembershipPanel> MembershipAsync(
        string ticker, DateOnly date, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);

        var identity = await IdentityAsync(ticker, ct).ConfigureAwait(false);
        var inForce = await InForceAsync(ticker, date, ct).ConfigureAwait(false);
        var rejection = await RejectionAsync(ticker, date, ct).ConfigureAwait(false);
        var thresholds = await ThresholdsAsync(date, ct).ConfigureAwait(false);

        return new MembershipPanel(identity, inForce, rejection, thresholds);
    }

    private async Task<SecurityIdentity?> IdentityAsync(string ticker, CancellationToken ct)
    {
        var rows = await _data.ReadAsync(
            "security",
            $"""
             SELECT name, first_seen, last_seen, delisted_date
             FROM security
             WHERE ticker = {Literal(ticker)};
             """,
            ct).ConfigureAwait(false);

        return rows.Count == 0
            ? null
            : new SecurityIdentity(
                rows[0][0] as string, Date(rows[0][1]), Date(rows[0][2]), Date(rows[0][3]));
    }

    /// <summary>
    /// The row in force, and the evaluation date it was written on.
    ///
    /// **The date the row was written on is carried rather than dropped**, because C01
    /// evaluates weekly and a Wednesday reads the Sunday before it. A panel showing the
    /// bucket without the date it was decided on invites a reader to take it for a daily
    /// fact, which is the reading `security_daily` exists to make impossible.
    ///
    /// `is_active` is not filtered, for the reason <see cref="Universe.AsOf"/> gives:
    /// filtering inside the pick resurrects a name whose latest row is the departure
    /// that ended its membership.
    /// </summary>
    private async Task<MembershipRow?> InForceAsync(string ticker, DateOnly date, CancellationToken ct)
    {
        var rows = await _data.ReadAsync(
            "security_daily",
            $"""
             SELECT s.date, s.sector, s.size_bucket, s.market_cap, s.is_active
             FROM security_daily s
             WHERE s.ticker = {Literal(ticker)} AND s.date <= DATE '{Iso(date)}'
             ORDER BY s.date DESC
             LIMIT 1;
             """,
            ct).ConfigureAwait(false);

        return rows.Count == 0
            ? null
            : new MembershipRow(
                Date(rows[0][0])!.Value,
                rows[0][1] as string,
                rows[0][2] as string,
                rows[0][3] as decimal?,
                (bool) rows[0][4]!);
    }

    /// <summary>
    /// The rejection in force, read the same as-of way as membership and for the same
    /// reason: C01 writes both on its weekly cadence, so a date between two evaluations
    /// reads the one before it.
    /// </summary>
    private async Task<RejectionRow?> RejectionAsync(string ticker, DateOnly date, CancellationToken ct)
    {
        var rows = await _data.ReadAsync(
            "universe_rejection",
            $"""
             SELECT r.date, r.criterion
             FROM universe_rejection r
             WHERE r.ticker = {Literal(ticker)} AND r.date <= DATE '{Iso(date)}'
             ORDER BY r.date DESC
             LIMIT 1;
             """,
            ct).ConfigureAwait(false);

        return rows.Count == 0
            ? null
            : new RejectionRow(Date(rows[0][0])!.Value, (string) rows[0][1]!);
    }

    /// <summary>
    /// Every criterion's value as it stood on the viewed date.
    ///
    /// `ResolveAsync` rather than `RequireAsync`: a date before a key came into force
    /// has no value, and that is a fact to show rather than a run to fail. A viewer is
    /// the one reader for which an absent config row is information [D-72].
    /// </summary>
    private async Task<IReadOnlyList<ThresholdInForce>> ThresholdsAsync(DateOnly date, CancellationToken ct)
    {
        var resolved = new List<ThresholdInForce>(CriterionKeys.Length);

        foreach (var key in CriterionKeys)
        {
            var row = await _config.ResolveAsync(key, date, ct).ConfigureAwait(false);
            resolved.Add(new ThresholdInForce(key, row?.Value, row?.Version, row?.SetOn));
        }

        return resolved;
    }

    private static DateOnly? Date(object? value)
        => value is DateTime d ? DateOnly.FromDateTime(d) : null;

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// A ticker as a SQL literal, with quotes doubled.
    ///
    /// The statement is interpolated because every statement in this repository is, and
    /// the one value that arrives from outside is escaped here rather than trusted. A
    /// ticker reaches this class from a route parameter, which is the only place in the
    /// system where a string a user typed reaches SQL at all.
    /// </summary>
    private static string Literal(string ticker)
        => "'" + ticker.Replace("'", "''", StringComparison.Ordinal) + "'";
}
