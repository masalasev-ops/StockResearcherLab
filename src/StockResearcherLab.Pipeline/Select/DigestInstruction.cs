using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace StockResearcherLab.Pipeline.Select;

/// <summary>
/// The instruction every link is sent, read out of `prompts/digest-instruction.md`.
///
/// **The file is product and is read at execution time** [`CLAUDE.md` §14], so it is not
/// a literal here. It is also prose written for a person as well as for a model: a header,
/// the instruction, and a section explaining why the instruction has the shape it has.
/// Sending the whole file would send the explanation, which is not an instruction and
/// which discusses what the model is likely to get wrong.
///
/// **So the boundary is stated rather than assumed.** The instruction is the section
/// headed `## The instruction`, up to the next horizontal rule. That rule is in the file
/// today and a test asserts the extraction finds the escape hatch and does not find the
/// explanation, so an edit that removed the rule fails rather than silently widening what
/// is sent.
/// </summary>
public static class DigestInstruction
{
    public const string Heading = "## The instruction";

    /// <summary>
    /// The instruction text and the hash of it.
    /// </summary>
    /// <param name="Text">What is sent, identical for every link [D-27].</param>
    /// <param name="Sha256">
    /// The version, in the only form this file has one. `prompts/` is versioned and edited
    /// deliberately, and a change to this text splits history into halves that cannot be
    /// pooled [`CLAUDE.md` §12], so the digest panel shows which text produced a row rather
    /// than the text in the file today [5.9].
    /// </param>
    public sealed record Instruction(string Text, string Sha256);

    public static Instruction Read(string path) => Parse(File.ReadAllText(path));

    public static Instruction Parse(string file)
    {
        // Line endings are normalised before anything else. The same file checked out on
        // two machines must produce the same hash, and `.gitattributes` says `eol=lf` for
        // this path while a working tree is a working tree [`CLAUDE.md` §6].
        var normalised = file.Replace("\r\n", "\n", StringComparison.Ordinal);

        var start = normalised.IndexOf(Heading, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException(
                $"The digest instruction file carries no '{Heading}' heading, so what is sent to a " +
                "link is not identifiable. The file is read at execution time and is product " +
                "[CLAUDE.md section 14], so this is a change to it rather than a bug here.");
        }

        var body = normalised[(start + Heading.Length)..];
        var end = body.IndexOf("\n---", StringComparison.Ordinal);

        if (end < 0)
        {
            throw new InvalidOperationException(
                "The digest instruction section is not closed by a horizontal rule, so its end " +
                "cannot be found and the explanation below it would be sent as instruction.");
        }

        var text = body[..end].Trim();

        if (text.Length == 0)
        {
            throw new InvalidOperationException("The digest instruction section is empty.");
        }

        return new Instruction(text, Hash(text));
    }

    private static string Hash(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))
            .ToLower(CultureInfo.InvariantCulture);
}
