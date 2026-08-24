using System.Text.RegularExpressions;
using Xunit;

namespace StockResearcherLab.Tests.Corpus;

/// <summary>
/// `SCREEN_LIFECYCLE.md` §4.5's prose counts against §4.5's own table [D-128, Q.6].
///
/// **Why this exists.** The section said eight readers mean "candidate" and its table
/// marked seven. Two of the three counts in the same sentence were right, which is what
/// made the wrong one hard to see: a reader checking it would tick two and stop. The
/// discrepancy was reported before phase 4 and survived the whole of it unchanged,
/// because nothing read either number.
///
/// **A table is checkable and a sentence is not, so the table is the specification and
/// the sentence is held against it** [D-128]. That is the same instrument
/// `WriteOwnershipConformanceTests` uses on `SCHEMA.md`'s split list, which was a
/// hardcoded array until it was read off the document instead.
/// </summary>
public sealed class ScreenLifecycleCountTests
{
    private static readonly string Path =
        System.IO.Path.Combine(RepositoryRoot(), "docs", "SCREEN_LIFECYCLE.md");

    /// <summary>
    /// The three groups §4.5's table sorts its readers into, plus the readers that do
    /// not read `attribution` at all.
    ///
    /// Stated as a record rather than three loose ints so that a count read into the
    /// wrong variable is a compile error rather than an assertion that still passes.
    /// </summary>
    private readonly record struct ReaderCounts(int Candidates, int Everything, int Both, int NotAtAll)
    {
        public int Total => Candidates + Everything + Both + NotAtAll;
    }

    [Fact]
    public void TheProseCountsMatchTheTableItSummarises()
    {
        var counts = Counts();

        // The table is the source. Asserted first and on its own, because if the parse
        // is wrong every comparison below is wrong in the same direction and would still
        // look like agreement.
        Assert.Equal(15, counts.Total);
        Assert.Equal(7, counts.Candidates);
        Assert.Equal(3, counts.Everything);
        Assert.Equal(2, counts.Both);
        Assert.Equal(3, counts.NotAtAll);

        var text = Flattened();

        // §4.5's summary sentence, whitespace-tolerant because this document is
        // hard-wrapped and a line-anchored pattern would miss silently [CLAUDE.md §7].
        Assert.Matches(
            @"Seven\s+readers\s+mean\s+.candidate.\s+and\s+would\s+be\s+wrong\s+without\s+a\s+filter",
            text);

        Assert.Matches(@"Three\s+mean\s+everything\s+surfaced", text);

        Assert.Matches(
            @"Three\s+that\s+a\s+reader\s+would\s+expect\s+to\s+appear\s+do\s+not\s+read\s+the\s+table",
            text);

        // §4.7's argument for the view, which restates the same count as a number of
        // chances to forget a WHERE clause. It drifted with §4.5's and is held here so
        // the two cannot part company again.
        Assert.Matches(
            @"Seven\s+readers\s+that\s+must\s+each\s+remember\s+a\s+.WHERE.\s+clause\s+is\s+seven\s+chances",
            text);
    }

    /// <summary>
    /// **The old count must not still be stated anywhere in the section**, which is the
    /// half a positive assertion cannot cover: a sentence saying seven can sit beside one
    /// saying eight and both patterns above would pass.
    ///
    /// The struck original is excluded, because a strike is the record of the correction
    /// rather than a live statement [`CLAUDE.md` §13].
    /// </summary>
    [Fact]
    public void NoLiveSentenceInTheSectionStillSaysEight()
    {
        var live = StrikeThrough.Replace(Section(), " ");

        Assert.DoesNotMatch(@"[Ee]ight\s+readers", Whitespace.Replace(live, " "));
    }

    /// <summary>
    /// A parse that matched nothing would return zeroes and fail the assertions above
    /// loudly, but a parse that matched the wrong column would return plausible numbers.
    /// So one row is named and its group checked.
    /// </summary>
    [Fact]
    public void TheParseReadsTheMeansColumnAndNotAnother()
    {
        var rows = Rows();

        Assert.Equal("everything surfaced", rows["C21 ForwardReturnFiller"]);
        Assert.Equal("candidates", rows["U7 Primary claim"]);
        Assert.Equal("both", rows["U4 Screens"]);
        Assert.Equal("n/a", rows["C15 DossierBuilder"]);
    }

    // ---------------------------------------------------------------- helpers ---

    private static ReaderCounts Counts()
    {
        var groups = Rows().Values.ToList();

        return new ReaderCounts(
            groups.Count(g => g == "candidates"),
            groups.Count(g => g == "everything surfaced"),
            groups.Count(g => g == "both"),
            groups.Count(g => g == "n/a"));
    }

    /// <summary>
    /// §4.5's table as reader to group. The Means column is found by its heading rather
    /// than by position, so a column inserted before it does not silently shift the
    /// parse onto the wrong one.
    /// </summary>
    private static IReadOnlyDictionary<string, string> Rows()
    {
        var lines = Section()
            .Split('\n')
            .Where(l => l.StartsWith('|'))
            .ToList();

        Assert.True(lines.Count > 2,
            "§4.5's table did not parse as a table. The section heading or the markup has moved.");

        var headings = Cells(lines[0]);
        var means = headings.IndexOf("Means");

        Assert.True(means >= 0,
            "§4.5's table has no Means column. Its headings are: " + string.Join(", ", headings));

        var found = new Dictionary<string, string>(StringComparer.Ordinal);

        // lines[1] is the alignment row.
        foreach (var line in lines.Skip(2))
        {
            var cells = Cells(line);
            found[cells[0]] = cells[means];
        }

        return found;
    }

    private static List<string> Cells(string line)
        => [.. line.Trim().Trim('|').Split('|').Select(c => c.Trim())];

    /// <summary>§4.5 alone, so §4.6's prose cannot satisfy an assertion about §4.5's.</summary>
    private static string Section()
    {
        var text = File.ReadAllText(Path);

        var from = text.IndexOf("### 4.5", StringComparison.Ordinal);
        var to = text.IndexOf("### 4.6", StringComparison.Ordinal);

        Assert.True(from >= 0 && to > from, "§4.5 and §4.6 are not both present, in that order.");

        return text[from..to];
    }

    /// <summary>
    /// §4.5 through §4.7 with whitespace collapsed, which is the range the two sentences
    /// under test live in.
    /// </summary>
    private static string Flattened()
    {
        var text = File.ReadAllText(Path);

        var from = text.IndexOf("### 4.5", StringComparison.Ordinal);
        var to = text.IndexOf("## 5.", StringComparison.Ordinal);

        Assert.True(from >= 0 && to > from, "§4.5 and §5 are not both present, in that order.");

        return Whitespace.Replace(StrikeThrough.Replace(text[from..to], " "), " ");
    }

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Struck text, which is a record of what a statement used to say and not a
    /// statement. Removed before either direction is asserted, so the correction's own
    /// wording cannot satisfy a pattern or trip the negative one.
    /// </summary>
    private static readonly Regex StrikeThrough = new(@"~~.*?~~", RegexOptions.Compiled | RegexOptions.Singleline);

    private static string RepositoryRoot()
    {
        var here = new DirectoryInfo(AppContext.BaseDirectory);

        while (here is not null && !File.Exists(System.IO.Path.Combine(here.FullName, "CLAUDE.md")))
        {
            here = here.Parent;
        }

        Assert.NotNull(here);

        return here.FullName;
    }
}
