using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace Severino.Core.Network;

public sealed record PortOwnerInfo(int ProcessId, string ProcessName)
{
    /// <summary>PID 4 is the kernel; on a listening port that means http.sys (IIS and friends).</summary>
    public bool IsHttpSys => ProcessId == 4;

    public string Describe() => IsHttpSys
        ? "o http.sys do Windows (PID 4), quase sempre o IIS ou outro serviço registrado nele"
        : $"{ProcessName} (PID {ProcessId})";
}

/// <summary>One row of the TCP listener table.</summary>
public readonly record struct TcpListenerRow(IPAddress Address, int Port, int ProcessId);

/// <summary>Finds which process listens on a TCP port, through <c>GetExtendedTcpTable</c>.</summary>
public static partial class PortOwner
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidListener = 3;
    private const int ErrorInsufficientBuffer = 122;

    public static PortOwnerInfo? Find(int port)
    {
        var row = Listeners().FirstOrDefault(r => r.Port == port);
        if (row.Address is null)
            return null;

        try
        {
            using var process = Process.GetProcessById(row.ProcessId);
            return new PortOwnerInfo(row.ProcessId, process.ProcessName);
        }
        catch (ArgumentException)
        {
            return new PortOwnerInfo(row.ProcessId, "processo encerrado");
        }
    }

    /// <summary>Every listening TCP socket, IPv4 first.</summary>
    public static IReadOnlyList<TcpListenerRow> Listeners() =>
    [
        .. Read(AfInet, rowSize: 24, addressOffset: 4, addressLength: 4, portOffset: 8, pidOffset: 20),
        .. Read(AfInet6, rowSize: 56, addressOffset: 0, addressLength: 16, portOffset: 20, pidOffset: 52),
    ];

    /// <param name="rowSize">Size of MIB_TCPROW_OWNER_PID (IPv4) or MIB_TCP6ROW_OWNER_PID (IPv6).</param>
    private static List<TcpListenerRow> Read(int family, int rowSize, int addressOffset, int addressLength, int portOffset, int pidOffset)
    {
        var rows = new List<TcpListenerRow>();
        var size = 0;
        var result = GetExtendedTcpTable(0, ref size, false, family, TcpTableOwnerPidListener, 0);
        if (result != ErrorInsufficientBuffer)
            return rows;

        var table = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(table, ref size, false, family, TcpTableOwnerPidListener, 0) != 0)
                return rows;

            var count = Marshal.ReadInt32(table);
            var address = new byte[addressLength];
            for (var i = 0; i < count; i++)
            {
                var row = table + 4 + i * rowSize;
                Marshal.Copy(row + addressOffset, address, 0, addressLength);
                // dwLocalPort keeps the port in network byte order in its low 16 bits.
                var raw = (uint)Marshal.ReadInt32(row, portOffset);
                var port = (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));
                rows.Add(new(new IPAddress(address), port, Marshal.ReadInt32(row, pidOffset)));
            }
            return rows;
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial int GetExtendedTcpTable(nint table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);
}
