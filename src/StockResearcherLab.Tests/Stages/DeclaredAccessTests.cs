using StockResearcherLab.Core.Stages;
using Xunit;

namespace StockResearcherLab.Tests.Stages;

/// <summary>
/// The 0.3 definition of done: a stage that reads a table it did not declare
/// fails at runtime with a named error rather than silently succeeding.
///
/// These exercise the guard directly. The same guard sits in front of the real
/// database in StageData, and the integration test beside this one proves it
/// fires there too, before a connection is opened.
/// </summary>
public sealed class DeclaredAccessTests
{
    private static DeclaredAccess Access() => new(
        "TestStage",
        readSet: ["price_daily"],
        writeSet: [new TableWrite("run_log", WriteOperation.Insert)]);

    [Fact]
    public void ReadingADeclaredTableIsAllowed()
        => Assert.True(Access().CanRead("price_daily"));

    [Fact]
    public void ReadingAnUndeclaredTableThrowsANamedError()
    {
        var ex = Assert.Throws<UndeclaredTableAccessException>(() => Access().EnsureCanRead("security"));

        Assert.Equal("TestStage", ex.Stage);
        Assert.Equal("security", ex.Table);
        Assert.Contains("does not declare", ex.Message, StringComparison.Ordinal);

        // The message has to name what the stage may touch, or the failure tells
        // you something is wrong without telling you what was expected.
        Assert.Contains("price_daily", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WritingAnUndeclaredOperationOnADeclaredTableThrows()
    {
        // run_log is declared for Insert and nothing else. Ownership is per
        // operation, so Update is a different claim [INVARIANT 10 as amended].
        var ex = Assert.Throws<UndeclaredTableAccessException>(
            () => Access().EnsureCanWrite("run_log", WriteOperation.Update));

        Assert.Equal("run_log", ex.Table);
    }

    [Fact]
    public void WritingADeclaredOperationIsAllowed()
        => Assert.True(Access().CanWrite("run_log", WriteOperation.Insert));

    [Fact]
    public void AStageMayReadWhatItWrites()
    {
        // Reading a row back in order to update it is the same access. Forcing it
        // into both lists would make the read set say something it does not mean.
        Assert.True(Access().CanRead("run_log"));
    }

    [Fact]
    public void TheErrorMessageIsOrdinalSortedRatherThanSetOrdered()
    {
        var access = new DeclaredAccess(
            "Wide",
            readSet: ["zeta", "alpha", "mu"],
            writeSet: []);

        var ex = Assert.Throws<UndeclaredTableAccessException>(() => access.EnsureCanRead("nope"));

        // Enumeration order of a HashSet is unspecified and must never reach
        // output. An exception message is output [CLAUDE.md section 6].
        Assert.Contains("alpha, mu, zeta", ex.Message, StringComparison.Ordinal);
    }
}
