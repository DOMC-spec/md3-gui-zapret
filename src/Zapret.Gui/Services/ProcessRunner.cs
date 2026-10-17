using System.Diagnostics;

namespace Zapret.Gui.Services;

public sealed record CommandResult(int ExitCode, string Output);

public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string executable, params string[] arguments);
}

public sealed class ProcessRunner : ICommandRunner
{
    public async Task<CommandResult> RunAsync(string executable, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        // CMD parses its /c tail as shell source, not as CRT argv. ArgumentList would
        // insert backslash-escaped quotes, corrupting quoted paths in this fixed invocation.
        if (executable.Equals(Path.Combine(Environment.SystemDirectory, "cmd.exe"), StringComparison.OrdinalIgnoreCase)
            && arguments.Length == 4 && arguments[0] == "/d" && arguments[1] == "/v:off" && arguments[2] == "/c")
            info.Arguments = "/d /v:off /c " + arguments[3];
        else foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException($"Не удалось запустить {executable}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            throw new TimeoutException($"Команда {Path.GetFileName(executable)} не завершилась за 35 секунд.");
        }
        return new CommandResult(process.ExitCode, (await stdout + "\n" + await stderr).Trim());
    }
}
