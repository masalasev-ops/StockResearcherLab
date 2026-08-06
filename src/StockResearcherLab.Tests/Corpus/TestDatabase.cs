using Microsoft.Extensions.Configuration;
using Npgsql;

namespace StockResearcherLab.Tests.Corpus;

/// <summary>
/// The connection the database-backed tests use, read from
/// appsettings.Secrets.json beside the test binary [D-55].
///
/// It does not skip when the database is unreachable. A skipped test reads green
/// and a test that has never failed has not been tested, so an absent database
/// fails loudly and names what is missing.
/// </summary>
public static class TestDatabase
{
    public static string ConnectionString { get; } = Resolve();

    public static async Task<NpgsqlConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = new NpgsqlConnection(ConnectionString);
        try
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            return conn;
        }
        catch (Exception ex)
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException(
                "The test database is unreachable. These tests assert against the real schema, " +
                "so there is nothing to assert without it. Run migrate.ps1 first. " +
                "Underlying error: " + ex.Message, ex);
        }
    }

    /// <summary>Every table in the public schema, ordinal-sorted.</summary>
    public static async Task<IReadOnlyList<string>> PublicTablesAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = 'public' AND table_type = 'BASE TABLE'
            ORDER BY table_name;
            """, conn);

        var names = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static string Resolve()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.Secrets.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        return config.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:Postgres is absent. Expected appsettings.Secrets.json beside " +
                "the test binary, copied from appsettings.Secrets.example.json at the repository " +
                "root [D-55].");
    }
}
