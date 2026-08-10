using System.Text.RegularExpressions;

namespace StockResearcherLab.Tests.Corpus;

/// <summary>
/// `ARCHITECTURE.html` section 3, the component catalogue, parsed.
///
/// Read rather than re-derived, for the same reason `SchemaDocument` reads
/// `SCHEMA.md`: a hardcoded list of component names in a test is a second list, and
/// a second list goes silently stale the moment the first one changes. The
/// architecture constrains the code, so the code is what gets checked against it
/// [CLAUDE.md section 13].
/// </summary>
public static class ArchitectureDocument
{
    // Each catalogue row opens with the component id in bold and its name as the
    // first text of the next cell: <b>C01</b></td><td>UniverseBuilder <span...
    private static readonly Regex Row = new(
        @"<b>(?<id>C\d+)</b>\s*</td>\s*<td>\s*(?<name>[A-Za-z][A-Za-z0-9]*)",
        RegexOptions.Compiled);

    /// <summary>Component id to name, ordinal by id.</summary>
    public static IReadOnlyDictionary<string, string> Components()
    {
        // SchemaDocument already walks up to the repository root. Reused rather
        // than duplicated, since two locators would be two things to keep in step.
        var path = Path.Combine(SchemaDocument.RepositoryRoot, "docs", "ARCHITECTURE.html");
        var html = File.ReadAllText(path);

        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Row.Matches(html))
        {
            found[m.Groups["id"].Value] = m.Groups["name"].Value;
        }

        return found;
    }

    /// <summary>Every component name section 3 gives, ordinal.</summary>
    public static IReadOnlySet<string> ComponentNames()
        => Components().Values.ToHashSet(StringComparer.Ordinal);
}
