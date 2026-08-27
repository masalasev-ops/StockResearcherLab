using StockResearcherLab.Core.Stages;
using Xunit;

namespace StockResearcherLab.Tests.Stages;

/// <summary>
/// The conversion that reads a `timestamptz` off <see cref="IStageData"/>'s object route
/// [5.9].
///
/// **The case that matters is the first one.** The driver returns a `DateTime` with
/// <see cref="DateTimeKind.Utc"/>, so the obvious cast, `as DateTimeOffset?`, compiles,
/// runs, throws nothing and yields null for every row. C33 read `headline.published_at`
/// that way between 5.8 and 5.9, and the consequence was not an error: D-143's
/// most-recent-first selection ordered on null for every article and the rendered input
/// told the model "date unknown" on dates the store held.
/// </summary>
public sealed class StoredInstantTests
{
    /// <summary>
    /// A UTC `DateTime`, which is what the driver hands back, converts to the same instant
    /// rather than to an absence. This is the assertion the defect needed.
    /// </summary>
    [Fact]
    public void AUtcDateTimeConvertsToTheSameInstant()
    {
        var stored = new DateTime(2026, 8, 11, 17, 22, 29, DateTimeKind.Utc);

        var at = StoredInstant.From(stored);

        Assert.NotNull(at);
        Assert.Equal(TimeSpan.Zero, at.Value.Offset);
        Assert.Equal(new DateTimeOffset(2026, 8, 11, 17, 22, 29, TimeSpan.Zero), at.Value);

        // The cast that was there, asserted to be the thing it is: not an error, an
        // absence. Stated so a later reader restoring it fails here rather than in a
        // night's output.
        Assert.Null((object) stored as DateTimeOffset?);
    }

    /// <summary>A `DateTimeOffset` passes through, in case a driver ever returns one.</summary>
    [Fact]
    public void ADateTimeOffsetPassesThrough()
    {
        var at = new DateTimeOffset(2026, 8, 11, 17, 22, 29, TimeSpan.Zero);

        Assert.Equal(at, StoredInstant.From(at));
    }

    /// <summary>
    /// A null column reads as null, which is the one absence this conversion is allowed to
    /// produce. `DBNull` is the same fact arriving by the other route.
    /// </summary>
    [Fact]
    public void ANullColumnIsTheOnlyAbsence()
    {
        Assert.Null(StoredInstant.From(null));
        Assert.Null(StoredInstant.From(DBNull.Value));
    }

    /// <summary>
    /// **An unexpected type throws rather than reading as absent**, which is the whole
    /// point: a conversion that fails by producing nothing is what put "date unknown" into
    /// a night of prompts without failing anything.
    /// </summary>
    [Theory]
    [InlineData("2026-08-11T17:22:29Z")]
    [InlineData(1_755_000_000L)]
    public void AnUnexpectedTypeThrows(object value)
        => Assert.Throws<InvalidOperationException>(() => StoredInstant.From(value));

    /// <summary>
    /// A `DateTime` whose kind is not UTC throws too. It would otherwise be read as local
    /// time by the constructor, which is an offset error rather than an absence and is the
    /// harder one to see.
    /// </summary>
    [Fact]
    public void ADateTimeThatDoesNotSayItIsUtcThrows()
        => Assert.Throws<InvalidOperationException>(
            () => StoredInstant.From(new DateTime(2026, 8, 11, 17, 22, 29, DateTimeKind.Unspecified)));
}
