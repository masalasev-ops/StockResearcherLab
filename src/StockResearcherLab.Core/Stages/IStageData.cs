namespace StockResearcherLab.Core.Stages;

/// <summary>
/// The only data access a stage has. Every call names the table it touches, and
/// the implementation checks that name against the stage's declared sets before
/// anything reaches the database.
///
/// Naming the table is what makes the check possible. Handing a stage a bare SQL
/// string with no declared table would put the registry back to documenting
/// intentions rather than enforcing them.
/// </summary>
public interface IStageData
{
    /// <summary>Reads from <paramref name="table"/>. Throws <see cref="UndeclaredTableAccessException"/> if it is not declared.</summary>
    Task<IReadOnlyList<IReadOnlyList<object?>>> ReadAsync(
        string table, string sql, CancellationToken ct = default);

    /// <summary>Writes to <paramref name="table"/>. Throws <see cref="UndeclaredTableAccessException"/> if that operation on it is not declared.</summary>
    Task<long> WriteAsync(
        string table, WriteOperation operation, string sql,
        IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken ct = default);

    /// <summary>
    /// Bulk-loads rows into <paramref name="table"/> through a staging table and a
    /// single upsert, which is how the ingest and the backfill write [ARCHITECTURE
    /// section 19].
    ///
    /// <c>COPY</c> takes no <c>ON CONFLICT</c>, so a copy straight into the target
    /// throws a duplicate key error the second time a date is loaded, and D-68
    /// requires every stage write to be idempotent on the table's own grain. The
    /// route is therefore COPY into a staging table and then one
    /// <c>INSERT ... SELECT ... ON CONFLICT DO UPDATE</c> [A2].
    ///
    /// <paramref name="write"/> is handed a writer and runs inside the one
    /// connection the staging table lives in. The scope is in the signature rather
    /// than left to a caller to hold open, because a staging table is <c>TEMP</c>
    /// and is invisible to any other connection: splitting the two statements
    /// across connections fails with a relation-does-not-exist error that reads
    /// like a migration problem [A24].
    /// </summary>
    /// <param name="table">The target table, checked against the declared write set before anything opens.</param>
    /// <param name="columns">Target columns, in the order <paramref name="write"/> writes them.</param>
    /// <param name="conflictTarget">
    /// The columns <c>ON CONFLICT</c> matches on. A <c>PRIMARY KEY</c> or
    /// <c>UNIQUE</c> index must cover exactly these, or Postgres raises before a
    /// row is written [A7, D-68].
    /// </param>
    /// <param name="write">Writes every row. Called once, inside the staging connection.</param>
    /// <returns>Rows inserted or updated in the target, which is not the number copied.</returns>
    Task<long> BulkUpsertAsync(
        string table,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> conflictTarget,
        Func<IBulkWriter, CancellationToken, Task> write,
        CancellationToken ct = default);
}

/// <summary>
/// Writes rows into the staging table. One call to <see cref="StartRowAsync"/> per
/// row, then one <c>WriteAsync</c> per column in the declared order.
///
/// Binary COPY is strict about types and its errors name neither the column nor the
/// row: <c>numeric</c> binds as <see cref="decimal"/>, <c>date</c> as
/// <see cref="DateOnly"/>, <c>timestamptz</c> as <see cref="DateTimeOffset"/>. Money
/// is <c>numeric</c> and therefore <see cref="decimal"/>, never a float or a double
/// [INVARIANT 16].
/// </summary>
public interface IBulkWriter
{
    /// <summary>Begins a row. Every column declared must then be written, in order.</summary>
    Task StartRowAsync(CancellationToken ct = default);

    /// <summary>Writes one column. Null means unknown and is written as NULL, never as zero [CLAUDE.md section 6].</summary>
    Task WriteAsync<T>(T? value, CancellationToken ct = default);

    /// <summary>
    /// Writes one <c>jsonb</c> column from a JSON document held as a string.
    ///
    /// **It needs its own method because binary COPY carries no type name and the
    /// driver infers one from the CLR type.** A string infers `text`, whose binary
    /// form is the bytes themselves, where `jsonb` expects a one-byte format version
    /// first. Postgres then reads the document's own opening brace as that version
    /// and refuses the row with "unsupported jsonb version number 123", which is
    /// `{` [2.9, found at 2.12 by running C10].
    ///
    /// Declared here as an intent rather than as a provider type, so `Core` keeps no
    /// reference to the driver. The mapping from intent to wire format belongs to
    /// whatever implements this.
    /// </summary>
    Task WriteJsonAsync(string? json, CancellationToken ct = default);
}
