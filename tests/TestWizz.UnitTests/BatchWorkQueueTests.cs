using System.Collections.Concurrent;
using TestWizz.Cli.Commands;

namespace TestWizz.UnitTests;

public sealed class BatchWorkQueueTests
{
    [Fact]
    public async Task ReplacesFinishedWorkerImmediatelyWithoutWaitingForSlowPeer()
    {
        var slow = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fast = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedThird = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new ConcurrentQueue<int>();
        var startedCount = 0;
        var finished = new List<int>();

        int[] targets = [0, 1, 2];
        var run = BatchWorkQueue.RunAsync(
            targets,
            2,
            (target, _) =>
            {
                started.Enqueue(target);
                if (Interlocked.Increment(ref startedCount) == 2) startedSecond.TrySetResult();
                if (target == 2) startedThird.TrySetResult();
                return target switch
                {
                    0 => slow.Task,
                    1 => fast.Task,
                    _ => Task.FromResult(2),
                };
            },
            (target, result, _) => finished.Add(target),
            null, null, TestContext.Current.CancellationToken);

        var initial = await Task.WhenAny(startedSecond.Task,
            Task.Delay(5000, TestContext.Current.CancellationToken));
        Assert.Same(startedSecond.Task, initial);
        Assert.Equal(2, started.Count);
        Assert.DoesNotContain(2, started);
        fast.SetResult(1);
        var next = await Task.WhenAny(startedThird.Task, Task.Delay(5000, TestContext.Current.CancellationToken));
        Assert.Same(startedThird.Task, next);
        Assert.False(slow.Task.IsCompleted);
        slow.SetResult(0);
        var outcome = await run;
        Assert.Equal(3, outcome.Started);
        Assert.Equal(3, outcome.Completed);
        Assert.False(outcome.Stopped);
        Assert.Equal(3, finished.Count);
        Assert.Equal(1, finished[0]);
        Assert.Contains(0, finished);
        Assert.Contains(2, finished);
    }

    [Fact]
    public async Task LimitsActualConcurrencyAndSerializesCompletionCallbacks()
    {
        var active = 0;
        var peak = 0;
        var callbacks = new List<int>();
        var result = await BatchWorkQueue.RunAsync(
            Enumerable.Range(0, 12).ToArray(), 3,
            async (i, ct) =>
            {
                var now = Interlocked.Increment(ref active);
                InterlockedExtensions.UpdateMaximum(ref peak, now);
                await Task.Delay(20, ct);
                Interlocked.Decrement(ref active);
                return i;
            },
            (item, value, remaining) => callbacks.Add(value),
            null, null, TestContext.Current.CancellationToken);
        Assert.Equal(12, result.Completed);
        Assert.Equal(12, result.Started);
        Assert.InRange(peak, 2, 3);
        Assert.Equal(12, callbacks.Distinct().Count());
        Assert.Equal(0, active);
    }

    [Fact]
    public async Task FailureStopsDispatchButDrainsAlreadyRunningWork()
    {
        var slow = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new ConcurrentQueue<int>();
        var startedSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var countStarted = 0;
        var finished = new List<int>();
        int[] targets = [0, 1, 2, 3];
        var run = BatchWorkQueue.RunAsync(
            targets, 2,
            (i, _) =>
            {
                started.Enqueue(i);
                if (Interlocked.Increment(ref countStarted) == 2) startedSecond.TrySetResult();
                return i == 0 ? Task.FromResult(-1) : slow.Task;
            },
            (i, result, _) => finished.Add(i),
            result => result < 0,
            null,
            TestContext.Current.CancellationToken);
        var signal = await Task.WhenAny(startedSecond.Task,
            Task.Delay(5000, TestContext.Current.CancellationToken));
        Assert.Same(startedSecond.Task, signal);
        Assert.Equal(2, started.Count);
        Assert.False(run.IsCompleted);
        slow.SetResult(1);
        var outcome = await run;
        Assert.True(outcome.Stopped);
        Assert.Equal(2, outcome.Started);
        Assert.Equal(2, outcome.Completed);
        Assert.Equal(2, finished.Count);
    }

    [Fact]
    public async Task CancellationWaitsForAllRunningWorkersToCleanUp()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedCount = 0;
        var cleaned = 0;
        int[] targets = [0, 1, 2];
        var run = BatchWorkQueue.RunAsync(
            targets, 2,
            async (target, token) =>
            {
                if (Interlocked.Increment(ref startedCount) == 2) started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException)
                {
                    await Task.Delay(10, TestContext.Current.CancellationToken);
                    Interlocked.Increment(ref cleaned);
                    throw;
                }
                return target;
            },
            (_, _, _) => { },
            null, null, cancellation.Token);
        var ready = await Task.WhenAny(started.Task,
            Task.Delay(5000, TestContext.Current.CancellationToken));
        Assert.Same(started.Task, ready);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(2, cleaned);
    }

    [Fact]
    public void DefaultAdaptiveLimit_RampsUpAndBacksOffWhenThroughputFalls()
    {
        var tuning = new AdaptiveWorkerLimit(4);
        Assert.Equal(1, tuning.Current);
        tuning.Completed(TimeSpan.FromSeconds(1));
        Assert.Equal(2, tuning.Current);
        tuning.Completed(TimeSpan.FromSeconds(1.5));
        Assert.Equal(2, tuning.Current);
        tuning.Completed(TimeSpan.FromSeconds(2));
        Assert.Equal(3, tuning.Current);
        tuning.Completed(TimeSpan.FromSeconds(2.5));
        tuning.Completed(TimeSpan.FromSeconds(3));
        tuning.Completed(TimeSpan.FromSeconds(5));
        Assert.Equal(2, tuning.Current);
    }

    [Fact]
    public async Task RejectsInvalidWorkerCount()
    {
        int[] targets = [1];
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await BatchWorkQueue.RunAsync(targets, 0, (i, _) => Task.FromResult(i),
                (_, _, _) => { }, null, null, TestContext.Current.CancellationToken));
    }

    private static class InterlockedExtensions
    {
        public static void UpdateMaximum(ref int destination, int value)
        {
            int previous;
            do
            {
                previous = Volatile.Read(ref destination);
                if (value <= previous) return;
            } while (Interlocked.CompareExchange(ref destination, value, previous) != previous);
        }
    }
}
