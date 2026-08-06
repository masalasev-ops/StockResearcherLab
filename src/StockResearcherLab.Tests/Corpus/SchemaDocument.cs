using System.Text.RegularExpressions;

namespace StockResearcherLab.Tests.Corpus;

/// <summary>
/// Reads SCHEMA.md. The document is the contract, so the tests parse it rather
/// than carrying a second copy of the table list: a list kept in two places goes
/// silently incomplete the moment one is updated and the stale copy is the one
/// nobody is looking at.
/// </summary>
public static class SchemaDocument
{
    /// <summary>
    /// Every table SCHEMA.md declares.
    ///
    /// A section is a table when its "### " heading is followed by a line
    /// beginning "Grain:". That is derived from the document's own shape rather
    /// than from a hardcoded exception list, which matters for the one heading
    /// that is not a table: "### percentile columns" states that percentiles are
    /// written alongside their source tables and declares no grain, so it drops
    /// out without being named here.
    ///
    /// A heading naming several tables separated by " / " declares all of them,
    /// which is how "### order / fill / position" expands to three.
    /// </summary>
    public static IReadOnlyList<string> Tables()
    {
        var lines = File.ReadAllLines(Path);
        var tables = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("### ", StringComparison.Ordinal))
            {
                continue;
            }

            var heading = lines[i]["### ".Length..].Trim();

            // The grain line is the next non-blank line.
            var j = i + 1;
            while (j < lines.Length && lines[j].Trim().Length == 0)
            {
                j++;
            }

            if (j >= lines.Length || !lines[j].TrimStart().StartsWith("Grain:", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var name in heading.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                tables.Add(name);
            }
        }

        return tables;
    }

    /// <summary>Absolute path to SCHEMA.md, found by walking up from the test binary.</summary>
    public static string Path => System.IO.Path.Combine(RepositoryRoot, "docs", "SCHEMA.md");

    public static string RepositoryRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (File.Exists(System.IO.Path.Combine(dir.FullName, "docs", "SCHEMA.md")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }

            throw new InvalidOperationException(
                "docs/SCHEMA.md was not found above " + AppContext.BaseDirectory +
                ". The corpus tests read the document rather than a copy of it, so they " +
                "cannot run outside the repository.");
        }
    }
}
