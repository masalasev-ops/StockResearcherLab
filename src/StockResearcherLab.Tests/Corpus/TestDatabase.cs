using Microsoft.Extensions.Configuration;
using Npgsql;
using StockResearcherLab.Data;

namespace StockResearcherLab.Tests.Corpus;

/// <summary>
/// The database the suite runs against, which is its own and never the one named by
/// the connection string it was handed.
///
/// **The supplied string says which server, and this class says which database.** The
/// name is the supplied one with `_tests` appended, so `stockresearcherlab` becomes
/// `stockresearcherlab_tests` and `ci.ps1`'s `stockresearcherlab_ci` becomes
/// `stockresearcherlab_ci_tests`. Two suites started at once from those two strings
/// therefore cannot reach one database, and neither of them can reach `ci.ps1`'s own,
/// which that script drops and migrates while the suite is running.
///
/// **Why it is derived rather than configured.** A second setting is a second thing to
/// get right, and the two occasions this suite wrote to the developer's store were both
/// a correct setting that nothing applied. Derivation has no unset state [open item 26].
///
/// **It creates, migrates and seeds what it finds missing**, so `dotnet test` from a
/// clean checkout needs no `migrate.ps1` and no `seed.ps1` in front of it. Seeding is
/// part of preparation rather than of each test class because it was not: config
/// reached the store only because `PriceBackfillTests` happened to seed it, that class
/// is not in the `database` collection, and so it ran in parallel with the classes that
/// needed the rows rather than before them. On a database that already had config the
/// race was invisible; on a fresh one it decided the run. `ci.ps1` and `ci.yml` both
/// migrate and neither seeds, which is what made a green suite there a coin toss
/// [3.13, open item 32].
///
/// **What happens if the developer's database is somehow still reachable.** Preparation
/// refuses to touch a database that exists and does not carry the marker this class
/// stamps, and the refusal names both databases. So a target that was created by hand,
/// or by anything other than this suite, fails the run at its first database test
/// instead of being written to. The convention was that tests clear only their own
/// rows; the convention held twice and then did not.
///
/// It does not skip when the server is unreachable. A skipped test reads green and a
/// test that has never failed has not been tested, so an absent server fails loudly and
/// names what is missing.
/// </summary>
public static class TestDatabase
{
    /// <summary>
    /// Appended to the supplied database name. Idempotent, so a string already naming a
    /// `_tests` database is taken at its word rather than becoming `_tests_tests`.
    /// </summary>
    private const string Suffix = "_tests";

    private static readonly Lazy<Prepared> State =
        new(Prepare, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The connection every database-backed test uses. Preparation runs on first read.</summary>
    public static string ConnectionString => State.Value.ConnectionString;

    /// <summary>The database this suite created and runs against.</summary>
    public static string DatabaseName => State.Value.Database;

    /// <summary>The database the supplied connection string named, which nothing here opens.</summary>
    public static string SuppliedDatabaseName => State.Value.Supplied;

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
                "so there is nothing to assert without it. Underlying error: " + ex.Message, ex);
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

    private sealed record Prepared(string ConnectionString, string Database, string Supplied);

    /// <summary>
    /// Runs once per test process, on the first read of any member here.
    ///
    /// Blocking on the async work is deliberate and is what makes this reachable from a
    /// property rather than from a fixture every test class would have to remember to
    /// take. The test host has no synchronisation context, so there is nothing to
    /// deadlock against, and `Lazy` caches the exception as well as the value: a failure
    /// here fails every database test with the same message rather than the first one
    /// only.
    /// </summary>
    private static Prepared Prepare()
    {
        var supplied = Supplied();
        var builder = new NpgsqlConnectionStringBuilder(supplied);

        var suppliedName = builder.Database
            ?? throw new InvalidOperationException(
                "The supplied connection string names no database, so there is nothing to " +
                "derive the test database from.");

        var database = suppliedName.EndsWith(Suffix, StringComparison.Ordinal)
            ? suppliedName
            : suppliedName + Suffix;

        builder.Database = database;
        var connectionString = builder.ConnectionString;

        EnsureAsync(connectionString, database, suppliedName).GetAwaiter().GetResult();

        return new Prepared(connectionString, database, suppliedName);
    }

    /// <summary>
    /// The string the environment supplied, which names the server and the developer's
    /// own database. Nothing opens it.
    /// </summary>
    private static string Supplied()
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
                "root [D-55]. The suite creates and migrates its own database on that server, so " +
                "the string is the only thing it needs and no migrate step goes in front of it.");
    }

    private static async Task EnsureAsync(string connectionString, string database, string supplied)
    {
        var maintenance = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = "postgres",
        }.ConnectionString;

        bool exists;
        await using (var conn = new NpgsqlConnection(maintenance))
        {
            try
            {
                await conn.OpenAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"The Postgres server is unreachable, so '{database}' cannot be created or " +
                    "opened. These tests assert against the real schema and there is nothing to " +
                    "assert without it. Underlying error: " + ex.Message, ex);
            }

            await using var cmd = new NpgsqlCommand(
                "SELECT 1 FROM pg_database WHERE datname = @d;", conn);
            cmd.Parameters.AddWithValue("d", database);
            exists = await cmd.ExecuteScalarAsync().ConfigureAwait(false) is not null;
        }

        if (exists && !await CarriesMarkerAsync(connectionString).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"'{database}' exists and does not carry the marker this suite stamps, so it was " +
                "not created here. The suite deletes rows and truncates tables, and it refuses to " +
                $"do that to a database it did not make. The connection string named '{supplied}' " +
                $"and this suite derives '{database}' from it by appending '{Suffix}'. Drop " +
                $"'{database}' if it is disposable, or point the string at another server.");
        }

        await new Migrator(connectionString, new SystemClock()).ApplyAsync().ConfigureAwait(false);
        await StampAsync(connectionString, database, supplied).ConfigureAwait(false);

        // Seeded here rather than per class, which is the race described above.
        // Idempotent: version 1 of every key on the first run and nothing on the rest.
        await new ConfigSeeder(connectionString).SeedAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the target already carries `meta.test_database`. Absent means either a
    /// database somebody else made or one made before this class stamped anything, and
    /// both are refused rather than told apart.
    /// </summary>
    private static async Task<bool> CarriesMarkerAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        await using var cmd = new NpgsqlCommand(
            """
            SELECT 1 FROM information_schema.tables
            WHERE table_schema = 'meta' AND table_name = 'test_database';
            """, conn);

        return await cmd.ExecuteScalarAsync().ConfigureAwait(false) is not null;
    }

    /// <summary>
    /// The marker, and the one assertion that the string reaches the database this class
    /// believes it does. `current_database()` is read from the open connection rather
    /// than parsed back out of the string, because a string that parses correctly and
    /// connects elsewhere is exactly the failure being guarded against.
    /// </summary>
    private static async Task StampAsync(string connectionString, string database, string supplied)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        await using (var check = new NpgsqlCommand("SELECT current_database();", conn))
        {
            var reached = (string) (await check.ExecuteScalarAsync().ConfigureAwait(false))!;
            if (!string.Equals(reached, database, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The suite connected to '{reached}' where it derived '{database}' from the " +
                    $"supplied '{supplied}'. Nothing further is run against it.");
            }
        }

        await using (var create = new NpgsqlCommand(
            """
            CREATE SCHEMA IF NOT EXISTS meta;
            CREATE TABLE IF NOT EXISTS meta.test_database (
                marker      text        NOT NULL PRIMARY KEY,
                stamped_at  timestamptz NOT NULL
            );
            """, conn))
        {
            await create.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await using var stamp = new NpgsqlCommand(
            """
            INSERT INTO meta.test_database (marker, stamped_at) VALUES (@m, @a)
            ON CONFLICT (marker) DO NOTHING;
            """, conn);
        stamp.Parameters.AddWithValue("m", "StockResearcherLab.Tests");
        stamp.Parameters.AddWithValue("a", new SystemClock().UtcNow);
        await stamp.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
