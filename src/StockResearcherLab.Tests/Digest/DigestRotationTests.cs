using StockResearcherLab.Core.Digest;
using Xunit;

namespace StockResearcherLab.Tests.Digest;

/// <summary>
/// D-138's selection: which of the night's candidates go to the chain's second link.
///
/// **What is being defended is that the pair cannot move underneath the record.** D-27's
/// paired sample is built one night at a time over months, so a selection that changed
/// with a framework upgrade would split it with nothing saying so, which is why the hash
/// is written in this repository rather than taken from <see cref="System.Random"/>
/// [D-138]. The published FNV-1a vectors are asserted here for the same reason one step
/// further down: a hash written here can drift from the algorithm it claims to be.
/// </summary>
public sealed class DigestRotationTests
{
    private static readonly string[] Set =
        ["AAPL.US", "BBBY.US", "CHEF.US", "DNOW.US", "ENPH.US", "FIVE.US"];

    /// <summary>
    /// The two published FNV-1a 64-bit vectors. If these move, the implementation is no
    /// longer the algorithm the decision names, whatever it is called.
    /// </summary>
    [Theory]
    [InlineData("a", 0xaf63dc4c8601ec8cUL)]
    [InlineData("foobar", 0x85944171f73967e8UL)]
    public void TheHashIsFnv1a(string text, ulong expected)
        => Assert.Equal(expected, DigestRotation.Hash(text));

    /// <summary>
    /// The empty string hashes to the offset basis, which is the one case a wrong loop
    /// still gets right and is stated so the constant itself is pinned.
    /// </summary>
    [Fact]
    public void TheEmptyStringIsTheOffsetBasis()
        => Assert.Equal(DigestRotation.OffsetBasis, DigestRotation.Hash(string.Empty));

    /// <summary>
    /// **One date, one candidate set, one pair, stated as literals.** A property test that
    /// only asserted "two of them" would pass over a selection that had quietly changed,
    /// which is the thing the paired sample cannot survive [D-27, D-138].
    /// </summary>
    [Theory]
    [InlineData(2026, 8, 11, "FIVE.US", "ENPH.US")]
    [InlineData(2026, 8, 12, "AAPL.US", "CHEF.US")]
    [InlineData(2026, 8, 13, "DNOW.US", "ENPH.US")]
    public void TheSelectionIsPinnedToTheseNames(int y, int m, int d, string first, string second)
        => Assert.Equal([first, second], DigestRotation.Select(new DateOnly(y, m, d), Set, 2));

    /// <summary>
    /// **Two adjacent dates over one candidate set produce different pairs**, which a
    /// selection that had stopped reading the date would fail and a constant one could not
    /// pass at all.
    /// </summary>
    [Fact]
    public void TwoAdjacentDatesProduceDifferentPairs()
    {
        var first = DigestRotation.Select(new DateOnly(2026, 8, 11), Set, 2);
        var second = DigestRotation.Select(new DateOnly(2026, 8, 12), Set, 2);
        var third = DigestRotation.Select(new DateOnly(2026, 8, 13), Set, 2);

        Assert.NotEqual(first, second);
        Assert.NotEqual(second, third);
    }

    /// <summary>
    /// The order the candidates arrive in does not reach the answer. C33 reads them ordered
    /// by the statement, and a selection that depended on that order would be one more
    /// thing a re-run has to reproduce [INVARIANT 6].
    /// </summary>
    [Fact]
    public void TheOrderTheCandidatesArriveInDoesNotChangeThePair()
    {
        var date = new DateOnly(2026, 8, 12);
        var reversed = Set.Reverse().ToArray();

        Assert.Equal(DigestRotation.Select(date, Set, 2), DigestRotation.Select(date, reversed, 2));
    }

    /// <summary>
    /// **A candidate set smaller than the count rotates whole and nothing is padded**
    /// [D-138]. A night with one candidate rotates that one rather than failing or
    /// inventing a second.
    /// </summary>
    [Fact]
    public void ASetSmallerThanTheCountRotatesWhole()
    {
        var selected = DigestRotation.Select(new DateOnly(2026, 8, 12), ["ONE.US"], 2);

        Assert.Equal(["ONE.US"], selected);
    }

    /// <summary>
    /// A count of zero or less selects nothing, and an empty candidate set selects nothing.
    /// Both are the same statement read from the other end: the rotation is a slice off an
    /// ordering, not a rule that must find something.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ACountOfNoneSelectsNothing(int count)
        => Assert.Empty(DigestRotation.Select(new DateOnly(2026, 8, 12), Set, count));

    /// <summary>An empty candidate set has nothing to rotate and says so rather than throwing.</summary>
    [Fact]
    public void AnEmptyCandidateSetSelectsNothing()
        => Assert.Empty(DigestRotation.Select(new DateOnly(2026, 8, 12), [], 2));
}
