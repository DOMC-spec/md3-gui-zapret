using System.Diagnostics;
using Zapret.Gui.Services;
using Zapret.Core;

// Inert executables used only by the original BAT manual-session fixture below.
var fixtureExecutable = Path.GetFileNameWithoutExtension(Environment.ProcessPath!);
if (fixtureExecutable.Equals("sc", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("STATE : 1 STOPPED");
    return;
}
if (fixtureExecutable.Equals("netsh", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("timestamps enabled");
    return;
}
if (fixtureExecutable.Equals("winws", StringComparison.OrdinalIgnoreCase))
{
    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "manual-capture.json"),
        System.Text.Json.JsonSerializer.Serialize(new { Arguments = args, NoUpdate = Environment.GetEnvironmentVariable("NO_UPDATE_CHECK"),
            Directory = Environment.CurrentDirectory }));
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return;
}

if (args.FirstOrDefault() == "--capture-bin")
{
    if (args.Length < 6) throw new Exception("Incomplete upstream command capture");
    File.WriteAllText(args[1], args[5]);
    return;
}

if (args.Length == 3 && args[0] == "--interrupted-child")
{
    await InterruptedOperationScenarios.Child(args[1], args[2]);
    return;
}

if (args.Length == 4 && args[0] == "--hold-guard")
{
    using var ready = EventWaitHandle.OpenExisting(args[2]);
    using var release = EventWaitHandle.OpenExisting(args[3]);
    var reads = 0;
    var child = new FlowsealAdapter(new RejectCommands(), () =>
    {
        if (++reads == 2)
        {
            ready.Set();
            if (!release.WaitOne(TimeSpan.FromSeconds(15))) throw new TimeoutException("Guard test release timed out");
        }
        return Task.FromResult(new ServiceSnapshot(ServiceState.Stopped, null,
            new ServiceConfiguration(CommandLine.Quote(Path.Combine(args[1], "bin", "winws.exe")))));
    });
    var result = await child.PreflightAsync(new(args[1], FlowsealAction.InstallService, new("general", Path.Combine(args[1], "general.bat"))));
    if (result.Outcome != FlowsealActionOutcome.RecoveryRequired) throw new Exception("Guard owner failed after release: " + result);
    return;
}

var root = Path.Combine(Path.GetTempPath(), "Zapret guard " + Guid.NewGuid());
try
{
    CopyReference(root);
    if (args.Contains("--game-filter-only"))
    {
        await GameFilterScenarios.Run(root);
        return;
    }
    if (args.Contains("--interrupted-only"))
    {
        await InterruptedOperationScenarios.Run(root);
        return;
    }
    if (args.Contains("--manual-only"))
    {
        await ManualSessionScenarios.Run(root);
        return;
    }
    if (args.Contains("--controls-only"))
    {
        await ServiceControlScenarios.Run(root);
        return;
    }
    if (args.Contains("--strategy-parity"))
    {
        await PreparationScenarios.RunStrategies(root);
        return;
    }
    if (args.Contains("--preparation-only"))
    {
        await PreparationScenarios.Run(root);
        return;
    }
    if (!args.Contains("--guards-only"))
    {
        await GameFilterScenarios.Run(root);
        await PreparationScenarios.Run(root);
        if (args.Contains("--review-recovery"))
        {
            await RecoveryScenarios.RunReview(root);
            return;
        }
        await RecoveryScenarios.Run(root);
        await ServiceControlScenarios.Run(root);
        await ManualSessionScenarios.Run(root);
        await InterruptedOperationScenarios.Run(root);
    }
    var runner = new RejectCommands();
    var installedByBat = new ServiceSnapshot(ServiceState.Stopped, null,
        new ServiceConfiguration(CommandLine.Quote(Path.Combine(root, "bin", "winws.exe")) + " --wf-tcp=443"));
    var adapter = new FlowsealAdapter(runner, () => Task.FromResult(installedByBat));
    var state = await adapter.ReadStateAsync(root);
    if (state.Ownership != ServiceOwnership.Owned)
        throw new Exception("Same-distribution BAT-installed service must be recognized by executable path without a GUI marker");
    Console.WriteLine("PASS same-distribution BAT service ownership without side effects");
    var request = new FlowsealActionRequest(root, FlowsealAction.InstallService,
        new Preset("general", Path.Combine(root, "general.bat")));
    var preflight = await adapter.PreflightAsync(request);
    if (preflight.Outcome != FlowsealActionOutcome.RecoveryRequired || preflight.State.Ownership != ServiceOwnership.Owned)
        throw new Exception("Replacing an owned service must be refused explicitly until recovery is implemented");
    var execution = await adapter.ExecuteAsync(request);
    if (execution.Outcome != FlowsealActionOutcome.RecoveryRequired || runner.Commands != 0)
        throw new Exception("Public execution must repeat safety checks and refuse replacement without external effects");
    Console.WriteLine("PASS owned replacement requires recovery in preflight and execution");
    var other = Path.Combine(root, "other", "winws.exe");
    Directory.CreateDirectory(Path.GetDirectoryName(other)!);
    File.WriteAllText(other, "foreign inert fixture");
    foreach (var scenario in new[]
    {
        (CommandLine.Quote(other), ServiceOwnership.Foreign, FlowsealActionOutcome.ForeignInstallation),
        (@"C:\unquoted folder\winws.exe --wf-tcp=443", ServiceOwnership.Unknown, FlowsealActionOutcome.UnknownOwnership),
        ("bin\\winws.exe --wf-tcp=443", ServiceOwnership.Unknown, FlowsealActionOutcome.UnknownOwnership),
        ("\"" + Path.Combine(root, "bin", "winws.exe"), ServiceOwnership.Unknown, FlowsealActionOutcome.UnknownOwnership)
    })
    {
        var observation = installedByBat with { Configuration = new(scenario.Item1) };
        var guarded = new FlowsealAdapter(runner, () => Task.FromResult(observation));
        var result = await guarded.ExecuteAsync(request);
        if (result.Outcome != scenario.Item3 || result.State.Ownership != scenario.Item2 || runner.Commands != 0)
            throw new Exception("Foreign or ambiguous service must remain untouched: " + scenario.Item1);
    }
    Console.WriteLine("PASS foreign and unknown ownership refuse action without side effects");
    var original = File.ReadAllBytes(request.Strategy!.FilePath);
    foreach (var malformed in new[] { "@echo off\ncall evil.bat\n", "start \"\" \"%BIN%winws.exe\" --wf-tcp=443&echo unsafe", "start \"\" \"%BIN%winws.exe\" --wf-tcp=443|evil" })
    {
        File.WriteAllText(request.Strategy.FilePath, malformed);
        var refused = await adapter.ExecuteAsync(request);
        if (refused.Outcome != FlowsealActionOutcome.UnsupportedStrategy || runner.Commands != 0)
            throw new Exception("Unsupported or glued CMD must be rejected before any action");
    }
    File.WriteAllBytes(request.Strategy.FilePath, original);
    Console.WriteLine("PASS changed and glued BAT commands refuse action before effects");
    foreach (var relative in new[] { "bin/WinDivert64.sys", "bin/tls_clienthello_4pda_to.bin", "lists/list-general-user.txt" })
    {
        var file = Path.Combine(root, relative);
        var bytes = File.ReadAllBytes(file);
        File.Delete(file);
        var refused = await adapter.ExecuteAsync(request);
        var expectedOutcome = relative == "lists/list-general-user.txt" ? FlowsealActionOutcome.RecoveryRequired : FlowsealActionOutcome.MissingFiles;
        if (refused.Outcome != expectedOutcome || File.Exists(file) || runner.Commands != 0)
            throw new Exception("Missing required or referenced file must be refused without creating it: " + relative);
        File.WriteAllBytes(file, bytes);
    }
    Console.WriteLine("PASS missing required and referenced files remain missing and block execution");
    File.AppendAllText(Path.Combine(root, "general (ALT).bat"), "\nrem unrelated edit\n");
    var unaffected = await adapter.PreflightAsync(request);
    if (unaffected.Outcome != FlowsealActionOutcome.RecoveryRequired)
        throw new Exception("An unrelated changed strategy must not mislabel the selected original strategy");
    Console.WriteLine("PASS unrelated source changes preserve selected original validation");
    foreach (var intervention in new[] { "state", "configuration", "list", "strategy", "game-filter", "binary" })
    {
        var reads = 0;
        var changing = new FlowsealAdapter(runner, () =>
        {
            reads++;
            if (reads == 2)
            {
                if (intervention == "state") return Task.FromResult(installedByBat with { State = ServiceState.Running });
                if (intervention == "configuration") return Task.FromResult(installedByBat with { Configuration = installedByBat.Configuration! with { StartType = 3 } });
                var file = Path.Combine(root, intervention switch
                {
                    "list" => "lists/list-general.txt", "strategy" => "general.bat",
                    "binary" => "bin/winws.exe", _ => "utils/game_filter.enabled"
                });
                File.AppendAllText(file, "external change\n");
            }
            return Task.FromResult(installedByBat);
        });
        var beforeFiles = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        var refused = await changing.ExecuteAsync(request);
        if (refused.Outcome != FlowsealActionOutcome.Conflict || runner.Commands != 0)
            throw new Exception("Detected external " + intervention + " change must abort before any action");
        if (intervention == "state" && refused.State.State != ServiceState.Running)
            throw new Exception("Conflict result must expose the externally observed state");
        if (intervention == "configuration" && refused.State.Configuration?.StartType != 3)
            throw new Exception("Conflict result must expose the externally observed configuration");
        if (intervention is not ("state" or "configuration"))
        {
            var externalFile = Path.Combine(root, intervention switch
            {
                "list" => "lists/list-general.txt", "strategy" => "general.bat",
                "binary" => "bin/winws.exe", _ => "utils/game_filter.enabled"
            });
            if (!File.ReadAllText(externalFile).EndsWith("external change\n"))
                throw new Exception("Conflict must preserve the external edit rather than restore over it");
        }
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            if (!beforeFiles.ContainsKey(file)) File.Delete(file);
        foreach (var file in beforeFiles) File.WriteAllBytes(file.Key, file.Value);
    }
    Console.WriteLine("PASS observed service state, configuration and relevant file changes abort actions");
    foreach (var action in Enum.GetValues<FlowsealAction>().Where(a => a is not (FlowsealAction.InstallService or FlowsealAction.StartService or FlowsealAction.StopService or FlowsealAction.RemoveService or FlowsealAction.ManualStop or FlowsealAction.RecoverInterrupted)))
    {
        var refused = await adapter.ExecuteAsync(request with { Action = action });
        if (refused.Outcome != FlowsealActionOutcome.Unavailable || runner.Commands != 0)
            throw new Exception("Unimplemented action must report its actual capability, not permit mutations: " + action);
    }
    var absent = new FlowsealAdapter(runner, () => Task.FromResult(new ServiceSnapshot(ServiceState.Missing, null)));
    var firstInstall = await absent.ExecuteAsync(request);
    if (firstInstall.Outcome != FlowsealActionOutcome.Unavailable || firstInstall.State.Ownership != ServiceOwnership.Missing)
        throw new Exception("First installation must remain unavailable until service implementation and parity tasks");
    Console.WriteLine("PASS all remaining mutation capabilities stay unavailable");
    var directoryImage = Path.Combine(root, "directory", "winws.exe");
    Directory.CreateDirectory(directoryImage);
    Environment.SetEnvironmentVariable("ZAPRET_TASK02_UNTRUSTED_ROOT", root);
    foreach (var observation in new[]
    {
        installedByBat with { Configuration = installedByBat.Configuration! with { ServiceType = 1 } },
        installedByBat with { Configuration = new(CommandLine.Quote(directoryImage)) },
        installedByBat with { Configuration = new("\"%ZAPRET_TASK02_UNTRUSTED_ROOT%\\bin\\winws.exe\" --wf-tcp=443") },
        installedByBat with { State = ServiceState.Running },
        installedByBat with { State = ServiceState.Running, Process = new(19, other, 100) }
    })
    {
        var guarded = new FlowsealAdapter(runner, () => Task.FromResult(observation));
        if ((await guarded.ReadStateAsync(root)).Ownership != ServiceOwnership.Unknown)
            throw new Exception("Unsupported type, directory image, untrusted environment or inconsistent running process cannot prove ownership");
    }
    Environment.SetEnvironmentVariable("ZAPRET_TASK02_UNTRUSTED_ROOT", null);
    var normalized = installedByBat with { Configuration = new(CommandLine.Quote(Path.Combine(root, "bin", "..", "bin", "WINWS.EXE"))) };
    var normalizedAdapter = new FlowsealAdapter(runner, () => Task.FromResult(normalized));
    if ((await normalizedAdapter.ReadStateAsync(root)).Ownership != ServiceOwnership.Owned)
        throw new Exception("Case and parent components of a quoted absolute executable must normalize correctly");
    var running = installedByBat with { State = ServiceState.Running, Process = new(19, Path.Combine(root, "bin", "winws.exe"), 100) };
    var runningAdapter = new FlowsealAdapter(runner, () => Task.FromResult(running));
    if ((await runningAdapter.ReadStateAsync(root)).Ownership != ServiceOwnership.Owned)
        throw new Exception("Running ownership requires both configured and observed executable plus process identity");
    Console.WriteLine("PASS ownership conservatively proves files, service type, environment and current process");
    var secondRoot = Path.Combine(root, "second distribution");
    CopyReference(secondRoot);
    var secondRequest = new FlowsealActionRequest(secondRoot, FlowsealAction.InstallService, new("general", Path.Combine(secondRoot, "general.bat")));
    var readyName = "Zapret.Guard.Ready." + Guid.NewGuid();
    var releaseName = "Zapret.Guard.Release." + Guid.NewGuid();
    using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName))
    using (var release = new EventWaitHandle(false, EventResetMode.ManualReset, releaseName))
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "--hold-guard", root, readyName, releaseName }) info.ArgumentList.Add(argument);
        using var child = Process.Start(info) ?? throw new Exception("Cannot start isolated guard-holder test process");
        try
        {
            if (!ready.WaitOne(TimeSpan.FromSeconds(15))) throw new Exception("Child did not acquire the guard");
            var competing = new FlowsealAdapter(runner, () => Task.FromResult(installedByBat));
            var conflict = await competing.ExecuteAsync(secondRequest);
            if (conflict.Outcome != FlowsealActionOutcome.Conflict || runner.Commands != 0)
                throw new Exception("Another GUI process with a different selected distribution must share the service guard");
        }
        finally { release.Set(); }
        await child.WaitForExitAsync();
        if (child.ExitCode != 0) throw new Exception(await child.StandardError.ReadToEndAsync());
    }
    var afterRelease = await adapter.ExecuteAsync(request);
    if (afterRelease.Outcome != FlowsealActionOutcome.RecoveryRequired)
        throw new Exception("The owner must release the named mutex on the acquiring worker thread");
    Console.WriteLine("PASS cross-process guard covers different distributions and releases after refusal");
    var trailing = await adapter.PreflightAsync(request with { Root = root + Path.DirectorySeparatorChar });
    if (trailing.Outcome != FlowsealActionOutcome.RecoveryRequired)
        throw new Exception("An adjacent application directory with a trailing separator must retain the verified selected strategy");
    Console.WriteLine("PASS adjacent root with trailing separator remains a valid distribution");
    var beforeDenied = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
    await adapter.ExecuteAsync(request);
    var afterDenied = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToArray();
    if (beforeDenied.Count != afterDenied.Length || afterDenied.Any(p => !beforeDenied.TryGetValue(p, out var bytes) || !bytes.SequenceEqual(File.ReadAllBytes(p)))
        || runner.Commands != 0)
        throw new Exception("Denied public action must leave every fixture file unchanged and execute no commands");
    Console.WriteLine("PASS refused operation leaves the complete filesystem fixture unchanged");
    if (args.Contains("--native-read"))
    {
        var observed = await new FlowsealAdapter(runner).ReadStateAsync(root);
        Console.WriteLine("PASS native read-only SCM observation: " + observed.State + ", " + observed.Ownership + "; " + observed.OwnershipReason + "; " + observed.ObservationError);
    }
}
finally
{
    if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
        || !Path.GetFileName(root).StartsWith("Zapret guard ")) throw new IOException("Unexpected test root");
    if (Directory.Exists(root)) Directory.Delete(root, true);
}

static void CopyReference(string destination)
{
    var reference = Path.Combine(AppContext.BaseDirectory, "fixtures", "flowseal-1.10.3");
    foreach (var file in Directory.EnumerateFiles(reference, "*", SearchOption.AllDirectories))
    {
        var copy = Path.Combine(destination, Path.GetRelativePath(reference, file));
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.Copy(file, copy);
    }
    string[] required = ["bin/winws.exe", "bin/WinDivert.dll", "bin/WinDivert64.sys", "bin/cygwin1.dll",
        "bin/ACTIVE_DISCORD_UDP.bin", "bin/ACTIVE_GAME_UDP.bin", "bin/quic_initial_4pda_to.bin", "bin/quic_initial_5ka_ru.bin",
        "bin/quic_initial_rutube_ru.bin", "bin/quic_initial_steamcommunity_com.bin", "bin/quic_initial_tencent_com.bin",
        "bin/quic_initial_www_google_com.bin", "bin/stun.bin", "bin/stun2.bin", "bin/tls_clienthello_4pda_to.bin",
        "bin/tls_clienthello_5ka_ru.bin", "bin/tls_clienthello_max_ru.bin", "bin/tls_clienthello_sochi_park.bin",
        "bin/tls_clienthello_www_google_com.bin", "bin/tls_clienthello_www_sferum_ru.bin", "lists/list-general.txt",
        "lists/list-exclude.txt", "lists/ipset-all.txt", "lists/list-google.txt", "lists/ipset-exclude.txt",
        "lists/list-general-user.txt", "lists/list-exclude-user.txt", "lists/ipset-exclude-user.txt"];
    foreach (var relative in required)
    {
        var file = Path.Combine(destination, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "inert test fixture\n");
    }
}

sealed class RejectCommands : ICommandRunner
{
    public int Commands { get; private set; }
    public Task<CommandResult> RunAsync(string executable, params string[] arguments)
    {
        Commands++;
        throw new Exception("The safety suite must never execute OS commands: " + executable);
    }
}
