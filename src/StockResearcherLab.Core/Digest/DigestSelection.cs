using System.Globalization;

namespace StockResearcherLab.Core.Digest;

/// <summary>
/// One article, as the digester sees it.
/// </summary>
/// <param name="PublishedAt">
/// What D-133 orders on. Null sorts last: an article whose publication instant the
/// provider did not send is not the most recent one, and guessing a timestamp would put
/// it inside or outside a window it does not belong to [`CLAUDE.md` §6].
/// </param>
/// <param name="Title">For the article's header line in the input.</param>
/// <param name="Source">D-133's tie-break. Measured absent on this provider [5.1].</param>
/// <param name="Url">Carried so the tie-break has a further key.</param>
/// <param name="Content">
/// The body. Null is the provider having sent none, and a row carrying null is excluded
/// from the input and counted rather than sent as an empty article [D-131].
/// </param>
public sealed record Article(
    DateTimeOffset? PublishedAt, string? Title, string? Source, string? Url, string? Content);

/// <summary>
/// D-143's selection: whole articles, most recent first, while the next one still fits
/// inside `digest.max_input_tokens`, and a minimum of one even where that one exceeds the
/// cap.
/// </summary>
public static class DigestSelection
{
    /// <summary>
    /// **Characters per token, and it is deliberately below both measured ratios.**
    ///
    /// D-143 caps the input in tokens and this system has no tokenizer. It cannot have a
    /// useful one, and that is the argument rather than the excuse: the selection must be
    /// identical whichever link answers, or the rotation compares two different inputs and
    /// D-27's paired sample stops being paired. A count taken from one provider's
    /// tokenizer would make the evidence depend on which link the chain picked that night.
    ///
    /// So the count is an estimate, stated here, provider-independent, and conservative in
    /// the direction that keeps a ceiling a ceiling. 5.1 measured two ratios: 4.30
    /// characters per token over 20 article bodies counted by `qwen3.6:latest`, and 3.80
    /// over a real three-article prompt of 28,463 characters that the same server evaluated
    /// at 7,498 tokens, the instruction and the framing being denser than prose. 3.5 sits
    /// below both, so the estimate over-counts, the selection sends no more than the cap
    /// allows, and D-143's $47.63 is not exceeded by an under-count.
    ///
    /// **Over-counting costs an article at the margin and under-counting costs the
    /// budget**, which is why the error is pointed this way rather than centred.
    /// </summary>
    public const decimal CharactersPerToken = 3.5m;

    /// <summary>Tokens, estimated. Never negative, and zero only for an empty string.</summary>
    public static int EstimateTokens(string text)
        => string.IsNullOrEmpty(text)
            ? 0
            : (int)Math.Ceiling(text.Length / CharactersPerToken);

    /// <summary>
    /// The articles to send, in the order they are sent.
    ///
    /// **Most recent first, and the tie-break is total** [D-133, extended at 5.8]. D-133
    /// breaks ties on the source string; 5.1 measured this provider sending no source at
    /// all, and 5.4's fixture is two articles sharing a publication instant, a title and a
    /// link and differing only in body. On those two, a tie-break on source alone leaves
    /// the order undecided, and an undecided order is a different prompt on two runs of
    /// one night [INVARIANT 6]. So the key continues through the source, the url and the
    /// body, all ordinal, which decides every pair that is not the same article twice.
    ///
    /// **Whole articles only.** The first article that does not fit ends the selection and
    /// the loop does not reach past it for a smaller one behind it, because a selection
    /// that reorders on size is no longer the most recent articles and no document
    /// describes what it would be [D-143].
    ///
    /// **A minimum of one, even where that one exceeds the cap.** Without it a candidate
    /// whose only recent article is an earnings-call transcript produces no digest, which
    /// is D-134's null state, which is D-60's "no digest available", which disqualifies the
    /// name for having long news. 5.1 measured a body of 14,099 tokens, so this is a real
    /// case rather than a contrived one.
    /// </summary>
    public static IReadOnlyList<Article> Select(IEnumerable<Article> articles, int maxInputTokens)
    {
        var ordered = articles
            .Where(a => !string.IsNullOrWhiteSpace(a.Content))
            .OrderByDescending(a => a.PublishedAt ?? DateTimeOffset.MinValue)
            .ThenBy(a => a.Source ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(a => a.Url ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(a => a.Content, StringComparer.Ordinal)
            .ToList();

        var taken = new List<Article>();
        var used = 0;

        foreach (var article in ordered)
        {
            var cost = EstimateTokens(article.Content!);

            if (taken.Count > 0 && used + cost > maxInputTokens)
            {
                break;
            }

            taken.Add(article);
            used += cost;
        }

        return taken;
    }

    /// <summary>
    /// The selected articles as one block of text, which is what a link is sent.
    ///
    /// Each article carries its publication date and title, because the instruction asks
    /// for facts attributed to dated articles and a body with no date attached makes that
    /// impossible to satisfy [`prompts/digest-instruction.md`].
    ///
    /// **Invariant formatting throughout.** A date rendered in the machine's locale is a
    /// different prompt on a different machine, which is INVARIANT 6's failure one prompt
    /// over [`CLAUDE.md` §6].
    /// </summary>
    public static string Render(IReadOnlyList<Article> articles)
        => string.Join(
            "\n\n",
            articles.Select(a => string.Create(
                CultureInfo.InvariantCulture,
                $"[{Published(a)}] {a.Title ?? "(no title)"}\n{a.Content}")));

    private static string Published(Article article)
        => article.PublishedAt is DateTimeOffset at
            ? at.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : "date unknown";
}
