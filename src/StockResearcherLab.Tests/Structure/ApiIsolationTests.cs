using System.Text.Json;
using System.Xml.Linq;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Structure;

/// <summary>
/// The Api never references Pipeline, and that is load-bearing rather than tidy
/// [CLAUDE.md section 4]. It is what structurally prevents the interface from
/// invoking a stage, which is how the read-only guarantee survives contact with
/// a future feature request.
///
/// Asserted by inspecting the project references rather than by convention,
/// because a convention is what this is meant to replace.
/// </summary>
public sealed class ApiIsolationTests
{
    private const string Api = "StockResearcherLab.Api";
    private const string Pipeline = "StockResearcherLab.Pipeline";

    [Fact]
    public void ApiDoesNotReferencePipeline()
    {
        var closure = TransitiveProjectReferences(ProjectPath(Api));

        Assert.DoesNotContain(Pipeline, closure);

        // The closure is checked transitively, not just directly. Api referencing
        // Data referencing Pipeline would satisfy a direct check and break the
        // guarantee just as completely.
        Assert.Contains("StockResearcherLab.Core", closure);
        Assert.Contains("StockResearcherLab.Data", closure);
    }

    [Fact]
    public void ApiDoesNotReferenceWorkerEither()
    {
        // Worker references Pipeline, so a reference to it would reach a stage by
        // one more hop. Not named in CLAUDE.md section 4 as a prohibition, but it
        // is the same guarantee.
        Assert.DoesNotContain("StockResearcherLab.Worker", TransitiveProjectReferences(ProjectPath(Api)));
    }

    [Fact]
    public void TheCompiledApiCarriesNoPipelineDependency()
    {
        // The project file is the declaration; this is what actually shipped. A
        // raw <Reference> to a dll, or a package that happened to drag Pipeline
        // in, would pass the csproj walk and fail here.
        var depsPath = Path.Combine(
            SchemaDocument.RepositoryRoot, "src", Api, "bin", "Debug", "net10.0", Api + ".deps.json");

        Assert.True(File.Exists(depsPath),
            $"{Api}.deps.json was not found at {depsPath}. Build the solution before running this test; " +
            "an absent file would otherwise let this assertion pass by not looking at anything.");

        using var doc = JsonDocument.Parse(File.ReadAllText(depsPath));
        var libraries = doc.RootElement.GetProperty("libraries").EnumerateObject()
            .Select(p => p.Name)
            .ToList();

        Assert.DoesNotContain(libraries, name => name.StartsWith(Pipeline, StringComparison.Ordinal));

        // Guards the assertion above rather than the Api. If deps.json ever stops
        // listing project libraries, DoesNotContain would pass over an empty set.
        Assert.Contains(libraries, name => name.StartsWith("StockResearcherLab.Data", StringComparison.Ordinal));
    }

    private static string ProjectPath(string project)
        => Path.Combine(SchemaDocument.RepositoryRoot, "src", project, project + ".csproj");

    /// <summary>Every project reachable from a project file by ProjectReference, transitively, excluding itself.</summary>
    private static IReadOnlyList<string> TransitiveProjectReferences(string projectPath)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Walk(projectPath);

        // Sorted explicitly. Set enumeration order is unspecified and this reaches
        // an assertion message [CLAUDE.md section 6].
        return seen.OrderBy(s => s, StringComparer.Ordinal).ToList();

        void Walk(string path)
        {
            var dir = Path.GetDirectoryName(path)!;
            var xml = XDocument.Load(path);

            foreach (var include in xml.Descendants("ProjectReference")
                         .Select(e => e.Attribute("Include")?.Value)
                         .Where(v => !string.IsNullOrWhiteSpace(v)))
            {
                var resolved = Path.GetFullPath(Path.Combine(dir, include!.Replace('\\', Path.DirectorySeparatorChar)));
                var name = Path.GetFileNameWithoutExtension(resolved);

                if (seen.Add(name) && File.Exists(resolved))
                {
                    Walk(resolved);
                }
            }
        }
    }
}
