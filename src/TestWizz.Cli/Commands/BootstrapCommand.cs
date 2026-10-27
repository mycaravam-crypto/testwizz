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
            new("--progress-jsonl", "Emit structured bootstrap progress events on stderr."),
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
        // Read the DocWizz scope before loading Roslyn. Excluded project sources must never be parsed.
        var includedProjects = DocWizzProjectScope.Read(root);
        context.Output.Stderr.WriteLine(includedProjects is null
            ? "testwizz bootstrap: loading C# sources and building Roslyn context..."
            : $"testwizz bootstrap: loading only {includedProjects.Count} DocWizz-scoped project(s)...");
        context.CancellationToken.ThrowIfCancellationRequested();
        var includeFile = includedProjects is null ? null : ScopedSourceFilter(root, includedProjects);
        var provider = includeFile is null
            ? RoslynContextProvider.Load(root, context.CancellationToken)
            : RoslynContextProvider.Load(root, includeFile, context.CancellationToken);
        context.Output.Stderr.WriteLine($"testwizz bootstrap: Roslyn context ready ({clock.Elapsed.TotalSeconds:F1}s); discovering eligible methods...");
        var discoveryClock = Stopwatch.StartNew();
        var lastDiscoveryReport = TimeSpan.Zero;
        var targets = provider.DiscoverMethods((done, total) =>
            {
                if (discoveryClock.Elapsed - lastDiscoveryReport < TimeSpan.FromSeconds(2) && done != total) return;
                lastDiscoveryReport = discoveryClock.Elapsed;
                context.Output.Stderr.WriteLine($"testwizz bootstrap: discovering methods: {done}/{total} source files ({discoveryClock.Elapsed.TotalSeconds:F1}s)");
            }, context.CancellationToken)
            .Where(target => includedProjects is null
                || (provider.GetContext(target, CliApp.Version).Project is { } project
                    && includedProjects.Contains(project.Replace('\\', '/'))))
            .ToArray();
        context.CancellationToken.ThrowIfCancellationRequested();
        context.Output.Stderr.WriteLine($"testwizz bootstrap: discovery completed ({clock.Elapsed.TotalSeconds:F1}s); loading configuration...");
        var config = ConfigCommand.Load(Path.Combine(root, TestWizzConfig.FileName));
        using var model = ModelClient.Create(config.Ai, context.GetEnvironmentVariable);
        var outputDirectory = context.Options.TryGetValue("--out", out var custom) && !string.IsNullOrWhiteSpace(custom)
            ? Path.GetFullPath(custom, context.WorkingDirectory)
            : Path.GetFullPath(config.Output, root);
        ProposalBundle.EnsureOutsideRepository(root, outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var checkpoint = Path.Combine(outputDirectory, "bootstrap-checkpoint.json");
        context.Output.Stderr.WriteLine("testwizz bootstrap: fingerprinting C# sources for safe resume...");
        var fingerprint = Fingerprint(root, config, targets, context.Has("--generate-only"), includedProjects, context.CancellationToken);
        context.Output.Stderr.WriteLine($"testwizz bootstrap: fingerprint ready ({clock.Elapsed.TotalSeconds:F1}s)");
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
        ProgressEvent(context, "discovered", targets.Length, 0, null, null, clock.Elapsed);
        var pending = targets.Where(target => !completed.ContainsKey(target)).ToArray();
        var generatedResults = new Dictionary<string, BootstrapItem>(StringComparer.Ordinal);
        var generationCheckpoint = new JsonArray();
        foreach (var entry in completed.Values)
            generationCheckpoint.Add(entry.DeepClone());
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
            ProgressEvent(context, "generation-started", targets.Length, completed.Count + position, batch[0], null, clock.Elapsed);
            ReportProgress(context, completed.Count + position, targets.Length, batch[0], $"generating ({batch.Length} active)", clock.Elapsed);
            context.Output.Stderr.WriteLine($"testwizz bootstrap: started wave at {position + 1}/{pending.Length}, workers={batch.Length}, first={batch[0]}");
            var tasks = batch.Select(target => Task.Run(() => GenerateTarget(context, provider, model, target),
                context.CancellationToken)).ToList();
            var remaining = new List<Task<BootstrapItem>>(tasks);
            while (remaining.Count > 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var completion = Task.WhenAny(remaining);
                // Wait at most 15 seconds between heartbeat messages even when an LLM is slow.
                var heartbeat = Task.Delay(TimeSpan.FromSeconds(15), context.CancellationToken);
                var winner = Task.WhenAny(completion, heartbeat).GetAwaiter().GetResult();
                if (winner == heartbeat)
                {
                    context.Output.Stderr.WriteLine($"testwizz bootstrap: waiting on {remaining.Count} LLM request(s), {clock.Elapsed:mm\\:ss} elapsed; completed {completed.Count + position + batch.Length - remaining.Count}/{targets.Length}");
                    continue;
                }
                var completedTask = completion.GetAwaiter().GetResult();
                var item = completedTask.GetAwaiter().GetResult();
                remaining.Remove(completedTask);
                generatedResults[item.Target] = item;
                generationCheckpoint.Add(new JsonObject
                {
                    ["target"] = item.Target,
                    ["generation"] = item.Generation,
                    ["execution"] = "not-run",
                    ["proposal"] = item.Proposal,
                    ["error"] = item.Error,
                });
                if (context.Has("--generate-only"))
                    SaveCheckpoint(checkpoint, fingerprint, generationCheckpoint);
                if (item.Diagnostics.Length > 0) context.Output.Stderr.Write(item.Diagnostics);
                var done = completed.Count + position + batch.Length - remaining.Count;
                ReportProgress(context, done, targets.Length, item.Target, "generated", clock.Elapsed);
                ProgressEvent(context, "generated", targets.Length, done, item.Target, item.Generation, clock.Elapsed);
                context.Output.Stderr.WriteLine($"testwizz bootstrap generated: {item.Target}: {item.Generation} ({item.Seconds:F1}s)");
            }
            waveClock.Stop();
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
                ProgressEvent(context, "resumed", targets.Length, i + 1, target, "ok", clock.Elapsed);
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
            ProgressEvent(context, "finalized", targets.Length, i + 1, target, generation == "ok" && execution is "passed" or "not-run" ? "ok" : "failed", clock.Elapsed);
        }

        if (targets.Length == 0)
            context.Output.Stderr.WriteLine("No eligible C# methods discovered.");
        text.Append(CultureInfo.InvariantCulture, $"\n{targets.Length} targets; {succeeded} successful, {failed} failed, {skipped} skipped. Proposals remain outside the repository; nothing was applied.\n");
        ProgressEvent(context, "finished", targets.Length, targets.Length, null, failed == 0 ? "ok" : "failed", clock.Elapsed);
        context.Output.Result("bootstrap", new JsonObject
        {
            ["schema"] = "testwizz.bootstrap/v1",
            ["total"] = targets.Length,
            ["eligible"] = targets.Length,
            ["resumed"] = completed.Count,
            ["generationWorkersMax"] = maxWorkers,
            ["succeeded"] = succeeded,
            ["failed"] = failed,
            ["skipped"] = skipped,
            ["items"] = rows,
        }, text.ToString(), failed == 0 && skipped == 0 ? "ok" : "failed");
        return failed == 0 && skipped == 0 ? ExitCodes.Success : ExitCodes.ValidationFailed;
    }

    private static void ProgressEvent(CommandContext context, string phase, int total, int completed,
        string? target, string? status, TimeSpan elapsed)
    {
        if (!context.Has("--progress-jsonl")) return;
        var payload = new JsonObject
        {
            ["schema"] = "testwizz.bootstrap-progress/v1",
            ["phase"] = phase,
            ["total"] = total,
            ["completed"] = completed,
            ["percent"] = total == 0 ? 100 : Math.Min(100, completed * 100 / total),
            ["target"] = target,
            ["status"] = status,
            ["elapsedSeconds"] = Math.Round(elapsed.TotalSeconds, 1),
        };
        context.Output.Stderr.WriteLine(payload.ToJsonString());
    }

    // Nearest .csproj wins, including when project directories are nested. Cache each
    // directory's owning project so large repositories do not repeatedly scan manifests.
    private static Func<string, bool> ScopedSourceFilter(string root, IReadOnlySet<string> projects)
    {
        var owners = new Dictionary<string, string?>(StringComparer.Ordinal);
        var rootFull = Path.GetFullPath(root);
        string? Owner(string directory)
        {
            if (owners.TryGetValue(directory, out var cached)) return cached;
            string? result = null;
            var relative = Path.GetRelativePath(rootFull, directory);
            if (relative != ".." && !relative.StartsWith("../", StringComparison.Ordinal)
                && !relative.StartsWith("..\\", StringComparison.Ordinal))
            {
                result = Directory.EnumerateFiles(directory, "*.csproj")
                    .Select(project => Path.GetRelativePath(rootFull, project).Replace('\\', '/'))
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (result is null && !Path.GetFullPath(directory).Equals(rootFull, StringComparison.OrdinalIgnoreCase))
                    result = Owner(Path.GetDirectoryName(directory)!);
            }
            owners[directory] = result;
            return result;
        }
        return relative => projects.Contains(Owner(Path.GetDirectoryName(Path.Combine(rootFull, relative))!) ?? "");
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

    private static string Fingerprint(string root, TestWizzConfig config, IReadOnlyList<string> targets, bool generateOnly, IReadOnlySet<string>? includedProjects, CancellationToken cancellationToken)
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
            cancellationToken.ThrowIfCancellationRequested();
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
