using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace TestWizz.Cli.Commands;

public static class HelpCommand
{
    public static CommandDefinition Definition { get; } = new(
        "help",
        "Show available commands or help for one command.",
        "testwizz help [command]",
        Options: [],
        MaxArguments: 1,
        Handler: context => Show(context.Output, context.Arguments));

    private static readonly OptionDefinition[] GlobalOptions =
    [
        CommandLineParser.FormatOption,
        new("--help", "Show help (also -h)."),
        new("--version", "Show the TestWizz version."),
    ];

    public static int Show(CliOutput output, IReadOnlyList<string> topic)
    {
        if (topic.Count == 0)
        {
            output.Result("help", Overview(), OverviewText());
            return ExitCodes.Success;
        }

        if (!CliApp.CommandsByName.TryGetValue(topic[0], out var command))
        {
            throw new CliException("usage", $"Unknown command '{topic[0]}'. Run 'testwizz help' for usage.");
        }

        output.Result("help", Describe(command), CommandText(command));
        return ExitCodes.Success;
    }

    private static JsonObject Overview()
    {
        var commands = new JsonArray();
        foreach (var command in CliApp.Commands)
        {
            commands.Add(Describe(command));
        }

        return new JsonObject
        {
            ["version"] = CliApp.Version,
            ["commands"] = commands,
            ["globalOptions"] = DescribeOptions(GlobalOptions),
            ["exitCodes"] = new JsonObject
            {
                ["success"] = ExitCodes.Success,
                ["validationFailed"] = ExitCodes.ValidationFailed,
                ["error"] = ExitCodes.Error,
            },
        };
    }

    private static JsonObject Describe(CommandDefinition command) => new()
    {
        ["name"] = command.Name,
        ["summary"] = command.Summary,
        ["usage"] = command.Usage,
        ["options"] = DescribeOptions(command.Options),
    };

    private static JsonArray DescribeOptions(IEnumerable<OptionDefinition> options)
    {
        var result = new JsonArray();
        foreach (var option in options)
        {
            result.Add(new JsonObject
            {
                ["name"] = option.Name,
                ["value"] = option.ValueName,
                ["description"] = option.Description,
            });
        }

        return result;
    }

    private static string OverviewText()
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"testwizz {CliApp.Version} - generate reviewable unit tests with a local AI model\n\n");
        text.Append("Usage: testwizz <command> [arguments] [options]\n\nCommands:\n");
        var width = CliApp.Commands.Max(c => c.Name.Length) + 2;
        foreach (var command in CliApp.Commands)
        {
            text.Append(CultureInfo.InvariantCulture, $"  {command.Name.PadRight(width)}{command.Summary}\n");
        }

        text.Append("\nGlobal options:\n");
        AppendOptions(text, GlobalOptions);
        text.Append(CultureInfo.InvariantCulture, $"\nExit codes: {ExitCodes.Success} success, {ExitCodes.ValidationFailed} validation failed, {ExitCodes.Error} error.\n");
        text.Append("Run 'testwizz help <command>' for details.\n");
        return text.ToString();
    }

    private static string CommandText(CommandDefinition command)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{command.Summary}\n\nUsage: {command.Usage}\n");
        if (command.Options.Count > 0)
        {
            text.Append("\nOptions:\n");
            AppendOptions(text, command.Options);
        }

        text.Append("\nGlobal options:\n");
        AppendOptions(text, GlobalOptions);
        return text.ToString();
    }

    private static void AppendOptions(StringBuilder text, IReadOnlyList<OptionDefinition> options)
    {
        var labels = options.Select(o => o.TakesValue ? $"{o.Name} <{o.ValueName}>" : o.Name).ToList();
        var width = labels.Max(l => l.Length) + 2;
        for (var i = 0; i < options.Count; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $"  {labels[i].PadRight(width)}{options[i].Description}\n");
        }
    }
}
