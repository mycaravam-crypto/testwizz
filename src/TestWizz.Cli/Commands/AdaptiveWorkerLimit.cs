namespace TestWizz.Cli.Commands;

/// <summary>
/// Maintains the old safe one-worker startup for local inference servers when no
/// explicit --max-workers setting was supplied. Replenishes slots continuously,
/// increasing cautiously and backing off on a measured throughput regression.
/// </summary>
internal sealed class AdaptiveWorkerLimit(int maximum)
{
    private int _desired = 1;
    private int _inWindow;
    private TimeSpan _lastSample;
    private double _previousThroughput;

    public int Current => _desired;

    public void Completed(TimeSpan elapsed)
    {
        _inWindow++;
        if (_inWindow < _desired) return;
        var seconds = (elapsed - _lastSample).TotalSeconds;
        if (seconds <= 0) return;
        var throughput = _inWindow / seconds;
        if (_previousThroughput > 0 && throughput < _previousThroughput * 0.9)
            _desired = Math.Max(1, _desired - 1);
        else if (_desired < maximum)
            _desired++;
        _previousThroughput = throughput;
        _lastSample = elapsed;
        _inWindow = 0;
    }
}
