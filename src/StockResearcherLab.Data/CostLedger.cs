using System.Globalization;
using System.Text.Json.Nodes;
using Npgsql;
using StockResearcherLab.Core.Config;
using StockResearcherLab.Core.Stages;

namespace StockResearcherLab.Data;

/// <summary>
/// What one paid call cost, priced from config rather than from a literal [5.14, D-140].
///
/// **Every field is decimal and none is `double`** [INVARIANT 16]. A price per million
/// tokens multiplied by a token count in binary floating point is the shape that produces
/// an annual total nobody can reconcile against an invoice.
/// </summary>
/// <param name="Input">Base input price per million tokens.</param>
/// <param name="Output">Output price per million tokens.</param>
/// <param name="CacheWrite">
/// The five-minute cache write price. **The one-hour rate is a different number and is
/// not held here**, because nothing in this system requests a one-hour cache; a key for a
/// rate no call can incur would be a value an audit could not verify.
/// </param>
/// <param name="CacheRead">Cache hit price per million tokens.</param>
public sealed record TokenPrice(decimal Input, decimal Output, decimal CacheWrite, decimal CacheRead);

/// <summary>
/// C26 CostLedger. One row per paid call.
///
/// **Not a stage.** It sits outside the layers in `ARCHITECTURE.html` §03 and still owns a
/// table, which is why it is an <see cref="IWriteOwner"/> and appears in the registry: a
/// writer the conformance test cannot see is a writer INVARIANT 10 is not enforced
/// against. C27 RunLog carries the same shape for the same reason.
///
/// **The digest half only, at this checkpoint.** Phase 6's researcher calls are the other
/// half and the carried obligation binds them to this table before the first full night
/// [BUILD_PLAN phase 6]. Nothing here is digest-specific except its caller.
///
/// **The local link is never recorded.** It costs nothing, and a zero row would put a
/// night of free calls into a table an operator reads as spend. Absence of a row is the
/// truthful record of a call that did not bill.
/// </summary>
public sealed class CostLedger : IWriteOwner
{
    /// <summary>
    /// The price table, as a JSON object keyed by model id with four rates each.
    ///
    /// **One key rather than four per model.** Phase 6 adds at least two more models, and
    /// a key per model per rate is sixteen rows to keep in step with a published price
    /// list; one row is one thing to check against it [`CLAUDE.md` §8].
    /// </summary>
    public const string PriceKey = "cost.price_per_mtok_usd";

    private const decimal PerMillion = 1_000_000m;

    private readonly string _connectionString;
    private readonly IConfigStore _config;

    public CostLedger(string connectionString, IConfigStore config)
    {
        _connectionString = connectionString;
        _config = config;
    }

    public string Name => "CostLedger";

    public IReadOnlyList<TableWrite> WriteSet { get; } =
    [
        new TableWrite(
            "cost_ledger",
            WriteOperation.Insert,
            [
                "date", "model_id", "portfolio_id", "input_tokens", "cache_write_tokens",
                "cache_read_tokens", "output_tokens", "cost", "was_batch",
            ]),
    ];

    /// <summary>
    /// Prices one call and inserts it.
    ///
    /// **A null token count prices as nothing and is stored as null** [`CLAUDE.md` §6]. A
    /// provider that did not report a count has not told us it was zero, and defaulting
    /// would make an under-reported bill look like a measured one. The row is still
    /// written, because a call happened and a missing row would be worse.
    /// </summary>
    /// <param name="date">The run's date, never the wall clock [INVARIANT 11].</param>
    /// <param name="modelId">
    /// What the provider said answered, which is the id the price table is keyed by. An id
    /// with no entry throws rather than pricing at zero: an unpriced model is a change
    /// nobody recorded, and a silent zero would hide it for as long as it ran.
    /// </param>
    /// <param name="asOf">
    /// The date the price is resolved as of, which is the run's date [INVARIANT 13]. A
    /// price change is a config version, so history stays segmentable across one.
    /// </param>
    public async Task<decimal> RecordAsync(
        DateOnly date,
        string modelId,
        int? inputTokens,
        int? outputTokens,
        int? cacheWriteTokens,
        int? cacheReadTokens,
        DateOnly asOf,
        string? portfolioId = null,
        bool wasBatch = false,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var price = await PriceAsync(modelId, asOf, ct).ConfigureAwait(false);

        var cost =
            (inputTokens ?? 0) * price.Input / PerMillion
            + (outputTokens ?? 0) * price.Output / PerMillion
            + (cacheWriteTokens ?? 0) * price.CacheWrite / PerMillion
            + (cacheReadTokens ?? 0) * price.CacheRead / PerMillion;

        await using var connection = new NpgsqlDataSourceBuilder(_connectionString).Build();
        await using var command = connection.CreateCommand(
            "INSERT INTO cost_ledger (date, model_id, portfolio_id, input_tokens, " +
            "cache_write_tokens, cache_read_tokens, output_tokens, cost, was_batch) " +
            "VALUES (@date, @model, @portfolio, @in, @cw, @cr, @out, @cost, @batch);");

        command.Parameters.AddWithValue("date", date);
        command.Parameters.AddWithValue("model", modelId);
        command.Parameters.AddWithValue("portfolio", (object?)portfolioId ?? DBNull.Value);
        command.Parameters.AddWithValue("in", (object?)inputTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("cw", (object?)cacheWriteTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("cr", (object?)cacheReadTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("out", (object?)outputTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("cost", cost);
        command.Parameters.AddWithValue("batch", wasBatch);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return cost;
    }

    /// <summary>
    /// The four rates for one model, resolved as of a date and refused where the model is
    /// not in the table.
    /// </summary>
    public async Task<TokenPrice> PriceAsync(string modelId, DateOnly asOf, CancellationToken ct = default)
    {
        var row = await _config.RequireAsync(PriceKey, asOf, ct).ConfigureAwait(false);

        if (JsonNode.Parse(row.Value) is not JsonObject table)
        {
            throw new InvalidOperationException(
                $"{PriceKey} resolved to '{row.Value}', which is not a JSON object.");
        }

        if (table[modelId] is not JsonObject rates)
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"{PriceKey} holds no price for '{modelId}' as of {asOf:yyyy-MM-dd}, so this call " +
                $"cannot be priced. Pricing an unknown model at zero would hide a model change for " +
                $"as long as it ran [5.14]."));
        }

        return new TokenPrice(
            Rate(rates, "input", modelId),
            Rate(rates, "output", modelId),
            Rate(rates, "cache_write", modelId),
            Rate(rates, "cache_read", modelId));
    }

    private static decimal Rate(JsonObject rates, string name, string modelId)
        => rates[name] is JsonValue value && value.TryGetValue<decimal>(out var rate)
            ? rate
            : throw new InvalidOperationException(
                $"{PriceKey} gives '{modelId}' no numeric '{name}' rate. All four are required, " +
                "because a missing one prices silently at nothing [INVARIANT 16].");
}
