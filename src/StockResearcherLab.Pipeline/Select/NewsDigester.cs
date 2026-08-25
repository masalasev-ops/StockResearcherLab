using System.Globalization;
using System.Text;
using StockResearcherLab.Core.Digest;
using StockResearcherLab.Core.Stages;

namespace StockResearcherLab.Pipeline.Select;

/// <summary>
/// C33. One digest per candidate per night, from the first link in the chain that answers
/// [D-133, D-134, D-136, D-143].
///
/// **It transforms evidence and never judges** [INVARIANT 7]. It selects which articles to
/// send by recency and length and nothing else, sends the one instruction every link
/// receives, and stores what came back. It never scores a candidate, never expresses a
/// view, and never removes one from the set. The read set is where that is made structural
/// rather than promised: `candidate_set` and `headline`, and nothing about a score, so a
/// component that gathered more evidence for a better-scoring name could not see which
/// name that was.
///
/// **The four outcomes are D-134's and they must stay separable.** No row, a null
/// `digest_text`, the escape hatch, or prose. The middle pair is the one that gets
/// collapsed, and collapsing it makes S5's no-digest disqualifier fire on thinly covered
/// small caps, which is the population this design exists to reach.
/// </summary>
public sealed class NewsDigester : IStage
{
    public static readonly string[] Columns =
        ["ticker", "date", "digest_text", "provider", "model_name", "was_rotation"];

    private readonly Func<StageContext, CancellationToken, Task<DigestChain>> _chain;
    private readonly Func<DigestInstruction.Instruction> _instruction;

    /// <param name="chain">
    /// Built per run, because it holds which links have failed [D-137]. Injected as a
    /// factory rather than as an instance so a re-used stage cannot carry one run's
    /// failures into the next.
    /// </param>
    /// <param name="instruction">
    /// `prompts/digest-instruction.md`. Identical for every link, because the rotation
    /// compares them and a difference in prompt would confound that [D-27].
    ///
    /// **Resolved when the stage runs rather than when it is registered.** Building the
    /// registry must touch nothing: the conformance tests enumerate it with a fictional
    /// connection string and no provider, and a constructor that read a file would make
    /// every one of them depend on the working directory.
    /// </param>
    public NewsDigester(
        Func<StageContext, CancellationToken, Task<DigestChain>> chain,
        Func<DigestInstruction.Instruction> instruction)
    {
        _chain = chain;
        _instruction = instruction;
    }

    public string Name => "NewsDigester";

    /// <summary>
    /// **`screen_score_daily` and `attribution` are absent and their absence is asserted**,
    /// which is INVARIANT 7 made structural at the component the invariant is about. C29
    /// carries the same absence one stage earlier for the same reason.
    /// </summary>
    public IReadOnlyList<string> ReadSet { get; } = ["candidate_set", "headline"];

    /// <summary>
    /// Insert and Delete, both this component's, and no Update.
    ///
    /// The grain is ticker by day and the table has that primary key, so an upsert would
    /// work; delete-then-insert is chosen for C14's and C29's reason, that a re-run
    /// replaces the night rather than merging with what a previous one left. The absence
    /// of Update is what makes a later "just refresh the ones that came back empty" throw
    /// before a connection opens rather than producing a table where some rows are
    /// tonight's and some are last week's.
    /// </summary>
    public IReadOnlyList<TableWrite> WriteSet { get; } =
    [
        new TableWrite("news_digest", WriteOperation.Delete, Columns),
        new TableWrite("news_digest", WriteOperation.Insert, Columns),
    ];

    /// <summary>D-134's escape hatch, exactly as `prompts/digest-instruction.md` defines it.</summary>
    public const string NoMaterialNews = "NO MATERIAL NEWS";

    public async Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
    {
        var lookback = (int)await LongAsync(context, "digest.lookback_days", ct).ConfigureAwait(false);
        var maxInput = (int)await LongAsync(context, "digest.max_input_tokens", ct).ConfigureAwait(false);
        var maxOutput = (int)await LongAsync(context, "digest.max_output_tokens", ct).ConfigureAwait(false);

        var candidates = await CandidatesAsync(context, ct).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return new StageResult(
                0, "ok",
                "no candidate on this date, so there is nothing to digest",
                ZeroRowsExpected: true);
        }

        var instruction = _instruction();
        var chain = await _chain(context, ct).ConfigureAwait(false);

        // **INVARIANT 15's gate.** No healthy link halts the run before any researcher call
        // and before any order exists. Continuing with digests absent would put a night of
        // thinner evidence into the record looking identical to every night around it,
        // which is the failure `CLAUDE.md` §1 is about. 5.11 asserts the observable.
        var ready = await chain.ReadyAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "No link in the digest chain is healthy, so the run halts here [INVARIANT 15, D-139]. " +
                string.Join(" ", chain.PassedOver));

        var from = context.Date.AddDays(-lookback);
        var rows = new List<Row>(candidates.Count);
        var counts = new Counts();

        foreach (var ticker in candidates)
        {
            var articles = await ArticlesAsync(context, ticker, from, ct).ConfigureAwait(false);
            var selected = DigestSelection.Select(articles, maxInput);

            if (selected.Count == 0)
            {
                // **D-134's second state, and it is not the third.** Nothing was sent
                // because there was nothing to send: no article in the window, or every
                // article carrying a null body. The row exists and carries the link that
                // would have answered, which is what the probe is for.
                counts.NoArticles++;
                rows.Add(new Row(ticker, null, ready.Provider, ready.LoadedModel));
                continue;
            }

            var answer = await chain.DigestAsync(
                new DigestRequest(instruction.Text, DigestSelection.Render(selected), maxOutput), ct)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Every link in the chain failed while digesting {ticker}, so the run halts " +
                    "[INVARIANT 15, D-137]. " + string.Join(" ", chain.PassedOver));

            var text = answer.Text.Trim();

            if (string.Equals(text, NoMaterialNews, StringComparison.Ordinal))
            {
                counts.NoMaterial++;
            }
            else
            {
                counts.Prose++;
            }

            counts.Articles += selected.Count;
            rows.Add(new Row(ticker, text, answer.Provider, answer.Model));
        }

        await context.Data.WriteAsync(
            "news_digest", WriteOperation.Delete,
            ClearSql(candidates, context.Date), parameters: null, ct).ConfigureAwait(false);

        var written = await context.Data.WriteAsync(
            "news_digest", WriteOperation.Insert,
            InsertSql(rows, context.Date), parameters: null, ct).ConfigureAwait(false);

        var detail = string.Format(
            CultureInfo.InvariantCulture,
            "{0:N0} digest(s) over {1:N0} candidate(s) from {2:N0} article(s): {3:N0} prose, " +
            "{4:N0} {5}, {6:N0} with no article to read. First healthy link {7} on {8}",
            written, candidates.Count, counts.Articles, counts.Prose, counts.NoMaterial,
            NoMaterialNews, counts.NoArticles,
            DigestProviders.Name(ready.Provider), ready.LoadedModel ?? "an unnamed model");

        // The instruction's hash, because a change to this text splits history into halves
        // that cannot be pooled and the run log is where a reader would look for when
        // [`CLAUDE.md` section 12].
        // Length-guarded rather than sliced. A hash from `DigestInstruction` is 64
        // characters and always will be, but a run must not fail on the shape of its own
        // log line, which is a stage failing for a reason that is not about the night.
        var version = instruction.Sha256.Length > 12 ? instruction.Sha256[..12] : instruction.Sha256;
        detail += string.Create(CultureInfo.InvariantCulture, $". Instruction {version}");

        if (chain.PassedOver.Count > 0)
        {
            // The second half of D-137's record. Without it a night that ran entirely on
            // the secondary looks the same whether the primary was down or second in order.
            detail += ". Passed over: " + string.Join(" ", chain.PassedOver);
        }

        return new StageResult(written, "ok", detail);
    }

    private sealed class Counts
    {
        public int Articles;
        public int Prose;
        public int NoMaterial;
        public int NoArticles;
    }

    // ---------------------------------------------------------------- reading ---

    private sealed record Row(string Ticker, string? Text, DigestProvider Provider, string? Model);

    private static async Task<IReadOnlyList<string>> CandidatesAsync(
        StageContext context, CancellationToken ct)
    {
        // Ordinal by the statement rather than by the server's collation, so two runs
        // over one date digest in one order whatever locale the database was created in
        // [`CLAUDE.md` section 6].
        var sql = string.Create(
            CultureInfo.InvariantCulture,
            $"SELECT ticker FROM candidate_set WHERE date = DATE '{context.Date:yyyy-MM-dd}' ORDER BY ticker COLLATE \"C\";");

        var rows = await context.Data.ReadAsync("candidate_set", sql, ct).ConfigureAwait(false);

        return [.. rows.Select(r => (string)r[0]!)];
    }

    /// <summary>
    /// One candidate's articles inside the window.
    ///
    /// **Bounded on `published_at`, which is the article's instant, not `date`, which is
    /// the run's** [D-133]. C29 stored a window already, so this is not a second filter so
    /// much as the statement of what the digester reads; on a re-run over a night whose
    /// `headline` rows came from a different lookback it is the thing that keeps the two
    /// answers the same.
    ///
    /// **A null `published_at` is inside no window.** Guessing one would put an article
    /// into a window it may not belong to, and the row is counted as bodiless by C29
    /// rather than being silently included here [`CLAUDE.md` §6].
    /// </summary>
    private static async Task<IReadOnlyList<Article>> ArticlesAsync(
        StageContext context, string ticker, DateOnly from, CancellationToken ct)
    {
        var safe = ticker.Replace("'", "''", StringComparison.Ordinal);

        var sql = string.Create(
            CultureInfo.InvariantCulture,
            $"SELECT published_at, title, source, url, content FROM headline WHERE date = DATE '{context.Date:yyyy-MM-dd}' AND ticker = '{safe}' AND published_at IS NOT NULL AND published_at >= TIMESTAMPTZ '{from:yyyy-MM-dd} 00:00:00+00' ORDER BY published_at DESC;");

        var rows = await context.Data.ReadAsync("headline", sql, ct).ConfigureAwait(false);

        // Ordered in `DigestSelection` rather than here, so one rule decides the order and
        // a test can exercise it without a store [D-133, 5.8].
        return
        [
            // **The instant goes through `StoredInstant` and not through a cast.** The
            // driver returns `timestamptz` as a `DateTime`, so `as DateTimeOffset?` is
            // null for every row and D-143's ordering silently loses the thing it orders
            // on. That was live between 5.8 and 5.9 [5.9].
            .. rows.Select(r => new Article(
                StoredInstant.From(r[0]),
                r[1] as string,
                r[2] as string,
                r[3] as string,
                r[4] as string)),
        ];
    }

    // ---------------------------------------------------------------- writing ---

    private static string ClearSql(IReadOnlyList<string> tickers, DateOnly date)
    {
        var sb = new StringBuilder("DELETE FROM news_digest WHERE date = DATE '");
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
            "INSERT INTO news_digest (ticker, date, digest_text, provider, model_name, was_rotation) VALUES ");

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
            Literal(sb, r.Text);
            sb.Append(", ");
            Literal(sb, DigestProviders.Name(r.Provider));
            sb.Append(", ");

            // **`model_name` is NOT NULL and non-blank** [D-134, `0022`]. A link that
            // answered without naming a model is a row that cannot be written, rather than
            // a row carrying a blank that reads as attributed. Fail closed at the column.
            Literal(sb, r.Model ?? throw new InvalidOperationException(
                $"The link that answered for {r.Ticker} named no model, and " +
                "news_digest.model_name is what separates a shift in results from a change " +
                "in the evidence [D-29]. A blank there records nothing."));

            // **The rotation is 5.10's and every row here is false.** Not defaulted: the
            // column has a DEFAULT and a writer that declines to think about it is what
            // the default makes easy, so the value is written.
            sb.Append(", FALSE)");
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
