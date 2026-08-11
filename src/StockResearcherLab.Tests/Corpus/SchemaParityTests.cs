using Npgsql;
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

    // ------------------------------------------------------- INVARIANT 16 ---

    /// <summary>
    /// INVARIANT 16 against the live database, which is the form the invariant
    /// states and the form `guards.ps1` cannot take: CI runs the guard before the
    /// migrate step, so there is no schema for it to read and it parses the
    /// migrations instead. Both read the same declaration in `SCHEMA.md` [1.8].
    ///
    /// This one is stronger where they disagree. A migration that applies
    /// differently from how it reads, or a column altered outside the ledger,
    /// shows up here and cannot show up there.
    /// </summary>
    [Fact]
    public async Task EveryApproximateColumnInTheDatabaseIsDeclaredNonMonetary()
    {
        var declared = SchemaDocument.NonMonetaryColumns();

        var undeclared = (await ApproximateColumnsAsync().ConfigureAwait(true))
            .Where(c => !declared.Contains(c))
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        Assert.True(undeclared.Count == 0,
            "The database has real or double precision columns SCHEMA.md does not declare as not " +
            "money: " + string.Join(", ", undeclared) + ". Declare them there rather than excluding " +
            "them in a script [INVARIANT 16].");
    }

    /// <summary>
    /// The other direction. Every column whose name says money is `numeric` unless
    /// the document says it is not money, and the count is asserted so the check
    /// cannot pass over a match set that quietly emptied.
    ///
    /// **The number lives in the assertion below and nowhere else here** [D-83]. It
    /// is what the live database carries, and `guards.ps1` states its own against the
    /// migrations. Both are read from the schema rather than from each other, so a
    /// parser that stops seeing part of it fails in one place and not the other
    /// rather than in neither. A comment restating the value would be a third copy
    /// that nothing checks, which is how this one came to say seventeen while the
    /// assertion said eighteen.
    /// </summary>
    [Fact]
    public async Task EveryMonetaryNamedColumnInTheDatabaseIsNumeric()
    {
        var declared = SchemaDocument.NonMonetaryColumns();
        var monetary = await MonetaryNamedColumnsAsync().ConfigureAwait(true);

        var wrong = monetary
            .Where(c => !c.Type.StartsWith("numeric", StringComparison.Ordinal) && !declared.Contains(c.Column))
            .Select(c => c.Column + " is " + c.Type)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        Assert.True(wrong.Count == 0,
            "Columns whose name says money and whose type does not: " + string.Join(", ", wrong) +
            " [INVARIANT 16].");

        // It moved at 2.2, when fundamental_snapshot.capital_expenditures arrived
        // and matched through "cap" [D-79], and it moves whenever a monetary column
        // does. guards.ps1 states its own against the migrations; this one is
        // against the live database. The value itself is stated once, below [D-83].
        Assert.Equal(18, monetary.Count(c => c.Type.StartsWith("numeric", StringComparison.Ordinal)));
    }

    /// <summary>`real` and `double precision`, as `table.column`.</summary>
    private static async Task<IReadOnlyList<string>> ApproximateColumnsAsync()
        => await QueryAsync(
            """
            SELECT table_name || '.' || column_name
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND (data_type IN ('real', 'double precision')
                   OR (data_type = 'ARRAY' AND udt_name IN ('_float4', '_float8')))
            ORDER BY 1;
            """).ConfigureAwait(false);

    private static async Task<IReadOnlyList<(string Column, string Type)>> MonetaryNamedColumnsAsync()
    {
        // The same pattern guards.ps1 runs, against the column name and never the
        // table's: `price_daily.date` matched once because its table carries the
        // word, and the check reported on rows that had nothing to do with money.
        var rows = await QueryAsync(
            """
            SELECT table_name || '.' || column_name, data_type
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND column_name ~ '(_usd|price|value|cap|cost|equity|pnl|dollar|amount)'
            ORDER BY 1;
            """, columns: 2).ConfigureAwait(false);

        return rows.Select(r =>
        {
            var parts = r.Split('\u001f');
            return (parts[0], parts[1]);
        }).ToList();
    }

    private static async Task<IReadOnlyList<string>> QueryAsync(string sql, int columns = 1)
    {
        await using var conn = await TestDatabase.OpenAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);

        var rows = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(false);

        while (await r.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(false))
        {
            rows.Add(columns == 1
                ? r.GetString(0)
                : r.GetString(0) + '\u001f' + r.GetString(1));
        }

        return rows;
    }
}
