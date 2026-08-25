using StockResearcherLab.Pipeline.Select;

namespace StockResearcherLab.Worker;

/// <summary>
/// The operator's end of <see cref="LocalModelPrecondition"/>, wired to the actual
/// terminal.
///
/// **`CanPrompt` is the whole unattended story** [5.13]. Redirected input is what CI, a
/// scheduled task and any piped invocation look like, and in every one of them there is
/// nobody to press Enter. Asking that question here rather than through a config key
/// keeps a value out of `CONFIG_REFERENCE.md` that nobody would ever want to set to the
/// wrong answer: whether a person is at the keyboard is a fact about the run.
/// </summary>
internal sealed class TerminalConsole : LocalModelPrecondition.IConsole
{
    public bool CanPrompt => !Console.IsInputRedirected;

    public void Say(string line) => Console.WriteLine(line);

    public string? AwaitLine()
    {
        Console.Write("> ");
        return Console.ReadLine();
    }
}
