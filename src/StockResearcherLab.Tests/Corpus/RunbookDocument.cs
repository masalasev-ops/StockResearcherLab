using System.Text.RegularExpressions;

namespace StockResearcherLab.Tests.Corpus;

/// <summary>
/// `RUNBOOK.md` read for the tests that hold it against the night that runs [5.5.14].
/// </summary>
public static class RunbookDocument
{
    public sealed record CycleRow(string Time, string What);

    public static string Path => System.IO.Path.Combine(SchemaDocument.RepositoryRoot, "docs", "RUNBOOK.md");

    /// <summary>
    /// The nightly cycle table, in document order: its time cell and what it says runs then.
    /// Fails rather than returning nothing if the section or its table has moved.
    /// </summary>
    public static IReadOnlyList<CycleRow> CycleTable()
    {
        var lines = File.ReadAllLines(Path);
        var start = Array.FindIndex(lines, l => l.StartsWith("## The nightly cycle", StringComparison.Ordinal));

        if (start < 0)
        {
            throw new InvalidOperationException("RUNBOOK.md has no '## The nightly cycle' heading.");
        }

        var rows = new List<CycleRow>();

        foreach (var line in lines.Skip(start + 1))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal) || (rows.Count > 0 && !line.StartsWith('|')))
            {
                break;
            }

            var m = Regex.Match(line, @"^\|\s*(?<time>[^|]+?)\s*\|\s*(?<what>.+?)\s*\|\s*$");

            if (m.Success && !line.StartsWith("|---", StringComparison.Ordinal)
                && !m.Groups["time"].Value.StartsWith("Time", StringComparison.Ordinal))
            {
                rows.Add(new CycleRow(m.Groups["time"].Value, m.Groups["what"].Value));
            }
        }

        if (rows.Count == 0)
        {
            throw new InvalidOperationException("RUNBOOK.md's nightly cycle section carries no table rows.");
        }

        return rows;
    }
}