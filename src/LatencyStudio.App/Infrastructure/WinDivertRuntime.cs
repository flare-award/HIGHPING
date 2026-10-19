using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using LatencyStudio.Core.Configuration;
using LatencyStudio.Core.Latency;
using LatencyStudio.Core.Runtime;
using LatencyStudio.Core.Scheduling;

namespace LatencyStudio.App.Infrastructure;

/// <summary>
/// System-wide Windows packet diversion using the official WinDivert 2.2 network layer.
/// Captured bytes are never modified; they are re-injected with their original direction
/// and interface metadata when their monotonic deadline expires.
/// </summary>
internal sealed class WinDivertRuntime : ITrafficRuntime
{
    private readonly BoundedPacketScheduler<BufferedPacket> _scheduler = new();
    private readonly TcpHandshakeTracker _handshakeTracker = new(Stopwatch.Frequency);
    private readonly AutoResetEvent _queueChanged = new(false);
    private readonly object _sendLock = new();
    private readonly object _errorLock = new();

    private AppSettings _settings = AppSettings.Defaults;
    private HashSet<ushort> _excludedPorts = new();
    private IntPtr _handle;
    private Task? _captureTask;
    private Task? _schedulerTask;
    private int _stopRequested;
    private int _captureCompleted;
    private int _faultRaised;
    private int _disposed;
    private long _processedPackets;
    private long _delayedPackets;
    private long _bypassedPackets;
    private long _errorCount;
    private string? _lastError;

    public event EventHandler<RuntimeFaultEventArgs>? Faulted;

    public Task StartAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();

        var validation = AppSettingsValidator.Validate(settings);
        if (!validation.IsValid)
        {
            throw new ArgumentException(validation.ErrorSummary, nameof(settings));
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Захват пакетов доступен только в Windows 10/11 x64.");
        }

        if (!IsAdministrator())
        {
            throw new UnauthorizedAccessException("Для установки временного драйвера WinDivert и перехвата системного трафика нужны права администратора. Подтвердите запрос UAC Windows.");
        }

        var dllPath = Path.Combine(AppContext.BaseDirectory, "WinDivert.dll");
        var driverPath = Path.Combine(AppContext.BaseDirectory, "WinDivert64.sys");
        if (!File.Exists(dllPath) || !File.Exists(driverPath))
        {
            throw new FileNotFoundException(
                "Не найдены WinDivert.dll и/или WinDivert64.sys. Переустановите приложение из полного установщика или ZIP-архива.",
                !File.Exists(dllPath) ? dllPath : driverPath);
        }

        Volatile.Write(ref _settings, settings);
        Volatile.Write(ref _excludedPorts, ParseExcludedPorts(settings));
        Volatile.Write(ref _stopRequested, 0);
        Volatile.Write(ref _captureCompleted, 0);
        Volatile.Write(ref _faultRaised, 0);
        Volatile.Write(ref _lastError, null);

        var filter = BuildFilter(settings);
        IntPtr handle;
        try
        {
            handle = WinDivertNative.WinDivertOpen(filter, WinDivertNative.NetworkLayer, priority: 0, flags: 0);
        }
        catch (DllNotFoundException exception)
        {
            throw new InvalidOperationException("WinDivert.dll не удалось загрузить. Проверьте целостность установленной папки приложения.", exception);
        }
        catch (EntryPointNotFoundException exception)
        {
            throw new InvalidOperationException("Версия WinDivert.dll не соответствует приложению.", exception);
        }

        if (handle == WinDivertNative.InvalidHandle || handle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            throw CreateOpenException(error);
        }

        _handle = handle;
        _captureTask = Task.Factory.StartNew(
            CaptureLoop,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        _schedulerTask = Task.Factory.StartNew(
            SchedulerLoop,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        return Task.CompletedTask;
    }

    public void UpdateSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var validation = AppSettingsValidator.Validate(settings);
        if (!validation.IsValid)
        {
            throw new ArgumentException(validation.ErrorSummary, nameof(settings));
        }

        Volatile.Write(ref _settings, settings);
        Volatile.Write(ref _excludedPorts, ParseExcludedPorts(settings));
        _queueChanged.Set();
    }

    public TrafficRuntimeSnapshot GetSnapshot()
    {
        var queue = _scheduler.Snapshot;
        return new TrafficRuntimeSnapshot(
            Interlocked.Read(ref _processedPackets),
            Interlocked.Read(ref _delayedPackets),
            Interlocked.Read(ref _bypassedPackets),
            Interlocked.Read(ref _errorCount),
            queue.PacketCount,
            queue.QueuedBytes,
            _handshakeTracker.GetCalibratedFlowCount(Stopwatch.GetTimestamp()),
            Volatile.Read(ref _lastError));
    }

    public bool TryGetBaseline(TcpFlowKey outboundFlow, out double baselineRttMs)
    {
        return _handshakeTracker.TryGetBaseline(outboundFlow, Stopwatch.GetTimestamp(), out baselineRttMs);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken; // Cleanup is intentionally completed even if the caller cancels.
        var handle = Volatile.Read(ref _handle);
        if (handle == IntPtr.Zero || handle == WinDivertNative.InvalidHandle)
        {
            return;
        }

        Interlocked.Exchange(ref _stopRequested, 1);
        if (!WinDivertNative.WinDivertShutdown(handle, WinDivertNative.ShutdownReceive))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 6) // ERROR_INVALID_HANDLE can occur after an already-reported fatal error.
            {
                RecordError($"Не удалось штатно остановить приём WinDivert (код {error}).", new Win32Exception(error));
            }
        }

        _queueChanged.Set();
        if (_captureTask is not null)
        {
            await _captureTask.ConfigureAwait(false);
        }

        Volatile.Write(ref _captureCompleted, 1);
        _queueChanged.Set();
        if (_schedulerTask is not null)
        {
            await _schedulerTask.ConfigureAwait(false);
        }

        Exception? closeError = null;
        lock (_sendLock)
        {
            // Defensive last drain: on normal shutdown the scheduler already did this.
            foreach (var pending in _scheduler.DrainAll())
            {
                SendPacket(pending.Item);
            }

            var ownedHandle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (ownedHandle != IntPtr.Zero && ownedHandle != WinDivertNative.InvalidHandle
                && !WinDivertNative.WinDivertClose(ownedHandle))
            {
                var error = Marshal.GetLastWin32Error();
                closeError = new Win32Exception(error, $"Не удалось закрыть дескриптор WinDivert (код {error}).");
                RecordError(closeError.Message, closeError);
            }
        }

        if (closeError is not null)
        {
            throw closeError;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _queueChanged.Dispose();
        }
    }

    internal static string BuildFilter(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        // User-configured port exclusions are evaluated in user mode so they take effect
        // immediately while the handler is running. The native filter only removes loopback
        // and already-injected impostor packets, preventing local loops and recapture.
        return "!loopback and !impostor";
    }

    private static HashSet<ushort> ParseExcludedPorts(AppSettings settings)
    {
        if (!AppSettingsValidator.TryParseExcludedPorts(settings.ExcludedPorts, out var ports, out var error))
        {
            throw new ArgumentException(error, nameof(settings));
        }

        return new HashSet<ushort>(ports);
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static Exception CreateOpenException(int error)
    {
        var win32 = new Win32Exception(error);
        return error switch
        {
            5 => new UnauthorizedAccessException("Windows отказала в доступе к WinDivert. Запустите Latency Studio с повышением прав через UAC.", win32),
            577 or 1275 => new InvalidOperationException("Windows заблокировала драйвер WinDivert или не смогла проверить его подпись. Защита Windows не изменялась; проверьте журнал безопасности и целостность пакета.", win32),
            2 or 3 => new FileNotFoundException("WinDivert не нашёл файл драйвера. Переустановите приложение из полного пакета.", win32),
            _ => new InvalidOperationException($"WinDivert не удалось запустить (код Windows {error}: {win32.Message}).", win32)
        };
    }

    private void CaptureLoop()
    {
        var buffer = new byte[WinDivertNative.MaximumPacketSize];
        try
        {
            // Shutdown(RECV) prevents new capture but allows WinDivertRecv to return
            // packets already queued in the native driver. Keep reading until the
            // post-shutdown ERROR_NO_DATA so normal stop can reinject those packets.
            while (true)
            {
                var address = default(WinDivertAddress);
                if (!WinDivertNative.WinDivertRecv(
                        Volatile.Read(ref _handle),
                        buffer,
                        (uint)buffer.Length,
                        out var receivedLength,
                        ref address))
                {
                    var error = Marshal.GetLastWin32Error();
                    var stopping = Volatile.Read(ref _stopRequested) != 0;
                    if (stopping && error is (WinDivertNative.ErrorNoData or WinDivertNative.ErrorOperationAborted
                        or WinDivertNative.ErrorInvalidHandle))
                    {
                        break;
                    }

                    RaiseFatal($"Ошибка чтения перехваченного пакета (код Windows {error}: {new Win32Exception(error).Message}).", new Win32Exception(error));
                    break;
                }

                if (receivedLength == 0 || receivedLength > buffer.Length)
                {
                    RaiseFatal("WinDivert вернул пакет с некорректной длиной; обработчик остановлен.");
                    break;
                }

                ProcessCapturedPacket(buffer, (int)receivedLength, address);
            }
        }
        catch (Exception exception)
        {
            RaiseFatal($"Сбой потока приёма WinDivert: {exception.Message}", exception);
        }
        finally
        {
            Volatile.Write(ref _captureCompleted, 1);
            _queueChanged.Set();
        }
    }

    private void ProcessCapturedPacket(byte[] receiveBuffer, int length, WinDivertAddress address)
    {
        Interlocked.Increment(ref _processedPackets);
        var packetBytes = ArrayPool<byte>.Shared.Rent(length);
        Buffer.BlockCopy(receiveBuffer, 0, packetBytes, 0, length);
        var ownsPacket = false;
        var direction = address.IsOutbound ? PacketDirection.Outbound : PacketDirection.Inbound;
        var capturedAt = Stopwatch.GetTimestamp();
        var packetSpan = packetBytes.AsSpan(0, length);
        var hasTcpInformation = TcpPacketReader.TryRead(packetSpan, out var tcpInformation);
        var hasTransportInformation = hasTcpInformation;
        var transportInformation = hasTcpInformation
            ? new TransportPacketInfo(6, tcpInformation.Flow)
            : default;
        if (!hasTcpInformation)
        {
            hasTransportInformation = TransportPacketReader.TryRead(packetSpan, out transportInformation);
        }
        var flow = hasTransportInformation ? transportInformation.Flow : (TcpFlowKey?)null;
        var packet = new BufferedPacket(packetBytes, length, address, flow);
        var isOutboundSyn = hasTcpInformation && direction == PacketDirection.Outbound
            && tcpInformation.IsSyn && !tcpInformation.IsAck;
        var currentSettings = Volatile.Read(ref _settings);
        var baselineRttMs = ObserveHandshake(hasTcpInformation, tcpInformation, direction, capturedAt, currentSettings.Mode);
        var delayMs = GetPacketDelayMs(currentSettings, hasTcpInformation, tcpInformation, direction, baselineRttMs);
        var excludedPorts = Volatile.Read(ref _excludedPorts);
        var isExcluded = hasTransportInformation
            && (excludedPorts.Contains(transportInformation.Flow.SourcePort)
                || excludedPorts.Contains(transportInformation.Flow.DestinationPort));

        try
        {
            if (isExcluded)
            {
                Interlocked.Increment(ref _bypassedPackets);
                SendExcludedImmediately(packet, direction, flow);
                ownsPacket = true;
                if (isOutboundSyn)
                {
                    _handshakeTracker.RecordOutboundSyn(tcpInformation.Flow, capturedAt, appliedDelayTicks: 0);
                }

                return;
            }

            if (address.IsLoopback || address.IsImpostor || Volatile.Read(ref _stopRequested) != 0)
            {
                Interlocked.Increment(ref _bypassedPackets);
                SendImmediatelyPreservingDirection(packet, direction, capturedAt);
                ownsPacket = true;
                if (isOutboundSyn)
                {
                    _handshakeTracker.RecordOutboundSyn(tcpInformation.Flow, capturedAt, appliedDelayTicks: 0);
                }

                return;
            }

            var requestedDelayTicks = delayMs <= 0
                ? 0
                : (long)Math.Ceiling(LatencyPolicy.ToStopwatchTicks(delayMs, Stopwatch.Frequency));
            var hasOlderPacketsInDirection = _scheduler.HasPendingDirection(direction);
            if (requestedDelayTicks > 0 || hasOlderPacketsInDirection)
            {
                var scheduleResult = _scheduler.TrySchedule(packet, direction, length, capturedAt, requestedDelayTicks);
                if (scheduleResult.Enqueued)
                {
                    ownsPacket = true;
                    if (scheduleResult.ActualDelayTicks > 0)
                    {
                        Interlocked.Increment(ref _delayedPackets);
                    }
                    else
                    {
                        Interlocked.Increment(ref _bypassedPackets);
                    }

                    if (isOutboundSyn)
                    {
                        _handshakeTracker.RecordOutboundSyn(tcpInformation.Flow, capturedAt, scheduleResult.ActualDelayTicks);
                    }

                    _queueChanged.Set();
                    return;
                }

                Interlocked.Increment(ref _bypassedPackets);
                lock (_sendLock)
                {
                    var flushed = _scheduler.DrainDirection(direction, Stopwatch.GetTimestamp());
                    Interlocked.Add(ref _bypassedPackets, flushed.Count);
                    foreach (var queued in flushed)
                    {
                        SendPacket(queued.Item);
                    }

                    SendPacket(packet);
                    ownsPacket = true;
                }

                if (isOutboundSyn)
                {
                    _handshakeTracker.RecordOutboundSyn(tcpInformation.Flow, capturedAt, appliedDelayTicks: 0);
                }

                _queueChanged.Set();
                return;
            }

            Interlocked.Increment(ref _bypassedPackets);
            lock (_sendLock)
            {
                SendPacket(packet);
                ownsPacket = true;
            }

            if (isOutboundSyn)
            {
                _handshakeTracker.RecordOutboundSyn(tcpInformation.Flow, capturedAt, appliedDelayTicks: 0);
            }
        }
        catch (Exception exception)
        {
            if (!ownsPacket)
            {
                Interlocked.Increment(ref _bypassedPackets);
                lock (_sendLock)
                {
                    SendPacket(packet);
                    ownsPacket = true;
                }
            }

            RaiseFatal($"Ошибка обработки пакета; трафик передан без дополнительной задержки, обработчик остановлен: {exception.Message}", exception);
        }
        finally
        {
            if (!ownsPacket)
            {
                ArrayPool<byte>.Shared.Return(packetBytes);
            }
        }
    }

    private double? ObserveHandshake(
        bool hasTcpInformation,
        TcpPacketInfo tcpInformation,
        PacketDirection direction,
        long capturedAt,
        LatencyMode mode)
    {
        if (!hasTcpInformation || mode != LatencyMode.Fixed)
        {
            if (hasTcpInformation && direction == PacketDirection.Inbound && tcpInformation.IsSyn && tcpInformation.IsAck
                && _handshakeTracker.ObserveInboundSynAck(tcpInformation.Flow, capturedAt, out var targetBaseline))
            {
                return targetBaseline;
            }

            if (hasTcpInformation && _handshakeTracker.TryGetBaseline(tcpInformation.Flow, capturedAt, out var knownBaseline))
            {
                return knownBaseline;
            }

            return null;
        }

        if (direction == PacketDirection.Inbound && tcpInformation.IsSyn && tcpInformation.IsAck
            && _handshakeTracker.ObserveInboundSynAck(tcpInformation.Flow, capturedAt, out var measuredBaseline))
        {
            return measuredBaseline;
        }

        return _handshakeTracker.TryGetBaseline(tcpInformation.Flow, capturedAt, out var fixedBaseline)
            ? fixedBaseline
            : null;
    }

    private static double GetPacketDelayMs(
        AppSettings settings,
        bool hasTcpInformation,
        TcpPacketInfo tcpInformation,
        PacketDirection direction,
        double? baselineRttMs)
    {
        if (settings.Mode == LatencyMode.Fixed)
        {
            return direction == PacketDirection.Outbound ? settings.OutgoingDelayMs : settings.IncomingDelayMs;
        }

        if (!hasTcpInformation)
        {
            return 0;
        }

        // Let each new TCP handshake provide an uncontaminated per-flow baseline.
        if (direction == PacketDirection.Outbound && tcpInformation.IsSyn && !tcpInformation.IsAck)
        {
            return 0;
        }

        var plan = LatencyPolicy.Calculate(settings, baselineRttMs);
        if (!plan.IsCalibrated)
        {
            return 0;
        }

        return direction == PacketDirection.Outbound ? plan.Delay.OutgoingMs : plan.Delay.IncomingMs;
    }

    private void SendImmediatelyPreservingDirection(BufferedPacket packet, PacketDirection direction, long nowTicks)
    {
        lock (_sendLock)
        {
            var pending = _scheduler.DrainDirection(direction, nowTicks);
            Interlocked.Add(ref _bypassedPackets, pending.Count);
            foreach (var queued in pending)
            {
                SendPacket(queued.Item);
            }

            SendPacket(packet);
        }
    }

    private void SendExcludedImmediately(BufferedPacket packet, PacketDirection direction, TcpFlowKey? flow)
    {
        lock (_sendLock)
        {
            if (flow.HasValue)
            {
                var sameFlow = _scheduler.DrainWhere(queued =>
                    queued.Direction == direction && queued.Item.Flow == flow);
                Interlocked.Add(ref _bypassedPackets, sameFlow.Count);
                foreach (var queued in sameFlow)
                {
                    SendPacket(queued.Item);
                }
            }

            SendPacket(packet);
        }
    }

    private void SchedulerLoop()
    {
        try
        {
            while (true)
            {
                if (Volatile.Read(ref _stopRequested) != 0 && Volatile.Read(ref _captureCompleted) != 0)
                {
                    lock (_sendLock)
                    {
                        foreach (var pending in _scheduler.DrainAll())
                        {
                            SendPacket(pending.Item);
                        }
                    }

                    return;
                }

                if (!_scheduler.TryPeek(out var next))
                {
                    _queueChanged.WaitOne();
                    continue;
                }

                var now = Stopwatch.GetTimestamp();
                if (next.DueAtTicks <= now)
                {
                    lock (_sendLock)
                    {
                        if (_scheduler.TryDequeueDue(Stopwatch.GetTimestamp(), out var duePacket))
                        {
                            SendPacket(duePacket.Item);
                        }
                    }

                    continue;
                }

                var remainingTicks = next.DueAtTicks - now;
                var waitMilliseconds = (int)Math.Clamp(
                    Math.Ceiling(remainingTicks * 1_000d / Stopwatch.Frequency),
                    1,
                    1_000);
                _queueChanged.WaitOne(waitMilliseconds);
            }
        }
        catch (Exception exception)
        {
            RaiseFatal($"Сбой планировщика отложенных пакетов: {exception.Message}", exception);
            lock (_sendLock)
            {
                foreach (var pending in _scheduler.DrainAll())
                {
                    SendPacket(pending.Item);
                }
            }
        }
    }

    private void SendPacket(BufferedPacket packet)
    {
        try
        {
            var handle = Volatile.Read(ref _handle);
            if (handle == IntPtr.Zero || handle == WinDivertNative.InvalidHandle)
            {
                throw new InvalidOperationException("Дескриптор WinDivert уже закрыт до повторной отправки пакета.");
            }

            var address = packet.Address;
            if (!WinDivertNative.WinDivertSend(
                    handle,
                    packet.Bytes,
                    (uint)packet.Length,
                    IntPtr.Zero,
                    ref address))
            {
                var error = Marshal.GetLastWin32Error();
                RaiseFatal($"WinDivert не смог повторно отправить пакет (код Windows {error}: {new Win32Exception(error).Message}).", new Win32Exception(error));
            }
        }
        catch (Exception exception)
        {
            RaiseFatal($"Исключение при повторной отправке пакета: {exception.Message}", exception);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(packet.Bytes);
        }
    }

    private void RaiseFatal(string message, Exception? exception = null)
    {
        Volatile.Write(ref _stopRequested, 1);
        lock (_errorLock)
        {
            _lastError = message;
        }

        if (Interlocked.Exchange(ref _faultRaised, 1) == 0)
        {
            Interlocked.Increment(ref _errorCount);
            var handle = Volatile.Read(ref _handle);
            if (handle != IntPtr.Zero && handle != WinDivertNative.InvalidHandle)
            {
                _ = WinDivertNative.WinDivertShutdown(handle, WinDivertNative.ShutdownReceive);
            }

            try
            {
                Faulted?.Invoke(this, new RuntimeFaultEventArgs(message, exception));
            }
            catch
            {
                // A subscriber must not terminate a packet worker thread.
            }
        }

        _queueChanged.Set();
    }

    private void RecordError(string message, Exception? exception)
    {
        lock (_errorLock)
        {
            _lastError = message;
        }

        if (exception is not null)
        {
            RaiseFatal(message, exception);
        }
    }

    private sealed class BufferedPacket(byte[] bytes, int length, WinDivertAddress address, TcpFlowKey? flow)
    {
        public byte[] Bytes { get; } = bytes;
        public int Length { get; } = length;
        public WinDivertAddress Address { get; set; } = address;
        public TcpFlowKey? Flow { get; } = flow;
    }
}
