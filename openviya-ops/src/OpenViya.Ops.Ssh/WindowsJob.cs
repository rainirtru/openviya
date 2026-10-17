using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenViya.Ops.Ssh;

/// <summary>Assigns an owned process to a kill-on-close job before it can be published.</summary>
internal static class WindowsJob
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private static readonly object Gate = new();
    private static readonly Dictionary<int, SafeJobHandle> OwnedJobs = [];

    internal static void AttachAndOwn(Process process)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("SSH tunnel ownership requires Windows Job Objects.");
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) { job.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create SSH process job."); }
        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose }
        };
        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, ptr, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ptr, (uint)size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enable kill-on-close for SSH job.");
            if (!AssignProcessToJobObject(job, process.Handle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not assign SSH process to owned job.");
        }
        catch { job.Dispose(); throw; }
        finally { Marshal.FreeHGlobal(ptr); }

        var pid = process.Id;
        lock (Gate) OwnedJobs[pid] = job;
        process.Exited += (_, _) => Release(pid);
        try { if (process.HasExited) Release(process.Id); }
        catch (InvalidOperationException) { Release(pid); }
    }

    internal static bool OwnsLoopbackListener(int port, int processId)
    {
        var owners = GetLoopbackListenerProcessIds(port);
        return owners.Contains(processId) && owners.All(pid => pid == processId);
    }

    internal static bool IsLoopbackPortInUse(int port) => GetLoopbackListenerProcessIds(port).Count > 0;

    internal static bool HasForeignLoopbackListener(int port, int processId) => GetLoopbackListenerProcessIds(port).Any(pid => pid != processId);

    private static List<int> GetLoopbackListenerProcessIds(int port)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows TCP ownership inspection is required.");
        const int afInet = 2;
        const int tcpTableOwnerPidListener = 3;
        uint size = 0;
        var status = GetExtendedTcpTable(IntPtr.Zero, ref size, false, afInet, tcpTableOwnerPidListener, 0);
        if (status != 122 && status != 0) throw new Win32Exception((int)status, "Could not inspect local TCP listeners.");
        if (size == 0) return [];
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var table = Marshal.AllocHGlobal((int)size);
            try
            {
                status = GetExtendedTcpTable(table, ref size, false, afInet, tcpTableOwnerPidListener, 0);
                if (status == 122) { size = checked(size + Math.Max(size / 2, 1024)); continue; } // Bounded retry after table growth.
                if (status != 0) throw new Win32Exception((int)status, "Could not inspect local TCP listeners.");
                var count = Marshal.ReadInt32(table);
                var rowPtr = IntPtr.Add(table, sizeof(uint));
                var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
                var owners = new List<int>();
                for (var i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(IntPtr.Add(rowPtr, i * rowSize));
                    var localPort = (ushort)IPAddress.NetworkToHostOrder((short)(row.LocalPort & 0xffff));
                    if (row.State == 2 && row.LocalAddress == 0x0100007f && localPort == port) owners.Add(checked((int)row.ProcessId));
                }
                return owners;
            }
            finally { Marshal.FreeHGlobal(table); }
        }
        throw new Win32Exception(122, "Local TCP listener table changed repeatedly during inspection.");
    }

    private static void Release(int pid)
    {
        SafeJobHandle? handle = null;
        lock (Gate) { if (OwnedJobs.Remove(pid, out var existing)) handle = existing; }
        handle?.Dispose();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeJobHandle CreateJobObject(IntPtr jobAttributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeJobHandle job, int infoClass, IntPtr info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle job, IntPtr process);
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr tcpTable, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, int addressFamily, int tableClass, uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State, LocalAddress, LocalPort, RemoteAddress, RemotePort, ProcessId;
    }
    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeJobHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
