using System.Text.Json.Nodes;
using TestWizz.Core.Configuration;

namespace TestWizz.Cli.Commands;

public static class InitCommand
{
    private const string ForceOption = "--force";

    public static CommandDefinition Definition { get; } = new(
        "init",
        $"Create a default {TestWizzConfig.FileName} in a directory.",
        "testwizz init [path] [--force]",
        Options: [new(ForceOption, $"Overwrite an existing {TestWizzConfig.FileName}.")],
        MaxArguments: 1,
        Handler: Run);

    private static int Run(CommandContext context)
    {
        var directory = context.PathArgument();
        if (!Directory.Exists(directory))
        {
            throw new CliException("path.not_found", $"Directory not found: {directory}");
        }

        var file = Path.Combine(directory, TestWizzConfig.FileName);
        var exists = File.Exists(file);
        if (exists && !context.Has(ForceOption))
        {
            throw new CliException("config.exists", $"{file} already exists. Use --force to overwrite it.");
        }

        var json = ConfigJson.ToJson(TestWizzConfig.Default).ToJsonString(CliOutput.JsonOptions) + "\n";
        File.WriteAllText(file, json);

        context.Output.Result(
            "init",
            new JsonObject { ["path"] = file, ["overwritten"] = exists },
            $"{(exists ? "Overwrote" : "Created")} {file}\nEdit ai.endpoint and ai.model to match your local model server.\n");
        return ExitCodes.Success;
    }
}
