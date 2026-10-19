namespace LatencyStudio.Core.Scheduling;

public enum PacketDirection
{
    Outbound,
    Inbound
}

public enum ScheduleStatus
{
    Enqueued,
    CapacityExceeded
}

public readonly record struct ScheduledPacket<T>(
    T Item,
    PacketDirection Direction,
    int ByteLength,
    long EnqueuedAtTicks,
    long DueAtTicks,
    long Sequence);

public readonly record struct ScheduleResult(ScheduleStatus Status, long DueAtTicks, long ActualDelayTicks, long Sequence)
{
    public bool Enqueued => Status == ScheduleStatus.Enqueued;
}

public readonly record struct PacketQueueSnapshot(int PacketCount, long QueuedBytes);

/// <summary>
/// Bounded monotonic-time scheduler for captured packets. It preserves capture order
/// within each direction by never assigning a later deadline to an earlier packet.
/// Callers must use their send lock around dequeue/send and emergency flush operations.
/// </summary>
public sealed class BoundedPacketScheduler<T>
{
    public const int DefaultMaximumPackets = 8_192;
    public const long DefaultMaximumBytes = 64L * 1024 * 1024;

    private readonly object _sync = new();
    private readonly PriorityQueue<ScheduledPacket<T>, (long DueAtTicks, long Sequence)> _queue = new();
    private readonly int _maximumPackets;
    private readonly long _maximumBytes;
    private int _packetCount;
    private int _outboundCount;
    private int _inboundCount;
    private long _queuedBytes;
    private long _nextSequence;
    private long _lastOutboundDue;
    private long _lastInboundDue;

    public BoundedPacketScheduler(
        int maximumPackets = DefaultMaximumPackets,
        long maximumBytes = DefaultMaximumBytes)
    {
        if (maximumPackets <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPackets));
        }

        if (maximumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        _maximumPackets = maximumPackets;
        _maximumBytes = maximumBytes;
    }

    public PacketQueueSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                return new PacketQueueSnapshot(_packetCount, _queuedBytes);
            }
        }
    }

    public bool HasPendingDirection(PacketDirection direction)
    {
        lock (_sync)
        {
            return direction == PacketDirection.Outbound ? _outboundCount > 0 : _inboundCount > 0;
        }
    }

    public ScheduleResult TrySchedule(T item, PacketDirection direction, int byteLength, long nowTicks, long requestedDelayTicks)
    {
        if (byteLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteLength));
        }

        lock (_sync)
        {
            if (_packetCount >= _maximumPackets || byteLength > _maximumBytes - _queuedBytes)
            {
                return new ScheduleResult(ScheduleStatus.CapacityExceeded, nowTicks, 0, 0);
            }

            var requestedDue = requestedDelayTicks <= 0 ? nowTicks : SaturatingAdd(nowTicks, requestedDelayTicks);
            var previousDue = direction == PacketDirection.Outbound ? _lastOutboundDue : _lastInboundDue;
            var dueAtTicks = Math.Max(requestedDue, previousDue);
            var sequence = ++_nextSequence;
            var packet = new ScheduledPacket<T>(item, direction, byteLength, nowTicks, dueAtTicks, sequence);
            _queue.Enqueue(packet, (dueAtTicks, sequence));
            _packetCount++;
            _queuedBytes += byteLength;

            if (direction == PacketDirection.Outbound)
            {
                _outboundCount++;
                _lastOutboundDue = dueAtTicks;
            }
            else
            {
                _inboundCount++;
                _lastInboundDue = dueAtTicks;
            }

            return new ScheduleResult(
                ScheduleStatus.Enqueued,
                dueAtTicks,
                Math.Max(0, dueAtTicks - nowTicks),
                sequence);
        }
    }

    public bool TryPeek(out ScheduledPacket<T> packet)
    {
        lock (_sync)
        {
            return _queue.TryPeek(out packet, out _);
        }
    }

    public bool TryDequeueDue(long nowTicks, out ScheduledPacket<T> packet)
    {
        lock (_sync)
        {
            if (!_queue.TryPeek(out _, out var priority) || priority.DueAtTicks > nowTicks)
            {
                packet = default;
                return false;
            }

            packet = _queue.Dequeue();
            _packetCount--;
            _queuedBytes -= packet.ByteLength;
            if (packet.Direction == PacketDirection.Outbound)
            {
                _outboundCount--;
            }
            else
            {
                _inboundCount--;
            }

            return true;
        }
    }

    public IReadOnlyList<ScheduledPacket<T>> DrainDirection(PacketDirection direction, long nowTicks)
    {
        var drained = DrainWhere(packet => packet.Direction == direction);
        lock (_sync)
        {
            ResetLastDue(direction, nowTicks);
        }

        return drained;
    }

    public IReadOnlyList<ScheduledPacket<T>> DrainWhere(Func<ScheduledPacket<T>, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        lock (_sync)
        {
            if (_packetCount == 0)
            {
                return Array.Empty<ScheduledPacket<T>>();
            }

            var selected = new List<ScheduledPacket<T>>();
            var retained = new List<ScheduledPacket<T>>();
            while (_queue.TryDequeue(out var packet, out _))
            {
                if (predicate(packet))
                {
                    selected.Add(packet);
                    _packetCount--;
                    _queuedBytes -= packet.ByteLength;
                    if (packet.Direction == PacketDirection.Outbound)
                    {
                        _outboundCount--;
                    }
                    else
                    {
                        _inboundCount--;
                    }
                }
                else
                {
                    retained.Add(packet);
                }
            }

            foreach (var packet in retained)
            {
                _queue.Enqueue(packet, (packet.DueAtTicks, packet.Sequence));
            }

            selected.Sort(static (left, right) => left.Sequence.CompareTo(right.Sequence));
            return selected;
        }
    }

    public IReadOnlyList<ScheduledPacket<T>> DrainAll()
    {
        lock (_sync)
        {
            var drained = new List<ScheduledPacket<T>>(_packetCount);
            while (_queue.TryDequeue(out var packet, out _))
            {
                drained.Add(packet);
            }

            drained.Sort(static (left, right) => left.Sequence.CompareTo(right.Sequence));
            _packetCount = 0;
            _outboundCount = 0;
            _inboundCount = 0;
            _queuedBytes = 0;
            _lastOutboundDue = 0;
            _lastInboundDue = 0;
            return drained;
        }
    }

    private void ResetLastDue(PacketDirection direction, long nowTicks)
    {
        if (direction == PacketDirection.Outbound)
        {
            _lastOutboundDue = nowTicks;
        }
        else
        {
            _lastInboundDue = nowTicks;
        }
    }

    private static long SaturatingAdd(long left, long right)
    {
        return right > long.MaxValue - left ? long.MaxValue : left + right;
    }
}
