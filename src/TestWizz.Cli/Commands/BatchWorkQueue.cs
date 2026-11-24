namespace TestWizz.Cli.Commands;

/// <summary>
/// Keeps a bounded number of independent work items in flight. Unlike waves, the next
/// item starts as soon as a worker completes; one slow model response never idles other slots.
/// Completion callbacks run on one coordinator and can safely persist checkpoints.
/// </summary>
internal static class BatchWorkQueue
{
    internal sealed record Summary(int Completed, int Started, bool Stopped);

    public static async Task<Summary> RunAsync<TItem, TResult>(
        IReadOnlyList<TItem> items,
        int maxWorkers,
        Func<TItem, CancellationToken, Task<TResult>> work,
        Action<TItem, TResult, int> completed,
        Func<TResult, bool>? stopOnResult,
        Action<int, int>? heartbeat,
        CancellationToken cancellationToken,
        Func<int>? desiredWorkers = null)
    {
        if (maxWorkers < 1 || maxWorkers > 8)
            throw new ArgumentOutOfRangeException(nameof(maxWorkers));

        var active = new List<Task<(int Index, TResult Result)>>();
        var next = 0;
        var processed = 0;
        var stopped = false;

        void Fill()
        {
            var slots = Math.Clamp(desiredWorkers?.Invoke() ?? maxWorkers, 1, maxWorkers);
            while (!stopped && next < items.Count && active.Count < slots)
            {
                var index = next++;
                // Work may perform CPU-bound Roslyn analysis before asynchronous model I/O.
                active.Add(Task.Run(async () =>
                    (index, await work(items[index], cancellationToken).ConfigureAwait(false)),
                    cancellationToken));
            }
        }

        try
        {
            Fill();
            while (active.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var selection = Task.WhenAny(active);
                using var interval = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var beat = Task.Delay(TimeSpan.FromSeconds(15), interval.Token);
                var signal = await Task.WhenAny(selection, beat).ConfigureAwait(false);
                if (signal == beat)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    heartbeat?.Invoke(processed, active.Count);
                    continue;
                }
                interval.Cancel();
                var finished = await selection.ConfigureAwait(false);
                active.Remove(finished);
                var (index, result) = await finished.ConfigureAwait(false);
                processed++;
                // Checkpoint before starting the replacement task so a crash preserves progress.
                completed(items[index], result, active.Count);
                if (stopOnResult?.Invoke(result) == true) stopped = true;
                Fill();
            }
        }
        catch
        {
            // A canceled model request or failed coordinator must not abandon other
            // in-flight container teardown. Drain first, then preserve the original error.
            try { await Task.WhenAll(active).ConfigureAwait(false); }
            catch (Exception) { /* The original error has priority. */ }
            throw;
        }

        return new Summary(processed, next, stopped);
    }
}
