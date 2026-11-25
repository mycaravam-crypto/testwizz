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

    /// <summary>
    /// Two bounded stages that overlap: an item enters the second stage as soon as its first stage completes
    /// (and <paramref name="firstCompleted"/> asks for it), while other items are still in the first stage.
    /// <paramref name="secondOnly"/> items skip the first stage. Every callback runs on this one coordinator,
    /// one at a time, so callbacks can safely mutate shared state and persist checkpoints. Second-stage items
    /// start in the order they became ready.
    /// </summary>
    public static async Task RunPipelineAsync<TItem, TFirst, TSecond>(
        IReadOnlyList<TItem> items,
        IReadOnlyList<TItem> secondOnly,
        int maxFirst,
        Func<TItem, CancellationToken, Task<TFirst>> first,
        Func<TItem, TFirst, int, bool> firstCompleted,
        int maxSecond,
        Func<TItem, CancellationToken, Task<TSecond>> second,
        Action<TItem, TSecond, int> secondCompleted,
        Action<int, int>? heartbeat,
        CancellationToken cancellationToken,
        Func<int>? desiredFirst = null,
        Func<bool>? stopFirst = null)
    {
        if (maxFirst < 1 || maxFirst > 8)
            throw new ArgumentOutOfRangeException(nameof(maxFirst));
        if (maxSecond < 1 || maxSecond > 8)
            throw new ArgumentOutOfRangeException(nameof(maxSecond));

        var firstActive = new List<Task<(TItem Item, TFirst Result)>>();
        var secondActive = new List<Task<(TItem Item, TSecond Result)>>();
        var ready = new Queue<TItem>(secondOnly);
        var next = 0;

        void Fill()
        {
            var slots = Math.Clamp(desiredFirst?.Invoke() ?? maxFirst, 1, maxFirst);
            // A stop only prevents new first-stage work: in-flight items finish and are reported.
            while (next < items.Count && firstActive.Count < slots && stopFirst?.Invoke() != true)
            {
                var item = items[next++];
                firstActive.Add(Task.Run(async () => (item, await first(item, cancellationToken).ConfigureAwait(false)), cancellationToken));
            }

            while (ready.Count > 0 && secondActive.Count < maxSecond)
            {
                var item = ready.Dequeue();
                secondActive.Add(Task.Run(async () => (item, await second(item, cancellationToken).ConfigureAwait(false)), cancellationToken));
            }
        }

        try
        {
            Fill();
            while (firstActive.Count + secondActive.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var selection = Task.WhenAny([.. firstActive.Cast<Task>(), .. secondActive]);
                using var interval = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var beat = Task.Delay(TimeSpan.FromSeconds(15), interval.Token);
                var signal = await Task.WhenAny(selection, beat).ConfigureAwait(false);
                if (signal == beat)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    heartbeat?.Invoke(firstActive.Count, secondActive.Count);
                    continue;
                }

                interval.Cancel();
                var finished = await selection.ConfigureAwait(false);
                // Checkpoint before starting replacements so a crash preserves progress.
                if (finished is Task<(TItem Item, TFirst Result)> generated && firstActive.Remove(generated))
                {
                    var (item, result) = await generated.ConfigureAwait(false);
                    if (firstCompleted(item, result, firstActive.Count))
                        ready.Enqueue(item);
                }
                else
                {
                    var verified = (Task<(TItem Item, TSecond Result)>)finished;
                    secondActive.Remove(verified);
                    var (item, result) = await verified.ConfigureAwait(false);
                    secondCompleted(item, result, secondActive.Count);
                }

                Fill();
            }
        }
        catch
        {
            // Never abandon in-flight model requests or container teardown; drain, then keep the original error.
            try { await Task.WhenAll([.. firstActive.Cast<Task>(), .. secondActive]).ConfigureAwait(false); }
            catch (Exception) { /* The original error has priority. */ }
            throw;
        }
    }
}
