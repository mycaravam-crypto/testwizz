using System.Reflection;
using TestWizz.Cli.Commands;

namespace TestWizz.Cli;

/// <summary>Entry point independent of the console so tests can run commands in-process.</summary>
public sealed class CliApp(
    TextWriter stdout,
    TextWriter stderr,
    string workingDirectory,
    Func<string, string?>? getEnvironmentVariable = null)
{
    private readonly Func<string, string?> _getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;

    public static string Version { get; } =
        typeof(CliApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>Implemented commands only, in the order help lists them.</summary>
    public static IReadOnlyList<CommandDefinition> Commands { get; } =
    [
        HelpCommand.Definition,
        InitCommand.Definition,
        ConfigCommand.Definition,
        AiCommand.Definition,
    ];

    public static IReadOnlyDictionary<string, CommandDefinition> CommandsByName { get; } =
        Commands.ToDictionary(c => c.Name, StringComparer.Ordinal);

    public int Run(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        var output = new CliOutput(stdout, stderr, CommandLineParser.DetectFormat(args));
        string? commandName = null;
        try
        {
            var parsed = CommandLineParser.Parse(args, CommandsByName);
            output = new CliOutput(stdout, stderr, parsed.Format);
            commandName = parsed.CommandName;

            if (parsed.Version)
            {
                if (parsed.CommandName is not null || parsed.Help)
                {
                    throw new CliException("usage", "--version cannot be combined with a command or --help.");
                }

                commandName = "version";
                output.Result("version", new() { ["version"] = Version }, $"testwizz {Version}\n");
                return ExitCodes.Success;
            }

            if (parsed.CommandName is null || parsed.Help)
            {
                // "testwizz", "testwizz --help" and "testwizz <command> --help" all show help.
                var topic = parsed.CommandName is null or "help" ? parsed.Arguments : [parsed.CommandName];
                commandName = "help";
                return HelpCommand.Show(output, topic);
            }

            var command = CommandsByName[parsed.CommandName];
            return command.Handler(new CommandContext(
                command, parsed.Arguments, parsed.Options, output, workingDirectory, _getEnvironmentVariable, cancellationToken));
        }
        catch (CliException ex)
        {
            output.Error(commandName, ex);
            return ex.ExitCode;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            output.Error(commandName, new CliException("io", ex.Message));
            return ExitCodes.Error;
        }
    }
}
