namespace StockResearcherLab.Core.Stages;

/// <summary>
/// Thrown when a stage touches a table it did not declare. Named, because the
/// alternative is silently succeeding: a stage that quietly reads outside its
/// declared set still produces plausible numbers, and the registry stops being
/// the single declaration of who touches what the moment one of them lies.
/// </summary>
public sealed class UndeclaredTableAccessException : InvalidOperationException
{
    public UndeclaredTableAccessException(string stage, string table, string attempted, string declared)
        : base($"Stage '{stage}' attempted to {attempted} '{table}', which it does not declare. " +
               $"Declared: {declared}. The read set and write set are the contract, not documentation " +
               $"[CLAUDE.md section 5].")
    {
        Stage = stage;
        Table = table;
    }

    public string Stage { get; }

    public string Table { get; }
}

/// <summary>
/// A stage's declared sets, and the gate that enforces them. Constructed per
/// stage, so the check knows which stage is asking and can name it.
/// </summary>
public sealed class DeclaredAccess
{
    private readonly HashSet<string> _reads;
    private readonly HashSet<(string Table, WriteOperation Operation)> _writes;

    public DeclaredAccess(IStage stage)
        : this(stage.Name, stage.ReadSet, stage.WriteSet)
    {
    }

    public DeclaredAccess(string stageName, IReadOnlyList<string> readSet, IReadOnlyList<TableWrite> writeSet)
    {
        StageName = stageName;
        ReadSet = readSet;
        WriteSet = writeSet;
        _reads = new HashSet<string>(readSet, StringComparer.Ordinal);
        _writes = new HashSet<(string, WriteOperation)>(
            writeSet.Select(w => (w.Table, w.Operation)));
    }

    public string StageName { get; }

    public IReadOnlyList<string> ReadSet { get; }

    public IReadOnlyList<TableWrite> WriteSet { get; }

    /// <summary>
    /// A stage may read what it writes without declaring it twice. Reading a row
    /// back to update it is the same access, and forcing it into both lists would
    /// make the read set say something it does not mean.
    /// </summary>
    public bool CanRead(string table)
        => _reads.Contains(table) || _writes.Any(w => string.Equals(w.Table, table, StringComparison.Ordinal));

    public bool CanWrite(string table, WriteOperation operation)
        => _writes.Contains((table, operation));

    public void EnsureCanRead(string table)
    {
        if (CanRead(table))
        {
            return;
        }

        throw new UndeclaredTableAccessException(
            StageName, table, "read",
            ReadSet.Count == 0 ? "(nothing)" : string.Join(", ", Ordered(ReadSet)));
    }

    public void EnsureCanWrite(string table, WriteOperation operation)
    {
        if (CanWrite(table, operation))
        {
            return;
        }

        throw new UndeclaredTableAccessException(
            StageName, table, operation.ToString().ToLowerInvariant() + " into",
            WriteSet.Count == 0 ? "(nothing)" : string.Join(", ", Ordered(WriteSet.Select(w => w.ToString()))));
    }

    // Sorted explicitly. Enumeration order of a set is unspecified and must never
    // reach output, and an exception message is output [CLAUDE.md section 6].
    private static IEnumerable<string> Ordered(IEnumerable<string> items)
        => items.OrderBy(s => s, StringComparer.Ordinal);
}
