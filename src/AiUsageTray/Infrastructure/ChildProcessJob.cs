using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AiUsageTray.Infrastructure;

/// <summary>
/// Wraps a Windows Job Object configured with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE. Any child process
/// assigned to it is killed by the OS the moment this process exits - including force-kill, crash,
/// and logoff, where normal Dispose/OnExit cleanup never runs. This is the only reliable way to
/// guarantee a hidden `codex app-server` (and the cmd/node shim tree above it) can't outlive the tray app.
/// </summary>
public sealed class ChildProcessJob : IDisposable
{
    private readonly nint _jobHandle;

    public ChildProcessJob()
    {
        _jobHandle = CreateJobObject(0, null);
        if (_jobHandle == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed.");
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE },
        };

        var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var infoPtr = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, infoPtr, fDeleteOld: false);
            if (!SetInformationJobObject(_jobHandle, JobObjectExtendedLimitInformation, infoPtr, (uint)length))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject failed.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(infoPtr);
        }
    }

    /// <summary>Best-effort: ties the child's lifetime to ours. Never throws - a failed assignment
    /// (e.g. the process already exited) must not break the refresh path it's called from.</summary>
    public void TryAssign(Process process)
    {
        try
        {
            if (!AssignProcessToJobObject(_jobHandle, process.Handle))
            {
                AppLog.Warn("ChildProcessJob", $"AssignProcessToJobObject failed (error {Marshal.GetLastWin32Error()}).");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("ChildProcessJob", $"Could not assign child process to job: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_jobHandle != 0)
        {
            CloseHandle(_jobHandle);
        }
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateJobObject(nint lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(nint hJob, int jobObjectInfoClass, nint lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(nint hJob, nint hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);
}
