using System.Globalization;
using System.Text.Json;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data.Eodhd;

namespace StockResearcherLab.Pipeline.Ingest;

/// <summary>
/// C04. Sentiment for the whole universe, with no pre-selection [D-23].
///
/// **This is the component convergence got into once already.** Sentiment was pulled
/// only for the top 400 by prior screen score, so a thinly covered name having the
/// exact coverage spike the sentiment screen exists to catch had no data and could
/// never be found [`ARCHITECTURE.html` §20, D-5]. Absolute filters live in the
/// universe definition and nowhere else, so this stage takes `security` whole and
/// narrows by nothing.
///
/// **A sparse series is the ordinary state, not a fault.** The probe measured 4 to
/// 122 days with a row out of 180 on small caps, and rows appear only on days that
/// carry news. A day with no row is a day nobody wrote about, which is not the same
/// as a day of zero attention: a z-score of zero says attention is exactly at its own
/// baseline, absent says nobody knows, and the screens treat those differently
/// [D-12, `CLAUDE.md` §6]. No row is fabricated to fill a gap.
///
/// **UNVERIFIED AGAINST THE PROVIDER.** The endpoint's row fields are taken from the
/// documented shape and from what the phase P probe reported measuring, not from a
/// recorded transcript: 1.9 captured only the envelope, an object keyed by ticker.
/// The parser therefore accepts the documented names and tolerates their absence
/// rather than assuming. Live verification is owed and is blocked on the provider
/// allowance.
/// </summary>
public sealed class SentimentIngestor : IStage
{
    public static readonly string[] Columns = ["ticker", "date", "article_count", "sentiment_score"];

    private static readonly string[] ConflictTarget = ["ticker", "date"];

    private readonly EodhdClient _client;

    public SentimentIngestor(EodhdClient client) => _client = client;

    public string Name => "SentimentIngestor";

    public IReadOnlyList<string> ReadSet { get; } = ["security"];

    public IReadOnlyList<TableWrite> WriteSet { get; } =
        [new TableWrite("sentiment_daily", WriteOperation.Insert, Columns)];

    public async Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
    {
        var perCall = (int) await LongAsync(context, "sentiment.tickers_per_call", ct).ConfigureAwait(false);
        var lookback = (int) await LongAsync(context, "sentiment.lookback_days", ct).ConfigureAwait(false);

        var universe = await UniverseAsync(context, ct).ConfigureAwait(false);
        if (universe.Count == 0)
        {
            // Nothing to do rather than nothing to say. C01 has not run, and a stage
            // that writes zero rows halts the sequence, which is the correct answer:
            // a night with no universe has nothing downstream can use.
            return new StageResult(0, "ok", "security is empty, so there is no universe to pull sentiment for");
        }

        var from = context.Date.AddDays(-lookback);
        long written = 0;
        var covered = 0;

        foreach (var batch in Batch(universe, perCall))
        {
            var rows = await FetchAsync(batch, from, context.Date, ct).ConfigureAwait(false);
            if (rows.Count == 0)
            {
                continue;
            }

            covered += rows.Select(r => r.Ticker).Distinct(StringComparer.Ordinal).Count();

            written += await context.Data.BulkUpsertAsync(
                "sentiment_daily", Columns, ConflictTarget,
                async (w, c) =>
                {
                    foreach (var r in rows)
                    {
                        await w.StartRowAsync(c).ConfigureAwait(false);
                        await w.WriteAsync(r.Ticker, c).ConfigureAwait(false);
                        await w.WriteAsync(r.Date, c).ConfigureAwait(false);
                        await w.WriteAsync(r.ArticleCount, c).ConfigureAwait(false);
                        await w.WriteAsync(r.Score, c).ConfigureAwait(false);
                    }
                }, ct).ConfigureAwait(false);
        }

        var detail = string.Format(
            CultureInfo.InvariantCulture,
            "{0:N0} of {1:N0} universe names returned a row over {2} days. A name with none is a name " +
            "nobody wrote about, which is not zero attention [D-12]",
            covered, universe.Count, lookback);

        return new StageResult(written, "ok", detail);
    }

    /// <summary>
    /// The universe, whole. No ordering by anything that could become a rank, and no
    /// limit: narrowing here is the failure this component is named in
    /// `ARCHITECTURE.html` §20 for.
    /// </summary>
    private static async Task<IReadOnlyList<string>> UniverseAsync(StageContext context, CancellationToken ct)
    {
        var rows = await context.Data.ReadAsync(
            "security", "SELECT ticker FROM security WHERE is_active ORDER BY ticker;", ct)
            .ConfigureAwait(false);

        return rows.Select(r => (string) r[0]!).ToList();
    }

    /// <summary>
    /// Batched, because the endpoint accepts a comma-separated symbol list and one
    /// call per name over a two-thousand name universe is two thousand calls a
    /// night. The batch size is configuration rather than a literal: the provider
    /// meters weighted units and this is one of the few places the cost can be
    /// traded against anything [`CLAUDE.md` §8, PROGRESS on metering].
    /// </summary>
    private static IEnumerable<IReadOnlyList<string>> Batch(IReadOnlyList<string> all, int size)
    {
        for (var i = 0; i < all.Count; i += size)
        {
            yield return all.Skip(i).Take(size).ToList();
        }
    }

    /// <summary>One ticker-day. Public because the parser is asserted directly, the field names being the least-verified thing in this phase.</summary>
    public readonly record struct Row(string Ticker, DateOnly Date, int? ArticleCount, float? Score);

    private async Task<IReadOnlyList<Row>> FetchAsync(
        IReadOnlyList<string> tickers, DateOnly from, DateOnly to, CancellationToken ct)
    {
        using var doc = await _client.GetAsync(
            "sentiments",
            [
                ("s", string.Join(",", tickers)),
                ("from", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ("to", to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ],
            ct).ConfigureAwait(false);

        return Parse(doc.RootElement);
    }

    /// <summary>
    /// The envelope is an object keyed by ticker, each value an array of daily rows.
    /// Shapes are checked rather than assumed, because the field names here are the
    /// least-verified thing in this phase.
    /// </summary>
    public static IReadOnlyList<Row> Parse(JsonElement root)
    {
        var rows = new List<Row>();

        if (root.ValueKind != JsonValueKind.Object)
        {
            return rows;
        }

        foreach (var byTicker in root.EnumerateObject())
        {
            if (byTicker.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var day in byTicker.Value.EnumerateArray())
            {
                if (day.ValueKind != JsonValueKind.Object
                    || !day.TryGetProperty("date", out var d)
                    || d.ValueKind != JsonValueKind.String
                    || !DateOnly.TryParseExact(d.GetString(), "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    continue;
                }

                rows.Add(new Row(byTicker.Name, date, Count(day), Score(day)));
            }
        }

        // Sorted, because COPY order reaches the table and two runs over the same
        // response must produce identical output [CLAUDE.md section 6].
        rows.Sort(static (a, b) =>
        {
            var t = string.CompareOrdinal(a.Ticker, b.Ticker);
            return t != 0 ? t : a.Date.CompareTo(b.Date);
        });

        return rows;
    }

    /// <summary>Articles that day. Absent stays null: a day with no count is not a day with none.</summary>
    private static int? Count(JsonElement day)
        => day.TryGetProperty("count", out var v) && v.ValueKind == JsonValueKind.Number
           && v.TryGetInt32(out var n)
            ? n
            : null;

    /// <summary>
    /// The normalised score. A 32-bit float because the column is <c>real</c> and this
    /// is not money [SCHEMA.md, INVARIANT 16].
    /// </summary>
    private static float? Score(JsonElement day)
        => day.TryGetProperty("normalized", out var v) && v.ValueKind == JsonValueKind.Number
           && v.TryGetSingle(out var f)
            ? f
            : null;

    private static async Task<long> LongAsync(StageContext context, string key, CancellationToken ct)
    {
        var row = await context.Config.RequireAsync(key, context.Date, ct).ConfigureAwait(false);
        return long.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new InvalidOperationException($"{key} resolved to '{row.Value}', which is not a whole number.");
    }
}
