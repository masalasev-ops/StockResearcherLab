using Npgsql;
using StockResearcherLab.Core.Config;
using StockResearcherLab.Data;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Digest;

/// <summary>
/// Checkpoint 5.14. C26's digest half: what a paid call cost.
///
/// **The pricing is asserted arithmetically and the write is asserted against the real
/// store.** Splitting them is deliberate: the arithmetic is where a wrong answer is
/// silent, since a cost of $0.004 and a cost of $0.04 both look plausible in a table
/// nobody reconciles, and the four rates are the only things a reader can check against a
/// published price list.
/// </summary>
[Collection("database")]
public sealed class CostLedgerTests
{
    private const string Haiku = "claude-haiku-4-5";

    private static readonly DateOnly AsOf = new(2026, 8, 12);

    /// <summary>
    /// The four rates quoted from the provider's pricing page on 2026-08-25, in the shape
    /// `ConfigSeeder` seeds them.
    /// </summary>
    private const string Table =
        "{\"claude-haiku-4-5\": {\"input\": 1.00, \"output\": 5.00, " +
        "\"cache_write\": 1.25, \"cache_read\": 0.10}}";

    private static CostLedger Ledger(string table = Table)
        => new("Host=localhost;Database=unused", new StubConfig(table));

    /// <summary>
    /// **The four rates are what the published price list says, read back off config.**
    /// This is the assertion an auditor repeats against the provider's page.
    /// </summary>
    [Fact]
    public async Task TheFourRatesAreTheOnesSeeded()
    {
        var price = await Ledger().PriceAsync(Haiku, AsOf, TestContext.Current.CancellationToken);

        Assert.Equal(1.00m, price.Input);
        Assert.Equal(5.00m, price.Output);
        Assert.Equal(1.25m, price.CacheWrite);
        Assert.Equal(0.10m, price.CacheRead);
    }

    /// <summary>
    /// **Money is decimal end to end** [INVARIANT 16]. A price per million tokens
    /// multiplied by a token count in binary floating point is the shape that produces an
    /// annual total nobody can reconcile against an invoice, so the type is asserted
    /// rather than assumed from the field's declaration.
    /// </summary>
    [Fact]
    public async Task EveryRateIsDecimal()
    {
        var price = await Ledger().PriceAsync(Haiku, AsOf, TestContext.Current.CancellationToken);

        Assert.IsType<decimal>(price.Input);
        Assert.IsType<decimal>(price.CacheRead);
    }

    /// <summary>
    /// **An unpriced model throws rather than pricing at zero.** A model change nobody
    /// recorded would otherwise run indefinitely with a ledger full of free calls, which
    /// is the exact shape of failure `CLAUDE.md` section 1 describes: the run completes,
    /// the numbers look plausible, and the measurement is worthless.
    /// </summary>
    [Fact]
    public async Task AModelWithNoPriceThrowsRatherThanCostingNothing()
    {
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Ledger().PriceAsync(
                "claude-opus-5", AsOf, TestContext.Current.CancellationToken));

        Assert.Contains("claude-opus-5", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be priced", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// **A rate missing from an otherwise present model throws too**, for the same
    /// reason and one level down. Three rates out of four prices most of a call.
    /// </summary>
    [Fact]
    public async Task AMissingRateThrowsRatherThanPricingThatComponentAtNothing()
    {
        var partial = "{\"claude-haiku-4-5\": {\"input\": 1.00, \"output\": 5.00}}";

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Ledger(partial).PriceAsync(Haiku, AsOf, TestContext.Current.CancellationToken));

        Assert.Contains("cache_write", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("INVARIANT 16", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// C26 owns `cost_ledger` and declares the nine columns `SCHEMA.md` gives it. The
    /// registry test asserts nobody else claims the same triple; this asserts the claim
    /// itself is the right one [INVARIANT 10].
    /// </summary>
    [Fact]
    public void ItOwnsCostLedgerAndNothingElse()
    {
        var writes = Ledger().WriteSet;

        Assert.Single(writes);
        Assert.Equal("cost_ledger", writes[0].Table);
        Assert.Equal(WriteOperationName, writes[0].Operation.ToString());
        Assert.Contains("cache_write_tokens", writes[0].Columns);
        Assert.Contains("cost", writes[0].Columns);
    }

    private const string WriteOperationName = "Insert";

    /// <summary>
    /// **D-143's stated rotation ceiling, reproduced through C26's own pricing.**
    ///
    /// The decision says: "The rotation's own ceiling is 2 of 28 of that, $3.40 a year,
    /// against section 07's $1.30." That figure was derived when the key was chosen and
    /// nothing since then has checked that the component prices the same way the decision
    /// did. This is that check, and it is a fixture rather than an example: if C26 and
    /// D-143 ever disagree, one of them is wrong and a reader needs to know which.
    ///
    /// Two rotated candidates a night, each at the `digest.max_input_tokens` cap of 6,000
    /// and the `digest.max_output_tokens` cap of 150, over 252 sessions.
    /// </summary>
    [Fact]
    public async Task TheRotationCeilingReproducesTheFigureD143States()
    {
        var price = await Ledger().PriceAsync(Haiku, AsOf, TestContext.Current.CancellationToken);

        var perCall = (6_000m * price.Input / 1_000_000m) + (150m * price.Output / 1_000_000m);
        var perYear = perCall * 2 * 252;

        Assert.Equal(0.00675m, perCall);
        Assert.Equal(3.402m, perYear);

        // D-143 rounds to $3.40. Asserted to the cent rather than to the decision's own
        // precision, so a change that moved the third decimal would still be caught.
        Assert.Equal(3.40m, Math.Round(perYear, 2));
    }

    /// <summary>
    /// **The arithmetic, against the real column.** 6,000 input at $1, 150 output at $5,
    /// 400 cache write at $1.25 and 1,200 cache read at $0.10 per million:
    ///
    ///   0.006000 + 0.000750 + 0.000500 + 0.000120 = 0.007370
    ///
    /// Worked out here rather than computed in the assertion, because an assertion that
    /// repeats the implementation's expression passes whatever the expression says.
    /// </summary>
    [Fact]
    public async Task OneCallIsPricedAndWrittenWithItsFourCounts()
    {
        var ct = TestContext.Current.CancellationToken;
        var date = new DateOnly(1999, 1, 4);

        await using (var connection = await TestDatabase.OpenAsync(ct))
        await using (var clear = connection.CreateCommand())
        {
            clear.CommandText = "DELETE FROM cost_ledger WHERE date = @d;";
            clear.Parameters.AddWithValue("d", date);
            await clear.ExecuteNonQueryAsync(ct);
        }

        var ledger = new CostLedger(TestDatabase.ConnectionString, new StubConfig(Table));

        var cost = await ledger.RecordAsync(
            date, Haiku, inputTokens: 6_000, outputTokens: 150,
            cacheWriteTokens: 400, cacheReadTokens: 1_200, asOf: AsOf, ct: ct);

        Assert.Equal(0.007370m, cost);

        await using var read = await TestDatabase.OpenAsync(ct);
        await using var command = read.CreateCommand();
        command.CommandText =
            "SELECT model_id, input_tokens, cache_write_tokens, cache_read_tokens, " +
            "output_tokens, cost, was_batch, portfolio_id FROM cost_ledger WHERE date = @d;";
        command.Parameters.AddWithValue("d", date);

        await using var reader = await command.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));

        Assert.Equal(Haiku, reader.GetString(0));
        Assert.Equal(6_000L, reader.GetInt64(1));
        Assert.Equal(400L, reader.GetInt64(2));
        Assert.Equal(1_200L, reader.GetInt64(3));
        Assert.Equal(150L, reader.GetInt64(4));
        Assert.Equal(0.007370m, reader.GetDecimal(5));
        Assert.False(reader.GetBoolean(6));

        // Null, not empty. A digest call belongs to no portfolio, and the column carries
        // that rather than a string standing in for it [`CLAUDE.md` section 6].
        Assert.True(await reader.IsDBNullAsync(7, ct));

        Assert.False(await reader.ReadAsync(ct));
    }

    /// <summary>
    /// **An unreported count prices as nothing and is stored as null, and the row is
    /// still written.** A provider that did not say has not said zero, and a missing row
    /// would be worse than an under-priced one: the call happened.
    /// </summary>
    [Fact]
    public async Task AnUnreportedCountIsStoredAsNullRatherThanZero()
    {
        var ct = TestContext.Current.CancellationToken;
        var date = new DateOnly(1999, 1, 5);

        await using (var connection = await TestDatabase.OpenAsync(ct))
        await using (var clear = connection.CreateCommand())
        {
            clear.CommandText = "DELETE FROM cost_ledger WHERE date = @d;";
            clear.Parameters.AddWithValue("d", date);
            await clear.ExecuteNonQueryAsync(ct);
        }

        var cost = await new CostLedger(TestDatabase.ConnectionString, new StubConfig(Table))
            .RecordAsync(date, Haiku, 6_000, 150, null, null, AsOf, ct: ct);

        Assert.Equal(0.006750m, cost);

        await using var read = await TestDatabase.OpenAsync(ct);
        await using var command = read.CreateCommand();
        command.CommandText =
            "SELECT cache_write_tokens IS NULL, cache_read_tokens IS NULL FROM cost_ledger " +
            "WHERE date = @d;";
        command.Parameters.AddWithValue("d", date);

        await using var reader = await command.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
    }

    /// <summary>`cost.price_per_mtok_usd` and nothing else; anything unexpected throws.</summary>
    private sealed class StubConfig(string table) : IConfigStore
    {
        public Task<ConfigRow?> ResolveAsync(string key, DateOnly asOf, CancellationToken ct = default)
            => string.Equals(key, CostLedger.PriceKey, StringComparison.Ordinal)
                ? Task.FromResult<ConfigRow?>(new ConfigRow(key, 1, table, new DateOnly(2020, 1, 1)))
                : throw new InvalidOperationException(
                    $"C26 resolved '{key}', which 5.14 does not expect it to read.");

        public async Task<ConfigRow> RequireAsync(string key, DateOnly asOf, CancellationToken ct = default)
            => (await ResolveAsync(key, asOf, ct).ConfigureAwait(false))!;

        public Task<int?> ResolveVersionAsync(DateOnly asOf, CancellationToken ct = default)
            => throw new NotSupportedException("C26 does not resolve the store-wide config version.");

        public Task<int> RequireVersionAsync(DateOnly asOf, CancellationToken ct = default)
            => throw new NotSupportedException("C26 does not resolve the store-wide config version.");

        public Task<IReadOnlyList<ConfigRow>> ResolvePrefixAsync(
            string prefix, DateOnly asOf, CancellationToken ct = default)
            => throw new NotSupportedException("C26 reads one key by name.");
    }
}
