using System.Text;

namespace Zapret.Core;

public static class CommandLine
{
    // Windows CRT quoting: backslashes before a closing quote must be doubled.
    public static string Quote(string value)
    {
        var output = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            output.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            output.Append(c);
            slashes = 0;
        }
        return output.Append('\\', slashes * 2).Append('"').ToString();
    }

    public static string Join(IEnumerable<string> arguments) => string.Join(" ", arguments.Select(Quote));

    public static IReadOnlyList<string> Split(string command)
    {
        var result = new List<string>();
        var token = new StringBuilder();
        var quoted = false;
        var started = false;
        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (started) { result.Add(token.ToString()); token.Clear(); started = false; }
                continue;
            }
            started = true;
            if (c == '\\')
            {
                var count = 1;
                while (i + 1 < command.Length && command[i + 1] == '\\') { count++; i++; }
                if (i + 1 < command.Length && command[i + 1] == '"')
                {
                    token.Append('\\', count / 2);
                    i++;
                    if (count % 2 == 1) token.Append('"'); else quoted = !quoted;
                }
                else token.Append('\\', count);
            }
            else if (c == '"') quoted = !quoted;
            else token.Append(c);
        }
        if (quoted) throw new FormatException("В пресете не закрыта кавычка.");
        if (started) result.Add(token.ToString());
        return result;
    }
}
