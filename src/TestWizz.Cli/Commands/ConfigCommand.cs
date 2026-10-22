using System.Text.Json.Nodes;
using TestWizz.Core.Configuration;

namespace TestWizz.Cli.Commands;

public static class ConfigCommand
{
    public static CommandDefinition Definition { get; } = new(
        "config",
        $"Validate {TestWizzConfig.FileName} and show the effective configuration.",
        "testwizz config [path]",
        Options: [],
        MaxArguments: 1,
        Handler: Run);

    private static int Run(CommandContext context)
    {
        var file = Path.Combine(context.PathArgument(), TestWizzConfig.FileName);
        var config = Load(file);
        var json = ConfigJson.ToJson(config);

        context.Output.Result(
            "config",
            new JsonObject { ["path"] = file, ["config"] = json.DeepClone() },
            $"{file} is valid.\n\n{json.ToJsonString(CliOutput.JsonOptions)}\n");
        return ExitCodes.Success;
    }

    /// <summary>Loads and validates a config file, turning every problem into a <see cref="CliException"/>.</summary>
    public static TestWizzConfig Load(string file)
    {
        if (!File.Exists(file))
        {
            throw new CliException("config.not_found", $"{file} not found. Run 'testwizz init' to create it.");
        }

        var result = ConfigJson.Parse(File.ReadAllText(file));
        if (result.Config is null)
        {
            throw new CliException(
                "config.invalid",
                $"{file} is invalid.",
                details: [.. result.Errors.Select(e => (e.Path, e.Message))]);
        }

        return result.Config;
    }
}
