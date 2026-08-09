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

    /// <summary>
    /// Table to the components SCHEMA.md declares as writing it [1.10, closing the
    /// open item that the conformance test read a hardcoded list of splits rather
    /// than the document's own declarations].
    ///
    /// **The declarations are not in one form and cannot be parsed as though they
    /// were.** Most read `**Writer: X.**`. Two read `**Writers: A inserts, B
    /// updates.**`. The order group reads `**RiskGate inserts orders. PaperBroker
    /// inserts fills and inserts positions. PositionManager updates positions to
    /// closed.**`, with no `Writer:` prefix at all, and three read `configuration`
    /// or `the UI`, which are not components.
    ///
    /// So the rule is the intersection rather than the sentence shape: take every
    /// bolded span in the section's opening paragraph, and keep the words that
    /// `ARCHITECTURE.html` section 3 catalogues as component names. That is
    /// tolerant of every form above and of any future one, and it cannot invent a
    /// writer, because a name it does not recognise is dropped rather than kept.
    ///
    /// The paragraph is joined before matching, because the corpus is hard-wrapped
    /// and `flow_daily`'s clause breaks across a line. A line-anchored match would
    /// return no writer for it and the failure would be silent [`CLAUDE.md`
    /// section 7].
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> WritersByTable()
    {
        var components = ArchitectureDocument.ComponentNames();
        var lines = File.ReadAllLines(Path);
        var writers = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("### ", StringComparison.Ordinal))
            {
                continue;
            }

            var heading = lines[i]["### ".Length..].Trim();

            var j = i + 1;
            while (j < lines.Length && lines[j].Trim().Length == 0)
            {
                j++;
            }

            if (j >= lines.Length || !lines[j].TrimStart().StartsWith("Grain:", StringComparison.Ordinal))
            {
                continue;
            }

            // The opening paragraph, joined. It ends at the first blank line.
            var paragraph = new List<string>();
            for (var k = j; k < lines.Length && lines[k].Trim().Length > 0; k++)
            {
                paragraph.Add(lines[k].Trim());
            }

            var text = string.Join(" ", paragraph);
            var named = new HashSet<string>(StringComparer.Ordinal);

            foreach (Match bold in Bold.Matches(text))
            {
                foreach (Match word in Word.Matches(bold.Groups[1].Value))
                {
                    if (components.Contains(word.Value))
                    {
                        named.Add(word.Value);
                    }
                }
            }

            foreach (var name in heading.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                writers[name] = named;
            }
        }

        return writers;
    }

    private static readonly Regex Bold = new(@"\*\*(.+?)\*\*", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex Word = new(@"[A-Za-z][A-Za-z0-9]*", RegexOptions.Compiled);

    /// <summary>
    /// Every `table.column` SCHEMA.md declares as not money, from its "Columns that
    /// are not money" section [INVARIANT 16, 1.8].
    ///
    /// `guards.ps1` parses the same section from the migrations side, before the
    /// database exists, because CI runs it before the migrate step. This reads the
    /// same declaration against the live database, which is the assertion the
    /// decision actually states. Two readers of one list rather than two lists.
    /// </summary>
    public static IReadOnlySet<string> NonMonetaryColumns()
    {
        var text = File.ReadAllText(Path);

        var section = Section.Match(text);
        if (!section.Success)
        {
            throw new InvalidOperationException(
                "docs/SCHEMA.md has no 'Columns that are not money' section. INVARIANT 16 is asserted " +
                "against that list and there is nothing to assert against without it.");
        }

        return Declared.Matches(section.Groups[1].Value)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static readonly Regex Section = new(
        @"###\s+Columns that are not money(.*?)\r?\n---",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex Declared = new(
        @"^\|\s*`([a-z_0-9]+\.[a-z_0-9]+)`",
        RegexOptions.Compiled | RegexOptions.Multiline);

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
