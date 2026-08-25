using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using StockResearcherLab.Core.Config;
using StockResearcherLab.Core.Digest;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;

namespace StockResearcherLab.Pipeline;

/// <summary>
/// C32. The OpenAI-compatible client for the local inference server: health, the loaded
/// model name and latency [D-136, D-144].
///
/// **A reader that owns no write, which is C36's shape** [D-109]. It declares
/// `local_model_config` and an empty write set, so any write at all throws through
/// <see cref="DeclaredAccess"/> before a connection opens. That is not decoration here:
/// `local_model_config` carries `last_health_check` and `last_loaded_model`, and this is
/// the component a later session would naturally have fill them. `SCHEMA.md` gives that
/// table to the UI [D-51], and §03's Writes cell for C32 says nothing, via C27. The
/// empty write set is what makes "emits through the run log rather than writing it"
/// structural rather than remembered [INVARIANT 10].
///
/// **It sits at the project root rather than in a layer folder**, with C28, because §01
/// puts it outside the seven layers.
///
/// **Health is a probe that came back with something, not a status code** [D-144]. The
/// whole of that decision is here: a reasoning model answers 200, with a normal usage
/// block and a `finish_reason`, and zero characters of content. A check reading the
/// status reports healthy on the one link that cannot produce a digest, the chain falls
/// through to the paid link for every candidate, and it does that every night with the
/// local server up and answering in under two seconds.
/// </summary>
public sealed class LocalModelClient : IDigestLink, IReadOwner
{
    /// <summary>
    /// The probe. Fixed, short, and deliberately not the digest instruction, so a health
    /// check costs nothing and cannot be mistaken for a digest in a provider's log.
    ///
    /// **It asks for prose rather than for a token.** A model asked to echo one word can
    /// satisfy that from a template; this asks for a sentence, which is the shape a
    /// digest is, and it is the shape a reasoning model fails to produce when it spends
    /// its budget before answering.
    /// </summary>
    public const string ProbeText = "In one short sentence, say that you are ready to summarise news articles.";

    /// <summary>
    /// The probe's own cap, which is not <c>digest.max_output_tokens</c> and not a config
    /// key. It bounds a fixed internal request that no design document has a length for,
    /// and tying it to the digest's length would make a change to the digest silently
    /// change what counts as healthy.
    /// </summary>
    public const int ProbeMaxTokens = 32;

    public const string ComponentName = "LocalModelClient";

    private static readonly string[] Tables = ["local_model_config"];

    private readonly IStageData _data;
    private readonly IConfigStore _config;
    private readonly HttpClient _http;

    public LocalModelClient(string connectionString, HttpClient http)
        : this(new StageData(connectionString, Access()), new ConfigStore(connectionString), http)
    {
    }

    /// <summary>
    /// The seam the tests use. It takes the three routes rather than a connection string,
    /// so the empty write set stays this class's own statement rather than something a
    /// caller supplies, and so a fabricated response can be put in front of it.
    /// </summary>
    public LocalModelClient(IStageData data, IConfigStore config, HttpClient http)
    {
        _data = data;
        _config = config;
        _http = http;
    }

    /// <summary>
    /// The declared access the production route runs behind: this read set, and an empty
    /// write set. Public so a test asserts over the object the constructor uses rather
    /// than over a reconstruction of it, which is C36's reason exactly.
    /// </summary>
    public static DeclaredAccess Access() => new(ComponentName, Tables, []);

    public string Name => ComponentName;

    public IReadOnlyList<string> ReadSet => Tables;

    public DigestProvider Provider => DigestProvider.Local;

    /// <summary>
    /// Whether the local link can answer now, what answered, and how long it took.
    ///
    /// **Three ways to be unhealthy and the detail line separates them**, because a
    /// server that is down and a server that is up and returning nothing call for
    /// opposite responses and "unhealthy" alone cannot tell an operator which happened.
    /// </summary>
    public async Task<LinkHealth> HealthAsync(CancellationToken ct = default)
    {
        var timeoutMs = await TimeoutAsync(ct).ConfigureAwait(false);
        var link = await LinkAsync(ct).ConfigureAwait(false);

        var stopwatch = Stopwatch.StartNew();

        if (link is null)
        {
            return Unhealthy(
                stopwatch,
                "no enabled local_model_config row at provider_order 1, so there is no address to probe");
        }

        // The whole probe, both calls, inside one bound. `digest.health_timeout_ms` is
        // how long the chain will wait for an answer, not how long one request may take,
        // and splitting it in two would let a link consume twice its budget.
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bound.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

        try
        {
            var model = await LoadedModelAsync(link.Endpoint, bound.Token).ConfigureAwait(false);
            if (model is null)
            {
                return Unhealthy(stopwatch, "the endpoint lists no model, so nothing is loaded to answer with");
            }

            var (content, answered) = await CompleteAsync(
                link.Endpoint, model, link.RequestOptions, bound.Token).ConfigureAwait(false);

            // **The assertion D-144 exists for.** Not a status code, not a
            // `finish_reason`, and not a usage block: characters of content.
            if (string.IsNullOrWhiteSpace(content))
            {
                return new LinkHealth(
                    Provider, false, answered, stopwatch.ElapsedMilliseconds,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{answered ?? model} answered with no content. The request shape is " +
                        $"local_model_config.request_options and this is what an absent " +
                        $"reasoning_effort produces [D-144]"));
            }

            return new LinkHealth(
                Provider, true, answered ?? model, stopwatch.ElapsedMilliseconds,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{answered ?? model} answered in {stopwatch.ElapsedMilliseconds:N0} ms " +
                    $"with {content!.Trim().Length:N0} character(s)"));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Unhealthy(
                stopwatch,
                string.Create(CultureInfo.InvariantCulture, $"no answer inside digest.health_timeout_ms of {timeoutMs:N0} ms"));
        }
        catch (HttpRequestException ex)
        {
            // The server being down is a transport failure and reads differently from a
            // server that is up and silent. Both are unhealthy; only one of them is
            // fixed by starting something.
            return Unhealthy(stopwatch, $"the endpoint could not be reached: {ex.Message}");
        }
        catch (JsonException ex)
        {
            return Unhealthy(stopwatch, $"the endpoint answered with something that is not the expected JSON: {ex.Message}");
        }
    }

    private LinkHealth Unhealthy(Stopwatch stopwatch, string detail)
        => new(Provider, false, null, stopwatch.ElapsedMilliseconds, detail);

    // ---------------------------------------------------------------- digest ---

    /// <summary>
    /// One digest call, returning what came back and judging none of it [D-137].
    ///
    /// **No timeout of its own.** `digest.health_timeout_ms` bounds a probe, which is a
    /// fixed 32-token request; a digest is up to `digest.max_output_tokens` over several
    /// thousand tokens of article and takes as long as it takes. Bounding it with the
    /// probe's number would mark a working link malformed on its slowest candidate, and
    /// D-137's fall-through would then run on the wrong evidence.
    ///
    /// **A transport failure throws rather than returning an empty answer**, because the
    /// chain has to tell them apart: D-137 counts both as malformed, and the run log line
    /// that explains a fall-through is useless if it cannot say whether the link was
    /// unreachable or merely silent.
    /// </summary>
    public async Task<DigestAnswer> DigestAsync(DigestRequest request, CancellationToken ct = default)
    {
        var link = await LinkAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "No enabled local_model_config row at provider_order 1, so the local link has no " +
                "address. The chain selects links from that table [D-136] and should not have " +
                "reached this one.");

        var model = await LoadedModelAsync(link.Endpoint, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"The endpoint at {link.Endpoint} lists no model, so nothing is loaded to answer with.");

        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = request.MaxOutputTokens,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = request.Instruction },
                new JsonObject { ["role"] = "user", ["content"] = request.Articles }),
        };

        Apply(body, link.RequestOptions);

        using var content = new StringContent(
            body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");

        using var response = await _http
            .PostAsync(Url(link.Endpoint, "chat/completions"), content, ct).ConfigureAwait(false);

        var answer = await response.Content.ReadFromJsonAsync<JsonNode>(ct).ConfigureAwait(false);

        var message = answer?["choices"] is JsonArray choices && choices.Count > 0
            ? (choices[0] as JsonObject)?["message"] as JsonObject
            : null;

        var usage = answer?["usage"] as JsonObject;

        return new DigestAnswer(
            message?["content"]?.GetValue<string>(),
            answer?["model"]?.GetValue<string>() ?? model,
            (int?)usage?["prompt_tokens"],
            (int?)usage?["completion_tokens"]);
    }

    // ---------------------------------------------------------------- reading ---

    /// <summary>The chain row this link is, or null where it is absent or disabled.</summary>
    public sealed record Link(int Order, string Endpoint, string? RequestOptions);

    /// <summary>
    /// `provider_order` 1 and `enabled`, which is D-136's rule applied to one row rather
    /// than to the list. The list itself is 5.7's, and this reads the single row so that
    /// the link is exercisable alone before a chain exists to select it.
    /// </summary>
    public async Task<Link?> LinkAsync(CancellationToken ct = default)
    {
        var rows = await _data.ReadAsync(
            "local_model_config",
            "SELECT provider_order, endpoint, request_options FROM local_model_config " +
            "WHERE enabled AND provider_order = 1 ORDER BY provider_order;",
            ct).ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return null;
        }

        var row = rows[0];
        return new Link(
            Convert.ToInt32(row[0], CultureInfo.InvariantCulture),
            (string)row[1]!,
            row[2] as string);
    }

    private async Task<long> TimeoutAsync(CancellationToken ct)
    {
        // Resolved at the frontier rather than as of a simulated date. A health check is
        // about the machine now, so there is no simulated date it could be as of, which
        // is the one shape INVARIANT 13 does not reach.
        var row = await _config.RequireAsync("digest.health_timeout_ms", DateOnly.MaxValue, ct).ConfigureAwait(false);

        return long.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
            ? ms
            : throw new InvalidOperationException(
                $"digest.health_timeout_ms resolved to '{row.Value}', which is not a whole number.");
    }

    // --------------------------------------------------------------- probing ---

    /// <summary>
    /// What the endpoint says it has, first by ordinal id.
    ///
    /// **"Loaded" is the endpoint's word and this server does not mean by it what §07
    /// assumes** [5.5 finding]. §07's table names LM Studio, which serves the one model
    /// it has loaded; the machine runs Ollama, which lists every model pulled. Today it
    /// lists exactly one, so the two readings agree and nothing is ambiguous. The
    /// tie-break is ordinal and stated so that it stays deterministic if a second is ever
    /// pulled, and the name recorded against a digest is the one the completion says
    /// answered rather than this one, which is the authoritative half either way.
    /// </summary>
    private async Task<string?> LoadedModelAsync(string endpoint, CancellationToken ct)
    {
        using var response = await _http.GetAsync(Url(endpoint, "models"), ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonNode>(ct).ConfigureAwait(false);

        if (body?["data"] is not JsonArray data)
        {
            return null;
        }

        return data
            .Select(m => (m as JsonObject)?["id"]?.GetValue<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>
    /// The probe call. Returns the content and the model the endpoint says answered,
    /// either of which may be null or empty, because deciding what an empty answer means
    /// is the caller's and not this method's.
    /// </summary>
    private async Task<(string? Content, string? Model)> CompleteAsync(
        string endpoint, string model, string? requestOptions, CancellationToken ct)
    {
        var request = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = ProbeMaxTokens,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "user", ["content"] = ProbeText }),
        };

        Apply(request, requestOptions);

        using var content = new StringContent(
            request.ToJsonString(), System.Text.Encoding.UTF8, "application/json");

        using var response = await _http
            .PostAsync(Url(endpoint, "chat/completions"), content, ct).ConfigureAwait(false);

        // Deliberately not `EnsureSuccessStatusCode`. A non-2xx with a body is still an
        // answer with no content, which is the state this check is about, and reading it
        // as a transport failure would put it in the wrong one of the three lines above.
        var body = await response.Content.ReadFromJsonAsync<JsonNode>(ct).ConfigureAwait(false);

        var message = body?["choices"] is JsonArray choices && choices.Count > 0
            ? (choices[0] as JsonObject)?["message"] as JsonObject
            : null;

        return (
            message?["content"]?.GetValue<string>(),
            body?["model"]?.GetValue<string>());
    }

    /// <summary>
    /// D-144's column, merged into the request.
    ///
    /// **Every property is copied and none is interpreted here.** The keys are a
    /// provider's rather than this system's, which is why the column carries no CHECK on
    /// its shape and why this method knows nothing about `reasoning_effort` by name. A
    /// client that special-cased one key would be the literal D-144 refuses.
    /// </summary>
    private static void Apply(JsonObject request, string? requestOptions)
    {
        if (string.IsNullOrWhiteSpace(requestOptions))
        {
            return;
        }

        if (JsonNode.Parse(requestOptions) is not JsonObject options)
        {
            throw new InvalidOperationException(
                "local_model_config.request_options is not a JSON object, so there is nothing to " +
                "merge into the request. Null is a link that needs no provider-specific parameter " +
                "[D-144]; anything else here is a shape nobody chose.");
        }

        // Ordinal by key so two runs build the same request. A JSON object's property
        // order is its own, and a request body that differs between runs is the shape
        // INVARIANT 6 is about one endpoint over [`CLAUDE.md` §6].
        foreach (var (key, value) in options.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            request[key] = value?.DeepClone();
        }
    }

    /// <summary>
    /// The endpoint is stored with its version segment, `http://host:port/v1`, so a path
    /// is appended rather than composed. One trailing slash either way is tolerated
    /// because the column is operator-editable through the UI at phase 9.
    /// </summary>
    private static string Url(string endpoint, string path)
        => endpoint.TrimEnd('/') + "/" + path;
}
