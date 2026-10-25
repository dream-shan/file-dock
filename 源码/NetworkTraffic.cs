using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace FileDockDemo;

// The Windows interface table distinguishes physical adapters from packet
// filters and virtual adapters, so the same traffic is never summed twice.
internal static class NetworkTraffic
{
    internal readonly record struct Counter(string Id, string Name, long Received, long Sent);
    internal static IEnumerable<Counter> Read()
    {
        if (GetIfTable2(out var table) != 0) yield break;
        try
        {
            int count = Marshal.ReadInt32(table), size = Marshal.SizeOf<InterfaceRow>();
            for (int i = 0; i < count; i++)
            {
                // MIB_IF_TABLE2.Table is aligned on an eight-byte boundary.
                var row = Marshal.PtrToStructure<InterfaceRow>(IntPtr.Add(table, 8 + i * size));
                if (!IsPhysicalTrafficInterface(row.Flags, row.Type, row.OperStatus)) continue;
                if (row.InOctets > long.MaxValue || row.OutOctets > long.MaxValue) continue;
                yield return new Counter(row.Guid.ToString(), row.Alias, (long)row.InOctets, (long)row.OutOctets);
            }
        }
        finally { FreeMibTable(table); }
    }
    internal static bool IsPhysicalTrafficInterface(byte flags, uint type, uint status) =>
        (flags & 1) != 0 && (flags & 2) == 0 && type is 6 or 71 && status == 1;

    [DllImport("iphlpapi.dll")] private static extern uint GetIfTable2(out IntPtr table);
    [DllImport("iphlpapi.dll")] private static extern void FreeMibTable(IntPtr table);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct InterfaceRow
    {
        public ulong Luid;
        public uint Index;
        public Guid Guid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Alias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Description;
        public uint PhysicalAddressLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PhysicalAddress;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PermanentPhysicalAddress;
        public uint Mtu, Type, TunnelType, MediaType, PhysicalMediumType, AccessType, DirectionType;
        public byte Flags;
        public uint OperStatus, AdminStatus, MediaConnectState;
        public Guid NetworkGuid;
        public uint ConnectionType;
        public ulong TransmitLinkSpeed, ReceiveLinkSpeed;
        public ulong InOctets, InUcastPkts, InNUcastPkts, InDiscards, InErrors, InUnknownProtos;
        public ulong InUcastOctets, InMulticastOctets, InBroadcastOctets;
        public ulong OutOctets, OutUcastPkts, OutNUcastPkts, OutDiscards, OutErrors;
        public ulong OutUcastOctets, OutMulticastOctets, OutBroadcastOctets, OutQLen;
    }
}
