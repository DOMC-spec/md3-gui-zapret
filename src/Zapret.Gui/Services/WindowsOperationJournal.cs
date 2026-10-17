using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Zapret.Gui.Services;

/// <summary>Machine-wide, administrator-owned journal for the machine-wide zapret service. Never reads recovery commands from user-writable files.</summary>
public sealed class WindowsOperationJournal : IOperationJournal
{
    private const string KeyPath = @"SOFTWARE\ZapretGui.Recovery";
    private const string ValueName = "OperationV1";
    public OperationRecord? Read()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(KeyPath, RegistryKeyPermissionCheck.ReadSubTree, RegistryRights.ReadKey);
        if (key == null) return null;
        ValidateSecurity(key.Handle);
        return ReadRecord(key);
    }
    public void Write(OperationRecord record)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor("O:BAG:BAD:P(A;;KA;;;SY)(A;;KA;;;BA)", 1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            var code = RegCreateKeyEx(new IntPtr(unchecked((int)0x80000002)), KeyPath, 0, null, 0,
                0x2001B | 0x100, ref attributes, out var handle, out _); // READ_CONTROL | QUERY/SET_VALUE, 64-bit view.
            if (code != 0) throw new Win32Exception(code);
            using (handle)
            using (var key = RegistryKey.FromHandle(handle, RegistryView.Registry64))
            {
                ValidateSecurity(handle);
                var bytes = OperationJournalCodec.Encode(record);
                key.SetValue(ValueName, bytes, RegistryValueKind.Binary);
                code = RegFlushKey(handle);
                if (code != 0) throw new Win32Exception(code);
                if (ReadRecord(key) != record) throw new IOException("Запись восстановления не подтверждена после сохранения.");
            }
        }
        finally { LocalFree(descriptor); }
    }
    private static OperationRecord? ReadRecord(RegistryKey key) => key.GetValue(ValueName) switch
    {
        null => null,
        byte[] bytes when key.GetValueKind(ValueName) == RegistryValueKind.Binary => OperationJournalCodec.Decode(bytes),
        _ => throw new IOException("Неизвестный формат журнала; изменение службы запрещено.")
    };
    private static void ValidateSecurity(SafeRegistryHandle handle)
    {
        uint size = 0;
        var code = RegGetKeySecurity(handle, 5, null, ref size); // OWNER | DACL
        if (code != 122 || size == 0 || size > 65536) throw new IOException("Защиту журнала нельзя подтвердить.");
        var bytes = new byte[size];
        code = RegGetKeySecurity(handle, 5, bytes, ref size);
        if (code != 0) throw new Win32Exception(code);
        var security = new RawSecurityDescriptor(bytes, 0);
        static bool Trusted(string? sid) => sid is "S-1-5-18" or "S-1-5-32-544";
        if (!Trusted(security.Owner?.Value) || !security.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected)
            || security.DiscretionaryAcl is not { Count: 2 } acl
            || acl.Cast<GenericAce>().Any(ace => ace is not CommonAce { AceQualifier: AceQualifier.AccessAllowed,
                AccessMask: 0xF003F, AceFlags: AceFlags.None } allowed || !Trusted(allowed.SecurityIdentifier.Value))
            || acl.Cast<CommonAce>().Select(ace => ace.SecurityIdentifier.Value).Distinct().Count() != 2)
            throw new IOException("Журнал доступен не только администраторам и SYSTEM или имеет неподтверждённого владельца. Восстановление запрещено.");
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
    [DllImport("advapi32.dll", EntryPoint = "RegCreateKeyExW", CharSet = CharSet.Unicode)]
    private static extern int RegCreateKeyEx(IntPtr hive, string path, int reserved, string? keyClass, int options, int rights,
        ref SecurityAttributes attributes, out SafeRegistryHandle handle, out int disposition);
    [DllImport("advapi32.dll")]
    private static extern int RegGetKeySecurity(SafeRegistryHandle handle, uint information, byte[]? descriptor, ref uint size);
    [DllImport("advapi32.dll")]
    private static extern int RegFlushKey(SafeRegistryHandle handle);
    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
