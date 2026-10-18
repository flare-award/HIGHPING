using LatencyStudio.Core.Configuration;

namespace LatencyStudio.Core.Latency;

public readonly record struct DirectionalDelay(double OutgoingMs, double IncomingMs)
{
    public double AddedRttMs => OutgoingMs + IncomingMs;
}

public readonly record struct LatencyPlan(
    bool IsCalibrated,
    DirectionalDelay Delay,
    double? BaselineRttMs,
    double? AddedRttMs);

public static class LatencyPolicy
{
    public static LatencyPlan Calculate(AppSettings settings, double? baselineRttMs)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.Mode == LatencyMode.Fixed)
        {
            var delay = new DirectionalDelay(settings.OutgoingDelayMs, settings.IncomingDelayMs);
            return new LatencyPlan(true, delay, baselineRttMs, delay.AddedRttMs);
        }

        if (!baselineRttMs.HasValue || !double.IsFinite(baselineRttMs.Value) || baselineRttMs.Value < 0)
        {
            return new LatencyPlan(false, new DirectionalDelay(0, 0), null, null);
        }

        var additionalRtt = Math.Max(0, settings.TargetRttMs - baselineRttMs.Value);
        var perDirection = additionalRtt / 2d;
        return new LatencyPlan(true, new DirectionalDelay(perDirection, perDirection), baselineRttMs, additionalRtt);
    }

    public static double ToStopwatchTicks(double milliseconds, long frequency)
    {
        if (!double.IsFinite(milliseconds) || milliseconds <= 0)
        {
            return 0;
        }

        if (frequency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frequency));
        }

        return milliseconds * frequency / 1_000d;
    }

    public static int ToRoundedMilliseconds(double milliseconds)
    {
        return (int)Math.Clamp(Math.Round(milliseconds, MidpointRounding.AwayFromZero), 0, int.MaxValue);
    }
}
