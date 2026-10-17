using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Zapret.Gui.Services;

/// <summary>Runs the original BAT. A private Windows job owns its detached descendants, never processes discovered by name.</summary>
public sealed class WindowsManualProcessBackend : IManualProcessBackend
{
    public void EnsureNoOtherWinws(ServiceProcess? serviceProcess)
    {
        foreach (var process in Process.GetProcessesByName("winws"))
        {
            using (process)
            {
                if (serviceProcess == null || process.Id != serviceProcess.Id)
                    throw new IOException("Обнаружен другой winws. Завершите его вручную перед пробным запуском; оболочка его не изменяет.");
                using var handle = OpenProcess(0x1000 | 0x100000, false, (uint)process.Id);
                if (handle.IsInvalid || Identity(handle, (uint)process.Id) != serviceProcess)
                    throw new IOException("Идентичность существующего winws изменилась; запуск запрещён.");
            }
        }
    }

    public IManualProcessSession Launch(string root, string strategyPath)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        strategyPath = Path.GetFullPath(strategyPath);
        if (!string.Equals(Path.GetDirectoryName(strategyPath), root, StringComparison.OrdinalIgnoreCase)
            || (root + strategyPath).Any(c => c > 127 || "&|<>^%!\r\n\"".Contains(c)))
            throw new IOException("Для исходного BAT требуется локальный ASCII-путь без специальных символов CMD.");
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        SafeProcessHandle? launcher = null;
        IntPtr environment = IntPtr.Zero;
        var native = new ProcessInformation();
        try
        {
            var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE; no breakaway.
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var variables = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .ToDictionary(e => (string)e.Key, e => (string)e.Value!, StringComparer.OrdinalIgnoreCase);
            variables["NO_UPDATE_CHECK"] = "1";
            variables["ComSpec"] = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            var block = string.Join('\0', variables.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase).Select(e => e.Key + "=" + e.Value)) + "\0\0";
            environment = Marshal.StringToHGlobalUni(block);
            var startup = new StartupInfo { Size = (uint)Marshal.SizeOf<StartupInfo>() };
            var command = new StringBuilder("\"" + variables["ComSpec"] + "\" /d /v:off /c call \"" + strategyPath + "\"");
            if (!CreateProcess(variables["ComSpec"], command, IntPtr.Zero, IntPtr.Zero, false,
                0x08000000 | 4 | 0x400, environment, root, ref startup, out native))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            launcher = new SafeProcessHandle(native.Process, true);
            // A child cannot detach before the launcher belongs to our job.
            if (!AssignProcessToJobObject(job, launcher) || ResumeThread(native.Thread) == uint.MaxValue)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return new JobSession(job, launcher, Path.Combine(root, "bin", "winws.exe"));
        }
        catch
        {
            if (launcher != null) { TerminateProcess(launcher, 1); launcher.Dispose(); }
            job.Dispose();
            throw;
        }
        finally
        {
            if (native.Thread != IntPtr.Zero) CloseHandle(native.Thread);
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
        }
    }

    private sealed class JobSession(JobHandle job, SafeProcessHandle launcher, string executable) : IManualProcessSession
    {
        private SafeProcessHandle? engine;
        private ServiceProcess? identity;
        public ManualProcessObservation Observe()
        {
            var launcherExited = WaitForSingleObject(launcher, 0) == 0;
            if (engine != null)
            {
                if (WaitForSingleObject(engine, 0) == 0) return new(null, launcherExited, true);
                if (Identity(engine, identity!.Id) != identity) throw new IOException("Идентичность пробного процесса изменилась.");
                return new(identity, launcherExited, false);
            }
            var ids = ProcessIds(job);
            foreach (var id in ids)
            {
                var handle = OpenProcess(0x1000 | 0x100000, false, id);
                if (handle.IsInvalid) { handle.Dispose(); continue; } // A short helper can exit between enumeration and OpenProcess.
                try
                {
                    if (WaitForSingleObject(handle, 0) == 0) continue;
                    if (!IsProcessInJob(handle, job, out var belongs) || !belongs)
                        throw new IOException("Процесс покинул собственный job; принадлежность не подтверждена.");
                    var found = Identity(handle, id);
                    if (!string.Equals(Path.GetFileName(found.Executable), "winws.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!found.Executable!.Equals(executable, StringComparison.OrdinalIgnoreCase) || identity != null)
                        throw new IOException("В собственном job обнаружен неожиданный winws.");
                    identity = found;
                    engine = handle;
                    handle = null!;
                }
                finally { handle?.Dispose(); }
            }
            return new(identity, launcherExited, ids.Length == 0);
        }
        public void Stop()
        {
            if (!TerminateJobObject(job, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var deadline = Environment.TickCount64 + 10000;
            while (ProcessIds(job).Length != 0)
            {
                if (Environment.TickCount64 >= deadline) throw new TimeoutException("Windows не подтвердил завершение собственного пробного job.");
                Thread.Sleep(100);
            }
        }
        public void Dispose() { job.Dispose(); engine?.Dispose(); launcher.Dispose(); }
    }

    private static uint[] ProcessIds(JobHandle job)
    {
        const int capacity = 256;
        var size = 8 + IntPtr.Size * capacity;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryInformationJobObject(job, 3, buffer, (uint)size, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var count = Marshal.ReadInt32(buffer, 4);
            if (count < 0 || count > capacity) throw new IOException("Слишком много процессов в пробном job.");
            return Enumerable.Range(0, count).Select(index => checked((uint)Marshal.ReadIntPtr(buffer, 8 + index * IntPtr.Size).ToInt64())).ToArray();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static ServiceProcess Identity(SafeProcessHandle process, uint id)
    {
        var path = new StringBuilder(32768);
        var length = (uint)path.Capacity;
        if (!QueryFullProcessImageName(process, 0, path, ref length) || !GetProcessTimes(process, out var created, out _, out _, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(id, Path.GetFullPath(path.ToString()), DateTime.FromFileTimeUtc(created).Ticks);
    }

    private sealed class JobHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    { protected override bool ReleaseHandle() => CloseHandle(handle); }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public UIntPtr MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcesses;
        public UIntPtr Affinity;
        public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public uint Size;
        public string? Reserved, Desktop, Title;
        public uint X, Y, Width, Height, Columns, Rows, Fill, Flags;
        public ushort Show, ReservedSize;
        public IntPtr ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern JobHandle CreateJobObject(IntPtr security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(JobHandle job, int kind, ref ExtendedLimits limits, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObject(JobHandle job, int kind, IntPtr buffer, uint length, out uint returned);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(JobHandle job, SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsProcessInJob(SafeProcessHandle process, JobHandle job, [MarshalAs(UnmanagedType.Bool)] out bool belongs);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateJobObject(JobHandle job, uint code);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processSecurity,
        IntPtr threadSecurity, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(SafeProcessHandle process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint timeout);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint id);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
