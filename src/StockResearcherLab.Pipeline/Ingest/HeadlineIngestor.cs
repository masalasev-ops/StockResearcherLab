using System.Globalization;
using System.Text;
using System.Text.Json;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data.Eodhd;

namespace StockResearcherLab.Pipeline.Ingest;

/// <summary>
/// C29. Headline strings and article bodies, for the candidates of one night and for
/// nothing else [D-23].
///
/// **It is in `Ingest/` because `ARCHITECTURE.html` §01's layer map puts it in layer
/// 1, and §03 says it "runs inside the select layer because candidates do not exist
/// until then".** Those disagree and both documents are human-edited. The layer map is
/// the document whose subject is which layer a component is in, so the folder follows
/// it and §03's sentence is read as a statement about when it runs. Reported rather
/// than resolved [5.2 findings, B2].
///
/// **Only candidates, and the reason is the opposite of C04's.** Sentiment covers the
/// whole universe because a screen ranks on it and a screen blind to a name cannot
/// surface it. Headlines exist for the dossier, only candidates reach the dossier, and
/// fetching them for 2,800 names would spend 14,000 units a night to store what nothing
/// reads [D-23]. **This is not a filter in INVARIANT 1's sense**: nothing here narrows
/// what can be discovered, because discovery already happened at 18:30.
///
/// **The grain is a run scope rather than a row identity** [D-132, D-68]. Two articles
/// about one company on one day are not a duplicate to be collapsed; they are two
/// articles, and 5.1 measured them sharing a title and a publication timestamp while
/// differing in body. So there is no unique index to upsert against, and idempotence
/// comes from deleting the ticker and date this run is about to write and reinserting.
/// Both operations are this component's, so INVARIANT 10 read per operation is
/// untouched, which is C14's shape on `candidate_set` one table over.
///
/// **The scope is ticker and date rather than date alone.** A night that fails part way
/// through must not have deleted the candidates it never reached.
/// </summary>
public sealed class HeadlineIngestor : IStage
{
    /// <summary>
    /// What a row carries. `content` is D-131's and is what the digest actually reads;
    /// `source` has no input on this endpoint and is written null [5.1].
    /// </summary>
    public static readonly string[] Columns =
        ["ticker", "date", "published_at", "title", "source", "url", "content"];

    private readonly EodhdClient _client;

    public HeadlineIngestor(EodhdClient client) => _client = client;

    public string Name => "HeadlineIngestor";

    /// <summary>
    /// `candidate_set` and the news endpoint, which is exactly §03's cell.
    ///
    /// **`screen_score_daily` and `attribution` are deliberately absent**, and their
    /// absence is INVARIANT 7 made structural one stage before the digest rather than
    /// documented at it. This component decides which names get evidence gathered; a
    /// component that could see a name's score could gather more for a better-scoring
    /// one, and nothing downstream would be able to tell.
    /// </summary>
    public IReadOnlyList<string> ReadSet { get; } = ["candidate_set"];

    /// <summary>
    /// Insert and Delete, both this component's, and no Update [D-132].
    ///
    /// The absence of Update is what makes a later session's "just refresh the bodies"
    /// throw through <see cref="DeclaredAccess"/> before a connection opens, rather
    /// than quietly producing a table where some rows are tonight's and some are last
    /// week's.
    /// </summary>
    public IReadOnlyList<TableWrite> WriteSet { get; } =
    [
        new TableWrite("headline", WriteOperation.Delete, Columns),
        new TableWrite("headline", WriteOperation.Insert, Columns),
    ];

    public async Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
    {
        var lookback = (int)await LongAsync(context, "digest.lookback_days", ct).ConfigureAwait(false);

        var candidates = await CandidatesAsync(context, ct).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            // **Zero rows and that is correct**, which is C14's warm-up case one stage
            // later and is why this says so rather than tripping the halt. No candidate
            // means no name reached a floor tonight; a night with no headlines is what
            // that produces and nothing downstream is short [§18, 5.4].
            return new StageResult(
                0, "ok",
                "no candidate on this date, so there is nothing to fetch headlines for",
                ZeroRowsExpected: true);
        }

        var from = context.Date.AddDays(-lookback);

        var rows = new List<Row>();
        var empty = 0;
        var bodiless = 0;

        foreach (var ticker in candidates)
        {
            var fetched = await FetchAsync(ticker, from, context.Date, ct).ConfigureAwait(false);
            if (fetched.Count == 0)
            {
                empty++;
                continue;
            }

            bodiless += fetched.Count(r => string.IsNullOrWhiteSpace(r.Content));
            rows.AddRange(fetched);
        }

        // Cleared before it is written, and scoped to the names this run fetched for,
        // so a re-run replaces the night rather than merging with what a previous one
        // left [D-132].
        await context.Data.WriteAsync(
            "headline", WriteOperation.Delete,
            ClearSql(candidates, context.Date), parameters: null, ct).ConfigureAwait(false);

        var written = rows.Count == 0
            ? 0
            : await context.Data.WriteAsync(
                "headline", WriteOperation.Insert,
                InsertSql(rows, context.Date), parameters: null, ct).ConfigureAwait(false);

        var detail = string.Format(
            CultureInfo.InvariantCulture,
            "{0:N0} article(s) over {1:N0} candidate(s) in {2} day(s); {3:N0} candidate(s) returned none, " +
            "{4:N0} article(s) carried no body. An absent body is the provider sending none, not an empty " +
            "one [D-131]",
            written, candidates.Count, lookback, empty, bodiless);

        // Zero rows over a non-empty candidate set is a real answer rather than an
        // expected one: it means no candidate carried an article in the window, which
        // 5.1 measured happening for three of thirty-two on a single name and would be
        // remarkable across all of them. It is not marked expected, so the halt applies.
        return new StageResult(written, "ok", detail);
    }

    // ---------------------------------------------------------------- reading ---

    private static async Task<IReadOnlyList<string>> CandidatesAsync(
        StageContext context, CancellationToken ct)
    {
        var sql = string.Create(
            CultureInfo.InvariantCulture,
            $"SELECT ticker FROM candidate_set WHERE date = DATE '{context.Date:yyyy-MM-dd}' ORDER BY ticker;");

        var rows = await context.Data.ReadAsync("candidate_set", sql, ct).ConfigureAwait(false);

        // Ordinal, and ordered by the statement rather than by enumeration, so two runs
        // over one date fetch in one order [`CLAUDE.md` §6].
        return [.. rows.Select(r => (string)r[0]!)];
    }

    // --------------------------------------------------------------- fetching ---

    private sealed record Row(
        string Ticker, DateTimeOffset? PublishedAt, string? Title, string? Url, string? Content);

    /// <summary>
    /// One call per candidate. `limit` is the endpoint's own cap on rows returned and
    /// is not D-143's input cap: this stage stores the window and the digester selects
    /// from it, so a cap applied here would decide what a later component can see.
    /// </summary>
    private async Task<IReadOnlyList<Row>> FetchAsync(
        string ticker, DateOnly from, DateOnly to, CancellationToken ct)
    {
        using var doc = await _client.GetAsync(
            "news",
            [
                ("s", ticker),
                ("from", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ("to", to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ("limit", "1000"),
            ],
            ct).ConfigureAwait(false);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var rows = new List<Row>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            rows.Add(new Row(
                ticker,
                Published(el),
                Text(el, "title"),
                Text(el, "link"),
                Text(el, "content")));
        }

        return rows;
    }

    /// <summary>
    /// The payload's `date`, which is the article's publication instant and is what
    /// D-133 orders on. Null where it is absent or unparseable, because a guessed
    /// timestamp would put an article inside or outside a window it does not belong to
    /// and nothing downstream could tell [`CLAUDE.md` §6].
    /// </summary>
    private static DateTimeOffset? Published(JsonElement el)
        => el.TryGetProperty("date", out var d)
           && d.ValueKind == JsonValueKind.String
           && DateTimeOffset.TryParse(
               d.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;

    private static string? Text(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    // ---------------------------------------------------------------- writing ---

    /// <summary>
    /// The delete, scoped to the tickers this run fetched for and to its date.
    ///
    /// **Not `WHERE date = ...` alone.** A run that failed after clearing and before
    /// writing would leave the night's other candidates with no headlines and no error,
    /// and the next run would not know they were missing.
    /// </summary>
    private static string ClearSql(IReadOnlyList<string> tickers, DateOnly date)
    {
        var sb = new StringBuilder("DELETE FROM headline WHERE date = DATE '");
        sb.Append(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        sb.Append("' AND ticker IN (");

        for (var i = 0; i < tickers.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            Literal(sb, tickers[i]);
        }

        return sb.Append(");").ToString();
    }

    private static string InsertSql(IReadOnlyList<Row> rows, DateOnly date)
    {
        var sb = new StringBuilder(
            "INSERT INTO headline (ticker, date, published_at, title, source, url, content) VALUES ");

        for (var i = 0; i < rows.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            var r = rows[i];
            sb.Append('(');
            Literal(sb, r.Ticker);
            sb.Append(", DATE '").Append(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append("', ");

            if (r.PublishedAt is DateTimeOffset published)
            {
                sb.Append("TIMESTAMPTZ '")
                    .Append(published.ToString("yyyy-MM-dd HH:mm:sszzz", CultureInfo.InvariantCulture))
                    .Append("', ");
            }
            else
            {
                sb.Append("NULL, ");
            }

            Literal(sb, r.Title);
            sb.Append(", ");

            // **`source` has no input and is written null** [5.1, D-124's disposition].
            // The payload carries content, date, link, sentiment, symbols, tags and
            // title, and nothing maps to source. The host of `link` is derivable and
            // deriving it is a choice rather than a read.
            sb.Append("NULL, ");

            Literal(sb, r.Url);
            sb.Append(", ");
            Literal(sb, r.Content);
            sb.Append(')');
        }

        return sb.Append(';').ToString();
    }

    private static void Literal(StringBuilder sb, string? value)
    {
        if (value is null)
        {
            sb.Append("NULL");
            return;
        }

        sb.Append('\'').Append(value.Replace("'", "''", StringComparison.Ordinal)).Append('\'');
    }

    private static async Task<long> LongAsync(StageContext context, string key, CancellationToken ct)
    {
        var row = await context.Config.RequireAsync(key, context.Date, ct).ConfigureAwait(false);
        return long.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new InvalidOperationException($"{key} resolved to '{row.Value}', which is not a whole number.");
    }
}
