using System.Text.Json.Nodes;
using TestWizz.Core.Configuration;

namespace TestWizz.Cli.Commands;

public static class InitCommand
{
    private const string ForceOption = "--force";

    public static CommandDefinition Definition { get; } = new(
        "init",
        $"Create a default testwizz.yaml in a directory.",
        "testwizz init [path] [--force]",
        Options: [new(ForceOption, $"Overwrite an existing testwizz.yaml.")],
        MaxArguments: 1,
        Handler: Run);

    private static int Run(CommandContext context)
    {
        var directory = context.PathArgument();
        if (!Directory.Exists(directory))
        {
            throw new CliException("path.not_found", $"Directory not found: {directory}");
        }

        var file = Path.Combine(directory, "testwizz.yaml");
        var legacy = Path.Combine(directory, TestWizzConfig.FileName);
        var exists = File.Exists(file) || File.Exists(legacy);
        if (exists && !context.Has(ForceOption))
        {
            throw new CliException("config.exists", $"{file} already exists. Use --force to overwrite it.");
        }

        File.WriteAllText(file, TestWizzYaml.DefaultText + "\n");

        context.Output.Result(
            "init",
            new JsonObject { ["path"] = file, ["overwritten"] = File.Exists(file) && context.Has(ForceOption) },
            $"{(exists ? "Overwrote" : "Created")} {file}\nEdit ai.endpoint and ai.model to match your local model server.\n");
        return ExitCodes.Success;
    }
}
