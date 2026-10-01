using System.Text.RegularExpressions;
using StockResearcherLab.Data;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Config;

/// <summary>
/// Checkpoint 5.5.12. `CONFIG_REFERENCE.md`'s Consumer column made true, and kept true.
///
/// **The column is what an audit is told to trust about a key's blast radius**, and
/// `CLAUDE.md` §8 asks that it record the verified consumer rather than the assumed one,
/// because an unverified entry lets an audit conclude a value is wired up, or reaches only
/// one place, when it does not. Nothing compared it with the code, and five rows had a
/// consumer the cell did not name, one key had no row at all, and eight cells did not name
/// the reader the document's own subsection says they name.
///
/// **A read site is a string literal equal to a key**, in any call. The plan asked for
/// <c>RequireAsync("key"</c> literals alone, and most of this codebase resolves config
/// through small helpers, <c>LongAsync(context, "key", ct)</c> and their kind, so that form
/// would have missed the consumer its own leading example names. A key built at run time,
/// as <c>facade.Own("metrics")</c> builds a screen's, has no literal and is not seen; the
/// screen keys are documented by pattern for that reason. The seeder and the store are
/// not consumers and their file is not scanned.
///
/// **The consumer is the file's own type**, the one named like the file, and the Worker
/// host for <c>Program.cs</c>, whose top-level statements have no type of their own. A
/// helper reading on a stage's behalf is named by its own name, and its row says which
/// stages call it.
/// </summary>
public sealed class ConfigReferenceDocumentTests
{
    [Fact]
    public void EveryConfigReadSiteIsNamedInItsConsumerCell()
    {
        var cells = ConsumerCells();
        var keys = cells.Keys.Concat(ConfigSeeder.Keys.Select(k => k.Key)).ToHashSet(StringComparer.Ordinal);

        var unnamed = ReadSites(keys)
            .Where(site => !cells.TryGetValue(site.Key, out var cell) || !Names(cell, site.Consumer))
            .Select(site => cells.ContainsKey(site.Key)
                ? $"{site.Key}: read by {site.Consumer} in {site.Path}, and its Consumer cell does not name it"
                : $"{site.Key}: read by {site.Consumer} in {site.Path}, and it has no row at all")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.True(unnamed.Count == 0,
            "CONFIG_REFERENCE.md's Consumer column does not name these read sites:\n  " +
            string.Join("\n  ", unnamed));
    }

    /// <summary>
    /// The scan reads what it is meant to, so the assertion above cannot pass over a scan
    /// that found nothing: dozens of read sites, over most of the documented keys.
    /// </summary>
    [Fact]
    public void TheScanFindsTheReadSitesItIsMeantTo()
    {
        var cells = ConsumerCells();
        var sites = ReadSites(cells.Keys.ToHashSet(StringComparer.Ordinal));

        Assert.True(cells.Count > 90, $"CONFIG_REFERENCE.md parsed as {cells.Count} key rows.");
        Assert.True(sites.Count > 80, $"The scan found {sites.Count} read sites.");
        Assert.True(sites.Select(s => s.Key).Distinct().Count() > 50,
            "The scan found read sites for fewer than fifty documented keys.");

        // The plan's leading example, read through a helper rather than RequireAsync.
        Assert.Contains(sites, s => s.Key == "events.earnings_backward_days" && s.Consumer == "FundamentalsIngestor");
    }

    private sealed record ReadSite(string Key, string Consumer, string Path);

    private static readonly Regex Literal = new(
        @"""(?<k>[a-z0-9_]+\.[a-z0-9_.]+)""", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static List<ReadSite> ReadSites(IReadOnlySet<string> keys)
    {
        var sites = new List<ReadSite>();

        foreach (var (path, text) in SourceTree.Files())
        {
            // The seeder and the store: they write and resolve every key, and consume none.
            if (path.EndsWith("/ConfigStore.cs", StringComparison.Ordinal))
            {
                continue;
            }

            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            var consumer = name == "Program" ? "Worker" : name;

            sites.AddRange(Literal.Matches(text)
                .Select(m => m.Groups["k"].Value)
                .Where(keys.Contains)
                .Select(k => new ReadSite(k, consumer, path)));
        }

        return sites;
    }

    /// <summary>A consumer is named when the cell carries its name as a word.</summary>
    private static bool Names(string cell, string consumer)
        => Regex.IsMatch(cell, $@"(?<![A-Za-z0-9]){Regex.Escape(consumer)}(?![A-Za-z0-9])");

    /// <summary>Every key row in the document, key to its Consumer cell, the fourth column.</summary>
    private static Dictionary<string, string> ConsumerCells()
    {
        var path = System.IO.Path.Combine(SchemaDocument.RepositoryRoot, "docs", "CONFIG_REFERENCE.md");
        var cells = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in File.ReadAllLines(path))
        {
            var m = Regex.Match(line, @"^\|\s*`(?<k>[a-z0-9_]+\.[a-z0-9_.]+)`\s*\|");

            if (!m.Success)
            {
                continue;
            }

            var parts = line.Split('|');

            // | key | default | set by | consumer | verified |, so the consumer is the fourth
            // cell. The reader subsection's rows are three cells wide and are not key rows.
            if (parts.Length >= 7)
            {
                cells[m.Groups["k"].Value] = parts[4];
            }
        }

        return cells;
    }
}
