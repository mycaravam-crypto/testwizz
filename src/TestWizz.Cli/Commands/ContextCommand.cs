using System.Globalization;
using System.Text;
using TestWizz.Core.Context;
using TestWizz.DotNet;

namespace TestWizz.Cli.Commands;

public static class ContextCommand
{
    private const string ForOption = "--for";
    private const string MaxCharsOption = "--max-chars";

    public static CommandDefinition Definition { get; } = new(
        "context",
        "Show what TestWizz knows about a C# type or method (testwizz.context/v1).",
        "testwizz context [path] --for <target> [--max-chars <n>]",
        Options:
        [
            new(ForOption, "Type or method, e.g. OrderService, OrderService.PlaceOrderAsync, Calculator.Percentage(decimal, int).", "target"),
            new(MaxCharsOption, $"Maximum source excerpt length (default {RoslynContextProvider.DefaultMaxExcerptChars}).", "n"),
        ],
        MaxArguments: 1,
        Handler: Run);

    public static OptionDefinition[] TargetOptions => [.. Definition.Options];

    private static int Run(CommandContext context)
    {
        var document = Load(context, Definition.Usage);
        context.Output.Result("context", ContextJson.ToJson(document), ToText(document));
        return ExitCodes.Success;
    }

    /// <summary>Resolves <c>--for</c> in the path argument's repository; shared by commands that start from a context.</summary>
    public static ContextDocument Load(CommandContext context, string usage)
    {
        if (!context.Options.TryGetValue(ForOption, out var target) || string.IsNullOrWhiteSpace(target))
        {
            throw new CliException("usage", $"Missing {ForOption} <target>. Usage: {usage}");
        }

        var maxChars = RoslynContextProvider.DefaultMaxExcerptChars;
        if (context.Options.TryGetValue(MaxCharsOption, out var maxText)
            && (!int.TryParse(maxText, NumberStyles.None, CultureInfo.InvariantCulture, out maxChars) || maxChars is < 100 or > 1_000_000))
        {
            throw new CliException("usage", $"{MaxCharsOption} must be a whole number between 100 and 1000000.");
        }

        var root = context.PathArgument();
        if (!Directory.Exists(root))
        {
            throw new CliException("path.not_found", $"Directory not found: {root}");
        }

        try
        {
            return RoslynContextProvider.Load(root).GetContext(target, CliApp.Version, maxChars);
        }
        catch (ContextException ex)
        {
            throw ToCliException(ex);
        }
    }

    public static CliException ToCliException(ContextException ex)
    {
        var hint = ex.Candidates.Count > 0 ? " Candidates are listed below." : "";
        return new CliException(ex.Code, ex.Message + hint, details: [.. ex.Candidates.Select(c => ("candidate", c))]);
    }

    private static string ToText(ContextDocument d)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{d.Target.Kind} {d.Target.Id}\n");
        text.Append(CultureInfo.InvariantCulture, $"  {d.Target.Signature}\n");
        text.Append(CultureInfo.InvariantCulture, $"  {d.Source.File}:{d.Source.StartLine}-{d.Source.EndLine}{(d.Project is null ? "" : $" ({d.Project})")}\n");
        if (d.Revision is not null)
        {
            text.Append(CultureInfo.InvariantCulture, $"  revision {d.Revision}\n");
        }

        Section(text, "Members", d.Members.Select(m => m.Signature));
        Section(text, "Constructors", d.Constructors.Select(c => c.Signature));
        Section(text, "Dependencies", d.Dependencies.Select(x => $"[{x.Role}] {x.Type}{(x.ParameterName.Length > 0 ? $" {x.ParameterName}" : "")}{(x.DeclaredIn is { } l ? $" ({l.File}:{l.Line})" : " (external)")}"));
        Section(text, "Throws", d.Throws.Select(t => $"{t.Type} (line {t.Line})"));
        Section(text, "Related tests (name references, not coverage)", d.RelatedTests.Select(t => $"{t.Name} ({t.File}:{t.Line})"));
        Section(text, "Unavailable", d.Unavailable.Select(u => $"{u.Field}: {u.Reason}"));
        Section(text, "Diagnostics", d.Diagnostics.Select(x => $"{x.Code}: {x.Message}"));
        text.Append("\nUse --format json for the full context including the source excerpt.\n");
        return text.ToString();
    }

    private static void Section(StringBuilder text, string title, IEnumerable<string> lines)
    {
        var items = lines.ToList();
        if (items.Count == 0)
        {
            return;
        }

        text.Append(CultureInfo.InvariantCulture, $"\n{title}:\n");
        foreach (var item in items)
        {
            text.Append(CultureInfo.InvariantCulture, $"  {item}\n");
        }
    }
}
