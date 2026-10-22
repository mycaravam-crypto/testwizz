namespace TestWizz.Cli;

public sealed record OptionDefinition(string Name, string Description, string? ValueName = null)
{
    public bool TakesValue => ValueName is not null;
}

public sealed record CommandDefinition(
    string Name,
    string Summary,
    string Usage,
    IReadOnlyList<OptionDefinition> Options,
    int MaxArguments,
    Func<CommandContext, int> Handler);

public sealed record CommandContext(
    CommandDefinition Command,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string?> Options,
    CliOutput Output,
    string WorkingDirectory)
{
    public bool Has(string option) => Options.ContainsKey(option);

    /// <summary>Resolves the optional path argument against the working directory; defaults to the working directory.</summary>
    public string PathArgument() =>
        Path.GetFullPath(Arguments.Count > 0 ? Arguments[0] : ".", WorkingDirectory);
}

public sealed record ParsedCommandLine(
    string? CommandName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string?> Options,
    OutputFormat Format,
    bool Help,
    bool Version);

/// <summary>
/// Parses <c>testwizz [command] [arguments] [options]</c>. Global options: <c>--format text|json</c>, <c>-h|--help</c>,
/// <c>--version</c>. Unknown options and surplus arguments are errors, never ignored.
/// </summary>
public static class CommandLineParser
{
    public static readonly OptionDefinition FormatOption = new("--format", "Output format: text (default) or json.", "text|json");

    /// <summary>Finds a valid <c>--format</c> even when the rest of the command line is invalid, so errors use the requested format.</summary>
    public static OutputFormat DetectFormat(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var value = args[i] == FormatOption.Name && i + 1 < args.Count ? args[i + 1]
                : args[i].StartsWith(FormatOption.Name + "=", StringComparison.Ordinal) ? args[i][(FormatOption.Name.Length + 1)..]
                : null;
            if (value is not null && TryParseFormat(value, out var format))
            {
                return format;
            }
        }

        return OutputFormat.Text;
    }

    public static ParsedCommandLine Parse(IReadOnlyList<string> args, IReadOnlyDictionary<string, CommandDefinition> commands)
    {
        string? commandName = null;
        CommandDefinition? command = null;
        var arguments = new List<string>();
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        var format = OutputFormat.Text;
        bool help = false, version = false, endOfOptions = false;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (!endOfOptions && arg == "--")
            {
                endOfOptions = true;
                continue;
            }

            if (endOfOptions || !arg.StartsWith('-') || arg == "-")
            {
                if (commandName is null)
                {
                    commandName = arg;
                    if (!commands.TryGetValue(arg, out command))
                    {
                        throw Usage($"Unknown command '{arg}'.");
                    }
                }
                else
                {
                    arguments.Add(arg);
                }

                continue;
            }

            var (name, inlineValue) = SplitOption(arg);
            switch (name)
            {
                case "-h" or "--help" when inlineValue is null:
                    help = true;
                    continue;
                case "--version" when inlineValue is null:
                    version = true;
                    continue;
                case "--format":
                    var formatText = inlineValue ?? NextValue(args, ref i, name);
                    if (!TryParseFormat(formatText, out format))
                    {
                        throw Usage($"Invalid value '{formatText}' for --format. Use text or json.");
                    }

                    continue;
            }

            var definition = command?.Options.FirstOrDefault(o => o.Name == name)
                ?? throw Usage(command is null
                    ? $"Unknown option '{name}'."
                    : $"Unknown option '{name}' for '{command.Name}'.");
            if (options.ContainsKey(name))
            {
                throw Usage($"Option '{name}' was given more than once.");
            }

            if (definition.TakesValue)
            {
                options[name] = inlineValue ?? NextValue(args, ref i, name);
            }
            else if (inlineValue is not null)
            {
                throw Usage($"Option '{name}' does not take a value.");
            }
            else
            {
                options[name] = null;
            }
        }

        if (command is not null && arguments.Count > command.MaxArguments)
        {
            throw Usage($"Too many arguments for '{command.Name}'. Usage: {command.Usage}");
        }

        return new ParsedCommandLine(commandName, arguments, options, format, help, version);
    }

    private static (string Name, string? Value) SplitOption(string arg)
    {
        var equals = arg.IndexOf('=', StringComparison.Ordinal);
        return arg.StartsWith("--", StringComparison.Ordinal) && equals > 0
            ? (arg[..equals], arg[(equals + 1)..])
            : (arg, null);
    }

    private static string NextValue(IReadOnlyList<string> args, ref int i, string name)
    {
        if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw Usage($"Option '{name}' requires a value.");
        }

        return args[++i];
    }

    private static bool TryParseFormat(string value, out OutputFormat format)
    {
        format = value switch
        {
            "text" => OutputFormat.Text,
            "json" => OutputFormat.Json,
            _ => (OutputFormat)(-1),
        };
        return format is OutputFormat.Text or OutputFormat.Json;
    }

    private static CliException Usage(string message) =>
        new("usage", $"{message} Run 'testwizz help' for usage.");
}
