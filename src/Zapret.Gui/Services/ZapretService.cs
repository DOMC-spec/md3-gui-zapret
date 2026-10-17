using System.ComponentModel;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Zapret.Gui.Services;

public enum ServiceState { Missing, Running, Stopped, Starting, Stopping, Paused, Unknown }
public enum ServiceOwnership { Missing, Owned, Foreign, Unknown }
public sealed record ServiceConfiguration(string BinaryPath, uint StartType = 2, uint ServiceType = 16,
    uint ErrorControl = 1, string Account = "LocalSystem", string Dependencies = "", string DisplayName = "zapret", string LoadOrderGroup = "", uint TagId = 0);
public sealed record ServiceProcess(uint Id, string? Executable, long? StartedAtUtcTicks);
public sealed record ServiceRecoveryData(string Description, string SecurityDescriptor, string? OperationId = null);
public sealed record ServiceSnapshot(ServiceState State, string? Preset, ServiceConfiguration? Configuration = null,
    ServiceProcess? Process = null, ServiceOwnership Ownership = ServiceOwnership.Unknown,
    string OwnershipReason = "Принадлежность службы не проверена.", string? ObservationError = null,
    ServiceRecoveryData? RecoveryData = null);

/// <summary>SCM observation with a fail-closed recovery snapshot; guarded mutation is implemented in ServiceMutationBackend.</summary>
public sealed partial class ZapretService
{
    public Task<ServiceSnapshot> ReadAsync() => Task.Run(Read);

    private static ServiceSnapshot Read()
    {
        using var manager = OpenSCManager(null, null, 1); // SC_MANAGER_CONNECT
        if (manager.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        using var service = OpenService(manager, "zapret", 1 | 4); // QUERY_CONFIG | QUERY_STATUS: basic state remains available without ACL access.
        if (service.IsInvalid)
        {
            var code = Marshal.GetLastWin32Error();
            if (code == 1060) return new(ServiceState.Missing, null);
            throw new Win32Exception(code);
        }
        const uint capacity = 8192; // Documented maximum QUERY_SERVICE_CONFIG buffer.
        var buffer = Marshal.AllocHGlobal((int)capacity);
        ServiceConfiguration? configuration = null;
        string? observationError = null;
        try
        {
            if (!QueryServiceConfig(service, buffer, capacity, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var native = Marshal.PtrToStructure<QueryConfig>(buffer);
            var dependencies = new List<string>();
            for (var current = native.Dependencies; current != IntPtr.Zero && Marshal.ReadInt16(current) != 0;)
            {
                var dependency = Marshal.PtrToStringUni(current)!;
                dependencies.Add(dependency);
                current += (dependency.Length + 1) * 2;
            }
            configuration = new(Marshal.PtrToStringUni(native.BinaryPath) ?? "", native.StartType,
                native.ServiceType, native.ErrorControl, Marshal.PtrToStringUni(native.Account) ?? "",
                string.Join('\0', dependencies), Marshal.PtrToStringUni(native.DisplayName) ?? "",
                Marshal.PtrToStringUni(native.LoadOrderGroup) ?? "", native.TagId);
        }
        catch (Win32Exception ex)
        {
            observationError = "QueryServiceConfig: " + ex.NativeErrorCode;
            // Windows caps this API at 8 KiB; long Flowseal command lines can also fail RPC with 1734.
            if (ex.NativeErrorCode is 122 or 1734)
            {
                try
                {
                    var fallback = ReadRegistryConfiguration();
                    if (Encoding.Unicode.GetByteCount(fallback.BinaryPath) + Marshal.SizeOf<QueryConfig>() > capacity)
                        configuration = fallback;
                    else observationError += "; размер конфигурации не подтверждает ограничение API";
                }
                catch (Exception registryError) when (registryError is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                { observationError += "; конфигурация из реестра недоступна: " + registryError.Message; }
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        if (!QueryServiceStatusEx(service, 0, out var status, (uint)Marshal.SizeOf<StatusProcess>(), out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var state = status.CurrentState switch
        {
            1 => ServiceState.Stopped, 2 or 5 => ServiceState.Starting, 3 => ServiceState.Stopping,
            4 => ServiceState.Running, 6 or 7 => ServiceState.Paused, _ => ServiceState.Unknown
        };
        ServiceProcess? process = null;
        if (status.ProcessId != 0 && state is ServiceState.Running or ServiceState.Paused)
        {
            try
            {
                using var observed = OpenProcess(0x1000, false, status.ProcessId); // PROCESS_QUERY_LIMITED_INFORMATION
                if (observed.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                var path = new StringBuilder(32768);
                var length = (uint)path.Capacity;
                if (!QueryFullProcessImageName(observed, 0, path, ref length)
                    || !GetProcessTimes(observed, out var created, out _, out _, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                process = new(status.ProcessId, path.ToString(), DateTime.FromFileTimeUtc(created).Ticks);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException)
            { process = new(status.ProcessId, null, null); }
        }
        string? preset = null;
        ServiceRecoveryData? recovery = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\zapret");
            preset = key?.GetValue("zapret-discord-youtube", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            recovery = ReadRecoveryData(service, key);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or Win32Exception)
        { observationError = (observationError == null ? "" : observationError + "; ") + "Снимок: " + ex.Message; }
        return new(state, preset, configuration, process, ObservationError: observationError, RecoveryData: recovery);
    }

    private static ServiceConfiguration ReadRegistryConfiguration()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\zapret")
            ?? throw new IOException("Запись службы исчезла во время наблюдения.");
        string RequiredString(string name) => key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string
            ?? throw new IOException("Нельзя подтвердить поле службы: " + name);
        uint RequiredDword(string name) => key.GetValue(name) is int value ? unchecked((uint)value)
            : throw new IOException("Нельзя подтвердить поле службы: " + name);
        string OptionalString(string name, string fallback) => key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) switch
        { null => fallback, string value => value, _ => throw new IOException("Неподдерживаемое поле службы: " + name) };
        string[] Multi(string name) => key.GetValue(name) switch
        { null => [], string[] values => values, _ => throw new IOException("Неподдерживаемые зависимости службы.") };
        var dependencies = Multi("DependOnService").Concat(Multi("DependOnGroup").Select(group => "+" + group));
        var tag = key.GetValue("Tag") switch { null => 0u, int value => unchecked((uint)value), _ => throw new IOException("Неподдерживаемое поле Tag.") };
        return new(RequiredString("ImagePath"), RequiredDword("Start"), RequiredDword("Type"), RequiredDword("ErrorControl"),
            RequiredString("ObjectName"), string.Join('\0', dependencies), OptionalString("DisplayName", "zapret"), OptionalString("Group", ""), tag);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct QueryConfig
    {
        public uint ServiceType, StartType, ErrorControl;
        public IntPtr BinaryPath, LoadOrderGroup;
        public uint TagId;
        public IntPtr Dependencies, Account, DisplayName;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StatusProcess
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode,
            CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }
    private sealed class ServiceHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenService(ServiceHandle manager, string name, uint access);
    [DllImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig(ServiceHandle service, IntPtr config, uint size, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(ServiceHandle service, int infoLevel, out StatusProcess status, uint size, out uint needed);
    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr service);
}
