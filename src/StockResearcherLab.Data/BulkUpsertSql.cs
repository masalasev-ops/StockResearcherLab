namespace StockResearcherLab.Data;

/// <summary>
/// The three statements the staged bulk write emits.
///
/// Pure and separate from <see cref="StageData"/> so the generated SQL can be
/// asserted verbatim, which is the only way to test the quoting: `order` and
/// `position` are Postgres reserved words and neither carries a unique index on a
/// writable column, so neither can be upserted into for a test to observe. The
/// quoting failure they would produce is a syntax error in generated SQL appearing
/// in phase 5, nowhere near where it was written [A25].
///
/// Identifiers are validated by the caller and then quoted here. Quoting also pins
/// case, since an unquoted identifier folds to lower case and this schema is
/// lower-case snake by convention rather than by enforcement.
/// </summary>
public static class BulkUpsertSql
{
    public static string Quote(string identifier) => "\"" + identifier + "\"";

    public static string ColumnList(IReadOnlyList<string> columns)
        => string.Join(", ", columns.Select(Quote));

    /// <summary>
    /// The staging table.
    ///
    /// <c>AS SELECT ... WITH NO DATA</c> rather than <c>LIKE</c>, because
    /// <c>LIKE</c> copies <c>NOT NULL</c> and would put the target's identity
    /// primary key into the staging table as a column the COPY never writes. That
    /// is `order`, `position`, `insider_transaction` and `events` [A25].
    /// </summary>
    public static string CreateStaging(string staging, string table, IReadOnlyList<string> columns)
        => $"CREATE TEMP TABLE {Quote(staging)} AS SELECT {ColumnList(columns)} FROM {Quote(table)} WITH NO DATA;";

    public static string Copy(string staging, IReadOnlyList<string> columns)
        => $"COPY {Quote(staging)} ({ColumnList(columns)}) FROM STDIN (FORMAT BINARY)";

    /// <summary>
    /// The upsert. Columns outside the conflict target are updated, so a re-run
    /// replaces values without touching the key [D-68].
    /// </summary>
    public static string Upsert(
        string table, string staging, IReadOnlyList<string> columns, IReadOnlyList<string> conflictTarget)
    {
        var list = ColumnList(columns);

        var updatable = columns
            .Where(c => !conflictTarget.Contains(c, StringComparer.Ordinal))
            .ToList();

        var action = updatable.Count == 0
            ? "DO NOTHING"
            : "DO UPDATE SET " + string.Join(", ", updatable.Select(c => $"{Quote(c)} = EXCLUDED.{Quote(c)}"));

        return $"INSERT INTO {Quote(table)} ({list}) SELECT {list} FROM {Quote(staging)} " +
               $"ON CONFLICT ({ColumnList(conflictTarget)}) {action};";
    }

    public static string DropStaging(string staging) => $"DROP TABLE IF EXISTS {Quote(staging)};";

    /// <summary>The staging table's name for a target. Deterministic, so two runs emit identical SQL.</summary>
    public static string StagingNameFor(string table) => "srl_stage_" + table.Replace('.', '_');
}
