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
        var html = File.ReadAllText(Path);

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

    /// <summary>
    /// Component name to the tables its Reads cell names [D-74].
    ///
    /// **Why this exists.** Nothing compared a declared `ReadSet` against the
    /// catalogue in either direction until now. `ReadSet` was asserted against
    /// hardcoded literals in four component test files, which is the second list this
    /// repository keeps finding, and two of the deviating components asserted nothing
    /// at all. Four Reads cells named an endpoint and no ticker source while
    /// `DeclaredAccess` enforced a read set the document never mentioned, and that
    /// silence is what let the fundamentals pool be drawn from `security` and close
    /// the universe over itself.
    ///
    /// **A table reference in the Reads column is a `code` element.** That is the
    /// document's own typography and it is what separates a table from an endpoint,
    /// a calendar or a symbol list, none of which are tables and none of which are
    /// marked. Reading bare words instead is not an option: `order`, `position`,
    /// `fill`, `alert`, `events`, `portfolio` and `security` are all table names and
    /// all ordinary English, and C03's own cell says "for rotation order" without
    /// meaning the `order` table.
    ///
    /// **The intersection with `SCHEMA.md` is the second filter**, so a `code`
    /// element holding something that is not a table drops out rather than being
    /// reported. C33 NewsDigester reads `digest_provider`, which is configuration and
    /// not a declared table, and it drops out here.
    ///
    /// **One limit, stated rather than discovered.** C15 DossierBuilder names
    /// `calibration` bare where its four neighbours are in `code`, so this parse does
    /// not see it. Nothing in phase 1 registers C15, so nothing asserts against it
    /// yet; when phase 5 does, the conformance test will report the table as declared
    /// and unnamed, and the fix is the markup rather than the assertion.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> ReadTablesByComponent()
    {
        var tables = SchemaDocument.Tables().ToHashSet(StringComparer.Ordinal);
        var html = File.ReadAllText(Path);

        var found = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

        foreach (Match row in CatalogueRow.Matches(html))
        {
            var id = row.Groups["id"].Value;

            // Cells rather than a per-column regex, because the name cell carries
            // nested spans and the Reads cell carries `code` and `rmv` spans, while
            // no cell ever contains another cell.
            var cells = row.Groups["cells"].Value.Split("</td>");
            if (cells.Length < 4)
            {
                throw new InvalidOperationException(
                    $"Catalogue row {id} has {cells.Length - 1} cell(s) after the id. Section 3 is " +
                    "ID, Component, Runs, Reads, Writes, and this reads the fourth. A row of another " +
                    "shape means the table changed and this parser did not.");
            }

            var name = LeadingName.Match(cells[0]);
            if (!name.Success)
            {
                throw new InvalidOperationException(
                    $"Catalogue row {id} opens its component cell with something this parser does not " +
                    "recognise as a name: " + cells[0]);
            }

            found[name.Groups["name"].Value] = CodeElement.Matches(cells[2])
                .Select(m => m.Groups[1].Value)
                .Where(tables.Contains)
                .ToHashSet(StringComparer.Ordinal);
        }

        return found;
    }

    /// <summary>
    /// One catalogue row, whole. Singleline is deliberately off: a row that lost its
    /// closing tag would otherwise swallow the rest of the document and read as one
    /// enormous Reads cell, which is a silent pass rather than a failure.
    /// </summary>
    private static readonly Regex CatalogueRow = new(
        @"<tr><td class=""mono""><b>(?<id>C\d+)</b></td>(?<cells>.*?)</tr>",
        RegexOptions.Compiled);

    private static readonly Regex LeadingName = new(
        @"^<td>\s*(?<name>[A-Za-z][A-Za-z0-9]*)",
        RegexOptions.Compiled);

    private static readonly Regex CodeElement = new(
        @"<code>([^<]+)</code>",
        RegexOptions.Compiled);

    /// <summary>
    /// Absolute path to `ARCHITECTURE.html`. `SchemaDocument` already walks up to the
    /// repository root, reused rather than duplicated since two locators would be two
    /// things to keep in step.
    /// </summary>
    public static string Path
        => System.IO.Path.Combine(SchemaDocument.RepositoryRoot, "docs", "ARCHITECTURE.html");
}
