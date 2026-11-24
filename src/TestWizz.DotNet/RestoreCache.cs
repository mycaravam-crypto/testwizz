using System.Security.Cryptography;
using System.Text;

namespace TestWizz.DotNet;

/// <summary>
/// Shares one <c>dotnet restore</c> between the isolated runs of a batch (and the attempts of one repair loop).
/// Restore only ever sees project and build files, so runs with identical restore files, test project and image
/// produce identical restore output. The first run restores into a cache directory; later runs copy its
/// <c>obj</c> output into their own fresh workspace and mount its package folder <b>read-only</b>, so a build or
/// test phase can never change what another run sees. Failed or cancelled restores are not kept: the next run
/// that needs the same inputs restores again.
/// </summary>
public sealed class RestoreCache : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Task<RestoredDependencies>> _entries = new(StringComparer.Ordinal);
    private readonly List<string> _directories = [];
    private bool _disposed;

    /// <summary>Number of restores that were actually run (not reused); for diagnostics and tests.</summary>
    public int Restores { get; private set; }

    /// <summary>
    /// The restore for <paramref name="key"/>: an earlier successful one, one already in progress, or a new one
    /// started by calling <paramref name="restore"/> with a new, empty cache directory.
    /// </summary>
    internal async Task<(RestoredDependencies Restored, bool Reused)> GetAsync(
        string key, Func<string, Task<RestoredDependencies>> restore)
    {
        Task<RestoredDependencies> task;
        bool reused;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            reused = _entries.TryGetValue(key, out task!);
            if (!reused)
            {
                var directory = Directory.CreateTempSubdirectory("testwizz-restore-").FullName;
                _directories.Add(directory);
                Restores++;
                task = Task.Run(() => restore(directory));
                _entries[key] = task;
            }
        }

        try
        {
            var restored = await task.ConfigureAwait(false);
            if (!restored.Succeeded)
            {
                Forget(key, task);
            }

            return (restored, reused);
        }
        catch
        {
            Forget(key, task);
            throw;
        }
    }

    /// <summary>A digest of everything restore can see, so only truly identical restores are shared.</summary>
    internal static string Key(string root, IEnumerable<string> restoreFiles, string image, string testProject)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(value));
            hash.AppendData([0]);
        }

        Add(image);
        Add(testProject);
        foreach (var file in restoreFiles.Order(StringComparer.Ordinal))
        {
            var path = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                continue;
            }

            Add(file);
            using var stream = File.OpenRead(path);
            Add(Convert.ToHexString(SHA256.HashData(stream)));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private void Forget(string key, Task<RestoredDependencies> task)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var current) && current == task)
            {
                _entries.Remove(key);
            }
        }
    }

    public void Dispose()
    {
        List<string> directories;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            directories = [.. _directories];
            _entries.Clear();
        }

        foreach (var directory in directories)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left in the system temp folder; it holds only project files and downloaded packages.
            }
        }
    }
}

/// <param name="Workspace">Restore files plus restore's <c>obj</c> output, laid out like a run workspace.</param>
/// <param name="Packages">The NuGet package folder; mounted read-only into build and test containers.</param>
internal sealed record RestoredDependencies(
    bool Succeeded, int ExitCode, long DurationMs, bool TimedOut, string Log, string Workspace, string Packages);
