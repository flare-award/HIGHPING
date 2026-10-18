using LatencyStudio.Core.Configuration;
using LatencyStudio.Core.Latency;

namespace LatencyStudio.Core.Runtime;

/// <summary>
/// Serializes start/stop operations and turns backend failures into an observable state.
/// The native packet driver is created only by the runtime factory when the user enables it.
/// </summary>
public sealed class TrafficEngineController : IAsyncDisposable
{
    private readonly Func<ITrafficRuntime> _runtimeFactory;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _sync = new();
    private ITrafficRuntime? _runtime;
    private TrafficEngineState _state = TrafficEngineState.Disabled;
    private string? _lastError;
    private AppSettings _settings = AppSettings.Defaults;

    public TrafficEngineController(Func<ITrafficRuntime> runtimeFactory)
    {
        _runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
    }

    public TrafficEngineSnapshot Snapshot
    {
        get
        {
            ITrafficRuntime? runtime;
            TrafficEngineState state;
            string? error;
            lock (_sync)
            {
                runtime = _runtime;
                state = _state;
                error = _lastError;
            }

            var runtimeSnapshot = runtime?.GetSnapshot();
            return new TrafficEngineSnapshot(
                state,
                runtimeSnapshot?.ProcessedPackets ?? 0,
                runtimeSnapshot?.DelayedPackets ?? 0,
                runtimeSnapshot?.BypassedPackets ?? 0,
                runtimeSnapshot?.ErrorCount ?? 0,
                runtimeSnapshot?.QueuedPackets ?? 0,
                runtimeSnapshot?.QueuedBytes ?? 0,
                runtimeSnapshot?.CalibratedTcpFlows ?? 0,
                runtimeSnapshot?.LastError ?? error);
        }
    }

    public AppSettings CurrentSettings
    {
        get
        {
            lock (_sync)
            {
                return _settings;
            }
        }
    }

    public bool HasRuntime
    {
        get
        {
            lock (_sync)
            {
                return _runtime is not null;
            }
        }
    }

    public async Task EnableAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var validation = AppSettingsValidator.Validate(settings);
        if (!validation.IsValid)
        {
            throw new ArgumentException(validation.ErrorSummary, nameof(settings));
        }

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ITrafficRuntime? candidate = null;
        try
        {
            lock (_sync)
            {
                if (_state == TrafficEngineState.Running && _runtime is not null)
                {
                    candidate = _runtime;
                }
                else
                {
                    _state = TrafficEngineState.Starting;
                    _lastError = null;
                    candidate = _runtimeFactory();
                    _runtime = candidate;
                    candidate.Faulted += OnRuntimeFaulted;
                }

                _settings = settings;
            }

            if (Snapshot.State == TrafficEngineState.Running)
            {
                candidate.UpdateSettings(settings);
                return;
            }

            await candidate.StartAsync(settings, cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                _settings = settings;
                if (_state == TrafficEngineState.Error)
                {
                    throw new InvalidOperationException(_lastError ?? "Сетевой обработчик завершился с ошибкой во время запуска.");
                }

                _state = TrafficEngineState.Running;
            }
        }
        catch (Exception exception)
        {
            if (candidate is not null)
            {
                try
                {
                    await candidate.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the start error. StopAsync is best-effort cleanup here.
                }

                candidate.Faulted -= OnRuntimeFaulted;
                try
                {
                    await candidate.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the original failure.
                }
            }

            lock (_sync)
            {
                if (ReferenceEquals(_runtime, candidate))
                {
                    _runtime = null;
                }

                _state = TrafficEngineState.Error;
                _lastError = exception.Message;
            }

            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public void UpdateSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var validation = AppSettingsValidator.Validate(settings);
        if (!validation.IsValid)
        {
            throw new ArgumentException(validation.ErrorSummary, nameof(settings));
        }

        ITrafficRuntime? runtime;
        lock (_sync)
        {
            _settings = settings;
            runtime = _runtime;
        }

        runtime?.UpdateSettings(settings);
    }

    public async Task DisableAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ITrafficRuntime? runtime;
            string? previousError;
            lock (_sync)
            {
                runtime = _runtime;
                previousError = _lastError;
                if (runtime is null)
                {
                    _state = TrafficEngineState.Disabled;
                    return;
                }

                _state = TrafficEngineState.Stopping;
            }

            Exception? stopError = null;
            try
            {
                await runtime.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                stopError = exception;
            }
            finally
            {
                runtime.Faulted -= OnRuntimeFaulted;
                try
                {
                    await runtime.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    stopError ??= exception;
                }

                lock (_sync)
                {
                    if (ReferenceEquals(_runtime, runtime))
                    {
                        _runtime = null;
                    }

                    _state = stopError is null ? TrafficEngineState.Disabled : TrafficEngineState.Error;
                    _lastError = stopError?.Message ?? previousError;
                }
            }

            if (stopError is not null)
            {
                throw stopError;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public bool TryGetBaseline(TcpFlowKey outboundFlow, out double baselineRttMs)
    {
        ITrafficRuntime? runtime;
        lock (_sync)
        {
            runtime = _runtime;
        }

        if (runtime is not null)
        {
            return runtime.TryGetBaseline(outboundFlow, out baselineRttMs);
        }

        baselineRttMs = 0;
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await DisableAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Dispose();
        }
    }

    private void OnRuntimeFaulted(object? sender, RuntimeFaultEventArgs eventArgs)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(sender, _runtime))
            {
                return;
            }

            _state = TrafficEngineState.Error;
            _lastError = eventArgs.Message;
        }
    }
}
