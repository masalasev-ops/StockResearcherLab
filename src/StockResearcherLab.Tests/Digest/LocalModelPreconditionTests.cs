using StockResearcherLab.Core.Digest;
using StockResearcherLab.Pipeline.Select;
using Xunit;

namespace StockResearcherLab.Tests.Digest;

/// <summary>
/// Checkpoint 5.13. The precondition that asks the operator to load the model rather
/// than reporting that it is not loaded.
///
/// **What these assert is that it can only ever delay a halt.** The precondition sits
/// ahead of C33 and returns a bool the caller does not act on, so the interesting
/// properties are about what it does not do: it does not write, it does not decide what
/// an unhealthy chain means, and above all it does not block a run that has nobody to
/// unblock it. That last one is the whole of the unattended case and it is asserted
/// twice, once by the prompt never being reached and once by the input ending.
/// </summary>
public sealed class LocalModelPreconditionTests
{
    private static readonly LocalModelPrecondition.Target Local =
        new("http://localhost:1234/v1", "qwen/qwen3.5-9b");

    private static LinkHealth Healthy(long ms = 2_491)
        => new(DigestProvider.Local, true, "qwen/qwen3.5-9b", ms, "answered");

    private static LinkHealth Cold()
        => new(DigestProvider.Local, false, null, 120_000,
            "no answer inside digest.warm_timeout_ms of 120,000 ms");

    /// <summary>
    /// A resident model is one probe and no conversation. The operator is not asked
    /// anything on the ordinary night, which is every night the machine has been in use.
    /// </summary>
    [Fact]
    public async Task AResidentModelIsOneProbeAndNoPrompt()
    {
        var console = new StubConsole();
        var probes = 0;

        var ready = await LocalModelPrecondition.EnsureAsync(
            _ => { probes++; return Task.FromResult(Healthy()); },
            Local, console, TestContext.Current.CancellationToken);

        Assert.True(ready);
        Assert.Equal(1, probes);
        Assert.Equal(0, console.Asked);
        Assert.Contains(console.Said, l => l.Contains("2,491 ms", StringComparison.Ordinal));
    }

    /// <summary>
    /// **The case the checkpoint exists for.** Cold, the operator loads the model, presses
    /// Enter, and the night continues without the command being re-run.
    /// </summary>
    [Fact]
    public async Task LoadingTheModelAtThePromptLetsTheNightContinue()
    {
        var console = new StubConsole(answers: [""]);
        var probes = 0;

        var ready = await LocalModelPrecondition.EnsureAsync(
            _ => Task.FromResult(++probes == 1 ? Cold() : Healthy()),
            Local, console, TestContext.Current.CancellationToken);

        Assert.True(ready);
        Assert.Equal(2, probes);
        Assert.Equal(1, console.Asked);
    }

    /// <summary>
    /// It keeps asking for as long as the operator keeps answering. Loading a model can
    /// take more than one try, and a prompt that gave up after one would send them back
    /// to re-run the command, which is the thing this exists to avoid.
    /// </summary>
    [Fact]
    public async Task ItAsksAgainForAsLongAsTheOperatorKeepsAnswering()
    {
        var console = new StubConsole(answers: ["", "", ""]);
        var probes = 0;

        var ready = await LocalModelPrecondition.EnsureAsync(
            _ => Task.FromResult(++probes < 4 ? Cold() : Healthy()),
            Local, console, TestContext.Current.CancellationToken);

        Assert.True(ready);
        Assert.Equal(4, probes);
        Assert.Equal(3, console.Asked);
    }

    /// <summary>
    /// **The unattended run, and the one property that must never break.** With input
    /// redirected there is nobody to press Enter, so nothing is asked and nothing waits.
    /// A CI run, a scheduled task or a piped invocation reaches the gate at the speed it
    /// always did.
    /// </summary>
    [Fact]
    public async Task WithNobodyToAskItPromptsNobodyAndReturns()
    {
        var console = new StubConsole { CanPrompt = false };
        var probes = 0;

        var ready = await LocalModelPrecondition.EnsureAsync(
            _ => { probes++; return Task.FromResult(Cold()); },
            Local, console, TestContext.Current.CancellationToken);

        Assert.False(ready);
        Assert.Equal(1, probes);
        Assert.Equal(0, console.Asked);
        Assert.Contains(console.Said, l => l.Contains("INVARIANT 15", StringComparison.Ordinal));
    }

    /// <summary>
    /// End of input is the operator done waiting, and it is the same answer as never
    /// having been able to ask: the run continues to the gate rather than hanging.
    /// </summary>
    [Fact]
    public async Task EndOfInputEndsTheWaitRatherThanTheProcess()
    {
        var console = new StubConsole(answers: []);

        var ready = await LocalModelPrecondition.EnsureAsync(
            _ => Task.FromResult(Cold()), Local, console, TestContext.Current.CancellationToken);

        Assert.False(ready);
        Assert.Equal(1, console.Asked);
        Assert.Contains(console.Said, l => l.Contains("gate halts it", StringComparison.Ordinal));
    }

    /// <summary>
    /// The prompt names the three things an operator needs and nothing they would have to
    /// look up: where to load it, what to load, and why the probe said no.
    /// </summary>
    [Fact]
    public async Task ThePromptNamesTheEndpointTheModelAndTheProbesOwnReason()
    {
        var console = new StubConsole(answers: []);

        await LocalModelPrecondition.EnsureAsync(
            _ => Task.FromResult(Cold()), Local, console, TestContext.Current.CancellationToken);

        var block = string.Join("\n", console.Said);

        Assert.Contains("http://localhost:1234/v1", block, StringComparison.Ordinal);
        Assert.Contains("qwen/qwen3.5-9b", block, StringComparison.Ordinal);
        Assert.Contains("digest.warm_timeout_ms", block, StringComparison.Ordinal);
    }

    /// <summary>
    /// **Where config names no model the prompt says so rather than naming one.** The
    /// client would fall back to the endpoint's ordinally first id, and printing that
    /// would tell the operator to load a model nobody chose [`CLAUDE.md` section 6].
    /// </summary>
    [Fact]
    public async Task WhereConfigNamesNoModelThePromptDoesNotInventOne()
    {
        var console = new StubConsole(answers: []);

        await LocalModelPrecondition.EnsureAsync(
            _ => Task.FromResult(Cold()),
            new LocalModelPrecondition.Target("http://localhost:1234/v1", null),
            console, TestContext.Current.CancellationToken);

        var block = string.Join("\n", console.Said);

        Assert.Contains("names none", block, StringComparison.Ordinal);
        Assert.DoesNotContain("qwen", block, StringComparison.Ordinal);
    }

    /// <summary>
    /// `Describe` reads the model off the same row the client sends from, and reports an
    /// absence as an absence in all three ways it can arise.
    /// </summary>
    [Theory]
    [InlineData("{\"model\": \"qwen/qwen3.5-9b\", \"reasoning_effort\": \"none\"}", "qwen/qwen3.5-9b")]
    [InlineData("{\"reasoning_effort\": \"none\"}", null)]
    [InlineData(null, null)]
    [InlineData("", null)]
    public void DescribeReadsTheModelOffRequestOptions(string? requestOptions, string? expected)
    {
        var target = LocalModelPrecondition.Describe("http://localhost:1234/v1", requestOptions);

        Assert.Equal("http://localhost:1234/v1", target.Endpoint);
        Assert.Equal(expected, target.Model);
    }

    private sealed class StubConsole(string[]? answers = null) : LocalModelPrecondition.IConsole
    {
        private readonly Queue<string> _answers = new(answers ?? []);

        public bool CanPrompt { get; init; } = true;

        public List<string> Said { get; } = [];

        public int Asked { get; private set; }

        public void Say(string line) => Said.Add(line);

        public string? AwaitLine()
        {
            Asked++;
            return _answers.Count > 0 ? _answers.Dequeue() : null;
        }
    }
}
