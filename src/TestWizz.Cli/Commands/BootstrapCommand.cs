using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using TestWizz.DotNet;

namespace TestWizz.Cli.Commands;

/// <summary>Initial, review-first generation and execution of every eligible C# method.</summary>
public static class BootstrapCommand
{
    public static CommandDefinition Definition { get; } = new(
        "bootstrap",
        "Discover all eligible C# methods, generate test proposals and optionally run them in isolated containers. Never applies changes.",
        "testwizz bootstrap [path] --all [--review] [--generate-only] [--out <dir>]",
        Options:
        [
            new("--all", "Discover and process all eligible C# methods."),
            new("--review", "Keep proposals for explicit review (default; no files are applied)."),
            new("--generate-only", "Skip container execution; only generate proposals."),
            new("--out", "Proposal directory outside the repository.", "dir"),
        ],
        MaxArguments: 1,
        Handler: Run);

    private static int Run(CommandContext context)
    {
        if (!context.Has("--all"))
            throw new CliException("usage", "bootstrap currently requires --all.");
        var root = context.PathArgument();
        var targets = RoslynContextProvider.Load(root).DiscoverMethods();
        var rows = new JsonArray();
        var succeeded = 0;
        var failed = 0;
        var skipped = 0;
        var text = new StringBuilder("# Initial test baseline\n\n");
        text.Append("| Target | Generation | Execution | Proposal |\n|---|---|---|---|\n");
        context.Output.Stderr.WriteLine($"Discovered {targets.Count} eligible C# method(s).");
        for (var i = 0; i < targets.Count; i++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var target = targets[i];
            ReportProgress(context, i, targets.Count, target, "generating");
            string generation = "error", execution = "not-run";
            string? proposal = null, error = null;
            try
            {
                using var generated = new StringWriter(CultureInfo.InvariantCulture);
                var options = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["--for"] = target,
                };
                if (context.Options.TryGetValue("--out", out var outputDirectory))
                    options["--out"] = outputDirectory;
                var generateContext = context with
                {
                    Command = GenerateCommand.Definition,
                    Options = options,
                    Output = new CliOutput(generated, context.Output.Stderr, OutputFormat.Json),
                };
                var generationExit = GenerateCommand.Definition.Handler(generateContext);
                var generationResponse = JsonNode.Parse(generated.ToString())!;
                proposal = (string?)generationResponse["result"]?["path"];
                generation = generationExit == ExitCodes.Success ? "ok" : "failed";
                if (generationExit == ExitCodes.Success && proposal is null)
                    throw new CliException("bootstrap.no_proposal", "Generation succeeded without a proposal path.");
                if (generationExit == ExitCodes.Success && proposal is not null && !context.Has("--generate-only"))
                {
                    ReportProgress(context, i, targets.Count, target, "running");
                    using var runOutput = new StringWriter(CultureInfo.InvariantCulture);
                    var runContext = context with
                    {
                        Command = RunCommand.Definition,
                        Options = RunOptions(context, proposal),
                        Output = new CliOutput(runOutput, context.Output.Stderr, OutputFormat.Json),
                    };
                    var runExit = RunCommand.Definition.Handler(runContext);
                    execution = runExit == ExitCodes.Success ? "passed" : "failed";
                }
                else if (generationExit != ExitCodes.Success)
                    execution = "not-run";
            }
            catch (Exception ex) when (ex is CliException or InvalidOperationException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
            {
                error = ex is CliException cli ? cli.Code + ": " + cli.Message : ex.Message;
                execution = "error";
                context.Output.Stderr.WriteLine($"Target {target}: {error}");
            }

            if (generation == "ok" && (execution is "passed" or "not-run")) succeeded++;
            else if (generation == "ok" && execution == "skipped") skipped++;
            else failed++;

            rows.Add(new JsonObject
            {
                ["target"] = target,
                ["generation"] = generation,
                ["execution"] = execution,
                ["proposal"] = proposal,
                ["error"] = error,
            });
            text.Append("| `").Append(target.Replace("|", "\\|", StringComparison.Ordinal))
                .Append("` | ").Append(generation).Append(" | ").Append(execution).Append(" | ")
                .Append(proposal is null ? "—" : "`" + proposal.Replace("|", "\\|", StringComparison.Ordinal) + "`")
                .Append("|\n");
            ReportProgress(context, i + 1, targets.Count, target, "complete");
        }

        if (targets.Count == 0)
            context.Output.Stderr.WriteLine("No eligible C# methods discovered.");
        text.Append(CultureInfo.InvariantCulture, $"\n{targets.Count} targets; {succeeded} successful, {failed} failed, {skipped} skipped. Proposals remain outside the repository; nothing was applied.\n");
        context.Output.Result("bootstrap", new JsonObject
        {
            ["schema"] = "testwizz.bootstrap/v1",
            ["total"] = targets.Count,
            ["succeeded"] = succeeded,
            ["failed"] = failed,
            ["skipped"] = skipped,
            ["items"] = rows,
        }, text.ToString(), failed == 0 && skipped == 0 ? "ok" : "failed");
        return failed == 0 && skipped == 0 ? ExitCodes.Success : ExitCodes.ValidationFailed;
    }

    private static Dictionary<string, string?> RunOptions(CommandContext context, string proposal)
    {
        var options = new Dictionary<string, string?>(StringComparer.Ordinal) { ["--proposal"] = proposal };
        if (context.Options.TryGetValue("--out", out var outputDirectory))
            options["--out"] = outputDirectory;
        return options;
    }

    private static void ReportProgress(CommandContext context, int completed, int total, string target, string phase)
    {
        var percent = total == 0 ? 100 : completed * 100 / total;
        var width = 20;
        var filled = percent * width / 100;
        var bar = new string('#', filled) + new string('-', width - filled);
        var writer = context.Output.Stderr;
        if (ReferenceEquals(writer, Console.Error) && !Console.IsErrorRedirected)
        {
            var maxWidth = Math.Max(20, Console.WindowWidth - 1);
            var line = $"[{bar}] {percent,3}% ({completed}/{total}) {phase}: {target}";
            writer.Write("\r" + (line.Length > maxWidth ? line[..maxWidth] : line).PadRight(maxWidth));
            if (completed == total) writer.WriteLine();
        }
        else
        {
            writer.WriteLine($"[{bar}] {percent,3}% ({completed}/{total}) {phase}: {target}");
        }
    }
}
