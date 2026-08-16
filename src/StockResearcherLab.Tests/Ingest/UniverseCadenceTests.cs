using Npgsql;
using StockResearcherLab.Pipeline;
using StockResearcherLab.Pipeline.Ingest;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Ingest;

/// <summary>
/// C01's evaluation cadence over a range [3.11, D-92].
///
/// **The cadence is the assertion, not an implementation detail.** D-92 turns on
/// backfilled cells sitting on the identical population rule as live ones: C11 reads the
/// most recent `security_daily` row at or before the date, so a denser grain would put a
/// population the live system never had underneath a percentile floor, which is D-58's
/// principle applied to membership. `ARCHITECTURE.html` §3 runs C01 weekly on Sunday and
/// this is the test that the backfill agrees.
///
/// **What is not asserted here is a range execution end to end**, and the reason is open
/// item 33's one table further on: C01's pool is `price_daily`, a real table on the
/// developer database that the suite resolves its connection string against [open items
/// 10 and 26]. A range execution in a test would run `LiquidAsync` against 109.8 million
/// real rows per evaluation date. So what is covered is the layer where the cadence can
/// be lost, and the membership rules are covered by the criteria tests that already
/// exist.
/// </summary>
public sealed class UniverseCadenceTests
{
    [Fact]
    public void EveryEvaluationDateIsASunday()
    {
        var dates = UniverseBuilder.EvaluationDates(new DateOnly(2021, 1, 4), new DateOnly(2021, 6, 30));

        Assert.NotEmpty(dates);
        Assert.All(dates, d => Assert.Equal(DayOfWeek.Sunday, d.DayOfWeek));
    }

    /// <summary>
    /// The range start is a Monday and the first evaluation date is the Sunday after it,
    /// never the start itself. A date off the live cadence is one no live run would have
    /// produced, and C11 would read it as the population for every date until the next.
    /// </summary>
    [Fact]
    public void TheFirstDateIsTheFirstSundayAtOrAfterTheStartRatherThanTheStart()
    {
        var from = new DateOnly(2021, 1, 4);
        var dates = UniverseBuilder.EvaluationDates(from, new DateOnly(2021, 2, 28));

        Assert.Equal(DayOfWeek.Monday, from.DayOfWeek);
        Assert.Equal(new DateOnly(2021, 1, 10), dates[0]);
    }

    [Fact]
    public void ARangeThatStartsOnASundayTakesThatSunday()
    {
        var from = new DateOnly(2021, 1, 10);
        var dates = UniverseBuilder.EvaluationDates(from, new DateOnly(2021, 2, 28));

        Assert.Equal(DayOfWeek.Sunday, from.DayOfWeek);
        Assert.Equal(from, dates[0]);
    }

    /// <summary>
    /// Six days spanning no Sunday. The range detail reports it rather than the run
    /// dividing by zero over an empty list, which is what the guard in
    /// <c>ExecuteRangeAsync</c> is for.
    /// </summary>
    [Fact]
    public void ARangeSpanningNoSundayEvaluatesNothing()
    {
        var dates = UniverseBuilder.EvaluationDates(new DateOnly(2021, 1, 4), new DateOnly(2021, 1, 9));

        Assert.Empty(dates);
    }

    [Fact]
    public void TheLastDateIsOnOrBeforeTheRangeEnd()
    {
        var to = new DateOnly(2026, 8, 15);
        var dates = UniverseBuilder.EvaluationDates(new DateOnly(2021, 1, 4), to);

        Assert.True(dates[^1] <= to);
        Assert.True(dates[^1].AddDays(7) > to);
    }

    /// <summary>
    /// The phase's own window, pinned. `PROGRESS.md` projects this checkpoint's cost per
    /// evaluation date, so the count that projection multiplies is worth being a number a
    /// test holds rather than one arrived at by dividing days by seven: that arithmetic
    /// gives 293 and the cadence gives 292.
    /// </summary>
    [Fact]
    public void ThePhaseWindowIs292WeeklyDates()
    {
        var dates = UniverseBuilder.EvaluationDates(new DateOnly(2021, 1, 4), new DateOnly(2026, 8, 15));

        Assert.Equal(292, dates.Count);
        Assert.Equal(new DateOnly(2021, 1, 10), dates[0]);
        Assert.Equal(new DateOnly(2026, 8, 9), dates[^1]);
    }

    /// <summary>
    /// Two calls produce the identical sequence. Determinism is a correctness property
    /// here and the evaluation set is what every written row is keyed on
    /// [`CLAUDE.md` §6].
    /// </summary>
    [Fact]
    public void TheSequenceIsIdenticalAcrossCalls()
    {
        var from = new DateOnly(2021, 1, 4);
        var to = new DateOnly(2023, 12, 31);

        Assert.Equal(UniverseBuilder.EvaluationDates(from, to), UniverseBuilder.EvaluationDates(from, to));
    }
}

/// <summary>
/// The departure rule both of C01's entry points retire members by [3.12].
///
/// **Why the rule rather than the write.** A name present on one date and absent on the
/// next needs an `is_active = false` row, because readers take the most recent
/// `security_daily` row at or before the date [D-92]: without it a name that left in 2023
/// keeps its last `true` row and is inherited into every later cell. The nightly path and
/// the range path differ only in where the previous membership comes from, one reading it
/// and one carrying it, so the rule is asserted once and both call it. Asserting it twice
/// against two copies is what lets two copies drift.
/// </summary>
public sealed class UniverseDepartureTests
{
    [Fact]
    public void ANameOnTheEarlierDateAndNotTheLaterOneDeparts()
    {
        var previous = new HashSet<string>(["AAA.US", "BBB.US", "CCC.US"], StringComparer.Ordinal);

        Assert.Equal(["BBB.US"], UniverseBuilder.Departures(previous, ["AAA.US", "CCC.US"]));
    }

    [Fact]
    public void ANameThatArrivesIsNotADeparture()
    {
        var previous = new HashSet<string>(["AAA.US"], StringComparer.Ordinal);

        Assert.Empty(UniverseBuilder.Departures(previous, ["AAA.US", "NEW.US"]));
    }

    [Fact]
    public void AnUnchangedMembershipRetiresNobody()
    {
        var previous = new HashSet<string>(["AAA.US", "BBB.US"], StringComparer.Ordinal);

        Assert.Empty(UniverseBuilder.Departures(previous, ["AAA.US", "BBB.US"]));
    }

    /// <summary>
    /// The first evaluated date of a fresh store has nothing before it, so nothing
    /// departs. The seed being empty is a fact about the store rather than a special case
    /// in the rule.
    /// </summary>
    [Fact]
    public void AnEmptyPreviousMembershipRetiresNobody()
    {
        Assert.Empty(UniverseBuilder.Departures(
            new HashSet<string>(StringComparer.Ordinal), ["AAA.US", "BBB.US"]));
    }

    /// <summary>Ordinal and ordered, so two runs write the same rows in the same order.</summary>
    [Fact]
    public void DeparturesAreOrderedOrdinally()
    {
        var previous = new HashSet<string>(["ZZZ.US", "AAA.US", "MMM.US"], StringComparer.Ordinal);

        Assert.Equal(
            ["AAA.US", "MMM.US", "ZZZ.US"],
            UniverseBuilder.Departures(previous, []));
    }
}

/// <summary>
/// The statement every universe reader now takes, asserted against the store rather than
/// reasoned about [3.12].
///
/// **Why against the store.** `security.is_active` had no writer from 3.11 and C03, C05
/// and C06 still filtered on it, so their universe was frozen at whatever C01 last wrote
/// while nothing errored. That is the shape that froze the fundamentals rotation and the
/// `fcf_yield` ratio, and the property that catches it is not "the read compiles" but "a
/// name that departed is gone the next night". All eight readers call
/// <see cref="Universe.MembersAsOf"/>, so asserting the statement once covers what eight
/// copies of it would each have to be asserted for separately.
/// </summary>
[Collection("database")]
public sealed class UniverseAsOfTests
{
    private const string Marker = "SRLASOF";

    private static readonly DateOnly Joined = new(2001, 3, 4);
    private static readonly DateOnly Left = new(2001, 3, 11);

    /// <summary>
    /// A member on one date, departed on the next, and absent from the universe every
    /// night after. The `false` row is what makes the second half true: readers take the
    /// most recent row at or before the date, so without it the `true` row is inherited
    /// for ever [D-92].
    /// </summary>
    [Fact]
    public async Task ADepartedNameIsAbsentFromTheUniverseOnEveryLaterNight()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClearAsync(ct).ConfigureAwait(true);

        await SeedAsync($"{Marker}STAY.US", Joined, active: true, ct).ConfigureAwait(true);
        await SeedAsync($"{Marker}GONE.US", Joined, active: true, ct).ConfigureAwait(true);
        await SeedAsync($"{Marker}GONE.US", Left, active: false, ct).ConfigureAwait(true);

        // On the joining date both are members.
        var onJoin = await MembersAsync(Joined, ct).ConfigureAwait(true);
        Assert.Contains($"{Marker}STAY.US", onJoin);
        Assert.Contains($"{Marker}GONE.US", onJoin);

        // On the departure date and every night after, one of them is gone and the other
        // is not. Both halves matter: a read that dropped everybody would pass the first.
        foreach (var date in new[] { Left, Left.AddDays(1), Left.AddDays(400) })
        {
            var members = await MembersAsync(date, ct).ConfigureAwait(true);

            Assert.Contains($"{Marker}STAY.US", members);
            Assert.DoesNotContain($"{Marker}GONE.US", members);
        }

        await ClearAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// The day before it joined, a member is not yet one. The bound is `date <=`, so a
    /// backfilled night reads the membership of its own date rather than the newest.
    /// </summary>
    [Fact]
    public async Task ANameIsNotAMemberBeforeItsFirstRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClearAsync(ct).ConfigureAwait(true);

        await SeedAsync($"{Marker}STAY.US", Joined, active: true, ct).ConfigureAwait(true);

        Assert.DoesNotContain(
            $"{Marker}STAY.US",
            await MembersAsync(Joined.AddDays(-1), ct).ConfigureAwait(true));

        await ClearAsync(ct).ConfigureAwait(true);
    }

    private static async Task<IReadOnlyList<string>> MembersAsync(DateOnly asOf, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(Universe.MembersAsOf(asOf), conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var members = new List<string>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            members.Add(reader.GetString(0));
        }

        return members;
    }

    private static async Task SeedAsync(string ticker, DateOnly date, bool active, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO security_daily (ticker, date, sector, size_bucket, market_cap, is_active)
            VALUES (@t, @d, 'SRLASOF-S', 'SRLASOF-B', 1000000000, @a)
            ON CONFLICT (ticker, date) DO UPDATE SET is_active = EXCLUDED.is_active;
            """, conn);

        cmd.Parameters.AddWithValue("t", ticker);
        cmd.Parameters.AddWithValue("d", date);
        cmd.Parameters.AddWithValue("a", active);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task ClearAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM security_daily WHERE ticker LIKE @p;", conn);

        cmd.Parameters.AddWithValue("p", Marker + "%");
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
