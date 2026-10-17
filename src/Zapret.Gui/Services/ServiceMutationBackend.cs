using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace Zapret.Gui.Services;

/// <summary>The Windows SCM boundary; test implementations never invoke OS commands.</summary>
public interface IServiceMutationBackend
{
    void Stop(ServiceSnapshot expected);
    void Delete(ServiceSnapshot expected);
    void Create(ServiceSnapshot desired);
    void Start(ServiceSnapshot expected);
}

public sealed partial class ZapretService : IServiceMutationBackend
{
    private static ServiceRecoveryData ReadRecoveryData(ServiceHandle handle, RegistryKey? key)
    {
        if (key == null) throw new IOException("Запись службы недоступна.");
        // Explicit supported profile. Reject config2 extensions/custom data rather than silently lose them.
        string[] values = ["ImagePath", "Start", "Type", "ErrorControl", "ObjectName", "DisplayName", "Group", "Tag",
            "DependOnService", "DependOnGroup", "Description", "zapret-discord-youtube", "ZapretGuiOperation"];
        if (key.GetValueNames().Except(values, StringComparer.OrdinalIgnoreCase).Any()
            || key.GetSubKeyNames().Any(name => !name.Equals("Security", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Дополнительные параметры службы не поддерживаются полным восстановлением; служба сохранена.");
        string? Text(string name) => key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) switch
        { null => null, string value when key.GetValueKind(name) == RegistryValueKind.String => value,
            _ => throw new IOException("Неподдерживаемый формат " + name) };
        _ = Text("zapret-discord-youtube");
        using (var securityKey = key.OpenSubKey("Security"))
        {
            if (securityKey != null)
            {
                if (securityKey.GetSubKeyNames().Length != 0 || securityKey.GetValueNames().Any(name => name != "Security")
                    || securityKey.GetValue("Security") is not byte[] persisted)
                    throw new IOException("Неподдерживаемая запись безопасности службы.");
                if (new RawSecurityDescriptor(persisted, 0).SystemAcl != null)
                    throw new IOException("Служба с SACL требует отдельного восстановления аудита; изменения запрещены.");
            }
        }
        using var manager = OpenSCManager(null, null, 1);
        if (manager.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        using var securityHandle = OpenService(manager, "zapret", 0x20000);
        if (securityHandle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        QueryServiceObjectSecurity(securityHandle, 7, null, 0, out var needed);
        if (needed == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var descriptor = new byte[needed];
        if (!QueryServiceObjectSecurity(securityHandle, 7, descriptor, needed, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var securityDescriptor = new RawSecurityDescriptor(descriptor, 0);
        using var identity = WindowsIdentity.GetCurrent();
        if ((securityDescriptor.Owner != identity.Owner && securityDescriptor.Owner != identity.User)
            || (securityDescriptor.Group != identity.User && identity.Groups?.Contains(securityDescriptor.Group!) != true))
            throw new IOException("Владелец или группа ACL не могут быть восстановлены текущим токеном; служба сохранена.");
        return new(Text("Description") ?? "", Convert.ToBase64String(descriptor), Text("ZapretGuiOperation"));
    }

    public void Stop(ServiceSnapshot expected) => Mutate(expected, 0x20, handle =>
    {
        if (!ControlService(handle, 1, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
    });
    public void Delete(ServiceSnapshot expected) => Mutate(expected, 0x10000, handle =>
    {
        if (!DeleteService(handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
    });
    public void Start(ServiceSnapshot expected) => Mutate(expected, 0x10, handle =>
    {
        if (!StartService(handle, 0, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
    });
    private static void Mutate(ServiceSnapshot expected, uint access, Action<ServiceHandle> action)
    {
        using var manager = OpenSCManager(null, null, 1);
        if (manager.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        using var handle = OpenService(manager, "zapret", access);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var current = Read();
        if (!FlowsealAdapter.SameObservation(expected, current)) throw new IOException("Служба изменилась перед командой Windows.");
        action(handle); // The handle still addresses this generation even if its name is subsequently reused.
    }
    public void Create(ServiceSnapshot desired)
    {
        var config = desired.Configuration ?? throw new IOException("Нет конфигурации создания.");
        using var manager = OpenSCManager(null, null, 2);
        if (manager.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var dependencies = config.Dependencies.Length == 0 ? null : config.Dependencies + "\0\0";
        using var handle = CreateService(manager, "zapret", config.DisplayName, 0xF01FF, config.ServiceType,
            config.StartType, config.ErrorControl, config.BinaryPath, null, IntPtr.Zero, dependencies, null, null);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        ServiceSnapshot? initialized = null;
        try
        {
            initialized = Read();
            if (initialized.RecoveryData == null || initialized.ObservationError != null || initialized.Configuration != desired.Configuration
                || initialized.State != ServiceState.Stopped)
                throw new IOException("Созданная запись не допускает полного наблюдения; инициализация прекращена.");
            // CreateService atomically refuses an occupied name. Never use config to overwrite an existing entry.
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\zapret", true)
                ?? throw new IOException("Созданная служба недоступна.");
            void Set(string name, string? value) { if (value != null) key.SetValue(name, value, RegistryValueKind.String); }
            Set("ZapretGuiOperation", desired.RecoveryData?.OperationId);
            initialized = initialized with { RecoveryData = initialized.RecoveryData! with { OperationId = desired.RecoveryData?.OperationId } };
            var description = new ServiceDescription { Text = desired.RecoveryData?.Description ?? "" };
            if (!ChangeServiceConfig2(handle, 1, ref description)) throw new Win32Exception(Marshal.GetLastWin32Error());
            initialized = initialized with { RecoveryData = initialized.RecoveryData! with { Description = description.Text } };
            Set("zapret-discord-youtube", desired.Preset);
            initialized = initialized with { Preset = desired.Preset };
            if (desired.RecoveryData?.SecurityDescriptor is { Length: > 0 } security
                && !SetServiceObjectSecurity(handle, SecurityFlags(security), Convert.FromBase64String(security))) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch
        {
            // Initialization failed: only delete the just-created object through its original handle.
            if (initialized != null && FlowsealAdapter.SameObservation(initialized, Read())) DeleteService(handle);
            throw;
        }
    }

    private static uint SecurityFlags(string descriptor) => 7u | ((new RawSecurityDescriptor(Convert.FromBase64String(descriptor), 0).ControlFlags
        & ControlFlags.DiscretionaryAclProtected) != 0 ? 0x80000000u : 0x20000000u);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceDescription { [MarshalAs(UnmanagedType.LPWStr)] public string Text; }
    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ChangeServiceConfig2(ServiceHandle handle, uint level, ref ServiceDescription description);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicStatus { public uint Type, State, Controls, Exit, SpecificExit, Checkpoint, WaitHint; }
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ControlService(ServiceHandle handle, uint control, out BasicStatus status);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteService(ServiceHandle handle);
    [DllImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool StartService(ServiceHandle handle, uint count, IntPtr arguments);
    [DllImport("advapi32.dll", EntryPoint = "CreateServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle CreateService(ServiceHandle manager, string name, string display, uint access, uint type,
        uint start, uint error, string binary, string? group, IntPtr tag, string? dependencies, string? account, string? password);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceObjectSecurity(ServiceHandle handle, uint info,
        byte[]? descriptor, uint size, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetServiceObjectSecurity(ServiceHandle handle, uint info, byte[] descriptor);
}
