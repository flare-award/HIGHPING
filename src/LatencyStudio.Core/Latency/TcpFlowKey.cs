using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace LatencyStudio.Core.Latency;

/// <summary>A compact directional five-tuple used to associate TCP handshake samples with packets.</summary>
public readonly record struct TcpFlowKey(
    ulong SourceHigh,
    ulong SourceLow,
    ulong DestinationHigh,
    ulong DestinationLow,
    ushort SourcePort,
    ushort DestinationPort,
    bool IsIPv6)
{
    public TcpFlowKey Reverse() => new(
        DestinationHigh,
        DestinationLow,
        SourceHigh,
        SourceLow,
        DestinationPort,
        SourcePort,
        IsIPv6);

    public static bool TryFromEndpoints(IPEndPoint local, IPEndPoint remote, out TcpFlowKey flow)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);
        flow = default;
        if (local.Port is < 0 or > 65535 || remote.Port is < 0 or > 65535)
        {
            return false;
        }

        var localAddress = NormalizeAddress(local.Address);
        var remoteAddress = NormalizeAddress(remote.Address);
        if (localAddress.AddressFamily != remoteAddress.AddressFamily)
        {
            return false;
        }

        flow = FromAddressBytes(
            localAddress.GetAddressBytes(),
            remoteAddress.GetAddressBytes(),
            checked((ushort)local.Port),
            checked((ushort)remote.Port));
        return true;
    }

    internal static TcpFlowKey FromAddressBytes(
        ReadOnlySpan<byte> sourceAddress,
        ReadOnlySpan<byte> destinationAddress,
        ushort sourcePort,
        ushort destinationPort)
    {
        if (sourceAddress.Length == 4 && destinationAddress.Length == 4)
        {
            return new TcpFlowKey(
                BinaryPrimitives.ReadUInt32BigEndian(sourceAddress),
                0,
                BinaryPrimitives.ReadUInt32BigEndian(destinationAddress),
                0,
                sourcePort,
                destinationPort,
                false);
        }

        if (sourceAddress.Length != 16 || destinationAddress.Length != 16)
        {
            throw new ArgumentException("IP-адрес должен содержать 4 или 16 байт.");
        }

        return new TcpFlowKey(
            BinaryPrimitives.ReadUInt64BigEndian(sourceAddress[..8]),
            BinaryPrimitives.ReadUInt64BigEndian(sourceAddress[8..]),
            BinaryPrimitives.ReadUInt64BigEndian(destinationAddress[..8]),
            BinaryPrimitives.ReadUInt64BigEndian(destinationAddress[8..]),
            sourcePort,
            destinationPort,
            true);
    }

    private static IPAddress NormalizeAddress(IPAddress address)
    {
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }
}

public readonly record struct TcpPacketInfo(TcpFlowKey Flow, bool IsSyn, bool IsAck);
public readonly record struct TransportPacketInfo(byte Protocol, TcpFlowKey Flow);

/// <summary>Parses enough of an IP packet to locate TCP/UDP ports without modifying it.</summary>
public static class TransportPacketReader
{
    private const int MaximumIpv6ExtensionHeaders = 8;

    public static bool TryRead(ReadOnlySpan<byte> packet, out TransportPacketInfo information)
    {
        information = default;
        if (!TryLocateTransport(packet, out var protocol, out var offset, out var flow))
        {
            return false;
        }

        if (protocol is not (6 or 17) || packet.Length < offset + 4)
        {
            return false;
        }

        flow = flow with
        {
            SourcePort = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(offset, 2)),
            DestinationPort = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(offset + 2, 2))
        };
        information = new TransportPacketInfo(protocol, flow);
        return true;
    }

    public static bool TryReadTcp(ReadOnlySpan<byte> packet, out TcpPacketInfo information)
    {
        information = default;
        if (!TryLocateTransport(packet, out var protocol, out var offset, out var flow)
            || protocol != 6
            || packet.Length < offset + 20)
        {
            return false;
        }

        flow = flow with
        {
            SourcePort = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(offset, 2)),
            DestinationPort = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(offset + 2, 2))
        };
        var flags = packet[offset + 13];
        information = new TcpPacketInfo(flow, (flags & 0x02) != 0, (flags & 0x10) != 0);
        return true;
    }

    private static bool TryLocateTransport(
        ReadOnlySpan<byte> packet,
        out byte protocol,
        out int transportOffset,
        out TcpFlowKey flow)
    {
        protocol = 0;
        transportOffset = 0;
        flow = default;
        if (packet.Length < 1)
        {
            return false;
        }

        var version = packet[0] >> 4;
        if (version == 4)
        {
            if (packet.Length < 20)
            {
                return false;
            }

            var headerLength = (packet[0] & 0x0F) * 4;
            if (headerLength < 20 || packet.Length < headerLength + 4)
            {
                return false;
            }

            var fragmentField = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(6, 2));
            if ((fragmentField & 0x1FFF) != 0)
            {
                return false;
            }

            protocol = packet[9];
            transportOffset = headerLength;
            flow = TcpFlowKey.FromAddressBytes(packet.Slice(12, 4), packet.Slice(16, 4), 0, 0);
            return protocol is 6 or 17;
        }

        if (version != 6 || packet.Length < 40)
        {
            return false;
        }

        protocol = packet[6];
        transportOffset = 40;
        flow = TcpFlowKey.FromAddressBytes(packet.Slice(8, 16), packet.Slice(24, 16), 0, 0);
        var extensionCount = 0;
        while (protocol != 6 && protocol != 17)
        {
            if (++extensionCount > MaximumIpv6ExtensionHeaders)
            {
                return false;
            }

            if (protocol is 0 or 43 or 60 or 135 or 139 or 140)
            {
                if (packet.Length < transportOffset + 2)
                {
                    return false;
                }

                var followingHeader = packet[transportOffset];
                var extensionLength = (packet[transportOffset + 1] + 1) * 8;
                if (extensionLength < 8 || packet.Length < transportOffset + extensionLength)
                {
                    return false;
                }

                protocol = followingHeader;
                transportOffset += extensionLength;
                continue;
            }

            if (protocol == 44)
            {
                if (packet.Length < transportOffset + 8)
                {
                    return false;
                }

                var fragmentOffset = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(transportOffset + 2, 2));
                if ((fragmentOffset & 0xFFF8) != 0)
                {
                    return false;
                }

                protocol = packet[transportOffset];
                transportOffset += 8;
                continue;
            }

            if (protocol == 51)
            {
                if (packet.Length < transportOffset + 2)
                {
                    return false;
                }

                var followingHeader = packet[transportOffset];
                var extensionLength = (packet[transportOffset + 1] + 2) * 4;
                if (extensionLength < 8 || packet.Length < transportOffset + extensionLength)
                {
                    return false;
                }

                protocol = followingHeader;
                transportOffset += extensionLength;
                continue;
            }

            // ESP, ICMPv6 and unknown extension chains are not TCP/UDP.
            return false;
        }

        return packet.Length >= transportOffset + 4;
    }
}

/// <summary>
/// Reads TCP header flags and flow information from IPv4 and IPv6 network packets.
/// Non-initial fragments and unsupported extension chains are left unclassified.
/// </summary>
public static class TcpPacketReader
{
    public static bool TryRead(ReadOnlySpan<byte> packet, out TcpPacketInfo information)
    {
        return TransportPacketReader.TryReadTcp(packet, out information);
    }
}
