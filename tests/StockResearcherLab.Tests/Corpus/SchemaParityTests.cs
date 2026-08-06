using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests;

/// <summary>
/// SCHEMA.md against the database, in both directions. Either direction alone
/// passes while the other is broken: a schema missing a declared table looks
/// fine to a test that only walks the database, and a stray table looks fine to
/// a test that only walks the document.
/// </summary>
public sealed class SchemaParityTests
{
    [Fact]
    public void SchemaDocumentDeclaresTheTablesItIsExpectedTo()
    {
        var tables = SchemaDocument.Tables();

        // Guards the parser rather than the schema. If SCHEMA.md's shape changes
        // so that grain lines stop following headings, Tables() would quietly
        // return nothing and both directions below would pass over an empty set.
        Assert.True(tables.Count >= 30,
            $"SCHEMA.md parsed to only {tables.Count} tables, which means the parser stopped " +
            "matching rather than that the document shrank. Both parity assertions would " +
            "pass vacuously in that state.");

        Assert.Contains("portfolio_selection", tables);
        Assert.Contains("order", tables);
        Assert.Contains("fill", tables);
        Assert.Contains("position", tables);
        Assert.DoesNotContain("percentile columns", tables);
    }

    [Fact]
    public async Task EveryTableInSchemaDocumentExistsInTheDatabase()
    {
        var declared = SchemaDocument.Tables();
        var actual = await TestDatabase.PublicTablesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        var missing = declared.Except(actual, StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0,
            "SCHEMA.md declares tables the database does not have: " + string.Join(", ", missing));
    }

    [Fact]
    public async Task EveryTableInTheDatabaseIsDeclaredInSchemaDocument()
    {
        var declared = SchemaDocument.Tables();
        var actual = await TestDatabase.PublicTablesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        var undeclared = actual.Except(declared, StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToList();

        Assert.True(undeclared.Count == 0,
            "The database has tables SCHEMA.md does not declare: " + string.Join(", ", undeclared) +
            ". The migration ledger is deliberately in the meta schema rather than public so that " +
            "this assertion needs no exception.");
    }
}
