using System.Text.RegularExpressions;

namespace Zapret.Core;

public sealed record Preset(string Name, string FilePath);
public sealed record ParsedPreset(IReadOnlyList<string> Arguments, IReadOnlyList<string> ReferencedFiles)
{
    public string ServiceCommand(string executable) => CommandLine.Quote(executable) + " " + CommandLine.Join(Arguments);
}

public static class PresetParser
{
    public static IReadOnlyList<Preset> Discover(string root) => Directory.EnumerateFiles(root, "*.bat")
        .Where(path => !Path.GetFileName(path).Equals("service.bat", StringComparison.OrdinalIgnoreCase))
        .Where(path => Regex.IsMatch(File.ReadAllText(path), @"(?im)^(?!\s*(?:rem\s|::)).*winws\.exe"))
        .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
        .Select(path => new Preset(Path.GetFileNameWithoutExtension(path), path)).ToArray();

    public static ParsedPreset Parse(string text, string root, IReadOnlyDictionary<string, string>? gameVariables = null)
    {
        root = Path.GetFullPath(root);
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["BIN"] = Path.Combine(root, "bin") + Path.DirectorySeparatorChar,
            ["LISTS"] = Path.Combine(root, "lists") + Path.DirectorySeparatorChar,
            ["GameFilter"] = "12", ["GameFilterTCP"] = "12", ["GameFilterUDP"] = "12"
        };
        if (gameVariables != null) foreach (var pair in gameVariables) variables[pair.Key] = pair.Value;
        var commands = Regex.Replace(text, @"\^\s*\r?\n", " ").Split('\n');
        string Expand(string value)
        {
            value = value.Replace("%~dp0", root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            return Regex.Replace(value, @"%([^%]+)%|(?<!\^)!([A-Za-z_][A-Za-z_0-9]*)!", match =>
            {
                var key = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                return variables.TryGetValue(key, out var resolved) ? resolved
                    : throw new FormatException($"Неизвестная переменная {match.Value}. Этот BAT требует ручной адаптации.");
            }).Replace("^!", "!");
        }
        var launches = new List<string>();
        foreach (var raw in commands)
        {
            var line = raw.Trim().TrimStart('@');
            if (line.StartsWith("::") || Regex.IsMatch(line, @"^rem(?:\s|$)", RegexOptions.IgnoreCase)) continue;
            var set = Regex.Match(line, "^set\\s+\"?(?<key>[A-Za-z_][A-Za-z_0-9]*)=(?<value>.*?)\"?$", RegexOptions.IgnoreCase);
            if (set.Success)
            {
                // Only literal assignments are supported; never execute batch code.
                variables[set.Groups["key"].Value] = Expand(set.Groups["value"].Value);
                continue;
            }
            var launch = Regex.Match(line, "^(?:start\\s+.*?|)(?:\"[^\"]*winws\\.exe\"|[^\\s\"]*winws\\.exe)\\s+(?<args>.*)$", RegexOptions.IgnoreCase);
            if (launch.Success) launches.Add(Expand(launch.Groups["args"].Value.Trim()));
        }
        if (launches.Count != 1) throw new FormatException("Пресет должен содержать ровно один прямой запуск winws.exe.");
        var tokens = CommandLine.Split(launches[0]);
        if (tokens.Count == 0 || tokens.Any(t => !t.StartsWith("--", StringComparison.Ordinal) || t.Contains('\r')))
            throw new FormatException("Поддерживаются только аргументы --параметр; команды BAT после winws.exe не допускаются.");
        var files = new List<string>();
        var normalized = tokens.Select(token =>
        {
            var parts = token.Split('=', 2);
            if (parts.Length != 2) return token;
            var key = parts[0];
            var value = parts[1];
            var isFile = key is "--hostlist" or "--hostlist-exclude" or "--hostlist-auto" or "--ipset" or "--ipset-exclude"
                || key.StartsWith("--dpi-desync-fake-", StringComparison.Ordinal) && value.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                || key == "--dpi-desync-split-seqovl-pattern" && value.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);
            if (!isFile) return token;
            // Flowseal starts winws in bin; all service file arguments must be absolute.
            value = Path.GetFullPath(value, Path.Combine(root, "bin"));
            files.Add(value);
            return key + "=" + value;
        }).ToArray();
        return new ParsedPreset(normalized, files.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

}
