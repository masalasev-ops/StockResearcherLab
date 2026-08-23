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
        var data = new NoRows();

        await new RecordInspector(data, new RecordingConfig())
            .MembershipAsync("AAPL.US", new DateOnly(2022, 6, 15), ct).ConfigureAwait(true);

        Assert.Equal(
            RecordInspector.Access().ReadSet.Order(StringComparer.Ordinal),
            data.Touched.Order(StringComparer.Ordinal));
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

        public Task<IReadOnlyList<IReadOnlyList<object?>>> ReadAsync(
            string table, string sql, CancellationToken ct = default, int? commandTimeoutSeconds = null)
        {
            Touched.Add(table);
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

    private sealed class RecordingConfig : IConfigStore
    {
        public List<DateOnly> AskedFor { get; } = [];

        public Task<ConfigRow?> ResolveAsync(string key, DateOnly asOf, CancellationToken ct = default)
        {
            AskedFor.Add(asOf);
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
