using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Zapret.Gui.Services;

public sealed record GameFilterSettings(string Mode, string TcpRange, string UdpRange)
{
    public GameFilterSettings Normalize()
    {
        var mode = Mode.ToLowerInvariant();
        if (mode is not ("disabled" or "all" or "tcp" or "udp")) throw new FormatException("Выберите режим Game Filter.");
        return new(mode, Range(TcpRange, "TCP"), Range(UdpRange, "UDP"));
    }

    private static string Range(string value, string protocol)
    {
        value = value.Replace(" ", "");
        if (!Regex.IsMatch(value, @"\A[1-9][0-9]{0,4}(?:-[1-9][0-9]{0,4})?(?:,[1-9][0-9]{0,4}(?:-[1-9][0-9]{0,4})?)*\z")
            || value.Split(',').Any(item => { var ends = item.Split('-').Select(int.Parse).ToArray();
                return ends.Any(port => port > 65535) || ends[0] > ends[^1]; }))
            throw new FormatException($"{protocol}: введите порты 1–65535 или возрастающие диапазоны через запятую, без ведущих нулей. Пустое значение недопустимо; стандартный диапазон — 1024-65535.");
        return value;
    }
}

public sealed record GameFilterSnapshot(GameFilterSettings Settings, string Revision, string Tcp, string Udp);

public sealed partial class FlowsealAdapter
{
    public Task<GameFilterSnapshot> ReadGameFilterAsync(string root) => Task.Run(() =>
    {
        using var source = OpenGameFilterSource(root);
        var path = Path.Combine(root, "utils", "game_filter.enabled");
        using var flag = File.Exists(path) ? OpenGameFilterFile(path, FileAccess.Read) : null;
        var bytes = ReadSettingsBytes(flag);
        return ReadFrozenSettings(source, bytes, Revision(bytes));
    });

    public Task<GameFilterSnapshot> SaveGameFilterAsync(string root, GameFilterSettings settings, string revision) =>
        Task.Factory.StartNew(() =>
        {
            settings = settings.Normalize();
            using var guard = new Mutex(false, GuardName);
            var acquired = false;
            try
            {
                try { acquired = guard.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; throw new IOException("Предыдущая операция прервана. Обновите состояние перед сохранением."); }
                if (!acquired) throw new IOException("Другая оболочка выполняет операцию. Дождитесь её завершения.");
                if (journal.Read() is { Phase: not OperationPhase.Completed }) throw new IOException("Сначала завершите восстановление незавершённой операции.");
                using var source = OpenGameFilterSource(root);
                var path = Path.Combine(root, "utils", "game_filter.enabled");
                // An exclusive handle prevents writes, renames and replacement during comparison/commit.
                using var flag = File.Exists(path) ? OpenGameFilterFile(path, FileAccess.ReadWrite) : null;
                var before = ReadSettingsBytes(flag);
                if (Revision(before) != revision) throw new IOException("Файл Game Filter изменился вне программы. Ваш ввод сохранён. Перечитайте настройки для сравнения и повторите сохранение.");
                var bytes = Encoding.ASCII.GetBytes($"mode={settings.Mode}\r\ntcp={settings.TcpRange}\r\nudp={settings.UdpRange}\r\n");
                // Validate the candidate with the native helper before touching the user's file.
                var candidate = ReadFrozenSettings(source, bytes, Revision(bytes));
                if (candidate.Settings != settings) throw new IOException("Flowseal прочитал настройки иначе. Файл не изменён.");
                if (flag == null)
                {
                    using var created = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                    return WriteAndReadBack(created, source, bytes, [], null);
                }
                var backup = path + "." + Guid.NewGuid().ToString("N") + ".bak";
                using (var copy = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { copy.Write(before!); copy.Flush(true); }
                return WriteAndReadBack(flag, source, bytes, before!, backup);
            }
            finally { if (acquired) guard.ReleaseMutex(); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private GameFilterSnapshot WriteAndReadBack(FileStream file, Stream source, byte[] bytes, byte[] before, string? backup)
    {
        try
        {
            WriteSettings(bytes);
            var actual = ReadSettingsBytes(file)!;
            if (!actual.SequenceEqual(bytes)) throw new IOException("Записанные настройки отличаются от ожидаемых.");
            return ReadFrozenSettings(source, actual, Revision(actual));
        }
        catch (Exception error) when (Recoverable(error))
        {
            try { WriteSettings(before); }
            catch (Exception recoveryError) when (Recoverable(recoveryError))
            {
                throw new IOException("Не удалось сохранить или вернуть настройки Game Filter. "
                    + (backup == null ? "Прежде файл отсутствовал; штатный режим — отключён." : "Исходные байты сохранены: " + backup)
                    + " Ошибка возврата: " + recoveryError.Message, error);
            }
            throw new IOException("Сохранение Game Filter не завершено; прежние настройки возвращены. "
                + (backup == null ? "Вместо отсутствовавшего файла оставлен пустой файл: Flowseal читает его как отключённый режим со стандартными диапазонами. " : "Резервная копия: " + backup + ". ")
                + error.Message, error);
        }

        void WriteSettings(byte[] content)
        {
            file.Position = 0;
            file.Write(content);
            file.SetLength(content.Length);
            file.Flush(true);
            file.Position = 0;
        }
    }

    private FileStream OpenGameFilterSource(string root)
    {
        var script = Path.Combine(root, "service.bat");
        if (!ServiceOwnershipCheck.TryProvePath(script, out _) || !ServiceOwnershipCheck.TryProvePath(Path.Combine(root, "utils"), out _))
            throw new IOException("Выберите локальную поставку Flowseal без ссылок с папкой utils.");
        var source = new FileStream(script, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!distribution.Describe(root).ServiceScriptVerified)
        { source.Dispose(); throw new IOException("service.bat не соответствует Flowseal 1.10.3. Настройки не изменены."); }
        return source;
    }

    private static FileStream OpenGameFilterFile(string path, FileAccess access)
    {
        if (!ServiceOwnershipCheck.TryProvePath(path, out _)) throw new IOException("Настройки Game Filter проходят через ссылку или недоступны.");
        return new FileStream(path, FileMode.Open, access, access == FileAccess.Read ? FileShare.Read : FileShare.None);
    }

    private static byte[]? ReadSettingsBytes(Stream? stream)
    {
        if (stream == null) return null;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
    private static string Revision(byte[]? bytes) => bytes == null ? "missing" : Convert.ToHexString(SHA256.HashData(bytes));

    private GameFilterSnapshot ReadFrozenSettings(Stream source, byte[]? bytes, string revision)
    {
        if (bytes?.Any(b => !(b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z'
            or >= (byte)'0' and <= (byte)'9' or (byte)'=' or (byte)'_' or (byte)',' or (byte)';' or (byte)' '
            or (byte)'-' or (byte)'\t' or (byte)'\r' or (byte)'\n')) == true)
            throw new IOException("Файл Game Filter содержит неподдерживаемые символы. Используйте ASCII mode/tcp/udp без команд CMD.");
        source.Position = 0;
        using var settings = new MemoryStream(bytes ?? []);
        var exported = ExportFrozenGameFilter(source, settings);
        if (exported.ExitCode != 0) throw new IOException("Flowseal не прочитал Game Filter: " + exported.Output);
        var values = exported.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2)).Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.OrdinalIgnoreCase);
        try
        {
            var normalized = new GameFilterSettings(values["GameFilterMode"], values["GameFilterTCPRange"], values["GameFilterUDPRange"]).Normalize();
            return new(normalized, revision, values["GameFilterTCP"], values["GameFilterUDP"]);
        }
        catch (Exception error) when (error is KeyNotFoundException or FormatException)
        { throw new IOException("Flowseal не экспортировал проверяемые настройки Game Filter.", error); }
    }
}
