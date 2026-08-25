using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using StockResearcherLab.Core.Config;
using StockResearcherLab.Core.Digest;
using StockResearcherLab.Core.Stages;

namespace StockResearcherLab.Pipeline.Select;

/// <summary>
/// The digest provider chain [D-136, D-137].
///
/// **Nothing here names a link.** The order comes from `local_model_config.provider_order`
/// filtered on `enabled`, the name at each position comes from `digest.chain`, and the
/// first link not already known to have failed answers. There is no primary and no
/// fallback branch, which is what `ARCHITECTURE.html` §07 asks for and what makes failover
/// work in both directions: a path that only executes during an outage is broken most of
/// the time and you discover it on the night you needed it.
///
/// **It is not a stage and owns no write.** It reads nothing itself beyond the two rows
/// C32 already declares, and it is composed by C33, which is the stage. This is a
/// collaborator rather than a component, so it appears in no registry and no §03 row: the
/// chain is how C33 reaches a link, not a thing the pipeline runs.
///
/// **It holds run state, deliberately, and is therefore constructed per run.** A link that
/// fails twice is unhealthy for the remainder of that run [D-137], which is what stops a
/// dead link costing two calls per candidate instead of two calls.
/// </summary>
public sealed class DigestChain
{
    private readonly IReadOnlyList<Link> _links;
    private readonly HashSet<DigestProvider> _failed = [];
    private readonly List<string> _passedOver = [];

    private DigestChain(IReadOnlyList<Link> links) => _links = links;

    /// <summary>One position in the chain: what it is called, and what implements it.</summary>
    private sealed record Link(int Order, DigestProvider Provider, IDigestLink Implementation);

    /// <summary>
    /// Builds the chain for one run, from the table and the key together.
    ///
    /// **The two sources are not redundant and this is where `digest.chain` gains its
    /// consumer** [5.3's open finding]. `local_model_config` gives the order and the
    /// address and carries no name; `digest.chain` gives the name at each position and
    /// carries no address. Pairing them by position is what lets a link's identity be
    /// versioned config while its address is an operator-editable row, which is the split
    /// D-51 and D-136 already make.
    ///
    /// **A length mismatch fails the run rather than being reconciled.** Config is
    /// versioned and this table is not, so the two can disagree; a chain with an address
    /// nothing can name, or a name nothing can reach, is not something to repair silently
    /// at 18:33.
    /// </summary>
    public static async Task<DigestChain> BuildAsync(
        IStageData data,
        IConfigStore config,
        DateOnly asOf,
        IReadOnlyDictionary<DigestProvider, IDigestLink> implementations,
        CancellationToken ct = default)
    {
        var names = await NamesAsync(config, asOf, ct).ConfigureAwait(false);
        var rows = await data.ReadAsync(
            "local_model_config",
            "SELECT provider_order FROM local_model_config WHERE enabled ORDER BY provider_order;",
            ct).ConfigureAwait(false);

        if (rows.Count != names.Count)
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"digest.chain names {names.Count} link(s) and local_model_config holds " +
                $"{rows.Count} enabled row(s). One of them has an address nothing can name or a " +
                $"name nothing can reach, and which is which is not this component's to guess."));
        }

        var links = new List<Link>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var provider = DigestProviders.Parse(names[i]);

            if (!implementations.TryGetValue(provider, out var implementation))
            {
                throw new InvalidOperationException(
                    $"digest.chain names '{names[i]}' at position {i + 1} and no link implements it. " +
                    "A link named in config and absent from the composition is a chain one shorter " +
                    "than the record says, and the fall-through would be invisible.");
            }

            links.Add(new Link(
                Convert.ToInt32(rows[i][0], CultureInfo.InvariantCulture), provider, implementation));
        }

        return new DigestChain(links);
    }

    /// <summary>The chain in order, as names. For the run log and for tests.</summary>
    public IReadOnlyList<DigestProvider> Order => [.. _links.Select(l => l.Provider)];

    /// <summary>
    /// Links this run has given up on, in the order they were given up on. A link is here
    /// after failing twice on one candidate [D-137].
    /// </summary>
    public IReadOnlyList<DigestProvider> Failed => [.. _failed];

    /// <summary>
    /// One digest, from the first link that answers acceptably.
    ///
    /// **D-137 in full, and the order of the two tests matters.** A response is tried, and
    /// if it is malformed or over-long it is tried once more on the same link. A second
    /// failure marks the link unhealthy for the rest of the run and moves to the next. A
    /// response is never cut to fit.
    ///
    /// **Every passed-over link is recorded with its reason**, because a fall-through that
    /// is only visible as a provider name on a digest row says that it happened and not
    /// why, and the two calls for opposite responses: a link that was unreachable is a
    /// machine problem and a link that answered with nothing is a configuration one
    /// [D-144].
    /// </summary>
    public async Task<ChainAnswer?> DigestAsync(DigestRequest request, CancellationToken ct = default)
    {
        foreach (var link in _links)
        {
            if (_failed.Contains(link.Provider))
            {
                continue;
            }

            for (var attempt = 1; attempt <= Attempts; attempt++)
            {
                var (answer, refusal) = await TryAsync(link, request, ct).ConfigureAwait(false);

                if (refusal is null)
                {
                    return new ChainAnswer(
                        link.Provider, answer!.Model, answer.Text!,
                        answer.PromptTokens, answer.CompletionTokens,
                        [.. _passedOver]);
                }

                if (attempt == Attempts)
                {
                    // **Unhealthy for the remainder of the run, not for this candidate.**
                    // A dead link costs two calls rather than two per candidate, which on
                    // 28 candidates is the difference between two and fifty-six.
                    _failed.Add(link.Provider);
                    _passedOver.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{DigestProviders.Name(link.Provider)} at position {link.Order} " +
                        $"failed twice and is unhealthy for the rest of this run: {refusal}"));
                }
            }
        }

        return null;
    }

    /// <summary>
    /// **One retry and it is not a config key** [D-137]. §18 states one retry as behaviour
    /// rather than as a tuning parameter, and a key here would invite raising it, which
    /// trades a visible fall-through for an invisible delay.
    /// </summary>
    private const int Attempts = 2;

    /// <summary>
    /// One call, and D-137's two tests over what came back. Returns the answer, or the
    /// reason it is refused.
    /// </summary>
    private static async Task<(DigestAnswer? Answer, string? Refusal)> TryAsync(
        Link link, DigestRequest request, CancellationToken ct)
    {
        DigestAnswer answer;
        try
        {
            answer = await link.Implementation.DigestAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            // A transport failure is malformed [D-137] and is named as itself, because
            // "unreachable" and "answered with nothing" call for opposite responses.
            return (null, $"the link could not be reached ({ex.Message})");
        }
        catch (JsonException ex)
        {
            return (null, $"the link answered with something that is not the expected JSON ({ex.Message})");
        }

        // Malformed: empty, or only whitespace. Deliberately not a content judgement. A
        // digest that reads oddly is not malformed, and a step that decided which
        // summaries were good enough would be judging [INVARIANT 7].
        if (string.IsNullOrWhiteSpace(answer.Text))
        {
            return (null, "the link answered with no content");
        }

        // Over-long, asserted on the response rather than trusted from the request,
        // because a provider that ignores the cap returns a long answer with a normal
        // status [D-137].
        if (answer.CompletionTokens is not int completion)
        {
            // **Unmeasurable is refused rather than accepted**, which is stricter than
            // D-137 says and is the fail-closed reading of it. The assertion on the
            // response is the whole mechanism; a link that cannot say how long its answer
            // was cannot be checked, and accepting it would put the one case D-137 exists
            // for outside the net. Both links report usage today, so this refuses nothing
            // that currently exists.
            return (null, "the link reported no completion token count, so the cap cannot be asserted");
        }

        if (completion > request.MaxOutputTokens)
        {
            return (null, string.Create(
                CultureInfo.InvariantCulture,
                $"the link answered with {completion:N0} completion token(s) against a cap of " +
                $"{request.MaxOutputTokens:N0}"));
        }

        return (answer, null);
    }

    private static async Task<IReadOnlyList<string>> NamesAsync(
        IConfigStore config, DateOnly asOf, CancellationToken ct)
    {
        var row = await config.RequireAsync("digest.chain", asOf, ct).ConfigureAwait(false);

        if (JsonNode.Parse(row.Value) is not JsonArray array)
        {
            throw new InvalidOperationException(
                $"digest.chain resolved to '{row.Value}', which is not a JSON array of link names.");
        }

        return [.. array.Select(n => n?.GetValue<string>() ?? string.Empty)];
    }
}

/// <summary>
/// A digest and which link produced it.
/// </summary>
/// <param name="Provider">Reaches `news_digest.provider`, closed to the vocabulary [D-134].</param>
/// <param name="Model">Reaches `news_digest.model_name` [D-29].</param>
/// <param name="Text">The digest, never truncated [D-137].</param>
/// <param name="PromptTokens">For C26 [D-140].</param>
/// <param name="CompletionTokens">For C26, and what the cap was asserted against.</param>
/// <param name="PassedOver">
/// One line per link the chain gave up on before this one answered, naming the link and
/// the reason. Empty on an ordinary night.
///
/// **This is the second half of D-137's record.** The digest row carries which link
/// answered; without this nothing carries which link did not, and a night that ran
/// entirely on the secondary would look the same whether the primary was down or merely
/// second in the order.
/// </param>
public sealed record ChainAnswer(
    DigestProvider Provider,
    string? Model,
    string Text,
    int? PromptTokens,
    int? CompletionTokens,
    IReadOnlyList<string> PassedOver);
