using System.Diagnostics;
using System.Runtime.InteropServices;

namespace StreamerHub;

// Puts child processes in a Windows Job Object with KILL_ON_JOB_CLOSE, so if the
// server dies by any means (including a hard kill) the children are terminated
// by the OS instead of being orphaned in the background.
public static class JobGuard
{
    const int JobObjectExtendedLimitInformation = 9;
    const int JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    static readonly IntPtr Job = CreateJob();
    static readonly object Gate = new();

    public static void Adopt(Process process)
    {
        if (process == null || Job == IntPtr.Zero) return;
        try
        {
            lock (Gate)
            {
                if (!AssignProcessToJobObject(Job, process.Handle))
                {
                    var error = Marshal.GetLastWin32Error();
                    Log.Warn("child could not be adopted by the app job (0x" + error.ToString("X") + ")");
                }
            }
        }
        catch
        {
            // adoption is a nicety; never crash the app for it
        }
    }

    static IntPtr CreateJob()
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = (uint)JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        var size = (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal((int)size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, size))
            {
                CloseHandle(job);
                return IntPtr.Zero;
            }
        }
        catch
        {
            CloseHandle(job);
            return IntPtr.Zero;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
        return job;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInformationClass, IntPtr lpJobObjectInformation, uint cbJobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);
}