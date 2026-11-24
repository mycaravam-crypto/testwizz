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
        var failureObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
            (i, result, _) =>
            {
                finished.Add(i);
                if (result < 0) failureObserved.TrySetResult();
            },
            result => result < 0,
            null,
            TestContext.Current.CancellationToken);
        var signal = await Task.WhenAny(startedSecond.Task,
            Task.Delay(5000, TestContext.Current.CancellationToken));
        Assert.Same(startedSecond.Task, signal);
        Assert.Equal(2, started.Count);
        // Release the slow item only after the coordinator has seen the failure. Otherwise, when the thread
        // pool runs item 1 before item 0, item 1 can complete first and legitimately trigger a refill.
        var observed = await Task.WhenAny(failureObserved.Task,
            Task.Delay(5000, TestContext.Current.CancellationToken));
        Assert.Same(failureObserved.Task, observed);
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
    public void DefaultAdaptiveLimit_RampsUpOnRollingWindowsAndBacksOffOnlyOnSustainedRegression()
    {
        var tuning = new AdaptiveWorkerLimit(4);
        Assert.Equal(1, tuning.Current);
        tuning.Completed(TimeSpan.FromSeconds(1));      // level 1: one window of 1 item/s
        Assert.Equal(2, tuning.Current);
        // Level 2 windows hold two completions; it needs two windows before it may change.
        tuning.Completed(TimeSpan.FromSeconds(1.5));
        tuning.Completed(TimeSpan.FromSeconds(2));      // window 1: 2 items/s
        Assert.Equal(2, tuning.Current);
        tuning.Completed(TimeSpan.FromSeconds(2.5));
        tuning.Completed(TimeSpan.FromSeconds(3));      // window 2: rolling mean 2/s > 1/s + 10%
        Assert.Equal(3, tuning.Current);

        // Level 3 windows hold three completions; only the window's last completion time matters.
        void Window(double end) { tuning.Completed(TimeSpan.FromSeconds(end - 0.2)); tuning.Completed(TimeSpan.FromSeconds(end - 0.1)); tuning.Completed(TimeSpan.FromSeconds(end)); }
        Window(4.25);                                   // 3 items in 1.25 s = 2.4/s (measuring)
        Window(6.25);                                   // one slow window, 1.5/s: rolling mean 1.95/s
        Assert.Equal(3, tuning.Current);                // stays inside the band around level 2 (2/s)
        Window(9.25);                                   // sustained regression, 1/s: rolling mean 1.63/s
        Assert.Equal(2, tuning.Current);                // < 2/s - 15%: one step down
        Assert.Equal("decrease", tuning.Controller.LastDecision);
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

public sealed class BatchPipelineTests
{
    [Fact]
    public async Task SecondStageStartsWhileFirstStageIsStillRunning()
    {
        // Item 1's first stage only finishes once item 0's second stage has started. Without overlap this deadlocks.
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new ConcurrentQueue<string>();

        var run = BatchWorkQueue.RunPipelineAsync<int, int, int>(
            [0, 1],
            [],
            1,
            async (item, _) =>
            {
                if (item == 1) await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                order.Enqueue($"first {item}");
                return item;
            },
            (_, _, _) => true,
            1,
            (item, _) =>
            {
                order.Enqueue($"second {item}");
                secondStarted.TrySetResult();
                return Task.FromResult(item);
            },
            (_, _, _) => { },
            null,
            TestContext.Current.CancellationToken);

        await run.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.Equal(["first 0", "second 0", "first 1", "second 1"], order);
    }

    [Fact]
    public async Task CallbacksNeverOverlapAndEveryItemIsProcessedOnce()
    {
        var inCallback = 0;
        var overlapped = false;
        var firstDone = new ConcurrentBag<int>();
        var secondDone = new List<int>();
        void Enter()
        {
            if (Interlocked.Increment(ref inCallback) > 1) overlapped = true;
            Thread.Sleep(1);
        }

        await BatchWorkQueue.RunPipelineAsync<int, int, int>(
            [.. Enumerable.Range(0, 40)],
            [100, 101],
            4,
            async (item, ct) => { await Task.Delay(item % 5, ct); return item; },
            (item, _, _) => { Enter(); firstDone.Add(item); Interlocked.Decrement(ref inCallback); return item % 3 != 0; },
            3,
            async (item, ct) => { await Task.Delay(item % 7, ct); return item; },
            (item, _, _) => { Enter(); secondDone.Add(item); Interlocked.Decrement(ref inCallback); },
            null,
            TestContext.Current.CancellationToken);

        Assert.False(overlapped);
        Assert.Equal(Enumerable.Range(0, 40), firstDone.Order());
        // Only items the first-stage callback accepted, plus the second-stage-only items, reach the second stage.
        Assert.Equal([.. Enumerable.Range(0, 40).Where(i => i % 3 != 0), 100, 101], secondDone.Order());
    }

    [Fact]
    public async Task SecondStageRespectsItsOwnLimit()
    {
        var active = 0;
        var peak = 0;

        await BatchWorkQueue.RunPipelineAsync<int, int, int>(
            [.. Enumerable.Range(0, 12)],
            [],
            4,
            (item, _) => Task.FromResult(item),
            (_, _, _) => true,
            2,
            async (item, ct) =>
            {
                var now = Interlocked.Increment(ref active);
                InterlockedMax(ref peak, now);
                await Task.Delay(5, ct);
                Interlocked.Decrement(ref active);
                return item;
            },
            (_, _, _) => { },
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, peak);
    }

    [Fact]
    public async Task FailureDrainsBothStagesAndKeepsTheOriginalError()
    {
        var verifying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var verified = 0;

        var run = BatchWorkQueue.RunPipelineAsync<int, int, int>(
            [0, 1],
            [],
            1,
            async (item, _) =>
            {
                if (item == 1)
                {
                    await verifying.Task;
                    throw new InvalidOperationException("model failed");
                }

                return item;
            },
            (_, _, _) => true,
            1,
            async (item, _) =>
            {
                verifying.TrySetResult();
                await release.Task;
                Interlocked.Increment(ref verified);
                return item;
            },
            (_, _, _) => { },
            null,
            TestContext.Current.CancellationToken);

        await verifying.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(run.IsCompleted);
        release.SetResult();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => run);
        Assert.Equal("model failed", error.Message);
        // The running verification (container teardown in real use) finished before the error surfaced.
        Assert.Equal(1, verified);
    }

    private static void InterlockedMax(ref int destination, int value)
    {
        int previous;
        do
        {
            previous = destination;
            if (value <= previous) return;
        } while (Interlocked.CompareExchange(ref destination, value, previous) != previous);
    }
}
