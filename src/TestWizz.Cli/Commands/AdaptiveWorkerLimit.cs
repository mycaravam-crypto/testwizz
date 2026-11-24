namespace TestWizz.Cli.Commands;

/// <summary>
/// Default (no explicit <c>--max-workers</c>) concurrency for the continuously replenished
/// <see cref="BatchWorkQueue"/>. It starts with one model request so local inference servers are not
/// overloaded. Completions are grouped into windows of <see cref="Current"/> items, and each window's
/// throughput feeds <see cref="AdaptiveConcurrency"/>: a rolling mean with hysteresis, so one slow or
/// fast window never changes the level on its own.
/// </summary>
internal sealed class AdaptiveWorkerLimit(int maximum)
{
    private readonly AdaptiveConcurrency _controller = new(maximum);
    private int _inWindow;
    private TimeSpan _windowStart;

    public int Current => _controller.Workers;

    /// <summary>Rolling throughput and last decision, for progress output.</summary>
    public AdaptiveConcurrency Controller => _controller;

    /// <summary>Records one completed item at <paramref name="elapsed"/> since the batch started.</summary>
    public void Completed(TimeSpan elapsed)
    {
        _inWindow++;
        if (_inWindow < Current) return;
        var interval = elapsed - _windowStart;
        if (interval <= TimeSpan.Zero) return;
        _controller.Record(_inWindow, interval);
        _inWindow = 0;
        _windowStart = elapsed;
    }
}
