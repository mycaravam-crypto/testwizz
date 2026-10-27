using System.Diagnostics;
using System.Threading.Tasks;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using TestWizz.Core.Configuration;
using TestWizz.Core.Proposals;
using TestWizz.DotNet;
using TestWizz.AI;

namespace TestWizz.Cli.Commands;

/// <summary>Initial, review-first generation and execution of every eligible C# method.</summary>
public static class BootstrapCommand
{
    public static CommandDefinition Definition { get; } = new(
        "bootstrap",
        "Discover all eligible C# methods, generate test proposals and optionally run them in isolated containers. Never applies changes.",
        "testwizz bootstrap [path] --all [--review] [--generate-only] [--resume] [--max-workers <n>] [--out <dir>]",
        Options:
        [
            new("--all", "Discover and process all eligible C# methods."),
            new("--review", "Keep proposals for explicit review (default; no files are applied)."),
            new("--generate-only", "Skip container execution; only generate proposals."),
            new("--resume", "Continue a matching scan, skipping previously successful targets."),
            new("--out", "Proposal directory outside the repository.", "dir"),
            new("--debug", "Print each generated test source to stderr (may contain sensitive source code)."),
            new("--max-workers", "Maximum adaptive generation workers (1-8; default 4).", "n"),
        ],
        MaxArguments: 1,
        Handler: Run);

    private static int Run(CommandContext context)
    {
        var clock = Stopwatch.StartNew();
        var maxWorkers = 4;
        if (context.Options.TryGetValue("--max-workers", out var workerText)
            && (!int.TryParse(workerText, NumberStyles.None, CultureInfo.InvariantCulture, out maxWorkers) || maxWorkers is < 1 or > 8))
            throw new CliException("usage", "--max-workers must be between 1 and 8.");
        if (!context.Has("--all"))
            throw new CliException("usage", "bootstrap currently requires --all.");
        var root = context.PathArgument();
        var provider = RoslynContextProvider.Load(root);
        var includedProjects = DocWizzProjectScope.Read(root);
        var targets = provider.DiscoverMethods()
            .Where(target => includedProjects is null
                || (provider.GetContext(target, CliApp.Version).Project is { } project
                    && includedProjects.Contains(project.Replace('\\', '/'))))
            .ToArray();
        var config = ConfigCommand.Load(Path.Combine(root, TestWizzConfig.FileName));
        using var model = ModelClient.Create(config.Ai, context.GetEnvironmentVariable);
        var outputDirectory = context.Options.TryGetValue("--out", out var custom) && !string.IsNullOrWhiteSpace(custom)
            ? Path.GetFullPath(custom, context.WorkingDirectory)
            : Path.GetFullPath(config.Output, root);
        ProposalBundle.EnsureOutsideRepository(root, outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var checkpoint = Path.Combine(outputDirectory, "bootstrap-checkpoint.json");
        var fingerprint = Fingerprint(root, config, targets, context.Has("--generate-only"), includedProjects);
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
        context.Output.Stderr.WriteLine($"Discovered {targets.Length} eligible C# method(s){(includedProjects is null ? "" : " in " + includedProjects.Count + " DocWizz-scoped project(s)")}.");
        var pending = targets.Where(target => !completed.ContainsKey(target)).ToArray();
        var generatedResults = new Dictionary<string, BootstrapItem>(StringComparer.Ordinal);
        var workers = 1;
        double previousThroughput = 0;
        var position = 0;
        // Run model generation in bounded waves. Only the coordinator writes checkpoints
        // and executes containers, avoiding concurrent checkpoint changes and test-environment collisions.
        while (position < pending.Length)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var batch = pending.Skip(position).Take(workers).ToArray();
            var waveClock = Stopwatch.StartNew();
            var tasks = batch.Select(target => Task.Run(() => GenerateTarget(context, provider, model, target),
                context.CancellationToken)).ToArray();
            Task.WhenAll(tasks).GetAwaiter().GetResult();
            waveClock.Stop();
            foreach (var task in tasks)
            {
                var item = task.Result;
                generatedResults[item.Target] = item;
                if (item.Diagnostics.Length > 0) context.Output.Stderr.Write(item.Diagnostics);
                context.Output.Stderr.WriteLine($"testwizz bootstrap generated: {item.Target}: {item.Generation} ({item.Seconds:F1}s)");
            }
            position += batch.Length;
            var throughput = batch.Length / Math.Max(waveClock.Elapsed.TotalSeconds, 0.001);
            context.Output.Stderr.WriteLine($"testwizz bootstrap: workers={workers}, throughput={throughput:F3} targets/s");
            if (workers > 1 && previousThroughput > 0 && throughput < previousThroughput * 0.9)
                workers = Math.Max(1, workers - 1);
            else if (workers < maxWorkers && position < pending.Length)
                workers++;
            previousThroughput = throughput;
        }

        for (var i = 0; i < targets.Length; i++)
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
                ReportProgress(context, i + 1, targets.Length, target, "resumed", clock.Elapsed);
                continue;
            }
            var item = generatedResults[target];
            var generation = item.Generation;
            var execution = "not-run";
            var proposal = item.Proposal;
            var error = item.Error;
            if (generation == "ok" && proposal is not null && !context.Has("--generate-only"))
            {
                ReportProgress(context, i, targets.Length, target, "running", clock.Elapsed);
                try
                {
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
                catch (Exception ex) when (ex is CliException or InvalidOperationException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
                {
                    error = ex is CliException cli ? cli.Code + ": " + cli.Message : ex.Message;
                    execution = "error";
                }
            }
            else if (generation == "error") execution = "error";

            if (generation == "ok" && (execution is "passed" or "not-run")) succeeded++;
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
            ReportProgress(context, i + 1, targets.Length, target, "complete", clock.Elapsed);
        }

        if (targets.Length == 0)
            context.Output.Stderr.WriteLine("No eligible C# methods discovered.");
        text.Append(CultureInfo.InvariantCulture, $"\n{targets.Length} targets; {succeeded} successful, {failed} failed, {skipped} skipped. Proposals remain outside the repository; nothing was applied.\n");
        context.Output.Result("bootstrap", new JsonObject
        {
            ["schema"] = "testwizz.bootstrap/v1",
            ["total"] = targets.Length,
            ["succeeded"] = succeeded,
            ["failed"] = failed,
            ["skipped"] = skipped,
            ["items"] = rows,
        }, text.ToString(), failed == 0 && skipped == 0 ? "ok" : "failed");
        return failed == 0 && skipped == 0 ? ExitCodes.Success : ExitCodes.ValidationFailed;
    }

    private sealed record BootstrapItem(string Target, string Generation, string? Proposal, string? Error, string Diagnostics, double Seconds);

    private static BootstrapItem GenerateTarget(CommandContext context, RoslynContextProvider provider, ModelClient model, string target)
    {
        var timer = Stopwatch.StartNew();
        using var generated = new StringWriter(CultureInfo.InvariantCulture);
        using var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        var options = new Dictionary<string, string?>(StringComparer.Ordinal) { ["--for"] = target };
        if (context.Has("--debug")) options["--debug"] = null;
        if (context.Options.TryGetValue("--out", out var output)) options["--out"] = output;
        var inner = context with
        {
            Command = GenerateCommand.Definition,
            Options = options,
            Output = new CliOutput(generated, diagnostics, OutputFormat.Json),
            SharedProvider = provider,
            SharedModel = model,
        };
        try
        {
            var exit = GenerateCommand.Definition.Handler(inner);
            var response = JsonNode.Parse(generated.ToString())!;
            var proposal = (string?)response["result"]?["path"];
            if (exit == ExitCodes.Success && proposal is null)
                throw new CliException("bootstrap.no_proposal", "Generation succeeded without a proposal path.");
            return new BootstrapItem(target, exit == ExitCodes.Success ? "ok" : "failed", proposal,
                null, diagnostics.ToString(), timer.Elapsed.TotalSeconds);
        }
        catch (Exception ex) when (ex is CliException or InvalidOperationException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        {
            var message = ex is CliException cli ? cli.Code + ": " + cli.Message : ex.Message;
            return new BootstrapItem(target, "error", null, message, diagnostics.ToString(), timer.Elapsed.TotalSeconds);
        }
    }

    private static void AppendRow(StringBuilder text, string target, string generation, string execution, string? proposal)
    {
        text.Append("| `").Append(target.Replace("|", "\\|", StringComparison.Ordinal))
            .Append("` | ").Append(generation).Append(" | ").Append(execution).Append(" | ")
            .Append(proposal is null ? "—" : "`" + proposal.Replace("|", "\\|", StringComparison.Ordinal) + "`")
            .Append("|\n");
    }

    private static string Fingerprint(string root, TestWizzConfig config, IReadOnlyList<string> targets, bool generateOnly, IReadOnlySet<string>? includedProjects)
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
        foreach (var project in (includedProjects ?? new HashSet<string>(StringComparer.Ordinal)).Order(StringComparer.Ordinal)) Add(project);
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

    // Match the 24-column, equals-filled stage bars used by DocWizz; keep CI logs free of control codes.
    private static void ReportProgress(CommandContext context, int completed, int total, string target, string phase, TimeSpan elapsed)
    {
        const int width = 24;
        var percent = total == 0 ? 100 : completed * 100 / total;
        var filled = percent * width / 100;
        var bar = new string('=', filled) + new string('-', width - filled);
        var duration = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : elapsed.ToString(@"m\:ss", CultureInfo.InvariantCulture);
        var writer = context.Output.Stderr;
        var line = $"testwizz [{bar}] {percent,3}% ({completed}/{total}) {phase}: {target} ({duration})";
        if (ReferenceEquals(writer, Console.Error) && !Console.IsErrorRedirected
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"))
            && Environment.GetEnvironmentVariable("TERM") != "dumb")
        {
            var maxWidth = Math.Max(20, Console.WindowWidth - 1);
            writer.Write("\r" + (line.Length > maxWidth ? line[..maxWidth] : line).PadRight(maxWidth));
            if (completed == total) writer.WriteLine();
        }
        else if (phase is "complete" or "resumed")
        {
            writer.WriteLine(line);
        }
    }
}
