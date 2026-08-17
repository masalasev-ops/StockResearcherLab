using System.Text.RegularExpressions;
using Npgsql;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Structure;

/// <summary>
/// That the suite runs against its own database and cannot reach the developer's.
///
/// **This is asserted rather than conventional because the convention failed twice.**
/// A sweep's `run_log` row was deleted by a test clearing by stage name, and a fixture
/// nearly ran `DELETE FROM security_daily` against a store holding 3.11's output. Both
/// were tests written correctly against the wrong database, so the correction is which
/// database they reach and not how carefully they delete [open item 26].
///
/// Two halves, and both are needed. The runtime half checks where this process actually
/// connected. The source half checks that no test has a second route to a connection
/// string, because the runtime half can only see the one it was given.
/// </summary>
public sealed class TestDatabaseIsolationTests
{
    /// <summary>ci.ps1's default, which it drops at the start of every run.</summary>
    private const string CiDatabase = "stockresearcherlab_ci";

    /// <summary>
    /// The database in use is the derived one, and the derivation is visible in the
    /// name rather than being a setting somebody could leave unset.
    /// </summary>
    [Fact]
    public void TheSuiteRunsAgainstADerivedDatabaseRatherThanTheSuppliedOne()
    {
        Assert.EndsWith("_tests", TestDatabase.DatabaseName, StringComparison.Ordinal);
        Assert.NotEqual(TestDatabase.SuppliedDatabaseName, TestDatabase.DatabaseName);
    }

    /// <summary>
    /// **The suite's database cannot be the one `ci.ps1` drops**, because the two may be
    /// running at once: `ci.ps1` runs the suite inside a worktree while a developer runs
    /// it in the working tree, and a drop landing on the database the other one is
    /// asserting against would read as a test failure rather than as a collision.
    ///
    /// `ci.ps1` supplies `stockresearcherlab_ci`, from which this derives
    /// `stockresearcherlab_ci_tests`, and a bare run derives `stockresearcherlab_tests`.
    /// The three names are distinct by construction and this pins the one that matters.
    /// </summary>
    [Fact]
    public void TheSuiteDatabaseIsNeverTheOneCiPs1Drops()
    {
        Assert.NotEqual(CiDatabase, TestDatabase.DatabaseName);
    }

    /// <summary>
    /// Where this process is actually connected, read from the server rather than parsed
    /// back out of the string it was built from. A string that parses one way and
    /// connects another is the failure the whole class is about.
    /// </summary>
    [Fact]
    public async Task TheConnectionReachesTheDatabaseTheSuiteDerived()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand("SELECT current_database();", conn);

        Assert.Equal(
            TestDatabase.DatabaseName,
            (string) (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true))!);
    }

    /// <summary>
    /// The marker this suite stamps is present, which is what preparation refuses to
    /// proceed without. A database carrying it is one this suite made and may empty.
    /// </summary>
    [Fact]
    public async Task TheDatabaseCarriesTheMarkerTheSuiteStamps()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(
            "SELECT marker FROM meta.test_database;", conn);

        Assert.Equal(
            "StockResearcherLab.Tests",
            (string) (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true))!);
    }

    /// <summary>
    /// Config resolves, which is the property the seeding race cost. A key needed by
    /// every stage is readable at a date inside the backfill window without any test
    /// class having seeded it first.
    ///
    /// **This is the assertion `FundamentalsRangeTests` was making by accident.** That
    /// class went through the real `ConfigStore` and never seeded, so it passed only
    /// when a class in another collection had already seeded in parallel. On a fresh
    /// database it failed, and the failure named the last stage to ask for a key rather
    /// than naming the absent rows.
    /// </summary>
    [Fact]
    public async Task ConfigIsSeededBeforeAnyTestAsksForIt()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM config_rows WHERE key = @k;", conn);
        cmd.Parameters.AddWithValue("k", "backfill.unit_reserve");

        Assert.Equal(1L, (long) (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true))!);
    }

    // --------------------------------------------------- the source half ---

    /// <summary>
    /// The three configuration calls that can produce a connection string appear in
    /// `Corpus/TestDatabase.cs` and nowhere else in the suite.
    ///
    /// Comment lines are stripped first, because this file and others discuss
    /// `appsettings.Secrets.json` in prose and a scan that counted prose would fail on
    /// its own documentation.
    ///
    /// **This file is exempt from this one check and not from the other**, because the
    /// pattern names the three calls in order to forbid them and therefore matches
    /// itself. The exemption is stated rather than worked around by splitting the
    /// literal, which would leave the rule readable only to whoever wrote it.
    /// </summary>
    [Fact]
    public void NoTestSourceBuildsAConnectionStringOfItsOwn()
    {
        const string Pattern = @"ConfigurationBuilder|AddJsonFile|GetConnectionString";

        var offenders = Sources()
            .Where(f => !IsTestDatabase(f) && !IsThisFile(f))
            .Where(f => Regex.IsMatch(Code(f), Pattern, RegexOptions.None, TimeSpan.FromSeconds(5)))
            .Select(Relative)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"Pattern /{Pattern}/ over non-comment lines matched outside Corpus/TestDatabase.cs: " +
            string.Join(", ", offenders) +
            ". A second route to a connection string is a second route to the developer's " +
            "database, and the derivation guards only the first.");
    }

    /// <summary>
    /// Every connection the suite opens is built from `TestDatabase.ConnectionString`.
    ///
    /// The pattern is whitespace-tolerant, so a call broken across lines by a formatter
    /// still matches. A line-anchored pattern would report zero and read as a pass
    /// [CLAUDE.md section 7].
    /// </summary>
    [Fact]
    public void EveryConnectionTheSuiteOpensComesFromTestDatabase()
    {
        const string Any = @"new\s+NpgsqlConnection\s*\(";
        const string Allowed = @"new\s+NpgsqlConnection\s*\(\s*TestDatabase\.ConnectionString\s*\)";

        var offenders = new List<string>();

        foreach (var file in Sources().Where(f => !IsTestDatabase(f)))
        {
            var code = Code(file);
            var all = Regex.Matches(code, Any, RegexOptions.None, TimeSpan.FromSeconds(5)).Count;
            var ok = Regex.Matches(code, Allowed, RegexOptions.None, TimeSpan.FromSeconds(5)).Count;

            if (all != ok)
            {
                offenders.Add($"{Relative(file)} ({all - ok} of {all})");
            }
        }

        Assert.True(offenders.Count == 0,
            $"Pattern /{Any}/ matched more often than /{Allowed}/ in: " +
            string.Join(", ", offenders) +
            ". A connection built from anything else is a connection this class cannot redirect.");
    }

    private static IEnumerable<string> Sources()
        => Directory.EnumerateFiles(
                Path.Combine(SchemaDocument.RepositoryRoot, "src", "StockResearcherLab.Tests"),
                "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static bool IsTestDatabase(string file)
        => Path.GetFileName(file).Equals("TestDatabase.cs", StringComparison.Ordinal);

    private static bool IsThisFile(string file)
        => Path.GetFileName(file).Equals("TestDatabaseIsolationTests.cs", StringComparison.Ordinal);

    /// <summary>The file with its line comments removed, so prose about a rule cannot break it.</summary>
    private static string Code(string file)
        => string.Join(
            "\n",
            File.ReadLines(file).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static string Relative(string file)
        => Path.GetRelativePath(SchemaDocument.RepositoryRoot, file).Replace('\\', '/');
}
