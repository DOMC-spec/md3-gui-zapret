using Zapret.Core;
using Zapret.Gui.Services;

static class PreparationScenarios
{
    public static async Task Run(string root)
    {
        var flag = Path.Combine(root, "utils", "game_filter.enabled");
        File.WriteAllText(flag, "");
        var original = new ServiceSnapshot(ServiceState.Stopped, "previous",
            new ServiceConfiguration(CommandLine.Quote(Path.Combine(root, "bin", "winws.exe")) + " --wf-tcp=80"),
            RecoveryData: new("Previous description", "AQID"));
        var backend = new MemoryService(original);
        var commands = new PreparedCommands();
        var adapter = new FlowsealAdapter(commands, () => Task.FromResult(backend.State), backend);
        var result = await adapter.ExecuteAsync(new(root, FlowsealAction.InstallService, new("general", Path.Combine(root, "general.bat"))));
        if (result.Outcome != FlowsealActionOutcome.Success || !result.State.Configuration!.BinaryPath.Contains("--wf-tcp=80,443,2053,2083,2087,2096,8443,12"))
            throw new Exception("Empty Flowseal 1.10.3 flag must install disabled Game Filter, not all ports: " + result);
        if (!commands.Prepared) throw new Exception("Service installation skipped Flowseal preparation");
        File.Delete(flag);
        Console.WriteLine("PASS empty 1.10.3 flag disables Game Filter and service installation performs preparation");

        var seeds = new Dictionary<string, string>
        {
            ["ipset-exclude-user.txt"] = "203.0.113.113/32\r\n",
            ["list-general-user.txt"] = "# Never leave this file empty\r\ndomain.example.abc\r\n",
            ["list-exclude-user.txt"] = "domain.example.abc\r\n"
        };
        foreach (var name in seeds.Keys) File.Delete(Path.Combine(root, "lists", name));
        backend = new MemoryService(original);
        commands = new PreparedCommands();
        adapter = new FlowsealAdapter(commands, () => Task.FromResult(backend.State), backend);
        var request = new FlowsealActionRequest(root, FlowsealAction.InstallService, new("general", Path.Combine(root, "general.bat")));
        var preflight = await adapter.PreflightAsync(request);
        if (preflight.Outcome != FlowsealActionOutcome.Ready || commands.Prepared || seeds.Keys.Any(name => File.Exists(Path.Combine(root, "lists", name))))
            throw new Exception("Missing optional user lists must allow a side-effect-free preflight");
        result = await adapter.ExecuteAsync(request);
        if (result.Outcome != FlowsealActionOutcome.Success || !commands.TimestampsEnabled)
            throw new Exception("Flowseal preparation must enable timestamps before installing the service: " + result.Explanation);
        foreach (var seed in seeds)
            if (!File.ReadAllBytes(Path.Combine(root, "lists", seed.Key)).SequenceEqual(System.Text.Encoding.ASCII.GetBytes(seed.Value)))
                throw new Exception("User list seed differs from Flowseal 1.10.3: " + seed.Key);
        var seedReference = Path.Combine(root, "seed-reference");
        Directory.CreateDirectory(Path.Combine(seedReference, "lists"));
        File.Copy(Path.Combine(root, "service.bat"), Path.Combine(seedReference, "service.bat"));
        await new ProcessRunner().RunAsync(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d", "/v:off", "/c",
            "call \"" + Path.Combine(seedReference, "service.bat") + "\" load_user_lists");
        foreach (var seed in seeds)
            if (!File.ReadAllBytes(Path.Combine(root, "lists", seed.Key)).SequenceEqual(File.ReadAllBytes(Path.Combine(seedReference, "lists", seed.Key))))
                throw new Exception("List initialization differs from the original external load_user_lists: " + seed.Key);
        Console.WriteLine("PASS absent user lists get exact Flowseal seeds and TCP timestamps are prepared");

        foreach (var failure in new[] { "tcp", "export", "timeout", "unsafe-settings" })
        {
            backend = new MemoryService(original);
            commands = new PreparedCommands { Failure = failure };
            adapter = new FlowsealAdapter(commands, () => Task.FromResult(backend.State), backend);
            if (failure == "unsafe-settings") File.WriteAllText(flag, "mode=all&echo injected\r\n");
            result = await adapter.ExecuteAsync(request);
            if (backend.State != original || result.Outcome == FlowsealActionOutcome.Success)
                throw new Exception("Preparation refusal must preserve the previous service: " + failure);
            if (failure == "unsafe-settings" && commands.Prepared)
                throw new Exception("Unsafe settings must be rejected before executing any upstream CMD");
            if (File.Exists(flag)) File.Delete(flag);
        }
        Console.WriteLine("PASS TCP/export failure and unsafe settings preserve the previous service");

        var preserved = seeds.Keys.ToDictionary(name => Path.Combine(root, "lists", name), name => name.StartsWith("ipset")
            ? Array.Empty<byte>() : new System.Text.UTF8Encoding(true).GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes("пример.рф\r\n")).ToArray());
        foreach (var file in preserved) File.WriteAllBytes(file.Key, file.Value);
        backend = new MemoryService(original);
        adapter = new FlowsealAdapter(new PreparedCommands(), () => Task.FromResult(backend.State), backend);
        result = await adapter.ExecuteAsync(request);
        if (result.Outcome != FlowsealActionOutcome.Success || preserved.Any(file => !File.ReadAllBytes(file.Key).SequenceEqual(file.Value)))
            throw new Exception("Preparation must preserve empty, BOM and user-edited lists byte for byte");
        backend = new MemoryService(original);
        var changedList = preserved.Keys.First();
        commands = new PreparedCommands { BeforeTcp = () => File.WriteAllText(changedList, "external edit") };
        result = await new FlowsealAdapter(commands, () => Task.FromResult(backend.State), backend).ExecuteAsync(request);
        if (result.Recovery != FlowsealRecoveryOutcome.Conflict || backend.State != original || File.ReadAllText(changedList) != "external edit")
            throw new Exception("External edits during preparation must survive without stopping the previous service");
        Console.WriteLine("PASS preparation preserves custom and empty lists and stops on external changes before service mutation");

        foreach (var item in new (string? Settings, string Tcp, string Udp)[]
        {
            (null, "12", "12"), ("", "12", "12"), ("all", "1024-65535", "1024-65535"),
            ("tcp", "1024-65535", "12"), ("udp", "12", "1024-65535"),
            ("mode=mystery\ntcp=2000", "12", "12"),
            ("MODE=AlL\nTCP=2000-3000\nUDP=4000,5000-6000", "2000-3000", "4000,5000-6000"),
            ("mode=tcp\ntcp=1, 22, 65535", "1,22,65535", "12"),
            ("mode=all\ntcp=0\nudp=65536", "1024-65535", "1024-65535"),
            ("mode=all\ntcp=0012\nudp=500-200", "1024-65535", "1024-65535"),
            ("mode=all\ntcp=80,,443\nudp=1-2-3", "1024-65535", "1024-65535"),
            ("mode=all\ntcp=2000\ntcp=bad\nudp=4000\nudp=5000", "1024-65535", "5000"),
            ("mode=all\nmode=udp\ntcp=2000\nudp=4000", "12", "4000"),
            ("; ignored comment\n mode=all\ntcp=2000", "12", "12"),
            ("all=ignored\nudp=3000", "1024-65535", "3000")
        })
        {
            if (item.Settings == null) { if (File.Exists(flag)) File.Delete(flag); }
            else File.WriteAllText(flag, item.Settings, new System.Text.UTF8Encoding(false));
            backend = new MemoryService(original);
            var actualCommands = new UpstreamPreparationCommands();
            result = await new FlowsealAdapter(actualCommands, () => Task.FromResult(backend.State), backend).ExecuteAsync(request);
            var actualArgs = result.State.Configuration == null ? [] : CommandLine.Split(result.State.Configuration.BinaryPath);
            if (result.Outcome != FlowsealActionOutcome.Success || !actualArgs.Contains("--wf-tcp=80,443,2053,2083,2087,2096,8443," + item.Tcp)
                || !actualArgs.Contains("--wf-udp=443,19294-19344,50000-50100," + item.Udp))
                throw new Exception("Upstream Game Filter mismatch for " + (item.Settings ?? "absent") + ": " + result.ApplyError + " / " + result.Explanation);
        }
        File.Delete(flag);
        Console.WriteLine("PASS original CMD Game Filter: absent/empty, legacy, modes, boundaries, invalid ranges, duplicates and case");
        await RunStrategies(root);
    }

    public static async Task RunStrategies(string root)
    {
        var original = new ServiceSnapshot(ServiceState.Missing, null);
        var flag = Path.Combine(root, "utils", "game_filter.enabled");
        foreach (var settings in new[] { null, "mode=all\r\ntcp=2000-3000\r\nudp=4000,5000-6000\r\n" })
        {
        if (settings == null) { if (File.Exists(flag)) File.Delete(flag); }
        else File.WriteAllText(flag, settings, new System.Text.UTF8Encoding(false));
        foreach (var strategy in new FlowsealDistributionAdapter().Describe(root).Strategies)
        {
            var backend = new MemoryService(original);
            var result = await new FlowsealAdapter(new UpstreamPreparationCommands(), () => Task.FromResult(backend.State), backend)
                .ExecuteAsync(new(root, FlowsealAction.InstallService, strategy.Preset));
            if (result.Outcome != FlowsealActionOutcome.Success) throw new Exception("Original strategy installation failed: " + strategy.Preset.Name + " / " + result.ApplyError);
            if (result.State.Preset != strategy.Preset.Name) throw new Exception("Strategy marker differs from upstream filename without extension: " + result.State.Preset);
            var expected = await CaptureUpstreamCommand(root, strategy.Preset);
            var actual = NormalizeOptions(CommandLine.Split(result.State.Configuration!.BinaryPath)
                .Select(value => value.Replace(root + Path.DirectorySeparatorChar, @"C:\Zapret parity\", StringComparison.OrdinalIgnoreCase)).ToArray());
            var reference = NormalizeOptions(CommandLine.Split(expected));
            if (!actual.SequenceEqual(reference))
                throw new Exception("Service arguments differ from original Flowseal install parser: " + strategy.Preset.Name
                    + "\nExpected first difference: " + reference.Where((value, index) => index >= actual.Length || actual[index] != value).FirstOrDefault()
                    + "\nActual first difference: " + actual.Where((value, index) => index >= reference.Length || reference[index] != value).FirstOrDefault());
        }
        Console.WriteLine("PASS all 22 service commands match original Flowseal parsing: " + (settings == null ? "disabled" : "custom TCP and UDP"));
        }
        if (File.Exists(flag)) File.Delete(flag);
    }

    private static string[] NormalizeOptions(IReadOnlyList<string> arguments)
    {
        // winws accepts --option=value and --option value; preserve order and repeated options.
        var result = new List<string>();
        for (var i = 0; i < arguments.Count; i++)
        {
            var value = arguments[i];
            if (value.StartsWith("--") && !value.Contains('=') && i + 1 < arguments.Count && !arguments[i + 1].StartsWith("--"))
                value += "=" + arguments[++i];
            result.Add(value);
        }
        return result.ToArray();
    }

    private static async Task<string> CaptureUpstreamCommand(string root, Preset strategy)
    {
        var source = File.ReadAllText(Path.Combine(root, "service.bat"));
        var begin = source.IndexOf(":: Args that should be followed by value", StringComparison.Ordinal);
        var end = source.IndexOf(":: Creating service with parsed args", begin, StringComparison.Ordinal);
        // A common short logical root avoids CMD's 8191-character truncation caused by
        // sandbox TEMP paths. Only root expansion changes; the upstream algorithm stays verbatim.
        var parser = source[begin..end].Replace("%~dp0", @"C:\Zapret parity\", StringComparison.OrdinalIgnoreCase);
        var script = Path.Combine(root, "capture.cmd");
        var output = Path.Combine(root, "captured-command.txt");
        if (File.Exists(output)) File.Delete(output);
        var text = "@echo off\r\nsetlocal EnableDelayedExpansion\r\ncd /d \"%~dp0\"\r\ncall service.bat load_game_filter\r\n"
            + "set \"selectedFile=" + Path.GetFileName(strategy.FilePath) + "\"\r\nset \"BIN_PATH=C:\\Zapret parity\\bin\\\"\r\n" + parser
            + "\r\nset ARGS=%args%\r\ncall set \"ARGS=%%ARGS:EXCL_MARK=^!%%\"\r\n"
            + "\"%TASK04_CAPTURE%\" --capture-bin \"%~dp0captured-command.txt\" create zapret binPath= \"\\\"%BIN_PATH%winws.exe\\\" !ARGS!\" DisplayName= \"zapret\" start= auto\r\n";
        // The oracle retains upstream parsing verbatim, while its only executable call captures argv.
        File.WriteAllText(script, text, System.Text.Encoding.ASCII);
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
        start.Arguments = "/d /c call \"" + script + "\"";
        start.Environment["TASK04_CAPTURE"] = Environment.ProcessPath!;
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw new TimeoutException("Upstream argument oracle timed out"); }
        if (process.ExitCode != 0 || !File.Exists(output)) throw new Exception("Argument oracle failed: " + await stdout + await stderr);
        return File.ReadAllText(output);
    }
}

sealed class UpstreamPreparationCommands : ICommandRunner
{
    public Task<CommandResult> RunAsync(string executable, params string[] arguments) =>
        Path.GetFileName(executable).Equals("cmd.exe", StringComparison.OrdinalIgnoreCase)
            ? new ProcessRunner().RunAsync(executable, arguments)
            : Task.FromResult(new CommandResult(0, "TCP preparation simulated; no netsh execution"));
}

sealed class PreparedCommands : ICommandRunner
{
    public bool Prepared { get; private set; }
    public bool TimestampsEnabled { get; private set; }
    public string? Failure { get; init; }
    public Action? BeforeTcp { get; init; }
    public Task<CommandResult> RunAsync(string executable, params string[] arguments)
    {
        Prepared = true;
        if (Failure == "timeout") throw new TimeoutException("Injected preparation timeout");
        if (Path.GetFileName(executable).Equals("netsh.exe", StringComparison.OrdinalIgnoreCase))
        {
            BeforeTcp?.Invoke();
            if (Failure == "tcp") return Task.FromResult(new CommandResult(5, "injected timestamp preparation failure"));
            TimestampsEnabled = true;
        }
        else if (Failure == "export") return Task.FromResult(new CommandResult(0, "GameFilterTCP=12"));
        return Task.FromResult(new CommandResult(0, "GameFilter=12\r\nGameFilterTCP=12\r\nGameFilterUDP=12"));
    }
}
