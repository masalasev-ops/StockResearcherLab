using System.Text.RegularExpressions;

namespace StockResearcherLab.Tests.Corpus;

/// <summary>
/// The C# source under <c>src/</c>, read for the conformance tests that hold a declaration
/// against the code that issues it [5.5.9, 5.5.10].
///
/// **Line comments are removed**, so prose about a statement is not read as one. The tests
/// and the build output are excluded: the first are not the code under check, and the
/// second is not source.
/// </summary>
public static class SourceTree
{
    /// <summary>Every source file, keyed by its repository path with forward slashes, ordinally ordered.</summary>
    public static IReadOnlyDictionary<string, string> Files()
    {
        var root = SchemaDocument.RepositoryRoot;
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(System.IO.Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
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

    /// <summary>Whether a file's text declares a class or record of this name, partial or not.</summary>
    public static bool Declares(string text, string typeName)
        => Regex.IsMatch(text, $@"\b(?:class|record)\s+{Regex.Escape(typeName)}\b");

    /// <summary>The text of every file declaring this type, concatenated in path order.</summary>
    public static string Of(IReadOnlyDictionary<string, string> files, string typeName)
        => string.Join("\n", files.Where(f => Declares(f.Value, typeName)).Select(f => f.Value));
}
