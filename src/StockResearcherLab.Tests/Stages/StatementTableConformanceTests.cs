using System.Text.RegularExpressions;
using Npgsql;
using StockResearcherLab.Api;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Pipeline;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Stages;

/// <summary>
/// Checkpoint 5.5.9. The declared-access gate reads the statement, not only the table it is
/// handed.
///
/// **<see cref="DeclaredAccess"/> checks the table a caller names and never the SQL it
/// runs.** <c>ReadAsync("screen_history", sql)</c> passes on <c>screen_history</c> being
/// declared whatever <c>sql</c> joins, so a statement can read a table its component never
/// declared and the gate stays green. Nothing at runtime can close that without parsing
/// SQL, so this closes it at the source: every table a statement names after
/// <c>FROM</c>, <c>JOIN</c>, <c>INTO</c> or <c>UPDATE</c> must be one the code issuing it
/// declares.
///
/// **What counts as a declaration, per source file.** The read and write sets of every
/// registered component whose class the file declares, and every inline
/// <c>new DeclaredAccess(...)</c> in the file, an argument naming a stage being resolved to
/// that stage's sets. A file whose statements no declaration covers and which declares a
/// static class is a helper, <c>Universe</c> being the one today, and its tables are
/// required of every declaring file that calls it, which is D-101's rule for
/// <c>security_daily</c> stated generally. Any other file carrying SQL is infrastructure
/// outside the stage contract, the config store and the run log among them, and is not
/// scanned.
///
/// **What it cannot see, stated so a green run is not read as more than it is.** A table
/// interpolated into a statement, <c>FROM {source.Table}</c>, has no literal name to read;
/// those statements stay with the column check. And the unit is the file, so a file with
/// two declarations is held to their union: a statement under one that reads a table only
/// the other declares passes here.
///
/// **Identifiers are kept only where the live schema has a table or view of that name**, so
/// a CTE, an alias, a function called after <c>FROM</c> and every word of prose drop out
/// without a list of exceptions. The keywords are matched in upper case, the convention
/// every statement in this repository follows, so C#'s lower-case <c>from</c> is not read.
/// </summary>
[Collection("database")]
public sealed class StatementTableConformanceTests
{
    [Fact]
    public async Task EveryTableAStatementNamesIsDeclaredByTheCodeThatIssuesIt()
    {
        var tables = await LiveTablesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        var violations = Violations(SourceFiles(), Owners(), tables);

        Assert.True(violations.Count == 0,
            "Statements name tables their code does not declare. DeclaredAccess passes these, because " +
            "it checks the table a caller names and not the SQL it runs:\n  " +
            string.Join("\n  ", violations));
    }

    /// <summary>
    /// **The check discriminates** [5.5.9]. The plan's own case: <c>SelectionRangeRun</c>'s
    /// floor read joins <c>screen_score_daily</c> under a declaration naming
    /// <c>screen_history</c> and <c>screen_score_daily</c>. Removed from a copy of that
    /// declaration, this check fails on it, and the runtime gate does not: the read is
    /// issued against <c>screen_history</c>, which is still declared, and the join runs.
    /// </summary>
    [Fact]
    public async Task TheCheckFailsWhenATableLeavesADeclaredList()
    {
        var tables = await LiveTablesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        var files = SourceFiles();
        var owners = Owners();

        var key = Assert.Single(files.Keys, k => k.EndsWith("/SelectionRangeRun.cs", StringComparison.Ordinal));

        const string declared = "[\"screen_history\", \"screen_score_daily\"]";
        Assert.Contains(declared, files[key], StringComparison.Ordinal);

        var mutated = new Dictionary<string, string>(files, StringComparer.Ordinal)
        {
            [key] = files[key].Replace(declared, "[\"screen_history\"]", StringComparison.Ordinal),
        };

        Assert.DoesNotContain($"{key}: screen_score_daily", Violations(files, owners, tables));
        Assert.Contains($"{key}: screen_score_daily", Violations(mutated, owners, tables));

        // The runtime gate, over the same mutilated declaration, refuses nothing: the
        // statement is issued against screen_history and that is all it checks.
        var access = new DeclaredAccess("SelectionRangeRun", ["screen_history"], []);
        access.EnsureCanRead("screen_history");
    }

    /// <summary>
    /// The scan reads what it is meant to, so the assertion above cannot pass over a scan
    /// that found nothing. Every registered component's file is found, and the plan's case
    /// is among the statements read.
    /// </summary>
    [Fact]
    public async Task TheScanReadsTheStatementsItIsMeantTo()
    {
        var tables = await LiveTablesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        var files = SourceFiles();

        foreach (var owner in Owners())
        {
            Assert.True(files.Values.Any(text => Declares(text, owner.TypeName)),
                $"{owner.TypeName} is a registered component and no source file under src/ declares it.");
        }

        var key = Assert.Single(files.Keys, k => k.EndsWith("/SelectionRangeRun.cs", StringComparison.Ordinal));
        Assert.Contains("screen_score_daily", StatementTables(files[key], tables));

        // And more than a handful of files carry statements, which a scan that broke on the
        // keyword pattern would not find.
        Assert.True(files.Values.Count(text => StatementTables(text, tables).Count > 0) >= 20,
            "The scan found statements in fewer than twenty files, so it is reading less than the repository holds.");
    }

    // ------------------------------------------------------------- the check ---

    private sealed record Owner(string TypeName, IReadOnlySet<string> Tables);

    private static readonly Regex Keyword = new(
        @"\b(?:FROM|JOIN|INTO|UPDATE)\s+(?:ONLY\s+)?""?(?<t>[a-z_][a-z0-9_]*)""?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Quoted = new(
        @"""(?<s>[^""]*)""", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Identifier = new(
        @"^[a-z_][a-z0-9_]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex StaticClass = new(
        @"\bstatic\s+(?:partial\s+)?class\s+(?<n>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>One line per violation, as "path: table", ordinally sorted.</summary>
    private static List<string> Violations(
        IReadOnlyDictionary<string, string> files, IReadOnlyList<Owner> owners, IReadOnlySet<string> tables)
    {
        var declared = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var (path, text) in files)
        {
            var scope = Declared(path, text, owners);

            if (scope is not null)
            {
                declared[path] = scope;
            }
        }

        // Helpers: statements in a file nothing declares for, in a static class. Their
        // tables are required of every declaring file that calls them.
        var helpers = files
            .Where(f => !declared.ContainsKey(f.Key))
            .Select(f => (Classes: StaticClass.Matches(f.Value).Select(m => m.Groups["n"].Value).ToList(),
                          Tables: StatementTables(f.Value, tables)))
            .Where(h => h.Classes.Count > 0 && h.Tables.Count > 0)
            .ToList();

        var violations = new List<string>();

        foreach (var (path, scope) in declared)
        {
            var needed = new SortedSet<string>(StatementTables(files[path], tables), StringComparer.Ordinal);

            foreach (var helper in helpers)
            {
                if (helper.Classes.Any(c => Regex.IsMatch(files[path], $@"\b{Regex.Escape(c)}\.")))
                {
                    needed.UnionWith(helper.Tables);
                }
            }

            violations.AddRange(needed.Where(t => !scope.Contains(t)).Select(t => $"{path}: {t}"));
        }

        violations.Sort(StringComparer.Ordinal);

        return violations;
    }

    /// <summary>
    /// Every table one file declares, or null if it declares nothing: the sets of every
    /// registered component whose class it declares, and every inline
    /// <c>new DeclaredAccess(...)</c> in it.
    /// </summary>
    private static HashSet<string>? Declared(string path, string text, IReadOnlyList<Owner> owners)
    {
        HashSet<string>? scope = null;

        foreach (var owner in owners.Where(o => Declares(text, o.TypeName)))
        {
            (scope ??= new HashSet<string>(StringComparer.Ordinal)).UnionWith(owner.Tables);
        }

        foreach (var arguments in DeclaredAccessArguments(text))
        {
            scope ??= new HashSet<string>(StringComparer.Ordinal);

            var strings = Quoted.Matches(arguments).Select(m => m.Groups["s"].Value).ToList();

            if (strings.Count > 0)
            {
                // The first string is the declaring name, the rest are its tables, and a
                // column name inside a TableWrite is a harmless extra rather than a table.
                scope.UnionWith(strings.Skip(1).Where(s => Identifier.IsMatch(s)));
                continue;
            }

            // A single argument naming a stage: resolve it to the stage's own sets, or fail
            // rather than skip, because a declaration this cannot read is one it is not checking.
            var argument = arguments.Trim();

            if (argument == "this")
            {
                continue;
            }

            var created = Regex.Match(text, $@"\b{Regex.Escape(argument)}\s*=\s*new\s+(?<t>[A-Za-z_][A-Za-z0-9_]*)\s*\(");

            if (created.Success && owners.FirstOrDefault(o => o.TypeName == created.Groups["t"].Value) is { } owner)
            {
                scope.UnionWith(owner.Tables);
            }
            else if (StatementTablesUnfiltered(text).Count > 0)
            {
                throw new InvalidOperationException(
                    $"{path} declares access through '{argument}', which this check cannot resolve to a " +
                    "registered component, and the file carries statements. Extend the resolution rather " +
                    "than let a declaration go unread.");
            }
        }

        return scope;
    }

    /// <summary>The argument text of every <c>new DeclaredAccess(...)</c>, parentheses balanced.</summary>
    private static IEnumerable<string> DeclaredAccessArguments(string text)
    {
        const string opening = "new DeclaredAccess(";

        for (var at = text.IndexOf(opening, StringComparison.Ordinal);
             at >= 0;
             at = text.IndexOf(opening, at + 1, StringComparison.Ordinal))
        {
            var start = at + opening.Length;
            var depth = 1;
            var i = start;
            var inString = false;

            for (; i < text.Length && depth > 0; i++)
            {
                var c = text[i];

                if (c == '"')
                {
                    inString = !inString;
                }
                else if (!inString && c == '(')
                {
                    depth++;
                }
                else if (!inString && c == ')')
                {
                    depth--;
                }
            }

            yield return text[start..(i - 1)];
        }
    }

    private static bool Declares(string text, string typeName)
        => Regex.IsMatch(text, $@"\b(?:class|record)\s+{Regex.Escape(typeName)}\b");

    /// <summary>Identifiers after the four keywords that the live schema has as a table or view.</summary>
    private static HashSet<string> StatementTables(string text, IReadOnlySet<string> tables)
        => [.. StatementTablesUnfiltered(text).Where(tables.Contains)];

    private static HashSet<string> StatementTablesUnfiltered(string text)
        => [.. Keyword.Matches(text).Select(m => m.Groups["t"].Value)];

    // ------------------------------------------------------------- the inputs ---

    /// <summary>
    /// Every registered component with what it declares: the write owners, the read owners
    /// the Pipeline hosts, and the Api's.
    /// </summary>
    private static List<Owner> Owners()
    {
        var cs = TestDatabase.ConnectionString;

        var all = new List<IComponent>();
        all.AddRange(PipelineComposition.AllOwnersForConformance(cs));
        all.AddRange(PipelineComposition.AllReadOwnersForConformance(cs));
        all.AddRange(ApiComposition.AllReadOwnersForConformance(cs));

        return [.. all
            .GroupBy(c => c.GetType().Name, StringComparer.Ordinal)
            .Select(g => new Owner(
                g.Key,
                g.SelectMany(c => (c is IReadOwner r ? r.ReadSet : [])
                        .Concat(c is IWriteOwner w ? w.WriteSet.Select(x => x.Table) : []))
                    .ToHashSet(StringComparer.Ordinal)))
            .OrderBy(o => o.TypeName, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Every C# source file under <c>src/</c> outside the tests and the build output, keyed
    /// by its repository path, with line comments removed so prose about SQL is not read as
    /// SQL.
    /// </summary>
    private static Dictionary<string, string> SourceFiles()
    {
        var root = SchemaDocument.RepositoryRoot;
        var src = System.IO.Path.Combine(root, "src");

        var files = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            var rel = System.IO.Path.GetRelativePath(root, file).Replace('\\', '/');

            if (rel.Contains("/bin/", StringComparison.Ordinal) || rel.Contains("/obj/", StringComparison.Ordinal)
                || rel.StartsWith("src/StockResearcherLab.Tests/", StringComparison.Ordinal))
            {
                continue;
            }

            files[rel] = Regex.Replace(File.ReadAllText(file), @"//[^\r\n]*", string.Empty);
        }

        return files;
    }

    private static async Task<IReadOnlySet<string>> LiveTablesAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public';", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(true);

        var tables = new HashSet<string>(StringComparer.Ordinal);

        while (await reader.ReadAsync(ct).ConfigureAwait(true))
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }
}
