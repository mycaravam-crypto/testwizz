namespace TestWizz.Cli;

/// <summary>
/// Chooses how many model requests a batch runs at once from measured throughput (targets per second per completion window).
/// A single slow or fast window never changes the level on its own:
/// <list type="bullet">
/// <item>Throughput is a rolling mean over the last <see cref="Window"/> windows at the current level.</item>
/// <item>The level rises only when that mean beats the level below by more than <see cref="IncreaseMargin"/>
/// (or at level 1, where there is nothing to compare with); a plateau inside the band keeps the level.</item>
/// <item>It falls only when the mean drops below the level below by more than <see cref="DecreaseMargin"/>; it then
/// holds for <see cref="HoldWindows"/> windows before probing upwards again.</item>
/// </list>
/// The level always stays within 1 and the caller's maximum (<c>--max-workers</c>). The class is pure and
/// deterministic: callers measure time, tests feed samples.
/// </summary>
internal sealed class AdaptiveConcurrency
{
    public const int Window = 3;
    public const int RequiredSamples = 2;
    public const double IncreaseMargin = 0.10;
    public const double DecreaseMargin = 0.15;
    public const int HoldWindows = 3;

    private readonly Queue<double> _samples = new();
    private readonly Dictionary<int, double> _levelMeans = [];
    private int _hold;

    public AdaptiveConcurrency(int maxWorkers)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxWorkers, 1);
        MaxWorkers = maxWorkers;
    }

    public int MaxWorkers { get; }

    public int Workers { get; private set; } = 1;

    /// <summary>Rolling mean throughput at the current level, or 0 before the first sample.</summary>
    public double RollingThroughput => _samples.Count == 0 ? 0 : _samples.Average();

    /// <summary>Last decision, for progress output: <c>increase</c>, <c>decrease</c>, <c>hold</c> or <c>measure</c>.</summary>
    public string LastDecision { get; private set; } = "measure";

    /// <summary>Records a completion window of <paramref name="completed"/> targets that took <paramref name="elapsed"/>.</summary>
    public int Record(int completed, TimeSpan elapsed)
    {
        if (completed <= 0) return Workers;
        var throughput = completed / Math.Max(elapsed.TotalSeconds, 0.001);
        _samples.Enqueue(throughput);
        while (_samples.Count > Window) _samples.Dequeue();
        if (_hold > 0) _hold--;

        var required = Workers == 1 && !_levelMeans.ContainsKey(1) ? 1 : RequiredSamples;
        if (_samples.Count < required)
        {
            LastDecision = "measure";
            return Workers;
        }

        var mean = RollingThroughput;
        _levelMeans[Workers] = mean;
        var below = Workers > 1 && _levelMeans.TryGetValue(Workers - 1, out var lower) ? lower : (double?)null;
        if (below is { } reference && mean < reference * (1 - DecreaseMargin))
        {
            Change(Workers - 1, "decrease");
            _hold = HoldWindows;
        }
        else if (Workers < MaxWorkers && _hold == 0 && (below is null || mean > below.Value * (1 + IncreaseMargin)))
        {
            Change(Workers + 1, "increase");
        }
        else
        {
            LastDecision = "hold";
        }

        return Workers;
    }

    private void Change(int workers, string decision)
    {
        Workers = Math.Clamp(workers, 1, MaxWorkers);
        _samples.Clear();
        LastDecision = decision;
    }
}
