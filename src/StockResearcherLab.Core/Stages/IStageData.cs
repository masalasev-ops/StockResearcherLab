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
}
