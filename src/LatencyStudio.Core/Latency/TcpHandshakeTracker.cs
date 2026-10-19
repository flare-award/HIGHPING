namespace LatencyStudio.Core.Latency;

/// <summary>
/// Learns a per-TCP-flow baseline from the observed outbound SYN to inbound SYN-ACK.
/// Any known delay applied to the outbound SYN is subtracted; the inbound packet has
/// not yet been delayed at that observation point.
/// </summary>
public sealed class TcpHandshakeTracker
{
    private const int MaximumEntries = 4_096;
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan BaselineLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaximumHandshakeSample = TimeSpan.FromSeconds(30);

    private readonly object _sync = new();
    private readonly Dictionary<TcpFlowKey, PendingSyn> _pending = new();
    private readonly Dictionary<TcpFlowKey, BaselineSample> _baselines = new();
    private readonly long _frequency;
    private long _observations;

    public TcpHandshakeTracker(long stopwatchFrequency)
    {
        if (stopwatchFrequency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stopwatchFrequency));
        }

        _frequency = stopwatchFrequency;
    }

    public void RecordOutboundSyn(TcpFlowKey flow, long capturedAtTicks, long appliedDelayTicks)
    {
        lock (_sync)
        {
            _pending[flow] = new PendingSyn(capturedAtTicks, Math.Max(0, appliedDelayTicks));
            PruneIfNeeded(capturedAtTicks);
        }
    }

    /// <param name="inboundFlow">The flow key as present on the inbound packet (remote to local).</param>
    public bool ObserveInboundSynAck(TcpFlowKey inboundFlow, long capturedAtTicks, out double baselineRttMs)
    {
        baselineRttMs = 0;
        lock (_sync)
        {
            var outboundFlow = inboundFlow.Reverse();
            if (!_pending.Remove(outboundFlow, out var pending))
            {
                PruneIfNeeded(capturedAtTicks);
                return false;
            }

            var elapsed = capturedAtTicks - pending.CapturedAtTicks;
            var maximumTicks = ToTicks(MaximumHandshakeSample);
            if (elapsed <= 0 || elapsed > maximumTicks)
            {
                PruneIfNeeded(capturedAtTicks);
                return false;
            }

            var networkTicks = Math.Max(0, elapsed - pending.AppliedDelayTicks);
            baselineRttMs = networkTicks * 1_000d / _frequency;
            if (!double.IsFinite(baselineRttMs))
            {
                baselineRttMs = 0;
                return false;
            }

            _baselines[outboundFlow] = new BaselineSample(baselineRttMs, capturedAtTicks);
            PruneIfNeeded(capturedAtTicks);
            return true;
        }
    }

    public bool TryGetBaseline(TcpFlowKey outboundFlow, long nowTicks, out double baselineRttMs)
    {
        lock (_sync)
        {
            PruneIfNeeded(nowTicks);
            if (_baselines.TryGetValue(outboundFlow, out var sample))
            {
                baselineRttMs = sample.RttMs;
                return true;
            }
        }

        baselineRttMs = 0;
        return false;
    }

    public int GetCalibratedFlowCount(long nowTicks)
    {
        lock (_sync)
        {
            PruneIfNeeded(nowTicks);
            return _baselines.Count;
        }
    }

    private void PruneIfNeeded(long nowTicks)
    {
        if (++_observations % 128 != 0 && _pending.Count <= MaximumEntries && _baselines.Count <= MaximumEntries)
        {
            return;
        }

        var pendingLifetime = ToTicks(PendingLifetime);
        foreach (var key in _pending.Where(pair => nowTicks - pair.Value.CapturedAtTicks > pendingLifetime)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _pending.Remove(key);
        }

        var baselineLifetime = ToTicks(BaselineLifetime);
        foreach (var key in _baselines.Where(pair => nowTicks - pair.Value.CapturedAtTicks > baselineLifetime)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _baselines.Remove(key);
        }

        TrimOldest(_pending, MaximumEntries, static pair => pair.Value.CapturedAtTicks);
        TrimOldest(_baselines, MaximumEntries, static pair => pair.Value.CapturedAtTicks);
    }

    private long ToTicks(TimeSpan value) => (long)(value.TotalSeconds * _frequency);

    private static void TrimOldest<TValue>(Dictionary<TcpFlowKey, TValue> values, int limit, Func<KeyValuePair<TcpFlowKey, TValue>, long> timestamp)
    {
        var excess = values.Count - limit;
        if (excess <= 0)
        {
            return;
        }

        foreach (var key in values.OrderBy(timestamp).Take(excess).Select(pair => pair.Key).ToArray())
        {
            values.Remove(key);
        }
    }

    private readonly record struct PendingSyn(long CapturedAtTicks, long AppliedDelayTicks);
    private readonly record struct BaselineSample(double RttMs, long CapturedAtTicks);
}
