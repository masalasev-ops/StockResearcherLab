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

    // ------------------------------------------- section 16, the store list ---

    /// <summary>One row of §16's store matrix, expanded.</summary>
    /// <param name="Name">The store. A row naming several yields one of these each.</param>
    /// <param name="NotYetInSchema">
    /// The row carries the `NOT YET IN SCHEMA` marker, meaning the store is designed
    /// here and its migration has not landed. The marker lives in the document rather
    /// than in an exclusion list here [D-83, D-76], and it is checked rather than
    /// trusted: a marked store that turns out to be declared fails.
    /// </param>
    public readonly record struct StoreRow(string Name, bool NotYetInSchema);

    /// <summary>§16's store matrix, from the document.</summary>
    public static IReadOnlyList<StoreRow> StoreMatrix() => StoreMatrixIn(File.ReadAllText(Path));

    /// <summary>
    /// §16's store matrix, from arbitrary markup, so the check can be run against a
    /// deliberately broken copy. A conformance test that has never failed has not been
    /// tested, and the only way to fail this one is to hand it a different document.
    ///
    /// **The `Total` row is excluded by rule and not by name.** It sits in `tfoot`
    /// where every store sits in `tbody`, so parsing the body alone drops it without
    /// this parser ever knowing the word. A row added to the summary later drops out
    /// the same way.
    ///
    /// **A row may name several stores.** `order / fill / position` and
    /// `cost_ledger / run_log` are each one row covering several, so the bold text is
    /// split on the separator rather than taken whole. Assuming one row is one store
    /// would leave four tables unchecked while the count still looked plausible.
    /// </summary>
    public static IReadOnlyList<StoreRow> StoreMatrixIn(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        var header = html.IndexOf(StoreHeader, StringComparison.Ordinal);
        if (header < 0)
        {
            throw new InvalidOperationException(
                "ARCHITECTURE.html section 16 has no store matrix header. This check reads that table " +
                "and would otherwise pass over an empty set, which is the failure it exists to prevent.");
        }

        var body = TableBody.Match(html, header);
        if (!body.Success)
        {
            throw new InvalidOperationException(
                "The store matrix has a header and no tbody. The rows are read from the body so that " +
                "the tfoot summary is excluded by structure rather than by matching the word Total.");
        }

        var rows = new List<StoreRow>();

        foreach (Match row in MatrixRow.Matches(body.Groups["rows"].Value))
        {
            var cell = row.Groups["store"].Value;
            var bold = BoldText.Match(cell);

            if (!bold.Success)
            {
                throw new InvalidOperationException(
                    "A store matrix row opens with a cell carrying no bold store name: " + cell);
            }

            var marked = cell.Contains(NotYetInSchemaMarker, StringComparison.Ordinal);

            foreach (var name in bold.Groups[1].Value.Split(
                         '/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                rows.Add(new StoreRow(name, marked));
            }
        }

        return rows;
    }

    // ------------------------------------------ section 05, the screen table ---

    /// <summary>One row of §05's screen table.</summary>
    /// <param name="Id">The screen id, which is also its config id segment.</param>
    /// <param name="Name">The screen's name, as the document gives it.</param>
    /// <param name="RanksOn">
    /// The Ranks-on cell split on its own separator, tags stripped. A token that is
    /// a bare lower-case identifier is a metric; anything else is prose, which is
    /// what S5's cell is entirely.
    /// </param>
    public readonly record struct ScreenRow(string Id, string Name, IReadOnlyList<string> RanksOn)
    {
        /// <summary>The tokens that are metric names rather than prose, ordinal.</summary>
        public IReadOnlySet<string> Metrics
            => RanksOn.Where(IsMetricToken).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// §05's screen table, from the document.
    ///
    /// **Why this exists.** A screen is a configuration row, so the only thing holding
    /// the seeded rows to the design is a reader who remembers what §05 says. That is
    /// the second list this repository keeps finding, one copy of it being prose and
    /// therefore never diffed. `screens.S3.metrics` could lose an input to a bad edit
    /// and every downstream number would stay plausible.
    ///
    /// **The separator is the document's own middle dot**, which is what the Ranks-on
    /// cells are written with. Splitting on it rather than on whitespace is what keeps
    /// `gate: S1 top quintile AND technical bottom quintile AND stabilising` one token
    /// instead of eight.
    ///
    /// **A decision chip inside the cell is dropped whole, tags and text together.**
    /// S4's cell carries one between two of its metrics, and stripping only the tags
    /// leaves `D-58` glued to `distinct_buyer_count`, which then fails to look like a
    /// metric and vanishes from the set. The screen would read as having one input where
    /// it has two, and the check would pass or fail for the wrong reason. Found by this
    /// check disagreeing with the seeded configuration on S4.
    /// </summary>
    public static IReadOnlyList<ScreenRow> ScreenTable() => ScreenTableIn(File.ReadAllText(Path));

    /// <summary>
    /// §05's screen table, from arbitrary markup, so the check can be run against a
    /// deliberately altered copy. A conformance test that has never failed has not been
    /// tested [`StoreMatrixIn`'s own reasoning].
    /// </summary>
    public static IReadOnlyList<ScreenRow> ScreenTableIn(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        var rows = new List<ScreenRow>();

        foreach (Match row in ScreenTableRow.Matches(html))
        {
            rows.Add(new ScreenRow(
                row.Groups["id"].Value,
                StripTags(row.Groups["name"].Value).Trim(),
                [.. StripTags(row.Groups["ranks"].Value)
                    .Split(RanksSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]));
        }

        if (rows.Count == 0)
        {
            throw new InvalidOperationException(
                "ARCHITECTURE.html section 05 has no screen table rows. This check reads that table and " +
                "would otherwise pass over an empty set, which is the failure it exists to prevent.");
        }

        return rows;
    }

    /// <summary>
    /// A Ranks-on token that is a metric name rather than prose. Lower-case, digits and
    /// underscores only, which is `METRICS.md`'s own naming and excludes every prose
    /// token in the table including S5's two whole clauses.
    /// </summary>
    private static bool IsMetricToken(string token)
        => token.Length > 0 && token.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_');

    private static string StripTags(string cell)
        => Tag.Replace(DecisionChip.Replace(cell, string.Empty), string.Empty);

    // Singleline off, for the reason CatalogueRow gives.
    private static readonly Regex ScreenTableRow = new(
        @"<tr><td class=""mono""><b>(?<id>S\d+)</b></td><td>(?<name>.*?)</td>"
        + @"<td class=""wrapmono"">(?<ranks>.*?)</td>",
        RegexOptions.Compiled);

    private static readonly Regex Tag = new(@"<[^>]*>", RegexOptions.Compiled);

    /// <summary>A decision citation, which is this document's markup rather than cell text.</summary>
    private static readonly Regex DecisionChip = new(
        @"<span class=""rmv"">[^<]*</span>",
        RegexOptions.Compiled);

    private static readonly char[] RanksSeparator = ['·'];

    /// <summary>The marker a store carries while §16 names it and `SCHEMA.md` does not declare it.</summary>
    public const string NotYetInSchemaMarker = "<span class=\"tag\">NOT YET IN SCHEMA</span>";

    private const string StoreHeader = "<th class=\"mono\">Store</th>";

    private static readonly Regex TableBody = new(
        @"<tbody>(?<rows>.*?)</tbody>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    // Singleline off, for the reason CatalogueRow gives: a row that lost its closing
    // tag would otherwise swallow the rest of the table and read as one store.
    private static readonly Regex MatrixRow = new(
        @"<tr><td class=""mono"">(?<store>.*?)</td>",
        RegexOptions.Compiled);

    private static readonly Regex BoldText = new(@"<b>([^<]+)</b>", RegexOptions.Compiled);

    /// <summary>
    /// Absolute path to `ARCHITECTURE.html`. `SchemaDocument` already walks up to the
    /// repository root, reused rather than duplicated since two locators would be two
    /// things to keep in step.
    /// </summary>
    public static string Path
        => System.IO.Path.Combine(SchemaDocument.RepositoryRoot, "docs", "ARCHITECTURE.html");
}
