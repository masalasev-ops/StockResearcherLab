using System.Globalization;
using System.Text.Json.Nodes;
using StockResearcherLab.Core.Digest;

namespace StockResearcherLab.Pipeline.Select;

/// <summary>
/// Whether the local model is resident, and somewhere for the operator to say when it
/// is [5.13].
///
/// **A precondition on the host, not a stage.** A stage completes or it fails the run
/// [`CLAUDE.md` section 6], and one that blocks on a person is neither. So this sits
/// ahead of C33 and never inside it: by the time the stage runs the link is healthy and
/// it takes its ordinary path, and if this gives up the stage still runs and still halts
/// on INVARIANT 15's gate. Nothing about D-137's fall-through or the gate changes,
/// because neither is consulted here.
///
/// **It writes nothing and decides nothing.** A probe and a prompt. The decision about
/// what an unhealthy chain means stays where it was.
///
/// **The unattended case needs no configuration.** Whether a person is at the keyboard
/// is a property of the run rather than a value to tune, so with input redirected, which
/// is what CI, a scheduled task and any piped invocation look like, this prompts nobody
/// and returns. Nothing waits in a run that has nobody to wait for.
/// </summary>
public static class LocalModelPrecondition
{
    /// <summary>
    /// The operator's end of the conversation, behind a seam so both paths run in CI.
    /// </summary>
    public interface IConsole
    {
        /// <summary>
        /// Whether there is somebody to ask. False for redirected input, which is the
        /// unattended run, and the one case that must never block.
        /// </summary>
        bool CanPrompt { get; }

        /// <summary>One line to the operator.</summary>
        void Say(string line);

        /// <summary>
        /// Blocks until the operator answers. Null is end of input, meaning they are
        /// done waiting, and is treated the same as never having been able to ask.
        /// </summary>
        string? AwaitLine();
    }

    /// <summary>What the operator is being asked to load, and where.</summary>
    /// <param name="Endpoint">From <c>local_model_config</c>, so the address it will be loaded at.</param>
    /// <param name="Model">
    /// What <c>request_options</c> names, or null where it names none. Null is config
    /// naming no model rather than a model called nothing [`CLAUDE.md` section 6].
    /// </param>
    public sealed record Target(string Endpoint, string? Model);

    /// <summary>
    /// What to tell the operator to load, read off the same row the client sends from.
    ///
    /// **The model is whatever `request_options` names and nothing is inferred.** Where
    /// it names none, the client falls back to the endpoint's ordinally first id, and
    /// naming that here would tell the operator to load a model config did not choose.
    /// Null says config named none, which is the true answer to what they should load.
    /// </summary>
    public static Target Describe(string endpoint, string? requestOptions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        if (string.IsNullOrWhiteSpace(requestOptions))
        {
            return new Target(endpoint, null);
        }

        return new Target(
            endpoint,
            JsonNode.Parse(requestOptions) is JsonObject options
                ? options["model"]?.GetValue<string>()
                : null);
    }

    /// <summary>
    /// Probes once, and where that fails and somebody is there, asks and probes again
    /// for as long as they keep answering.
    /// </summary>
    /// <returns>Whether the link answered. False leaves the caller to run the stage anyway.</returns>
    public static async Task<bool> EnsureAsync(
        Func<CancellationToken, Task<LinkHealth>> probe,
        Target target,
        IConsole console,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(console);

        var health = await probe(ct).ConfigureAwait(false);

        if (health.Healthy)
        {
            console.Say(Answered(health));
            return true;
        }

        if (!console.CanPrompt)
        {
            console.Say($"  The local model is not answering: {health.Detail}");
            console.Say("  Nothing is attached to ask, so nothing waits here. The run continues and the");
            console.Say("  digest gate halts it [INVARIANT 15].");
            return false;
        }

        console.Say(string.Empty);
        console.Say("  The local model is not answering.");
        console.Say($"    endpoint  {target.Endpoint}");
        console.Say($"    model     {target.Model ?? "(local_model_config.request_options names none)"}");
        console.Say($"    reason    {health.Detail}");
        console.Say(string.Empty);
        console.Say($"  {Instruction(target)}");
        console.Say("  Ctrl-C to abandon the night. Nothing has been written.");

        while (console.AwaitLine() is not null)
        {
            health = await probe(ct).ConfigureAwait(false);

            if (health.Healthy)
            {
                console.Say(Answered(health));
                return true;
            }

            console.Say($"  still not answering: {health.Detail}");
            console.Say($"  {Instruction(target)}");
        }

        console.Say("  Waiting ended without an answer. The run continues and the digest gate halts it.");
        return false;
    }

    private static string Instruction(Target target)
        => target.Model is null
            ? "Load a model at that endpoint, then press Enter to probe again."
            : $"Load {target.Model} at that endpoint, then press Enter to probe again.";

    private static string Answered(LinkHealth health)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"  {health.LoadedModel ?? "the local link"} answered in {health.LatencyMs:N0} ms. Continuing.");
}
