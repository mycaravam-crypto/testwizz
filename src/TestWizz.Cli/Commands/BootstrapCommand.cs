using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using TestWizz.Core.Configuration;
using TestWizz.Core.Proposals;
using TestWizz.DotNet;

namespace TestWizz.Cli.Commands;

/// <summary>Initial, review-first generation and execution of every eligible C# method.</summary>
public static class BootstrapCommand
{
    public static CommandDefinition Definition { get; } = new(
        "bootstrap",
        "Discover all eligible C# methods, generate test proposals and optionally run them in isolated containers. Never applies changes.",
        "testwizz bootstrap [path] --all [--review] [--generate-only] [--resume] [--out <dir>]",
        Options:
        [
            new("--all", "Discover and process all eligible C# methods."),
            new("--review", "Keep proposals for explicit review (default; no files are applied)."),
            new("--generate-only", "Skip container execution; only generate proposals."),
            new("--resume", "Continue a matching scan, skipping previously successful targets."),
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
        var config = ConfigCommand.Load(Path.Combine(root, TestWizzConfig.FileName));
        var outputDirectory = context.Options.TryGetValue("--out", out var custom) && !string.IsNullOrWhiteSpace(custom)
            ? Path.GetFullPath(custom, context.WorkingDirectory)
            : Path.GetFullPath(config.Output, root);
        ProposalBundle.EnsureOutsideRepository(root, outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var checkpoint = Path.Combine(outputDirectory, "bootstrap-checkpoint.json");
        var fingerprint = Fingerprint(root, config, targets, context.Has("--generate-only"));
        var saved = context.Has("--resume") ? ReadCheckpoint(checkpoint, fingerprint) : new JsonArray();
        var completed = saved.OfType<JsonObject>()
            .Where(row => (string?)row["generation"] == "ok"
                && (string?)row["execution"] is "passed" or "not-run"
                && (string?)row["proposal"] is { } proposal
                && File.Exists(Path.Combine(proposal, ProposalBundle.ManifestFile)))
            .ToDictionary(row => (string)row["target"]!, row => row, StringComparer.Ordinal);
        if (!context.Has("--resume"))
            SaveCheckpoint(checkpoint, fingerprint, new JsonArray());
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
            if (completed.TryGetValue(target, out var previous))
            {
                var restored = previous.DeepClone().AsObject();
                rows.Add(restored);
                succeeded++;
                AppendRow(text, target, (string?)restored["generation"] ?? "ok",
                    (string?)restored["execution"] ?? "not-run", (string?)restored["proposal"]);
                ReportProgress(context, i + 1, targets.Count, target, "resumed");
                continue;
            }
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
            AppendRow(text, target, generation, execution, proposal);
            SaveCheckpoint(checkpoint, fingerprint, rows);
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

    private static void AppendRow(StringBuilder text, string target, string generation, string execution, string? proposal)
    {
        text.Append("| `").Append(target.Replace("|", "\\|", StringComparison.Ordinal))
            .Append("` | ").Append(generation).Append(" | ").Append(execution).Append(" | ")
            .Append(proposal is null ? "—" : "`" + proposal.Replace("|", "\\|", StringComparison.Ordinal) + "`")
            .Append("|\n");
    }

    private static string Fingerprint(string root, TestWizzConfig config, IReadOnlyList<string> targets, bool generateOnly)
    {
        // Include tracked C# contents, not only HEAD: dirty worktrees must invalidate resume.
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part =>
                part is ".git" or "bin" or "obj" or "node_modules"))
            .Order(StringComparer.Ordinal);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value) => hash.AppendData(Encoding.UTF8.GetBytes(value + "\n"));
        Add(Path.GetFullPath(root));
        Add(ConfigJson.ToJson(config).ToJsonString());
        Add(generateOnly.ToString());
        foreach (var target in targets) Add(target);
        foreach (var file in files)
        {
            Add(Path.GetRelativePath(root, file));
            hash.AppendData(File.ReadAllBytes(file));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static JsonArray ReadCheckpoint(string path, string fingerprint)
    {
        if (!File.Exists(path))
            throw new CliException("bootstrap.no_checkpoint", "No saved scan found. Start with 'bootstrap --all' first.");
        try
        {
            var state = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
            if ((string?)state?["fingerprint"] != fingerprint)
                throw new CliException("bootstrap.changed", "Sources, configuration or scan mode changed; start without --resume.");
            return state?["items"]?.AsArray() ?? throw new System.Text.Json.JsonException("Missing checkpoint items.");
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            throw new CliException("bootstrap.checkpoint_invalid", "Checkpoint is invalid; start without --resume.");
        }
    }

    private static void SaveCheckpoint(string path, string fingerprint, JsonArray items)
    {
        var state = new JsonObject
        {
            ["schema"] = "testwizz.bootstrap-checkpoint/v1",
            ["fingerprint"] = fingerprint,
            ["items"] = items.DeepClone(),
        };
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, state.ToJsonString() + "\n");
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
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
