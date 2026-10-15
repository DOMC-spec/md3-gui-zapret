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
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
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
