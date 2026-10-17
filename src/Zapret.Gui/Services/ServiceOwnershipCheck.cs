namespace Zapret.Gui.Services;

internal static class ServiceOwnershipCheck
{
    public static ServiceSnapshot Classify(string root, ServiceSnapshot snapshot)
    {
        ServiceSnapshot Result(ServiceOwnership ownership, string reason) => snapshot with { Ownership = ownership, OwnershipReason = reason };
        if (snapshot.State == ServiceState.Missing) return Result(ServiceOwnership.Missing, "Служба zapret не установлена.");
        if (snapshot.State is ServiceState.Unknown or ServiceState.Starting or ServiceState.Stopping
            || snapshot.Configuration == null || snapshot.Configuration.ServiceType != 16 || !TryExecutable(snapshot.Configuration.BinaryPath, out var configured)
            || !TryProvePath(Path.Combine(root, "bin", "winws.exe"), out var expected)
            || !TryProvePath(configured, out configured) || !File.Exists(expected) || !File.Exists(configured))
            return Result(ServiceOwnership.Unknown, "Принадлежность не подтверждена: путь или состояние службы нельзя проверить. Изменения запрещены; проверьте установку и обновите состояние.");
        if (snapshot.State is ServiceState.Running or ServiceState.Paused)
        {
            if (snapshot.Process is not { Id: > 0, StartedAtUtcTicks: not null, Executable: not null } observed
                || !TryProvePath(observed.Executable, out var actual) || !File.Exists(actual)
                || !actual.Equals(configured, StringComparison.OrdinalIgnoreCase))
                return Result(ServiceOwnership.Unknown, "Не удалось подтвердить исполняемый файл работающего процесса службы. Конфигурация следующего запуска не доказывает принадлежность текущего процесса.");
        }
        return configured.Equals(expected, StringComparison.OrdinalIgnoreCase)
            ? Result(ServiceOwnership.Owned, "Своя служба: проверен bin/winws.exe выбранной поставки. Работа службы не подтверждает доступность сервисов.")
            : Result(ServiceOwnership.Foreign, "Сторонняя установка: служба использует другую папку. Она останется нетронутой; выберите соответствующую поставку.");
    }

    private static bool TryExecutable(string command, out string path)
    {
        path = "";
        // SCM environment differs from the GUI/user environment. Only fixed Windows locations are provable here.
        command = System.Text.RegularExpressions.Regex.Replace(command, "%([^%]+)%", match =>
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return match.Groups[1].Value.ToUpperInvariant() switch
            {
                "SYSTEMROOT" or "WINDIR" => windows,
                "SYSTEMDRIVE" => Path.GetPathRoot(windows)!.TrimEnd('\\'),
                _ => match.Value
            };
        }).Trim();
        if (command.Length == 0 || command.IndexOfAny(['\r', '\n', '%']) >= 0) return false;
        if (command[0] == '"')
        {
            var end = command.IndexOf('"', 1);
            if (end < 0 || (end + 1 < command.Length && !char.IsWhiteSpace(command[end + 1]))) return false;
            path = command[1..end];
            if (path.EndsWith('\\')) return false;
        }
        else
        {
            path = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
            // Unquoted paths with spaces are ambiguous to CreateProcess, even if a candidate happens to exist.
            if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || path.Contains('"')) return false;
        }
        return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool TryProvePath(string path, out string full)
    {
        full = "";
        try
        {
            if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)
                || path.IndexOf(':', 2) >= 0 || path.IndexOfAny(['"', '\r', '\n']) >= 0) return false;
            full = Path.GetFullPath(path);
            if (!File.Exists(full) && !Directory.Exists(full)) return false;
            for (var current = full; current != null; current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        { return false; }
    }
}
