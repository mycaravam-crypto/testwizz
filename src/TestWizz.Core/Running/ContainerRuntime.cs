using System.Diagnostics;
using System.Globalization;
using System.Text;
using TestWizz.Core.Configuration;

namespace TestWizz.Core.Running;

/// <summary>Resource limits applied to every container TestWizz starts.</summary>
public sealed record ContainerLimits(double Cpus, int MemoryMegabytes, int Pids)
{
    public static ContainerLimits Default { get; } = new(Cpus: 2, MemoryMegabytes: 4096, Pids: 1024);
}

/// <summary>
/// One container invocation. The workspace is a throw-away copy on the host, mounted at <see cref="ContainerWorkspace"/>;
/// the user's repository is never mounted.
/// </summary>
/// <param name="Network">Only dependency restore gets a network; build and test always run with <c>--network none</c>.</param>
public sealed record ContainerSpec(
    string Name,
    string Image,
    string Workspace,
    IReadOnlyList<string> Command,
    bool Network,
    ContainerLimits Limits,
    IReadOnlyDictionary<string, string> Environment)
{
    public const string ContainerWorkspace = "/work";
}

/// <param name="TimedOut">The container was stopped because it exceeded its time limit.</param>
public sealed record ContainerResult(int ExitCode, string Output, TimeSpan Duration, bool TimedOut);

public interface IContainerRuntime
{
    string Name { get; }

    /// <summary>Null when the runtime's server answers, otherwise a reason it cannot be used.</summary>
    Task<string?> CheckAsync(CancellationToken cancellationToken);

    /// <summary>Runs the container to completion, removing it afterwards, also on timeout and cancellation.</summary>
    Task<ContainerResult> RunAsync(ContainerSpec spec, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Docker or Podman through their command-line clients.</summary>
public sealed class CliContainerRuntime(ContainerRuntime runtime) : IContainerRuntime
{
    private const int MaxOutputChars = 2 * 1024 * 1024;
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);

    public string Name { get; } = ConfigJson.RuntimeName(runtime);

    public async Task<string?> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CheckTimeout);
            var (exit, output) = await RunProcessAsync(["version", "--format", "{{.Server.Version}}"], timeout.Token).ConfigureAwait(false);
            return exit == 0 ? null : $"'{Name} version' failed: {Last(output)}";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return $"'{Name}' is not installed or not on PATH.";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return $"'{Name}' did not answer within {CheckTimeout.TotalSeconds:0} seconds.";
        }
    }

    public async Task<ContainerResult> RunAsync(ContainerSpec spec, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            var (exit, output) = await RunProcessAsync(RunArguments(spec, runtime, CurrentUser()), limit.Token).ConfigureAwait(false);
            return new ContainerResult(exit, output, watch.Elapsed, TimedOut: false);
        }
        catch (OperationCanceledException)
        {
            // Killing the client does not stop the container; remove it explicitly.
            await RemoveAsync(spec.Name).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new ContainerResult(-1, $"Stopped after {timeout.TotalSeconds:0} seconds.", watch.Elapsed, TimedOut: true);
        }
    }

    /// <summary>
    /// Arguments for <c>run</c>: no capabilities, no privilege escalation, read-only root file system, resource limits,
    /// and <c>--network none</c> unless the phase is dependency restore.
    /// </summary>
    public static IReadOnlyList<string> RunArguments(ContainerSpec spec, ContainerRuntime runtime, string? user)
    {
        var args = new List<string>
        {
            "run", "--rm", "--name", spec.Name,
            "--network", spec.Network ? "bridge" : "none",
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "--read-only",
            "--tmpfs", "/tmp:rw,exec,size=1g",
            "--cpus", spec.Limits.Cpus.ToString(CultureInfo.InvariantCulture),
            "--memory", $"{spec.Limits.MemoryMegabytes.ToString(CultureInfo.InvariantCulture)}m",
            "--pids-limit", spec.Limits.Pids.ToString(CultureInfo.InvariantCulture),
            "--volume", $"{spec.Workspace}:{ContainerSpec.ContainerWorkspace}",
            "--workdir", ContainerSpec.ContainerWorkspace,
        };

        // Rootless Podman already maps container root to the calling user; Docker needs the uid so the host can
        // delete the workspace afterwards.
        if (runtime == ContainerRuntime.Docker && user is not null)
        {
            args.AddRange(["--user", user]);
        }

        foreach (var (name, value) in spec.Environment.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            args.AddRange(["--env", $"{name}={value}"]);
        }

        args.Add(spec.Image);
        args.AddRange(spec.Command);
        return args;
    }

    private static string? CurrentUser()
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        var uid = Id("-u");
        var gid = Id("-g");
        return uid is null || gid is null ? null : $"{uid}:{gid}";
    }

    private static string? Id(string flag)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("id", flag) { RedirectStandardOutput = true, UseShellExecute = false });
            var text = process?.StandardOutput.ReadToEnd().Trim();
            process?.WaitForExit();
            return process?.ExitCode == 0 && text is { Length: > 0 } && text.All(char.IsAsciiDigit) ? text : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private async Task RemoveAsync(string name)
    {
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await RunProcessAsync(["rm", "--force", name], cleanup.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private async Task<(int ExitCode, string Output)> RunProcessAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(Name)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = start };
        var output = new StringBuilder();
        void Append(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (output)
            {
                if (output.Length < MaxOutputChars)
                {
                    output.Append(line).Append('\n');
                }
            }
        }

        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }

        lock (output)
        {
            return (process.ExitCode, output.ToString());
        }
    }

    private static string Last(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "no output";
}
