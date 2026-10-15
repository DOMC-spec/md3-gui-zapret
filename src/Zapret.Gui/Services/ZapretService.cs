using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Win32;
using Zapret.Core;

namespace Zapret.Gui.Services;

public enum ServiceState { Missing, Running, Stopped, Starting, Stopping, Paused, Unknown }
public sealed record ServiceSnapshot(ServiceState State, string? Preset);

public sealed class ZapretService(ICommandRunner runner)
{
    private const string RegistryPath = @"SYSTEM\CurrentControlSet\Services\zapret";
    private readonly string sc = Path.Combine(Environment.SystemDirectory, "sc.exe");

    private static int NativeCode(Exception ex) => ex is Win32Exception win ? win.NativeErrorCode
        : ex.InnerException != null ? NativeCode(ex.InnerException) : -1;

    public Task<ServiceSnapshot> ReadAsync() => Task.Run(() =>
    {
        try
        {
            using var service = new ServiceController("zapret");
            var state = service.Status switch
            {
                ServiceControllerStatus.Running => ServiceState.Running,
                ServiceControllerStatus.Stopped => ServiceState.Stopped,
                ServiceControllerStatus.StartPending or ServiceControllerStatus.ContinuePending => ServiceState.Starting,
                ServiceControllerStatus.StopPending => ServiceState.Stopping,
                ServiceControllerStatus.Paused or ServiceControllerStatus.PausePending => ServiceState.Paused,
                _ => ServiceState.Unknown
            };
            using var key = Registry.LocalMachine.OpenSubKey(RegistryPath);
            return new ServiceSnapshot(state, key?.GetValue("zapret-discord-youtube") as string);
        }
        catch (Exception ex) when (NativeCode(ex) == 1060) { return new ServiceSnapshot(ServiceState.Missing, null); }
    });

    private async Task ScAsync(int[] allowed, params string[] args)
    {
        var result = await runner.RunAsync(sc, args);
        if (result.ExitCode != 0 && !allowed.Contains(result.ExitCode))
            throw new IOException($"sc {string.Join(' ', args.Take(2))}: код {result.ExitCode}\n{result.Output}");
    }

    private async Task WaitForAsync(Func<ServiceSnapshot, bool> ready, string failure)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (ready(await ReadAsync())) return;
            await Task.Delay(300);
        }
        throw new System.TimeoutException(failure);
    }

    public async Task RemoveAsync()
    {
        await Task.Run(async () =>
        {
            foreach (var process in Process.GetProcessesByName("winws"))
            {
                using (process)
                {
                    try
                    {
                        process.Kill(true);
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await process.WaitForExitAsync(timeout.Token);
                    }
                    catch (InvalidOperationException) { /* Process already exited. */ }
                }
            }
        });
        await ScAsync([1060, 1062], "stop", "zapret");
        await WaitForAsync(s => s.State is ServiceState.Missing or ServiceState.Stopped,
            "Служба zapret не остановилась. Повторите операцию после проверки служб Windows.");
        await ScAsync([1060, 1072], "delete", "zapret");
        await WaitForAsync(s => s.State == ServiceState.Missing,
            "Служба помечена для удаления. Закройте services.msc и повторите попытку.");
        // sc returns numeric codes independent of the Windows UI language.
        await ScAsync([1060, 1062], "stop", "WinDivert");
        await Task.Run(() =>
        {
            try
            {
                using var driver = new ServiceController("WinDivert");
                driver.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            }
            catch (Exception ex) when (NativeCode(ex) == 1060) { }
        });
    }

    public async Task ApplyAsync(string root, Preset preset)
    {
        var executable = Path.Combine(root, "bin", "winws.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("Не найден bin\\winws.exe. Поместите конфигуратор в корневую папку распакованного zapret.");
        var parsed = PresetParser.Parse(await File.ReadAllTextAsync(preset.FilePath), root, PresetParser.ReadGameFilter(root));
        foreach (var path in parsed.ReferencedFiles)
        {
            if (File.Exists(path)) continue;
            // Upstream service.bat creates these optional user lists on first use.
            var name = Path.GetFileName(path);
            if (Path.GetDirectoryName(path)!.Equals(Path.Combine(root, "lists"), StringComparison.OrdinalIgnoreCase)
                && name is "list-general-user.txt" or "list-exclude-user.txt" or "ipset-exclude-user.txt")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, name.StartsWith("ipset") ? "203.0.113.113/32\r\n" : "# User domains\r\ndomain.example.abc\r\n");
            }
            else throw new FileNotFoundException($"Пресет ссылается на отсутствующий файл: {path}");
        }
        // Validate everything before disrupting the currently running configuration.
        await RemoveAsync();
        await ScAsync([], "create", "zapret", "binPath=", parsed.ServiceCommand(executable),
            "start=", "auto", "displayname=", "Zapret DPI Bypass");
        await ScAsync([], "start", "zapret");
        await WaitForAsync(s => s.State == ServiceState.Running, "Служба зарегистрирована, но не перешла в состояние «Работает».");
        await Task.Delay(800);
        if ((await ReadAsync()).State != ServiceState.Running)
            throw new IOException("winws завершился после запуска. Проверьте стратегию, файлы bin и журнал событий Windows.");
        using var key = Registry.LocalMachine.OpenSubKey(RegistryPath, true)
            ?? throw new IOException("Служба исчезла после запуска.");
        key.SetValue("zapret-discord-youtube", preset.Name);
    }
}
