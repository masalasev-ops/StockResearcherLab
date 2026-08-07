namespace StockResearcherLab.Core.Stages;

/// <summary>
/// The operations a component can own on a table. Ownership is per operation,
/// not per table [INVARIANT 10 as amended]: attribution is inserted by the
/// allocator and updated by the filler, and neither may perform the other's
/// operation.
/// </summary>
public enum WriteOperation
{
    Insert,
    Update,
    Delete,
}

/// <summary>
/// One declared write: a table, an operation, and the columns the stage owns.
/// The column set is what lets ForwardReturnFiller declare that it updates only
/// the nine return columns of attribution rather than the row.
/// </summary>
/// <param name="Table">Table name exactly as SCHEMA.md declares it.</param>
/// <param name="Operation">The operation this stage owns on that table.</param>
/// <param name="Columns">
/// Columns owned. Empty means every column, which is the ordinary case for a
/// stage that owns the whole table.
/// </param>
public sealed record TableWrite(string Table, WriteOperation Operation, IReadOnlyList<string> Columns)
{
    public TableWrite(string table, WriteOperation operation)
        : this(table, operation, Array.Empty<string>())
    {
    }

    public override string ToString()
        => Columns.Count == 0
            ? $"{Table}.{Operation}"
            : $"{Table}.{Operation}({string.Join(", ", Columns)})";
}

/// <summary>What a stage did. Row counts land in run_log.</summary>
/// <param name="RowsWritten">Rows this stage wrote. Zero is a legitimate answer and is not an error on its own.</param>
/// <param name="Status">
/// What the runner records. <c>ok</c> for the ordinary case, and a stage may return
/// something else where completing successfully is not the whole story.
///
/// This exists because two components must raise an alert and neither may write the
/// <c>alert</c> table, which has ConcentrationMonitor as its only writer. Both emit
/// through the run log instead, exactly as C07 already does for its abort, and a
/// band that produced a row indistinguishable from a clean one would not be an
/// alert at all [A3, INVARIANT 10].
/// </param>
/// <param name="Detail">
/// The line worth reading when <see cref="Status"/> is not <c>ok</c>. It lands in
/// <c>run_log.error</c>, which is the only free-text column that table has.
/// </param>
public readonly record struct StageResult(long RowsWritten, string Status = "ok", string? Detail = null)
{
    public static StageResult None => new(0);

    /// <summary>Completed, and something about it is worth an operator's attention.</summary>
    public static StageResult Alert(long rowsWritten, string detail) => new(rowsWritten, "alert", detail);
}

/// <summary>
/// Every pipeline component is a stage. It declares its read set and its write
/// set as data, takes a date and a config version, and is a pure function of
/// those inputs [CLAUDE.md section 5].
///
/// Purity is what lets any night be replayed and produce identical output, which
/// is how an accidental clock read gets caught rather than argued about. A stage
/// that needs the time takes an <see cref="IClock"/>; nothing reads system time
/// directly [INVARIANT 11].
/// </summary>
public interface IStage : IWriteOwner
{
    /// <summary>Tables this stage may read. Reaching outside it is an error, not a warning.</summary>
    IReadOnlyList<string> ReadSet { get; }

    Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default);
}

/// <summary>
/// Anything that owns a write. Most are stages, and a few are not: RunLog,
/// CostLedger and ConcentrationMonitor sit outside the layers in
/// ARCHITECTURE.html section 3 and still own a table each.
///
/// The split exists so the conformance test sees every writer rather than only
/// the runnable ones. A writer the registry cannot see is a writer INVARIANT 10
/// is not enforced against, which is the whole failure mode.
/// </summary>
public interface IWriteOwner
{
    /// <summary>The component name as ARCHITECTURE.html section 3 gives it, for example UniverseBuilder.</summary>
    string Name { get; }

    /// <summary>Writes this component owns, per operation.</summary>
    IReadOnlyList<TableWrite> WriteSet { get; }
}

/// <summary>
/// Everything a stage is allowed to reach. There is no other route to data, and
/// no ambient clock: both the date being processed and the current instant
/// arrive here rather than being read from the machine.
/// </summary>
public sealed class StageContext
{
    public StageContext(
        DateOnly date, int configVersion, IStageData data, IClock clock, Config.IConfigStore config)
    {
        Date = date;
        ConfigVersion = configVersion;
        Data = data;
        Clock = clock;
        Config = config;
    }

    /// <summary>The trading date being processed. A label the exchange gave a session, never a timezone conversion.</summary>
    public DateOnly Date { get; }

    /// <summary>
    /// The config version in force for <see cref="Date"/>. Config resolves as of
    /// the simulated date, never as of now [D-43, INVARIANT 13].
    /// </summary>
    public int ConfigVersion { get; }

    /// <summary>The only data access a stage has, and it enforces the declared sets.</summary>
    public IStageData Data { get; }

    /// <summary>Injected. Nothing reads system time outside the clock implementation [INVARIANT 11].</summary>
    public IClock Clock { get; }

    /// <summary>
    /// Configuration, resolved as of <see cref="Date"/> and never as of now.
    ///
    /// It sits here rather than being constructed by a stage so that the date a
    /// stage resolves against is the date it was handed. A stage building its own
    /// store could resolve against a different one, and the failure would be a
    /// plausible number rather than an error [D-43, INVARIANT 13].
    /// </summary>
    public Config.IConfigStore Config { get; }
}
