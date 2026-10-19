using System.Buffers.Binary;
using System.Net;
using LatencyStudio.Core.Configuration;
using LatencyStudio.Core.Latency;
using LatencyStudio.Core.Runtime;
using LatencyStudio.Core.Scheduling;
using Xunit;

namespace LatencyStudio.Core.Tests;

public sealed class CoreBehaviorTests
{
    [Fact]
    public void Defaults_AreValidAndMatchProductDefaults()
    {
        var validation = AppSettingsValidator.Validate(AppSettings.Defaults);

        Assert.True(validation.IsValid, validation.ErrorSummary);
        Assert.Equal(LatencyMode.TargetRtt, AppSettings.Defaults.Mode);
        Assert.Equal(95, AppSettings.Defaults.TargetRttMs);
        Assert.Equal(40, AppSettings.Defaults.OutgoingDelayMs);
        Assert.Equal(40, AppSettings.Defaults.IncomingDelayMs);
        Assert.False(AppSettings.Defaults.StartWithWindows);
    }

    [Theory]
    [InlineData(-1, 40, 40)]
    [InlineData(95, -1, 40)]
    [InlineData(95, 40, 1_001)]
    [InlineData(2_001, 40, 40)]
    public void Validator_RejectsOutOfRangeDelayValues(int target, int outgoing, int incoming)
    {
        var settings = AppSettings.Defaults with
        {
            TargetRttMs = target,
            OutgoingDelayMs = outgoing,
            IncomingDelayMs = incoming
        };

        Assert.False(AppSettingsValidator.Validate(settings).IsValid);
    }

    [Theory]
    [InlineData("53,123", 2)]
    [InlineData("53; 123  443", 3)]
    [InlineData("", 0)]
    public void ExcludedPorts_AreParsedAndDeduplicated(string input, int expectedCount)
    {
        Assert.True(AppSettingsValidator.TryParseExcludedPorts(input, out var ports, out var error), error);
        Assert.Equal(expectedCount, ports.Count);
    }

    [Fact]
    public void ExcludedPorts_RejectInvalidAndExcessiveInput()
    {
        Assert.False(AppSettingsValidator.TryParseExcludedPorts("53, nope", out _, out _));
        Assert.False(AppSettingsValidator.TryParseExcludedPorts("0", out _, out _));
        var tooMany = string.Join(",", Enumerable.Range(1, AppSettingsValidator.MaximumExcludedPorts + 1));
        Assert.False(AppSettingsValidator.TryParseExcludedPorts(tooMany, out _, out _));
    }

    [Theory]
    [InlineData("1.1.1.1:443", "1.1.1.1", 443)]
    [InlineData("example.com", "example.com", 443)]
    [InlineData("[2001:db8::1]:8443", "2001:db8::1", 8443)]
    public void ProbeEndpoint_ParsesExpectedNode(string input, string expectedHost, int expectedPort)
    {
        Assert.True(ProbeEndpoint.TryParse(input, out var endpoint, out var error), error);
        Assert.Equal(expectedHost, endpoint.Host);
        Assert.Equal(expectedPort, endpoint.Port);
    }

    [Fact]
    public void ProbeEndpoint_RejectsInvalidPort()
    {
        Assert.False(ProbeEndpoint.TryParse("example.com:70000", out _, out _));
        Assert.False(ProbeEndpoint.TryParse("example.com:abc", out _, out _));
    }

    [Fact]
    public void TargetRtt_CalculatesSymmetricRemainingDelay()
    {
        var settings = AppSettings.Defaults with { Mode = LatencyMode.TargetRtt, TargetRttMs = 95 };

        var plan = LatencyPolicy.Calculate(settings, baselineRttMs: 20);

        Assert.True(plan.IsCalibrated);
        Assert.Equal(37.5, plan.Delay.OutgoingMs, precision: 6);
        Assert.Equal(37.5, plan.Delay.IncomingMs, precision: 6);
        Assert.Equal(75d, plan.AddedRttMs!.Value);
    }

    [Fact]
    public void TargetRtt_DoesNotClaimCalibrationWithoutBaselineAndNeverSubtractsLatency()
    {
        var settings = AppSettings.Defaults with { Mode = LatencyMode.TargetRtt, TargetRttMs = 95 };

        var unknown = LatencyPolicy.Calculate(settings, baselineRttMs: null);
        var alreadySlow = LatencyPolicy.Calculate(settings, baselineRttMs: 120);

        Assert.False(unknown.IsCalibrated);
        Assert.Null(unknown.AddedRttMs);
        Assert.Equal(0d, alreadySlow.Delay.AddedRttMs);
    }

    [Fact]
    public void FixedMode_ReportsSumOfDirectionalDelays()
    {
        var settings = AppSettings.Defaults with
        {
            Mode = LatencyMode.Fixed,
            OutgoingDelayMs = 40,
            IncomingDelayMs = 45
        };

        var plan = LatencyPolicy.Calculate(settings, baselineRttMs: 20);

        Assert.True(plan.IsCalibrated);
        Assert.Equal(40, plan.Delay.OutgoingMs);
        Assert.Equal(45, plan.Delay.IncomingMs);
        Assert.Equal(85d, plan.AddedRttMs!.Value);
    }

    [Fact]
    public void BoundedScheduler_ReleasesOnlyWhenMonotonicDeadlineHasPassed()
    {
        var scheduler = new BoundedPacketScheduler<string>(maximumPackets: 8, maximumBytes: 1024);
        var scheduled = scheduler.TrySchedule("packet", PacketDirection.Outbound, 100, nowTicks: 1_000, requestedDelayTicks: 50);

        Assert.True(scheduled.Enqueued);
        Assert.Equal(1_050, scheduled.DueAtTicks);
        Assert.False(scheduler.TryDequeueDue(1_049, out _));
        Assert.True(scheduler.TryDequeueDue(1_050, out var due));
        Assert.Equal("packet", due.Item);
        Assert.Equal(new PacketQueueSnapshot(0, 0), scheduler.Snapshot);
    }

    [Fact]
    public void BoundedScheduler_PreservesOrderWithinDirectionAndDrainsOnStop()
    {
        var scheduler = new BoundedPacketScheduler<string>(maximumPackets: 8, maximumBytes: 1024);
        scheduler.TrySchedule("out-1", PacketDirection.Outbound, 10, 100, 80);
        scheduler.TrySchedule("in-1", PacketDirection.Inbound, 10, 100, 20);
        scheduler.TrySchedule("out-2", PacketDirection.Outbound, 10, 101, 0);

        Assert.True(scheduler.TryPeek(out var next));
        Assert.Equal("in-1", next.Item);
        var drained = scheduler.DrainAll();

        Assert.Equal(new[] { "out-1", "in-1", "out-2" }, drained.Select(packet => packet.Item));
        Assert.Equal(new PacketQueueSnapshot(0, 0), scheduler.Snapshot);
    }

    [Fact]
    public void BoundedScheduler_OverflowReturnsBypassDispositionWithoutDroppingQueuedPackets()
    {
        var scheduler = new BoundedPacketScheduler<string>(maximumPackets: 1, maximumBytes: 100);
        Assert.True(scheduler.TrySchedule("queued", PacketDirection.Outbound, 50, 0, 50).Enqueued);

        var overflow = scheduler.TrySchedule("send-now", PacketDirection.Outbound, 1, 1, 50);

        Assert.Equal(ScheduleStatus.CapacityExceeded, overflow.Status);
        Assert.Equal(1, scheduler.Snapshot.PacketCount);
        Assert.Equal("queued", scheduler.DrainDirection(PacketDirection.Outbound, nowTicks: 1)[0].Item);
    }

    [Fact]
    public void TcpPacketReader_ParsesIpv4TcpSynWithoutChangingPayload()
    {
        var packet = CreateIpv4TcpPacket("10.0.0.2", "203.0.113.9", 50_000, 443, syn: true, ack: false);

        Assert.True(TcpPacketReader.TryRead(packet, out var information));
        Assert.True(information.IsSyn);
        Assert.False(information.IsAck);
        Assert.Equal(50_000, information.Flow.SourcePort);
        Assert.Equal(443, information.Flow.DestinationPort);
        Assert.Equal(6, packet[9]);
    }

    [Fact]
    public void TransportPacketReader_ParsesIpv6UdpThroughExtensionHeader()
    {
        var packet = CreateIpv6UdpPacket("2001:db8::2", "2001:db8::9", 53, 52_000);

        Assert.True(TransportPacketReader.TryRead(packet, out var information));
        Assert.Equal(17, information.Protocol);
        Assert.True(information.Flow.IsIPv6);
        Assert.Equal(53, information.Flow.SourcePort);
        Assert.Equal(52_000, information.Flow.DestinationPort);
        Assert.Equal((ulong)0x20010db800000000, information.Flow.SourceHigh);
    }

    [Fact]
    public void TcpHandshakeTracker_SubtractsKnownOutboundDelayFromRealSample()
    {
        var tracker = new TcpHandshakeTracker(stopwatchFrequency: 1_000);
        Assert.True(TcpFlowKey.TryFromEndpoints(
            new IPEndPoint(IPAddress.Parse("10.0.0.2"), 50_000),
            new IPEndPoint(IPAddress.Parse("203.0.113.9"), 443),
            out var flow));
        tracker.RecordOutboundSyn(flow, capturedAtTicks: 1_000, appliedDelayTicks: 40);

        var measured = tracker.ObserveInboundSynAck(flow.Reverse(), capturedAtTicks: 1_070, out var baseline);

        Assert.True(measured);
        Assert.Equal(30, baseline, precision: 6);
        Assert.True(tracker.TryGetBaseline(flow, nowTicks: 1_071, out var stored));
        Assert.Equal(30, stored, precision: 6);
    }

    [Fact]
    public async Task TrafficEngineController_StartUpdateAndStopHaveValidLifecycle()
    {
        var runtime = new FakeTrafficRuntime();
        var controller = new TrafficEngineController(() => runtime);
        var settings = AppSettings.Defaults with { Mode = LatencyMode.Fixed };

        await controller.EnableAsync(settings);
        Assert.Equal(TrafficEngineState.Running, controller.Snapshot.State);
        Assert.Equal(1, runtime.StartCount);

        var updated = settings with { OutgoingDelayMs = 77 };
        controller.UpdateSettings(updated);
        Assert.Equal(updated, runtime.LastSettings);

        await controller.DisableAsync();
        Assert.Equal(TrafficEngineState.Disabled, controller.Snapshot.State);
        Assert.Equal(1, runtime.StopCount);
        Assert.False(controller.HasRuntime);
    }

    [Fact]
    public async Task TrafficEngineController_CleansUpBackendAndReportsStartFailure()
    {
        var runtime = new FakeTrafficRuntime { StartException = new UnauthorizedAccessException("administrator required") };
        var controller = new TrafficEngineController(() => runtime);

        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => controller.EnableAsync(AppSettings.Defaults));

        Assert.Contains("administrator required", exception.Message);
        Assert.Equal(TrafficEngineState.Error, controller.Snapshot.State);
        Assert.False(controller.HasRuntime);
        Assert.Equal(1, runtime.StopCount);
        Assert.Equal(1, runtime.DisposeCount);
    }

    [Fact]
    public async Task TrafficEngineController_ExposesRuntimeFaultAndStopsBackend()
    {
        var runtime = new FakeTrafficRuntime();
        var controller = new TrafficEngineController(() => runtime);
        await controller.EnableAsync(AppSettings.Defaults);

        runtime.ReportFault("test fault");
        Assert.Equal(TrafficEngineState.Error, controller.Snapshot.State);
        Assert.Equal("test fault", controller.Snapshot.LastError);

        await controller.DisableAsync();
        Assert.Equal(TrafficEngineState.Disabled, controller.Snapshot.State);
        Assert.Equal(1, runtime.StopCount);
    }

    [Fact]
    public async Task SettingsStore_RoundTripsAndRepairsInvalidUserFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LatencyStudio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new SettingsStore(path);
            var settings = AppSettings.Defaults with
            {
                Mode = LatencyMode.Fixed,
                TargetRttMs = 130,
                OutgoingDelayMs = 23,
                IncomingDelayMs = 61,
                StartWithWindows = true,
                MinimizeToTray = false,
                TestNode = "example.net:8443",
                ExcludedPorts = "53, 123"
            };

            await store.SaveAsync(settings);
            Assert.Equal(settings, store.Load());

            await File.WriteAllTextAsync(path, "{\"mode\":\"FutureMode\",\"targetRttMs\":99999,\"outgoingDelayMs\":40,\"incomingDelayMs\":40,\"testNode\":\"1.1.1.1:443\",\"excludedPorts\":\"\"}");
            var repaired = store.Load();
            Assert.Equal(AppSettings.Defaults.Mode, repaired.Mode);
            Assert.Equal(AppSettings.Defaults.TargetRttMs, repaired.TargetRttMs);
            Assert.False(string.IsNullOrWhiteSpace(store.LastLoadError));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static byte[] CreateIpv4TcpPacket(string source, string destination, ushort sourcePort, ushort destinationPort, bool syn, bool ack)
    {
        var sourceBytes = IPAddress.Parse(source).GetAddressBytes();
        var destinationBytes = IPAddress.Parse(destination).GetAddressBytes();
        var packet = new byte[40];
        packet[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), (ushort)packet.Length);
        packet[8] = 64;
        packet[9] = 6;
        sourceBytes.CopyTo(packet.AsSpan(12, 4));
        destinationBytes.CopyTo(packet.AsSpan(16, 4));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20, 2), sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22, 2), destinationPort);
        packet[32] = 0x50;
        packet[33] = (byte)((syn ? 0x02 : 0) | (ack ? 0x10 : 0));
        return packet;
    }

    private static byte[] CreateIpv6UdpPacket(string source, string destination, ushort sourcePort, ushort destinationPort)
    {
        var packet = new byte[56];
        packet[0] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), 16);
        packet[6] = 60; // Destination Options extension header.
        packet[7] = 64;
        IPAddress.Parse(source).GetAddressBytes().CopyTo(packet.AsSpan(8, 16));
        IPAddress.Parse(destination).GetAddressBytes().CopyTo(packet.AsSpan(24, 16));
        packet[40] = 17;
        packet[41] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(48, 2), sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(50, 2), destinationPort);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(52, 2), 8);
        return packet;
    }

    private sealed class FakeTrafficRuntime : ITrafficRuntime
    {
        public event EventHandler<RuntimeFaultEventArgs>? Faulted;

        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int DisposeCount { get; private set; }
        public AppSettings? LastSettings { get; private set; }
        public Exception? StartException { get; init; }

        public Task StartAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            StartCount++;
            LastSettings = settings;
            return StartException is null ? Task.CompletedTask : Task.FromException(StartException);
        }

        public void UpdateSettings(AppSettings settings) => LastSettings = settings;

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return Task.CompletedTask;
        }

        public TrafficRuntimeSnapshot GetSnapshot() => new(1, 1, 0, 0, 0, 0, 1, null);

        public bool TryGetBaseline(TcpFlowKey outboundFlow, out double baselineRttMs)
        {
            baselineRttMs = 0;
            return false;
        }

        public void ReportFault(string message) => Faulted?.Invoke(this, new RuntimeFaultEventArgs(message));

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
