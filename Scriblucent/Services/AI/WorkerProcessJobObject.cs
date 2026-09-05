using System.Runtime.InteropServices;

namespace Scriblucent.Services.AI;

/// <summary>
/// Binds every spawned Scriblucent.AI.Worker.exe process to this app's process lifetime via a
/// Windows Job Object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, so a crash, a forced "End Task",
/// or an ungraceful logoff can't orphan a worker holding a multi-GB model in RAM. Windows closes
/// the job handle (and thus kills every process still assigned to it) automatically when this
/// process's handle table is torn down, for any exit path - there's no code in this app that
/// needs to run for this to take effect.
/// </summary>
internal static class WorkerProcessJobObject
{
    private static readonly Lazy<IntPtr> JobHandle = new(CreateKillOnCloseJob);

    /// <summary>Best-effort: a failure here just means today's behavior (no crash-time cleanup
    /// guarantee) rather than throwing and blocking worker startup.</summary>
    public static void Track(System.Diagnostics.Process process)
    {
        try
        {
            var job = JobHandle.Value;
            if (job != IntPtr.Zero)
                AssignProcessToJobObject(job, process.Handle);
        }
        catch
        {
        }
    }

    private static IntPtr CreateKillOnCloseJob()
    {
        try
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
                return IntPtr.Zero;

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                }
            };

            var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var infoPtr = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, infoPtr, false);
                if (!SetInformationJobObject(job, JobObjectInfoType.ExtendedLimitInformation, infoPtr, (uint)length))
                {
                    CloseHandle(job);
                    return IntPtr.Zero;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(infoPtr);
            }

            return job;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    private enum JobObjectInfoType
    {
        ExtendedLimitInformation = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, JobObjectInfoType infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
