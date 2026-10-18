using System.Globalization;
using System.Text;
using System.Security.Cryptography;

namespace Zapret.Core;

public sealed record HostlistSnapshot(string Path, string Text, string Revision);

public sealed class HostlistStore(string root)
{
    public static IReadOnlyDictionary<string, string> UserListSeeds { get; } = new Dictionary<string, string>
    {
        ["ipset-exclude-user.txt"] = "203.0.113.113/32\r\n",
        ["list-general-user.txt"] = "# Never leave this file empty\r\ndomain.example.abc\r\n",
        ["list-exclude-user.txt"] = "domain.example.abc\r\n"
    };
    private readonly string root = Path.GetFullPath(root);
    public IReadOnlyList<string> Discover() => Directory.EnumerateFiles(root, "list*.txt", new EnumerationOptions
    {
        RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true
    }).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();

    private string Validate(string path)
    {
        path = Path.GetFullPath(path);
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Список должен находиться внутри папки zapret.");
        for (var current = new FileInfo(path) as FileSystemInfo; current != null;
             current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent)
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current.FullName); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Ссылки на внешние файлы не поддерживаются.");
        }
        return path;
    }

    public Task<HostlistSnapshot> ReadSnapshotAsync(string path) => Task.Run(() =>
    {
        path = Validate(path);
        if (!File.Exists(path))
        {
            if (!IsOptionalUserList(path)) throw new FileNotFoundException("Список не найден. Восстановите файл поставки.", path);
            return new HostlistSnapshot(path, UserListSeeds[Path.GetFileName(path)], "missing");
        }
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var bytes = ReadBytes(file);
        return new HostlistSnapshot(path, Decode(bytes).Text, Revision(bytes));
    });

    public Task<HostlistSnapshot> SaveSnapshotAsync(HostlistSnapshot original, string text) => Task.Run(() =>
    {
        var path = Validate(original.Path);
        if (!File.Exists(path))
        {
            if (original.Revision != "missing") throw Conflict();
            if (!IsOptionalUserList(path)) throw new IOException("Создание разрешено только для штатных пользовательских списков.");
            using var created = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            return WriteWithRollback(created, text, new UTF8Encoding(false, true), Encoding.ASCII.GetBytes(UserListSeeds[Path.GetFileName(path)]), null);
        }
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var bytes = ReadBytes(file);
        if (Revision(bytes) != original.Revision) throw Conflict();
        var encoding = Decode(bytes).Encoding;
        var backup = Validate(path + ".bak");
        using (var copy = new FileStream(backup, FileMode.Create, FileAccess.Write, FileShare.None))
        { copy.Write(bytes); copy.Flush(true); }
        return WriteWithRollback(file, text, encoding, bytes, backup);

        HostlistSnapshot WriteWithRollback(FileStream target, string content, Encoding encoding, byte[] before, string? backup)
        {
            try
            {
                var encoded = encoding.GetPreamble().Concat(encoding.GetBytes(content)).ToArray();
                Write(encoded);
                var actual = ReadBytes(target);
                if (!actual.SequenceEqual(encoded)) throw new IOException("Перечитывание списка не подтвердило запись.");
                return new(path, content, Revision(actual));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or EncoderFallbackException)
            {
                try { Write(before); }
                catch (Exception recovery) when (recovery is IOException or UnauthorizedAccessException)
                { throw new IOException("Возврат списка не подтверждён. Резервная копия: " + (backup ?? "прежде файл отсутствовал") + ". " + recovery.Message, error); }
                throw new IOException("Список не сохранён; прежние байты возвращены. "
                    + (backup == null ? "Прежде файл отсутствовал; оставлено штатное начальное содержимое Flowseal. " : "Резервная копия: " + backup + ". ") + error.Message, error);
            }
            void Write(byte[] value) { target.Position = 0; target.Write(value); target.SetLength(value.Length); target.Flush(true); target.Position = 0; }
        }
    });

    private bool IsOptionalUserList(string path) => Path.GetDirectoryName(path)!.Equals(Path.Combine(root, "lists"), StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(path) is "list-general-user.txt" or "list-exclude-user.txt" && Directory.Exists(Path.GetDirectoryName(path));
    private static IOException Conflict() => new("Файл изменился вне программы. Черновик сохранён в редакторе: перечитайте файл для сравнения или сохраните свои правки отдельно.");
    private static byte[] ReadBytes(Stream stream) { using var copy = new MemoryStream(); stream.CopyTo(copy); return copy.ToArray(); }
    private static string Revision(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static (string Text, Encoding Encoding) Decode(byte[] bytes)
    {
        Encoding[] encodings = [new UTF8Encoding(true, true), new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true),
            new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true)];
        var encoding = encodings.FirstOrDefault(e => bytes.AsSpan().StartsWith(e.GetPreamble())) ?? new UTF8Encoding(false, true);
        var skip = bytes.AsSpan().StartsWith(encoding.GetPreamble()) ? encoding.GetPreamble().Length : 0;
        try { return (encoding.GetString(bytes, skip, bytes.Length - skip), encoding); }
        catch (DecoderFallbackException error) { throw new IOException("Кодировка списка не поддерживается: используйте UTF-8 или Unicode с BOM. Исходный файл не изменён.", error); }
    }

    public async Task<string> ReadAsync(string path) => await File.ReadAllTextAsync(Validate(path));

    public async Task SaveAsync(string path, string text, string original)
    {
        path = Validate(path);
        if (await File.ReadAllTextAsync(path) != original)
            throw new IOException("Файл изменился вне программы. Перечитайте список перед сохранением.");
        var bytes = await File.ReadAllBytesAsync(path);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, text, new UTF8Encoding(hasBom));
            File.Replace(temporary, path, path + ".bak", true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static string NormalizeDomain(string input)
    {
        input = input.Trim().TrimEnd('.');
        if (input.Length == 0 || input.Contains('/') || input.Contains(':') || input.Contains('*'))
            throw new FormatException("Введите домен без https://, пути и символа *.");
        string ascii;
        try { ascii = new IdnMapping().GetAscii(input).ToLowerInvariant(); }
        catch (ArgumentException) { throw new FormatException("Некорректное имя домена."); }
        if (ascii.Length > 253 || !ascii.Contains('.') || Uri.CheckHostName(ascii) != UriHostNameType.Dns
            || ascii.Split('.').Any(label => label.Length is 0 or > 63 || label.StartsWith('-') || label.EndsWith('-') || label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
            throw new FormatException("Некорректное имя домена.");
        return ascii;
    }

    public static string AddDomain(string text, string input)
    {
        var domain = NormalizeDomain(input);
        if (text.Split('\n').Any(line =>
        {
            try { return NormalizeDomain(line) == domain; }
            catch (FormatException) { return false; }
        })) return text;
        var newline = text.Contains("\r\n") || text.Length == 0 ? "\r\n" : "\n";
        return text + (text.Length == 0 || text.EndsWith('\n') ? "" : newline) + domain + newline;
    }
}
