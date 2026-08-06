using Npgsql;
using StockResearcherLab.Core.Stages;

namespace StockResearcherLab.Data;

/// <summary>
/// The database behind <see cref="IStageData"/>. Every call checks the named
/// table against the stage's declared sets first, so an undeclared access fails
/// before a connection is touched rather than succeeding quietly.
///
/// The check is here rather than in the stage because a stage checking itself is
/// not a check.
/// </summary>
public sealed class StageData : IStageData
{
    private readonly string _connectionString;
    private readonly DeclaredAccess _access;

    public StageData(string connectionString, DeclaredAccess access)
    {
        _connectionString = connectionString;
        _access = access;
    }

    public async Task<IReadOnlyList<IReadOnlyList<object?>>> ReadAsync(
        string table, string sql, CancellationToken ct = default)
    {
        _access.EnsureCanRead(table);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var rows = new List<IReadOnlyList<object?>>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[i] = await reader.IsDBNullAsync(i, ct).ConfigureAwait(false)
                    ? null
                    : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    public async Task<long> WriteAsync(
        string table, WriteOperation operation, string sql,
        IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken ct = default)
    {
        _access.EnsureCanWrite(table, operation);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);

        if (parameters is not null)
        {
            // Sorted explicitly. Dictionary enumeration order is unspecified and
            // must never reach output, and a command's parameter order is output
            // in every sense that matters here [CLAUDE.md section 6].
            foreach (var key in parameters.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                cmd.Parameters.AddWithValue(key, parameters[key] ?? DBNull.Value);
            }
        }

        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
