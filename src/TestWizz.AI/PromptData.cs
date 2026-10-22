namespace TestWizz.AI;

/// <summary>
/// Markers for the single block of repository-derived text in a prompt. Every system prompt declares the block as
/// untrusted data; <see cref="Neutralize"/> stops repository text from closing it early.
/// </summary>
public static class PromptData
{
    public const string Begin = "<<<UNTRUSTED_REPOSITORY_DATA";
    public const string End = "UNTRUSTED_REPOSITORY_DATA>>>";

    public static string Neutralize(string text) =>
        text.Replace(Begin, "[marker removed]", StringComparison.Ordinal).Replace(End, "[marker removed]", StringComparison.Ordinal);
}
