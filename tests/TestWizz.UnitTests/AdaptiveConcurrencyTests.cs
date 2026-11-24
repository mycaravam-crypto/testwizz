using TestWizz.Cli;

namespace TestWizz.UnitTests;

public sealed class AdaptiveConcurrencyTests
{
    /// <summary>Feeds one completion window of <c>workers</c> targets finishing at <paramref name="throughput"/> targets/s.</summary>
    private static int Wave(AdaptiveConcurrency controller, double throughput) =>
        controller.Record(controller.Workers, TimeSpan.FromSeconds(controller.Workers / throughput));

    [Fact]
    public void ScalingThroughput_RampsUpToTheConfiguredMaximumAndNoFurther()
    {
        var controller = new AdaptiveConcurrency(maxWorkers: 3);

        var levels = new List<int>();
        for (var i = 0; i < 12; i++) levels.Add(Wave(controller, controller.Workers));

        Assert.Equal(3, controller.Workers);
        Assert.All(levels, level => Assert.InRange(level, 1, 3));
        // Level 1 needs one sample; each higher level needs a rolling window of two before it may move.
        Assert.Equal([2, 2, 3, 3, 3], levels.Take(5));
    }

    [Fact]
    public void Plateau_HoldsTheLevelInsteadOfOscillating()
    {
        var controller = new AdaptiveConcurrency(maxWorkers: 8);
        Wave(controller, 1.0);
        Wave(controller, 1.8);
        Wave(controller, 1.8);
        Assert.Equal(3, controller.Workers);

        // More workers no longer help: within the hysteresis band the level stays put.
        var levels = Enumerable.Range(0, 10).Select(_ => Wave(controller, 1.85)).ToList();

        Assert.All(levels, level => Assert.Equal(3, level));
        Assert.Equal("hold", controller.LastDecision);
    }

    [Fact]
    public void SingleSlowWave_DoesNotReduceWorkers()
    {
        var controller = new AdaptiveConcurrency(maxWorkers: 2);
        Wave(controller, 1.0);
        Wave(controller, 2.0);
        Wave(controller, 2.0);

        Wave(controller, 0.5); // rolling mean (2, 2, 0.5) = 1.5 stays inside the band
        Wave(controller, 2.0);

        Assert.Equal(2, controller.Workers);
    }

    [Fact]
    public void SustainedDegradation_StepsDownOnceAndHoldsBeforeProbingAgain()
    {
        var controller = new AdaptiveConcurrency(maxWorkers: 4);
        Wave(controller, 2.0);
        Wave(controller, 2.0);
        Wave(controller, 2.0);
        Assert.Equal(2, controller.Workers);

        Assert.Equal(2, Wave(controller, 1.2)); // rolling mean (2, 2, 1.2) is still inside the band
        Assert.Equal(1, Wave(controller, 1.2)); // (2, 1.2, 1.2) falls below the level below by more than 15 %
        Assert.Equal("decrease", controller.LastDecision);

        // Even with strong samples the controller holds the lower level for the hold period.
        var levels = Enumerable.Range(0, AdaptiveConcurrency.HoldWindows - 1).Select(_ => Wave(controller, 3.0)).ToList();
        Assert.All(levels, level => Assert.Equal(1, level));
        Assert.Equal(2, Wave(controller, 3.0));
    }

    [Fact]
    public void MaximumOfOne_NeverAddsWorkers()
    {
        var controller = new AdaptiveConcurrency(maxWorkers: 1);
        for (var i = 0; i < 5; i++) Assert.Equal(1, Wave(controller, 10));
    }

    [Fact]
    public void EmptyWave_IsIgnored()
    {
        var controller = new AdaptiveConcurrency(maxWorkers: 4);
        Assert.Equal(1, controller.Record(0, TimeSpan.Zero));
        Assert.Equal(0, controller.RollingThroughput);
    }
}
