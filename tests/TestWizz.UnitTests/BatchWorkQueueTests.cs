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
        var startedThird = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new ConcurrentQueue<int>();
        var finished = new List<int>();

        int[] targets = [0, 1, 2];
        var run = BatchWorkQueue.RunAsync(
            targets,
            2,
            (target, _) =>
            {
                started.Enqueue(target);
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

        var initial = await Task.WhenAny(
            Task.Run(async () =>
            {
                while (started.Count < 2)
                    await Task.Delay(10, TestContext.Current.CancellationToken);
            }, TestContext.Current.CancellationToken),
            Task.Delay(5000, TestContext.Current.CancellationToken));
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
        var finished = new List<int>();
        int[] targets = [0, 1, 2, 3];
        var run = BatchWorkQueue.RunAsync(
            targets, 2,
            (i, _) =>
            {
                started.Enqueue(i);
                return i == 0 ? Task.FromResult(-1) : slow.Task;
            },
            (i, result, _) => finished.Add(i),
            result => result < 0,
            null,
            TestContext.Current.CancellationToken);
        var early = await Task.WhenAny(run, Task.Delay(150, TestContext.Current.CancellationToken));
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
