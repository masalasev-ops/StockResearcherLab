using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace StockResearcherLab.Tests.Corpus;

/// <summary>
/// Checkpoint 5.5.13. `FIXTURES.md` made true, and kept true.
///
/// **The registry is the one list `CLAUDE.md` §9 trusts**, and it is the only place a
/// fixture is named, so a citation in it that resolves to nothing is a fixture the
/// repository says it has and does not. That had happened: a row cited a method that left
/// the suite when D-95 reversed the behaviour it asserted, and the row stood, unstruck and
/// live, for two phases.
///
/// **Every live citation in the Used-by column resolves by reflection** to a method of a
/// type in this assembly. Struck text is skipped, being a record of what a row once cited
/// rather than a claim about the suite. A citation opening with a dot names another method
/// of the class cited last in the same cell, which is the registry's own shorthand.
/// </summary>
public sealed class FixtureRegistryConformanceTests
{
    [Fact]
    public void EveryLiveCitationInTheRegistryResolvesToATestMethod()
    {
        var unresolved = Citations()
            .Where(c => !Resolves(c.Type, c.Method))
            .Select(c => $"{c.Fixture}: {c.Type}.{c.Method}")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.True(unresolved.Count == 0,
            "FIXTURES.md cites test methods the suite does not have. Strike the row in place and " +
            "name its replacement, or register the method it should cite:\n  " +
            string.Join("\n  ", unresolved));
    }

    /// <summary>
    /// The parse reads the registry rather than nothing, so the assertion above cannot pass
    /// over an empty parse: dozens of citations, and the struck row at the top of the file
    /// contributes its live replacement and not its struck original.
    /// </summary>
    [Fact]
    public void TheParseReadsTheRegistry()
    {
        var citations = Citations();

        Assert.True(citations.Count > 60, $"FIXTURES.md parsed as {citations.Count} citations.");

        Assert.Contains(citations, c => c.Type == "EodhdClientTests"
            && c.Method == "APagedReadTheServerRanOutOfIsRecordedRatherThanThrown");
        Assert.DoesNotContain(citations, c => c.Method == "APagedReadShortOfItsReportedTotalFailsRatherThanReturning");
    }

    /// <summary>
    /// The check discriminates. A citation to a method that does not exist is reported, and
    /// one whose class does not exist is too.
    /// </summary>
    [Fact]
    public void AnUnresolvableCitationIsReported()
    {
        Assert.True(Resolves("FixtureRegistryConformanceTests", nameof(AnUnresolvableCitationIsReported)));
        Assert.False(Resolves("FixtureRegistryConformanceTests", "AMethodThatWasNeverWritten"));
        Assert.False(Resolves("AClassThatWasNeverWritten", "Anything"));
    }

    private sealed record Citation(string Fixture, string Type, string Method);

    private static readonly Regex Struck = new(@"~~.*?~~", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex Cited = new(
        @"`(?<type>[A-Z][A-Za-z0-9_]*)?\.(?<method>[A-Z][A-Za-z0-9_]*)`", RegexOptions.Compiled);

    /// <summary>Every live citation in the registry table's Used-by column.</summary>
    private static List<Citation> Citations()
    {
        var path = System.IO.Path.Combine(SchemaDocument.RepositoryRoot, "docs", "FIXTURES.md");
        var lines = File.ReadAllLines(path);

        var start = Array.FindIndex(lines, l => l.StartsWith("## Registry", StringComparison.Ordinal));
        Assert.True(start >= 0, "FIXTURES.md has no '## Registry' heading.");

        var citations = new List<Citation>();

        foreach (var line in lines.Skip(start + 1))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                break;
            }

            if (!line.StartsWith('|') || line.StartsWith("|---", StringComparison.Ordinal)
                || line.StartsWith("| Fixture |", StringComparison.Ordinal))
            {
                continue;
            }

            var cells = line.Split('|');

            if (cells.Length < 6)
            {
                continue;
            }

            var fixture = Struck.Replace(cells[1], string.Empty).Trim();
            var usedBy = Struck.Replace(cells[4], string.Empty);

            string? last = null;

            foreach (Match m in Cited.Matches(usedBy))
            {
                var type = m.Groups["type"].Success ? m.Groups["type"].Value : last;

                if (type is null)
                {
                    continue;
                }

                last = type;
                citations.Add(new Citation(fixture, type, m.Groups["method"].Value));
            }
        }

        return citations;
    }

    private static bool Resolves(string typeName, string method)
        => typeof(FixtureRegistryConformanceTests).Assembly.GetTypes()
            .Where(t => t.Name == typeName)
            .Any(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .Any(m => m.Name == method));
}
