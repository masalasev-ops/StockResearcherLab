using StockResearcherLab.Core;
using StockResearcherLab.Core.Digest;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;
using StockResearcherLab.Data.Eodhd;
using StockResearcherLab.Pipeline.Compute;
using StockResearcherLab.Pipeline.Ingest;
using StockResearcherLab.Pipeline.Select;

namespace StockResearcherLab.Pipeline;

/// <summary>
/// Builds the one registry the whole system uses. There is a single place that
/// says which components exist, so the conformance test at 0.4 and the runner
/// read the same declaration rather than two lists that can drift apart.
/// </summary>
public static class PipelineComposition
{
    /// <param name="connectionString">Postgres.</param>
    /// <param name="apiToken">
    /// The data provider token. Null builds a registry whose provider-backed stages
    /// are present and cannot run, which is what the conformance tests want: they
    /// assert over declarations rather than execute anything, and requiring a
    /// secret to enumerate the registry would make them unrunnable in CI.
    /// </param>
    /// <param name="clock">Injected; nothing here reads system time [INVARIANT 11].</param>
    public static StageRegistry BuildRegistry(
        string connectionString, string? apiToken = null, IClock? clock = null)
        => BuildRegistry(
            connectionString,
            string.IsNullOrWhiteSpace(apiToken)
                ? null
                : new EodhdClient(EodhdClient.CreateHttpClient(), apiToken, clock ?? new SystemClock()));

    /// <summary>
    /// The same registry over a client the caller already holds.
    ///
    /// **This exists because the rate limiter is per client and the provider's limit is
    /// not** [`EodhdRateLimiter`, 1,000 requests a minute]. A backfill needs both the
    /// registry and `UnitAllowance`, which reads `/api/user` before every unit of work,
    /// so a driver that let this method build its own client would be running two
    /// independent sliding windows of 1,000 against one limit of 1,000 and would meet it
    /// as a 429 in the middle of a sweep, which is a failed stage after the units before
    /// it are spent. One client, one window.
    /// </summary>
    /// <param name="eodhd">
    /// Null builds a registry whose provider-backed stages are absent, which is the
    /// no-token case above.
    /// </param>
    public static StageRegistry BuildRegistry(string connectionString, EodhdClient? eodhd)
    {
        var owners = new List<IWriteOwner>
        {
            // Not a stage. Sits outside the layers and owns run_log.
            new RunLog(connectionString),

            // Layer 2. Every compute stage derives from tables the ingest wrote and
            // calls no provider, so all of them are registered whether or not a token
            // is present.
            new FlowEngine(),
            new IndicatorEngine(),
            new ValuationEngine(),
            new SentimentEngine(),
            new MarketContextEngine(),
            new PercentileEngine(),

            // Layer 3. Selection derives from the percentile store and calls no
            // provider either, so it registers on the same terms as layer 2.
            //
            // C12 is in this layer too. Section 04 puts it in the Select band at
            // 18:20, before C13 at 18:25, and it reads gate_result in nothing after:
            // C13 does not read that table and must not [D-117].
            new GateEngine(),
            new ScreenEngine(),
            new CandidateAllocator(),

            // Outside the layers with RunLog, per section 02, and registered here
            // because it reads stores the pipeline wrote and calls nothing.
            new ConcentrationMonitor(),

            // C33. Layer 3, and registered unconditionally because it calls no data
            // provider: its links are the digest chain rather than EODHD [5.8].
            //
            // **The chain is built per run and not per registry**, holding which links
            // have failed [D-137], so a re-used stage cannot carry one night's failures
            // into the next.
            //
            // **It reaches `local_model_config` through the declaration C32 owns.** That
            // is why C33's read set is `candidate_set` and `headline` alone: the chain is
            // a collaborator rather than a component, it has no §03 row, and giving C33 a
            // declaration on that table would say it reads something it does not
            // [INVARIANT 7, D-109].
            new NewsDigester(
                (context, ct) => DigestChain.BuildAsync(
                    new StageData(connectionString, LocalModelClient.Access()),
                    context.Config,
                    context.Date,
                    new Dictionary<DigestProvider, IDigestLink>
                    {
                        [DigestProvider.Local] = new LocalModelClient(connectionString, DigestHttp),
                    },
                    ct),
                () => DigestInstruction.Read(DigestInstructionPath)),
        };

        if (eodhd is not null)
        {
            owners.Add(new PriceIngestor(eodhd));
            owners.Add(new FreshnessGuard(eodhd));
            owners.Add(new FundamentalsIngestor(eodhd));
            owners.Add(new UniverseBuilder(eodhd));
            owners.Add(new SentimentIngestor(eodhd));
            owners.Add(new FlowIngestor(eodhd));
            owners.Add(new EventsIngestor(eodhd));

            // C29. Provider-backed like the six above, and it is the first stage that
            // reads a table the select layer wrote rather than a source [5.4].
            owners.Add(new HeadlineIngestor(eodhd));
        }

        return new StageRegistry(owners);
    }

    /// <summary>
    /// One `HttpClient` for every digest call, which is what the type is designed for and
    /// what a per-call one exhausts sockets doing.
    ///
    /// **No timeout of its own.** `digest.health_timeout_ms` bounds a probe and a digest
    /// takes as long as it takes, so a client-wide timeout would be a second bound nothing
    /// documents, applied to the wrong one of the two [5.5].
    /// </summary>
    private static readonly HttpClient DigestHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// `prompts/digest-instruction.md`, found by walking up from the binary until a
    /// directory holding `prompts/` appears.
    ///
    /// **The runtime prompts are product read at execution time and are deliberately not
    /// copied to the output** [`CLAUDE.md` §14]. A copied prompt is a prompt the operator
    /// can edit without the running system seeing the edit, which is the failure this
    /// corpus keeps naming: nothing errors and the evidence quietly changes.
    ///
    /// The walk rather than a relative path, because the Worker, the tests and a scratch
    /// host all sit at different depths, and a `..\..\..` that is right for one is wrong
    /// for the others in a way that only shows up when it runs.
    /// </summary>
    public static string DigestInstructionPath { get; } = FindPrompt("digest-instruction.md");

    private static string FindPrompt(string file)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "prompts", file);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Returned rather than thrown, so building the registry still touches nothing and
        // the failure lands when the stage runs and says what it could not find.
        return Path.Combine(AppContext.BaseDirectory, "prompts", file);
    }

    /// <summary>
    /// Every stage this phase registers, whether or not a token is present, so the
    /// conformance tests see the declarations rather than only the runnable subset.
    /// A component the registry cannot see is a component INVARIANT 10 is not
    /// enforced against.
    /// </summary>
    public static IReadOnlyList<IWriteOwner> AllOwnersForConformance(string connectionString)
        => BuildRegistry(connectionString, apiToken: "not-a-real-token-registry-only").Owners;

    /// <summary>
    /// Every read owner this project hosts that owns no write, which is C32 alone
    /// [D-109, D-136].
    ///
    /// **The counterpart of `ApiComposition.AllReadOwnersForConformance` and it exists
    /// for the same reason.** A reader nothing enumerates is a reader D-74 is not
    /// enforced against, and the registry above holds write owners only, so a component
    /// that writes nothing cannot be in it. C36 needed one of these in the Api; C32 is
    /// the first in the Pipeline.
    ///
    /// **It takes a connection string and opens nothing**, and the `HttpClient` it hands
    /// C32 makes no request while a declaration is being read. Enumerating readers must
    /// be as free as building the registry.
    /// </summary>
    public static IReadOnlyList<IReadOwner> AllReadOwnersForConformance(string connectionString)
        => [new LocalModelClient(connectionString, new HttpClient())];
}
