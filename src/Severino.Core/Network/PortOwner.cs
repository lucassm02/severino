using System.Diagnostics;
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

/// <summary>Finds which process listens on a TCP port, through <c>GetExtendedTcpTable</c>.</summary>
public static partial class PortOwner
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidListener = 3;
    private const int ErrorInsufficientBuffer = 122;

    public static PortOwnerInfo? Find(int port)
    {
        var pid = FindPid(port, AfInet, rowSize: 24, portOffset: 8, pidOffset: 20)
            ?? FindPid(port, AfInet6, rowSize: 56, portOffset: 20, pidOffset: 52);
        if (pid is null)
            return null;

        try
        {
            using var process = Process.GetProcessById(pid.Value);
            return new PortOwnerInfo(pid.Value, process.ProcessName);
        }
        catch (ArgumentException)
        {
            return new PortOwnerInfo(pid.Value, "processo encerrado");
        }
    }

    /// <param name="rowSize">Size of MIB_TCPROW_OWNER_PID (IPv4) or MIB_TCP6ROW_OWNER_PID (IPv6).</param>
    private static int? FindPid(int port, int family, int rowSize, int portOffset, int pidOffset)
    {
        var size = 0;
        var result = GetExtendedTcpTable(0, ref size, false, family, TcpTableOwnerPidListener, 0);
        if (result != ErrorInsufficientBuffer)
            return null;

        var table = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(table, ref size, false, family, TcpTableOwnerPidListener, 0) != 0)
                return null;

            var count = Marshal.ReadInt32(table);
            for (var i = 0; i < count; i++)
            {
                var row = table + 4 + i * rowSize;
                // dwLocalPort keeps the port in network byte order in its low 16 bits.
                var raw = (uint)Marshal.ReadInt32(row, portOffset);
                var rowPort = (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));
                if (rowPort == port)
                    return Marshal.ReadInt32(row, pidOffset);
            }
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial int GetExtendedTcpTable(nint table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);
}
