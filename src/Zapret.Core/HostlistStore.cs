using System.Globalization;
using System.Text;

namespace Zapret.Core;

public sealed class HostlistStore(string root)
{
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
        for (var current = new FileInfo(path) as FileSystemInfo; current != null && current.FullName != root;
             current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Ссылки на внешние файлы не поддерживаются.");
        return path;
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
        if (text.Split('\n').Any(line => line.Trim().Equals(domain, StringComparison.OrdinalIgnoreCase))) return text;
        var newline = text.Contains("\r\n") || text.Length == 0 ? "\r\n" : "\n";
        return text + (text.Length == 0 || text.EndsWith('\n') ? "" : newline) + domain + newline;
    }
}
