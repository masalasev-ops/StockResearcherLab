using System.Globalization;
using StockResearcherLab.Api;
using StockResearcherLab.Api.Contracts;
using StockResearcherLab.Core.Config;
using StockResearcherLab.Core.Stages;
using Xunit;

namespace StockResearcherLab.Tests.Api;

/// <summary>
/// C36's two structural guarantees and the one behaviour that would break silently
/// [D-109].
///
/// **The write set is empty and that is the read-only guarantee**, not a convention and
/// not a review note. Every write operation on every table it declares is refused before
/// a connection opens, by the same <see cref="DeclaredAccess"/> a stage runs behind.
///
/// **The thresholds resolve as of the date being viewed.** A page reading today's floors
/// beside a 2022 verdict answers a different question and looks right doing it, which is
/// the failure mode INVARIANT 13 exists for and the one a viewer is most likely to have.
/// </summary>
public sealed class RecordInspectorTests
{
    /// <summary>
    /// Every declared table, every operation, refused. Asserted over
    /// <see cref="RecordInspector.Access"/> itself rather than over a rebuild of its
    /// arguments, so a constructor that passed something else would fail here.
    /// </summary>
    [Fact]
    public void EveryWriteOnEveryDeclaredTableIsRefused()
    {
        var access = RecordInspector.Access();

        Assert.Empty(access.WriteSet);
        Assert.NotEmpty(access.ReadSet);

        foreach (var table in access.ReadSet)
        {
            Assert.True(access.CanRead(table), $"{table} is declared and is not readable.");

            foreach (var operation in Enum.GetValues<WriteOperation>())
            {
                Assert.False(access.CanWrite(table, operation),
                    $"{table} accepts {operation} through a reader that declares no write at all.");

                Assert.Throws<UndeclaredTableAccessException>(
                    () => access.EnsureCanWrite(table, operation));
            }
        }
    }

    /// <summary>
    /// A table outside the declaration is refused for reading too, which is the half
    /// that stops the page quietly growing a dependency the catalogue does not carry.
    /// </summary>
    [Fact]
    public void ATableOutsideTheDeclarationIsNotReadable()
    {
        var access = RecordInspector.Access();

        Assert.False(access.CanRead("proposal"));
        Assert.Throws<UndeclaredTableAccessException>(() => access.EnsureCanRead("proposal"));
    }

    /// <summary>
    /// The declaration in code and the one the interface exposes are the same list. The
    /// conformance test holds the interface against the catalogue, so a constructor
    /// building its guard from a different list would leave the guard unchecked.
    /// </summary>
    [Fact]
    public void TheGuardAndTheDeclaredReadSetAreTheSameList()
    {
        var inspector = new RecordInspector("Host=nowhere;Database=none");

        Assert.Equal(RecordInspector.Access().ReadSet, inspector.ReadSet);
        Assert.Equal("RecordInspector", inspector.Name);
        Assert.Equal("RecordInspector", RecordInspector.Access().StageName);
    }

    /// <summary>
    /// **Every threshold is resolved for the date being viewed and never for another
    /// one.** The double records the dates it was asked for, which is the property at
    /// risk: a page reading `Today` would produce a panel that is correct-looking and
    /// wrong on every historical date [D-43, INVARIANT 13].
    /// </summary>
    [Fact]
    public async Task EveryThresholdIsResolvedAsOfTheDateBeingViewed()
    {
        var ct = TestContext.Current.CancellationToken;
        var viewed = new DateOnly(2022, 6, 15);
        var config = new RecordingConfig();

        await new RecordInspector(new NoRows(), config).MembershipAsync("AAPL.US", viewed, ct)
            .ConfigureAwait(true);

        Assert.NotEmpty(config.AskedFor);
        Assert.All(config.AskedFor, d => Assert.Equal(viewed, d));
        Assert.Equal(RecordInspector.CriterionKeys.Length, config.AskedFor.Count);
    }

    /// <summary>
    /// Two versions of one key, either side of the viewed date, resolved through the
    /// production rule rather than through a stub of it. The earlier date takes the
    /// older value and the later date the newer one, which is what the panel showing a
    /// 2022 floor beside a 2022 verdict depends on.
    /// </summary>
    [Theory]
    [InlineData(2021, 1, 1, "5")]
    [InlineData(2023, 1, 1, "8")]
    public async Task AThresholdTakesTheVersionInForceOnTheViewedDate(int y, int m, int d, string expected)
    {
        var ct = TestContext.Current.CancellationToken;

        var config = new TwoVersions(
            "universe.min_price",
            new ConfigRow("universe.min_price", 1, "5", new DateOnly(2020, 1, 1)),
            new ConfigRow("universe.min_price", 2, "8", new DateOnly(2022, 6, 1)));

        var panel = await new RecordInspector(new NoRows(), config)
            .MembershipAsync("AAPL.US", new DateOnly(y, m, d), ct).ConfigureAwait(true);

        var price = panel.Thresholds.Single(t => t.Key == "universe.min_price");

        Assert.Equal(expected, price.Value);
    }

    /// <summary>
    /// A key that had not come into force on the viewed date reads absent rather than
    /// taking the newest row. That is the resolver's rule and the panel has to carry it
    /// through rather than falling back, because a fallback is exactly how today's
    /// configuration reaches a historical date [D-72].
    /// </summary>
    [Fact]
    public async Task AKeyNotYetInForceReadsAbsentRatherThanTheNewestValue()
    {
        var ct = TestContext.Current.CancellationToken;

        var config = new TwoVersions(
            "universe.min_price",
            new ConfigRow("universe.min_price", 1, "5", new DateOnly(2020, 1, 1)),
            new ConfigRow("universe.min_price", 2, "8", new DateOnly(2022, 6, 1)));

        var panel = await new RecordInspector(new NoRows(), config)
            .MembershipAsync("AAPL.US", new DateOnly(2019, 1, 1), ct).ConfigureAwait(true);

        var price = panel.Thresholds.Single(t => t.Key == "universe.min_price");

        Assert.Null(price.Value);
        Assert.Null(price.Version);
    }

    /// <summary>
    /// A ticker the store has never seen produces three absences rather than an error or
    /// an empty-looking row. Absence is a state the panel renders, and it is a different
    /// one from a departure and from a rejection [`CLAUDE.md` §6].
    /// </summary>
    [Fact]
    public async Task ATickerTheStoreHasNeverSeenIsThreeAbsences()
    {
        var ct = TestContext.Current.CancellationToken;

        var panel = await new RecordInspector(new NoRows(), new RecordingConfig())
            .MembershipAsync("NOTHING.US", new DateOnly(2022, 6, 15), ct).ConfigureAwait(true);

        Assert.Null(panel.Identity);
        Assert.Null(panel.InForce);
        Assert.Null(panel.Rejection);
        Assert.NotEmpty(panel.Thresholds);
    }

    /// <summary>
    /// The reader reaches only what it declares. Asserted over the tables it actually
    /// asked for, because the guard proves a table outside the set is refused and this
    /// proves the set is not wider than the work.
    /// </summary>
    [Fact]
    public async Task ItReadsExactlyTheTablesItDeclares()
    {
        var ct = TestContext.Current.CancellationToken;
        var data = new WithMembership();

        await new RecordInspector(data, new RecordingConfig())
            .ReadAsync("AAPL.US", new DateOnly(2022, 6, 15), ct).ConfigureAwait(true);

        // The whole record rather than one panel, because the declaration covers the
        // page and a per-panel assertion would pass while the set was wider than the
        // work. Distinct, since a panel may read a table another panel already read.
        Assert.Equal(
            RecordInspector.Access().ReadSet.Order(StringComparer.Ordinal),
            data.Touched.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// **A name with no membership row is not looked up in the cell store**, and that is
    /// the behaviour rather than an omission: it belonged to no cell, so there is nothing
    /// keyed for it to read. Asserted because the test above needs a membership row to
    /// reach all nine tables, and without this the difference between "does not read it"
    /// and "cannot read it" would be invisible.
    /// </summary>
    [Fact]
    public async Task ANameWithNoMembershipRowIsNotLookedUpInTheCellStore()
    {
        var ct = TestContext.Current.CancellationToken;
        var data = new NoRows();

        var view = await new RecordInspector(data, new RecordingConfig())
            .ReadAsync("AAPL.US", new DateOnly(2022, 6, 15), ct).ConfigureAwait(true);

        Assert.DoesNotContain("percentile_cell_daily", data.Touched);
        Assert.Null(view.Metrics.Cell);
        Assert.All(view.Metrics.Metrics, m => Assert.Null(m.RankedScope));
    }

    /// <summary>
    /// **A member with an earlier rejection shows no rejection in force**, which is the
    /// ordinary case rather than an edge: 205,940 active `security_daily` rows over 2,228
    /// tickers carry a rejection at an earlier date, measured at the 3.5 sign-off.
    ///
    /// `ABNB.US` at 2024-06-02 is the measured instance and is used as the fixture rather
    /// than an invented one: a member on that evaluation date, carrying
    /// `insufficient_history` from 2021-12-05. Read as two independent as-of picks the
    /// panel says member and rejected at once, and `SCHEMA.md` has the two tables
    /// partitioning the evaluated population between them precisely so that cannot
    /// happen.
    /// </summary>
    [Fact]
    public async Task AMemberWithAnEarlierRejectionShowsNoRejectionInForce()
    {
        var ct = TestContext.Current.CancellationToken;

        var data = new AsOfPair(
            memberOn: new DateTime(2024, 6, 2), isActive: true,
            rejectedOn: new DateTime(2021, 12, 5), criterion: "insufficient_history");

        var panel = await new RecordInspector(data, new RecordingConfig())
            .MembershipAsync("ABNB.US", new DateOnly(2024, 6, 5), ct).ConfigureAwait(true);

        Assert.NotNull(panel.InForce);
        Assert.True(panel.InForce.IsActive);
        Assert.Null(panel.Rejection);

        // The bound is the statement's rather than this test's arithmetic, and it is the
        // in-force evaluation date exactly.
        Assert.Contains(
            "r.date >= DATE '2024-06-02'",
            data.Sql.Single(s => s.Contains("universe_rejection", StringComparison.Ordinal)),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// **A departure and its criterion share an evaluation date, and the criterion is
    /// still the answer.** That is what makes the bound inclusive rather than strict: C01
    /// writes both rows on the one date when a name leaves, the departure into
    /// `security_daily` and the criterion into `universe_rejection`, and a `&gt;` would
    /// hide the only thing that says why it left.
    ///
    /// Without this the correction above is a blanket that suppresses every rejection a
    /// name with any membership row ever had.
    /// </summary>
    [Fact]
    public async Task ADepartureAndItsCriterionShareADateAndTheCriterionIsInForce()
    {
        var ct = TestContext.Current.CancellationToken;

        var data = new AsOfPair(
            memberOn: new DateTime(2024, 6, 2), isActive: false,
            rejectedOn: new DateTime(2024, 6, 2), criterion: "below_market_cap");

        var panel = await new RecordInspector(data, new RecordingConfig())
            .MembershipAsync("ABNB.US", new DateOnly(2024, 6, 5), ct).ConfigureAwait(true);

        Assert.NotNull(panel.InForce);
        Assert.False(panel.InForce.IsActive);
        Assert.NotNull(panel.Rejection);
        Assert.Equal("below_market_cap", panel.Rejection.Criterion);
        Assert.Equal(new DateOnly(2024, 6, 2), panel.Rejection.EvaluationDate);
    }

    /// <summary>
    /// **A name with no membership row is not bounded at all**, so its rejection reads
    /// whatever C01 last wrote for it. A bound derived from an absent row would be the
    /// same blanket by another route, and this is the state `ASML.US` is in on the date
    /// the phase demonstrates.
    /// </summary>
    [Fact]
    public async Task ANameWithNoMembershipRowLeavesTheRejectionUnbounded()
    {
        var ct = TestContext.Current.CancellationToken;
        var data = new NoRows();

        await new RecordInspector(data, new RecordingConfig())
            .MembershipAsync("ASML.US", new DateOnly(2024, 6, 5), ct).ConfigureAwait(true);

        var sql = data.Sql.Single(s => s.Contains("universe_rejection", StringComparison.Ordinal));

        Assert.Contains("r.date <= DATE '2024-06-05'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("r.date >=", sql, StringComparison.Ordinal);
    }

    // ------------------------------------------ inputs and market [3.5.3, 3.5.4] ---

    /// <summary>
    /// **The filings read keys on `filing_date_effective` and never on `period_end`**
    /// [INVARIANT 12, D-46, D-62].
    ///
    /// Period end is the natural-looking key and hands a reader quarterly numbers weeks
    /// before they were public. A viewer that filtered on it would not error, would look
    /// entirely right, and would teach that reading to everyone who opens the page.
    ///
    /// **`period_end` does appear, as the tiebreak inside one effective date, and that is
    /// asserted rather than forbidden.** Two filings can share an effective date and the
    /// order between them has to be stable; a tiebreak cannot change which rows are
    /// readable, where a filter or a leading sort key can. So what this asserts is the
    /// filter and the leading sort, which are the two that decide readability.
    /// </summary>
    [Fact]
    public async Task TheFilingsReadKeysOnTheEffectiveFilingDateAndNeverOnPeriodEnd()
    {
        var ct = TestContext.Current.CancellationToken;
        var data = new NoRows();

        await new RecordInspector(data, new RecordingConfig())
            .InputsAsync("AAPL.US", new DateOnly(2022, 6, 15), ct).ConfigureAwait(true);

        var sql = data.Sql.Single(s => s.Contains("fundamental_snapshot", StringComparison.Ordinal));

        var where = sql[sql.IndexOf("WHERE", StringComparison.Ordinal)
            ..sql.IndexOf("ORDER BY", StringComparison.Ordinal)];

        Assert.Contains("filing_date_effective", where, StringComparison.Ordinal);
        Assert.DoesNotContain("period_end", where, StringComparison.Ordinal);

        var order = sql[sql.IndexOf("ORDER BY", StringComparison.Ordinal)..];
        Assert.StartsWith("ORDER BY filing_date_effective", order, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sentiment window is `sentiment.lookback_days` as of the viewed date, and the
    /// insider window is the ninety days the column name carries. Neither is invented
    /// here, which is the first rule in the form it takes on this panel.
    /// </summary>
    [Fact]
    public async Task TheInputWindowsAreTheComponentsOwnRatherThanThePages()
    {
        var ct = TestContext.Current.CancellationToken;
        var viewed = new DateOnly(2022, 6, 15);
        var config = new RecordingConfig();

        var panel = await new RecordInspector(new NoRows(), config)
            .InputsAsync("AAPL.US", viewed, ct).ConfigureAwait(true);

        Assert.Equal(RecordInspector.InsiderWindowDays, panel.InsiderWindowDays);
        Assert.Equal(90, panel.InsiderWindowDays);

        // Resolved for the viewed date, like every other key this reader reads.
        Assert.All(config.AskedFor, d => Assert.Equal(viewed, d));
        Assert.Contains("sentiment.lookback_days", config.Keys);
        Assert.Contains("inspector.recent_bars", config.Keys);
    }

    /// <summary>
    /// A store holding nothing gives four empty lists rather than an error, and the panel
    /// renders each as an absence. A name with no bars is exactly the case the membership
    /// panel points at when it says a name was not evaluated.
    /// </summary>
    [Fact]
    public async Task ANameWithNoInputsIsFourAbsencesRatherThanAnError()
    {
        var ct = TestContext.Current.CancellationToken;

        var panel = await new RecordInspector(new NoRows(), new RecordingConfig())
            .InputsAsync("NOTHING.US", new DateOnly(2022, 6, 15), ct).ConfigureAwait(true);

        Assert.Empty(panel.Bars);
        Assert.Empty(panel.Filings);
        Assert.Empty(panel.SentimentDays);
        Assert.Empty(panel.InsiderFilings);
    }

    /// <summary>
    /// A date C10 never ran for reads as no row rather than as a market of zeroes, and
    /// `vix` is absent by design rather than blank [D-80].
    /// </summary>
    [Fact]
    public async Task ADateWithNoMarketRowSaysSoRatherThanRenderingZeroes()
    {
        var ct = TestContext.Current.CancellationToken;

        var panel = await new RecordInspector(new NoRows(), new RecordingConfig())
            .MarketAsync(new DateOnly(2022, 6, 15), "Technology", ct).ConfigureAwait(true);

        Assert.False(panel.Present);
        Assert.Null(panel.Breadth);
        Assert.Null(panel.RegimeLabel);
        Assert.Null(panel.Vix);
        Assert.Equal("Technology", panel.Sector);
    }

    /// <summary>
    /// The sector composite is taken out of the stored object by key rather than
    /// recomputed. Asserted on the statement, because a reader that rebuilt the composite
    /// from `price_daily` would produce a number that looks right and is C10's
    /// construction reimplemented by hand.
    /// </summary>
    [Fact]
    public async Task TheSectorCompositeIsReadOutOfTheStoredObjectByKey()
    {
        var ct = TestContext.Current.CancellationToken;
        var data = new NoRows();

        await new RecordInspector(data, new RecordingConfig())
            .MarketAsync(new DateOnly(2022, 6, 15), "Technology", ct).ConfigureAwait(true);

        var sql = data.Sql.Single(s => s.Contains("market_context_daily", StringComparison.Ordinal));

        Assert.Contains("sector_relative_strength ->> 'Technology'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("price_daily", sql, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- doubles ---

    /// <summary>
    /// A store holding nothing, recording which tables were asked for. Every write
    /// route throws: the seam takes an <see cref="IStageData"/>, so a test double that
    /// silently accepted a write would hide the guarantee rather than prove it.
    /// </summary>
    private sealed class NoRows : IStageData
    {
        public List<string> Touched { get; } = [];

        /// <summary>The statements issued, so a test can assert what a read keys on.</summary>
        public List<string> Sql { get; } = [];

        public Task<IReadOnlyList<IReadOnlyList<object?>>> ReadAsync(
            string table, string sql, CancellationToken ct = default, int? commandTimeoutSeconds = null)
        {
            Touched.Add(table);
            Sql.Add(sql);
            return Task.FromResult<IReadOnlyList<IReadOnlyList<object?>>>([]);
        }

        public Task<long> WriteAsync(
            string table, WriteOperation operation, string sql,
            IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken ct = default)
            => throw new UndeclaredTableAccessException("RecordInspector", table, operation.ToString(), "none");

        public Task<long> BulkUpsertAsync(
            string table, IReadOnlyList<string> columns, IReadOnlyList<string> conflictTarget,
            Func<IBulkWriter, CancellationToken, Task> write, CancellationToken ct = default)
            => throw new UndeclaredTableAccessException("RecordInspector", table, "Insert", "none");
    }

    /// <summary>
    /// The same, with one `security_daily` row, so the metrics panel has a cell to look
    /// up and the reader reaches every table it declares.
    /// </summary>
    private sealed class WithMembership : IStageData
    {
        public List<string> Touched { get; } = [];

        public Task<IReadOnlyList<IReadOnlyList<object?>>> ReadAsync(
            string table, string sql, CancellationToken ct = default, int? commandTimeoutSeconds = null)
        {
            Touched.Add(table);

            IReadOnlyList<IReadOnlyList<object?>> rows = table switch
            {
                // date, sector, size_bucket, market_cap, is_active
                "security_daily" =>
                    [[new DateTime(2022, 6, 12), "Technology", "mid", 4_000_000_000m, true]],
                _ => [],
            };

            return Task.FromResult(rows);
        }

        public Task<long> WriteAsync(
            string table, WriteOperation operation, string sql,
            IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken ct = default)
            => throw new UndeclaredTableAccessException("RecordInspector", table, operation.ToString(), "none");

        public Task<long> BulkUpsertAsync(
            string table, IReadOnlyList<string> columns, IReadOnlyList<string> conflictTarget,
            Func<IBulkWriter, CancellationToken, Task> write, CancellationToken ct = default)
            => throw new UndeclaredTableAccessException("RecordInspector", table, "Insert", "none");
    }

    /// <summary>
    /// One `security_daily` row and one `universe_rejection` row, with the rejection read
    /// honouring the lower bound the statement carries.
    ///
    /// **The double applies the bound rather than ignoring it, and that is what makes the
    /// absent rejection a behaviour instead of an artifact.** A double returning its row
    /// whatever it was asked would make the corrected reader fail and the uncorrected one
    /// pass, which is the assertion inverted. The parse is deliberately literal: it reads
    /// the one bound the statement can carry and nothing else, so a statement that stopped
    /// carrying it fails here rather than reading as a store with no such row.
    /// </summary>
    private sealed class AsOfPair(DateTime memberOn, bool isActive, DateTime rejectedOn, string criterion)
        : IStageData
    {
        public List<string> Sql { get; } = [];

        public Task<IReadOnlyList<IReadOnlyList<object?>>> ReadAsync(
            string table, string sql, CancellationToken ct = default, int? commandTimeoutSeconds = null)
        {
            Sql.Add(sql);

            IReadOnlyList<IReadOnlyList<object?>> rows = table switch
            {
                // date, sector, size_bucket, market_cap, is_active
                "security_daily" => [[memberOn, "Technology", "large", 90_000_000_000m, isActive]],
                "universe_rejection" when Admits(sql) => [[rejectedOn, criterion]],
                _ => [],
            };

            return Task.FromResult(rows);
        }

        /// <summary>Whether the statement's lower bound, if it carries one, admits the held row.</summary>
        private bool Admits(string sql)
        {
            const string Marker = "r.date >= DATE '";
            var at = sql.IndexOf(Marker, StringComparison.Ordinal);

            if (at < 0)
            {
                return true;
            }

            var bound = DateTime.ParseExact(
                sql.Substring(at + Marker.Length, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture);

            return rejectedOn >= bound;
        }

        public Task<long> WriteAsync(
            string table, WriteOperation operation, string sql,
            IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken ct = default)
            => throw new UndeclaredTableAccessException("RecordInspector", table, operation.ToString(), "none");

        public Task<long> BulkUpsertAsync(
            string table, IReadOnlyList<string> columns, IReadOnlyList<string> conflictTarget,
            Func<IBulkWriter, CancellationToken, Task> write, CancellationToken ct = default)
            => throw new UndeclaredTableAccessException("RecordInspector", table, "Insert", "none");
    }

    private sealed class RecordingConfig : IConfigStore
    {
        public List<DateOnly> AskedFor { get; } = [];

        public List<string> Keys { get; } = [];

        public Task<ConfigRow?> ResolveAsync(string key, DateOnly asOf, CancellationToken ct = default)
        {
            AskedFor.Add(asOf);
            Keys.Add(key);
            return Task.FromResult<ConfigRow?>(new ConfigRow(key, 1, "1", new DateOnly(2020, 1, 1)));
        }

        public async Task<ConfigRow> RequireAsync(string key, DateOnly asOf, CancellationToken ct = default)
            => (await ResolveAsync(key, asOf, ct).ConfigureAwait(false))!;

        public Task<int?> ResolveVersionAsync(DateOnly asOf, CancellationToken ct = default)
            => throw new NotSupportedException("A reader resolves keys, never the store-wide version.");

        public Task<int> RequireVersionAsync(DateOnly asOf, CancellationToken ct = default)
            => throw new NotSupportedException("A reader resolves keys, never the store-wide version.");
    }

    /// <summary>
    /// Two real versions of one key, run through <see cref="ConfigResolution.Resolve"/>
    /// rather than through a hand-written rule, so what is tested is the panel passing
    /// the right date into the production resolver.
    /// </summary>
    private sealed class TwoVersions(string key, params ConfigRow[] rows) : IConfigStore
    {
        public Task<ConfigRow?> ResolveAsync(string k, DateOnly asOf, CancellationToken ct = default)
            => Task.FromResult(string.Equals(k, key, StringComparison.Ordinal)
                ? ConfigResolution.Resolve(rows, k, asOf)
                : new ConfigRow(k, 1, "1", new DateOnly(2020, 1, 1)));

        public async Task<ConfigRow> RequireAsync(string k, DateOnly asOf, CancellationToken ct = default)
            => (await ResolveAsync(k, asOf, ct).ConfigureAwait(false))!;

        public Task<int?> ResolveVersionAsync(DateOnly asOf, CancellationToken ct = default)
            => throw new NotSupportedException("A reader resolves keys, never the store-wide version.");

        public Task<int> RequireVersionAsync(DateOnly asOf, CancellationToken ct = default)
            => throw new NotSupportedException("A reader resolves keys, never the store-wide version.");
    }
}
