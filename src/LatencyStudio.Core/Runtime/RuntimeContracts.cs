using LatencyStudio.Core.Configuration;
using LatencyStudio.Core.Latency;

namespace LatencyStudio.Core.Runtime;

public enum TrafficEngineState
{
    Disabled,
    Starting,
    Running,
    Stopping,
    Error
}

public sealed record TrafficRuntimeSnapshot(
    long ProcessedPackets,
    long DelayedPackets,
    long BypassedPackets,
    long ErrorCount,
    int QueuedPackets,
    long QueuedBytes,
    int CalibratedTcpFlows,
    string? LastError);

public sealed record TrafficEngineSnapshot(
    TrafficEngineState State,
    long ProcessedPackets,
    long DelayedPackets,
    long BypassedPackets,
    long ErrorCount,
    int QueuedPackets,
    long QueuedBytes,
    int CalibratedTcpFlows,
    string? LastError);

public sealed class RuntimeFaultEventArgs(string message, Exception? exception = null) : EventArgs
{
    public string Message { get; } = message;
    public Exception? Exception { get; } = exception;
}

public interface ITrafficRuntime : IAsyncDisposable
{
    event EventHandler<RuntimeFaultEventArgs>? Faulted;

    Task StartAsync(AppSettings settings, CancellationToken cancellationToken = default);
    void UpdateSettings(AppSettings settings);
    Task StopAsync(CancellationToken cancellationToken = default);
    TrafficRuntimeSnapshot GetSnapshot();
    bool TryGetBaseline(TcpFlowKey outboundFlow, out double baselineRttMs);
}
