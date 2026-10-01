using System.Text.RegularExpressions;
using StockResearcherLab.Pipeline;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Stages;

/// <summary>
/// Checkpoint 5.5.14. `RUNBOOK.md`'s nightly cycle table describes the night that runs.
///
/// **The cycle table is what an operator reads at 18:40 on a night that stopped**, to learn
/// what should have run by then. Its 18:05 row named three engines where the code runs five,
/// and `FlowEngine` and `SentimentEngine` appeared nowhere in the document, so a night halted
/// in either read as a stage the runbook had never heard of.
///
/// **Each stage in <see cref="NightlyRun.EveningOrder"/> must be named by the row at the clock
/// time its own comment in `NightlyRun.cs` gives.** The table is prose, so a stage is named by
/// its subject: its class name with the last word, the role, dropped, so `FlowEngine` is
/// "flow" and `MarketContextEngine` is "market context", matched case-insensitively at the
/// start of a word. That rule is derived rather than listed, so a stage added to the order is
/// held to the table without anyone extending this file.
/// </summary>
public sealed class RunbookCycleTests
{
    [Fact]
    public void EveryStageOfTheEveningOrderIsNamedAtTheTimeItsCommentGives()
    {
        var table = RunbookDocument.CycleTable();
        var times = CommentTimes();

        var missing = new List<string>();

        foreach (var stage in NightlyRun.EveningOrder)
        {
            Assert.True(times.ContainsKey(stage),
                $"{stage} is in the evening order and its line in NightlyRun.cs gives no clock time.");

            var time = times[stage];
            var subject = Subject(stage);
            var rows = table.Where(r => r.Time == time).ToList();

            if (!rows.Any(r => Regex.IsMatch(r.What, $@"\b{Regex.Escape(subject)}", RegexOptions.IgnoreCase)))
            {
                missing.Add($"{stage} at {time}, as \"{subject}\": the row reads \"{string.Join(" / ", rows.Select(r => r.What))}\"");
            }
        }

        Assert.True(missing.Count == 0,
            "RUNBOOK.md's cycle table does not name these stages at the time NightlyRun.cs gives them:\n  " +
            string.Join("\n  ", missing));
    }

    /// <summary>The parse and the subject rule read what they are meant to.</summary>
    [Fact]
    public void TheTableAndTheSubjectsReadAsIntended()
    {
        Assert.True(RunbookDocument.CycleTable().Count > 12, "RUNBOOK.md's cycle table parsed short.");
        Assert.Equal(NightlyRun.EveningOrder.Length, CommentTimes().Count);

        Assert.Equal("flow", Subject("FlowEngine"));
        Assert.Equal("market context", Subject("MarketContextEngine"));
        Assert.Equal("freshness", Subject("FreshnessGuard"));
    }

    /// <summary>"MarketContextEngine" to "market context": the words of the name, the last dropped.</summary>
    private static string Subject(string stage)
    {
        var words = Regex.Matches(stage, "[A-Z][a-z0-9]*").Select(m => m.Value.ToLowerInvariant()).ToList();

        return string.Join(" ", words.Take(Math.Max(1, words.Count - 1)));
    }

    /// <summary>Each evening-order entry's clock time, read off its own comment in `NightlyRun.cs`.</summary>
    private static Dictionary<string, string> CommentTimes()
    {
        var path = System.IO.Path.Combine(SchemaDocument.RepositoryRoot, "src", "StockResearcherLab.Pipeline", "NightlyRun.cs");

        return Regex.Matches(File.ReadAllText(path), @"""(?<stage>\w+)"",\s*//\s*C\d+\s+(?<time>\d{2}:\d{2})")
            .ToDictionary(m => m.Groups["stage"].Value, m => m.Groups["time"].Value, StringComparer.Ordinal);
    }
}