using System.Text;
using Zapret.Core;

namespace Zapret.Gui.Services;

public sealed record DomainListDescription(string Path, string Name, string Purpose, bool? UsedByStrategy);

public sealed partial class FlowsealAdapter
{
    public Task<IReadOnlyList<DomainListDescription>> ReadDomainListsAsync(string root, Preset? strategy) => Task.Run<IReadOnlyList<DomainListDescription>>(() =>
    {
        var store = new HostlistStore(root);
        if (!Directory.Exists(root)) return [];
        var files = store.Discover().ToList();
        if (Directory.Exists(Path.Combine(root, "lists")))
            files.AddRange(new[] { "list-general-user.txt", "list-exclude-user.txt" }.Select(name => Path.Combine(root, "lists", name)));
        return files.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Select((path, index) =>
            DescribeDomainList(root, path, strategy, index + 1)).ToArray();
    });

    private DomainListDescription DescribeDomainList(string root, string path, Preset? strategy, int index)
    {
        var (name, purpose) = Path.GetFileName(path).ToLowerInvariant() switch
        {
            "list-general-user.txt" => ("Мои сайты", "Добавляйте сайты для обработки выбранной стратегией. Содержимое поставки хранится отдельно."),
            "list-exclude-user.txt" => ("Мои исключения", "Домены, которые выбранная стратегия должна исключить из обработки."),
            "list-general.txt" => ("Основные сайты поставки", "Список сайтов Flowseal. Для своих дополнений удобнее использовать «Мои сайты»."),
            "list-exclude.txt" => ("Исключения поставки", "Штатные исключения Flowseal. Для своих дополнений используйте «Мои исключения»."),
            "list-google.txt" => ("Сайты Google", "Специальный список поставки для правил, которые ссылаются на него."),
            _ => ("Дополнительный список " + index, "Дополнительный список доменов; его назначение зависит от правил стратегии.")
        };
        bool? used = null;
        var verified = distribution.Describe(root).Strategies.FirstOrDefault(s => s.Verified
            && s.Preset.FilePath.Equals(strategy?.FilePath, StringComparison.OrdinalIgnoreCase));
        if (verified != null)
        {
            try { used = PresetParser.Parse(File.ReadAllText(verified.Preset.FilePath), root).ReferencedFiles.Contains(Path.GetFullPath(path), StringComparer.OrdinalIgnoreCase); }
            catch (Exception error) when (error is IOException or FormatException or UnauthorizedAccessException) { }
        }
        return new(path, name, purpose, used);
    }

    private static void CheckDomainListPath(string path)
    {
        if (!Path.GetFileName(path).StartsWith("list", StringComparison.OrdinalIgnoreCase)
            || !Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Разрешены только текстовые списки доменов list*.txt внутри поставки.");
    }
    public Task<HostlistSnapshot> ReadDomainListAsync(string root, string path)
    {
        CheckDomainListPath(path);
        return new HostlistStore(root).ReadSnapshotAsync(path);
    }

    public Task<HostlistSnapshot> SaveDomainListAsync(string root, HostlistSnapshot original, string text) => Task.Factory.StartNew(() =>
    {
        CheckDomainListPath(original.Path);
        using var guard = new Mutex(false, GuardName);
        var acquired = false;
        try
        {
            try { acquired = guard.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; throw new IOException("Предыдущая операция прервана. Обновите состояние перед сохранением списка."); }
            if (!acquired) throw new IOException("Другая оболочка выполняет операцию. Дождитесь её завершения.");
            if (journal.Read() is { Phase: not OperationPhase.Completed }) throw new IOException("Сначала завершите восстановление операции.");
            using var script = File.Open(Path.Combine(root, "service.bat"), FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!distribution.Describe(root).Capabilities.Any(c => c.Action == FlowsealAction.EditLists && c.Available)
                || !ServiceOwnershipCheck.TryProvePath(Path.Combine(root, "lists"), out _))
                throw new IOException("Для сохранения выберите проверенную поставку Flowseal с локальной папкой lists без ссылок.");
            return new HostlistStore(root).SaveSnapshotAsync(original, text).GetAwaiter().GetResult();
        }
        finally { if (acquired) guard.ReleaseMutex(); }
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public Task ExportDomainListAsync(string root, string path, string text) => Task.Run(() =>
    {
        path = Path.GetFullPath(path);
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !ServiceOwnershipCheck.TryProvePath(Path.GetDirectoryName(path)!, out _))
            throw new IOException("Выберите новый файл вне поставки, в локальной папке без ссылок.");
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var bytes = new UTF8Encoding(false, true).GetBytes(text);
        output.Write(bytes);
        output.Flush(true);
    });
}
