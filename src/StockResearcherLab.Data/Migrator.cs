using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace StockResearcherLab.Data;

/// <summary>
/// Applies the snapshot-first schema. Migrations are embedded .sql resources in
/// this assembly, applied in filename order, each recorded in
/// meta.schema_migration with the hash of the text that was applied.
///
/// Idempotent twice over. Every statement in the snapshot is IF NOT EXISTS, and
/// the ledger skips a file whose hash has already been recorded, so a second run
/// changes nothing and says so.
/// </summary>
public sealed class Migrator
{
    private readonly string _connectionString;
    private readonly Action<string> _say;

    public Migrator(string connectionString, Action<string>? say = null)
    {
        _connectionString = connectionString;
        _say = say ?? (_ => { });
    }

    /// <summary>Applied filenames, in the order they were applied this run. Empty when everything was already present.</summary>
    public async Task<IReadOnlyList<string>> ApplyAsync(CancellationToken ct = default)
    {
        await EnsureDatabaseExistsAsync(ct).ConfigureAwait(false);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        // Bootstrap the ledger before reading it. The snapshot creates these too,
        // and both are IF NOT EXISTS, so whichever runs first is irrelevant.
        await ExecuteAsync(conn, "CREATE SCHEMA IF NOT EXISTS meta;", ct).ConfigureAwait(false);
        await ExecuteAsync(conn, """
            CREATE TABLE IF NOT EXISTS meta.schema_migration (
                filename    text        NOT NULL PRIMARY KEY,
                applied_at  timestamptz NOT NULL,
                sha256      text        NOT NULL
            );
            """, ct).ConfigureAwait(false);

        var applied = new List<string>();

        foreach (var (filename, sql) in EmbeddedMigrations())
        {
            var hash = Sha256(sql);
            var recorded = await ScalarAsync(conn,
                "SELECT sha256 FROM meta.schema_migration WHERE filename = @f;",
                ct, ("f", filename)).ConfigureAwait(false) as string;

            if (recorded is not null)
            {
                if (!string.Equals(recorded, hash, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Migration '{filename}' has already been applied with a different hash. " +
                        "Snapshot-first means a change adds a numbered file beside this one rather " +
                        "than editing it, so an edited file that has already run is a mistake rather " +
                        "than a migration.");
                }

                _say($"  {filename,-28} already applied");
                continue;
            }

            await using (var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false))
            {
                await ExecuteAsync(conn, sql, ct, tx).ConfigureAwait(false);
                await ExecuteAsync(conn,
                    "INSERT INTO meta.schema_migration (filename, applied_at, sha256) VALUES (@f, @a, @h);",
                    ct, tx, ("f", filename), ("a", DateTime.UtcNow), ("h", hash)).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }

            _say($"  {filename,-28} applied");
            applied.Add(filename);
        }

        return applied;
    }

    /// <summary>
    /// Creates the target database when it does not exist, by connecting to the
    /// maintenance database on the same server. Without this "runs clean against
    /// an empty database" would carry an undocumented manual step in front of it.
    /// </summary>
    private async Task EnsureDatabaseExistsAsync(CancellationToken ct)
    {
        var target = new NpgsqlConnectionStringBuilder(_connectionString);
        var database = target.Database
            ?? throw new InvalidOperationException("The connection string names no database.");

        var maintenance = new NpgsqlConnectionStringBuilder(_connectionString) { Database = "postgres" };

        await using var conn = new NpgsqlConnection(maintenance.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        var exists = await ScalarAsync(conn,
            "SELECT 1 FROM pg_database WHERE datname = @d;", ct, ("d", database)).ConfigureAwait(false);

        if (exists is not null)
        {
            return;
        }

        // CREATE DATABASE cannot run inside a transaction and takes no parameter,
        // so the name is quoted rather than bound.
        await ExecuteAsync(conn, $"CREATE DATABASE \"{database.Replace("\"", "\"\"")}\";", ct)
            .ConfigureAwait(false);
        _say($"  created database {database}");
    }

    /// <summary>Embedded .sql resources, ordered by filename. Ordinal so the order does not depend on a locale.</summary>
    public static IReadOnlyList<(string Filename, string Sql)> EmbeddedMigrations()
    {
        var assembly = typeof(Migrator).Assembly;
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var result = new List<(string, string)>(names.Count);
        foreach (var name in names)
        {
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Embedded resource '{name}' could not be opened.");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            result.Add((ShortName(name), reader.ReadToEnd()));
        }

        return result;
    }

    private static string ShortName(string resourceName)
    {
        var i = resourceName.LastIndexOf("Migrations.", StringComparison.Ordinal);
        return i < 0 ? resourceName : resourceName[(i + "Migrations.".Length)..];
    }

    private static string Sha256(string s)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))
            .ToLower(CultureInfo.InvariantCulture);

    private static async Task ExecuteAsync(
        NpgsqlConnection conn, string sql, CancellationToken ct,
        params (string Name, object Value)[] parameters)
        => await ExecuteAsync(conn, sql, ct, null, parameters).ConfigureAwait(false);

    private static async Task ExecuteAsync(
        NpgsqlConnection conn, string sql, CancellationToken ct,
        NpgsqlTransaction? tx, params (string Name, object Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<object?> ScalarAsync(
        NpgsqlConnection conn, string sql, CancellationToken ct,
        params (string Name, object Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is DBNull ? null : result;
    }
}
