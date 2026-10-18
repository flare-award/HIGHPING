using System.Runtime.InteropServices;

namespace LatencyStudio.App.Infrastructure;

internal static class WinDivertNative
{
    internal const uint NetworkLayer = 0;
    internal const uint ShutdownReceive = 1;
    internal const int ErrorInvalidHandle = 6;
    internal const int ErrorNoData = 232;
    internal const int ErrorOperationAborted = 995;
    internal const int MaximumPacketSize = 65_535;
    internal static readonly IntPtr InvalidHandle = new(-1);

    [DllImport("WinDivert.dll", EntryPoint = "WinDivertOpen", ExactSpelling = true,
        CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Ansi, SetLastError = true)]
    internal static extern IntPtr WinDivertOpen(
        [MarshalAs(UnmanagedType.LPStr)] string filter,
        uint layer,
        short priority,
        ulong flags);

    [DllImport("WinDivert.dll", EntryPoint = "WinDivertRecv", ExactSpelling = true,
        CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinDivertRecv(
        IntPtr handle,
        [Out] byte[] packet,
        uint packetLength,
        out uint receivedLength,
        ref WinDivertAddress address);

    [DllImport("WinDivert.dll", EntryPoint = "WinDivertSend", ExactSpelling = true,
        CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinDivertSend(
        IntPtr handle,
        [In] byte[] packet,
        uint packetLength,
        IntPtr sentLength,
        ref WinDivertAddress address);

    [DllImport("WinDivert.dll", EntryPoint = "WinDivertShutdown", ExactSpelling = true,
        CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinDivertShutdown(IntPtr handle, uint how);

    [DllImport("WinDivert.dll", EntryPoint = "WinDivertClose", ExactSpelling = true,
        CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinDivertClose(IntPtr handle);
}

/// <summary>
/// The native WinDivert 2.2 WINDIVERT_ADDRESS is 80 bytes on x64. The first 8 bytes
/// are the timestamp, the next 8 contain bit fields, and the 64-byte union begins at 16.
/// Only the network IfIdx/SubIfIdx fields (union offsets 0 and 4) are interpreted here;
/// the remainder is preserved as reserved union storage by the marshaler.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 80)]
internal struct WinDivertAddress
{
    private const ulong OutboundMask = 1UL << 17;
    private const ulong LoopbackMask = 1UL << 18;
    private const ulong ImpostorMask = 1UL << 19;

    [FieldOffset(0)]
    public long Timestamp;

    [FieldOffset(8)]
    public ulong Flags;

    [FieldOffset(16)]
    public uint InterfaceIndex;

    [FieldOffset(20)]
    public uint SubInterfaceIndex;

    public readonly bool IsOutbound => (Flags & OutboundMask) != 0;
    public readonly bool IsLoopback => (Flags & LoopbackMask) != 0;
    public readonly bool IsImpostor => (Flags & ImpostorMask) != 0;
}
