using Zapret.Core;
using Zapret.Gui.Services;

static class ManualSessionScenarios
{
    public static async Task Run(string root)
    {
        var original = new ServiceSnapshot(ServiceState.Stopped, "BAT strategy",
            new ServiceConfiguration(CommandLine.Quote(Path.Combine(root, "bin", "winws.exe")) + " --wf-tcp=443"),
            RecoveryData: new("Original", "AQID"));
        var service = new MemoryService(original);
        var processes = new MemoryManualProcesses(root);
        IFlowsealAdapter adapter = new FlowsealAdapter(new PreparedCommands(), () => Task.FromResult(service.State), service, processes);
        var request = new FlowsealActionRequest(root, FlowsealAction.ManualStart, new("general", Path.Combine(root, "general.bat")));
        var result = await adapter.ExecuteAsync(request);
        try
        {
            if (result.Outcome != FlowsealActionOutcome.Success || service.State != original
                || !(await adapter.ReadManualSessionAsync()).IsActive || !processes.Running)
                throw new Exception("Manual start must observe detached winws without changing registration: " + result);
        }
        finally { await adapter.ExecuteAsync(new(root, FlowsealAction.ManualStop)); }
        if (processes.Running || service.State != original || (await adapter.ReadManualSessionAsync()).IsActive)
            throw new Exception("Manual stop must terminate only owned process and retain stopped registration");
        Console.WriteLine("PASS detached manual session starts/stops without registering or starting a stopped service");
        foreach (var initialState in new[] { ServiceState.Running, ServiceState.Stopped, ServiceState.Missing })
        {
            var initial = initialState == ServiceState.Missing ? new ServiceSnapshot(ServiceState.Missing, null)
                : original with { State = initialState, Process = initialState == ServiceState.Running
                    ? new(1, Path.Combine(root, "bin", "winws.exe"), 9) : null };
            var controlled = new MemoryService(initial);
            var engine = new MemoryManualProcesses(root);
            var session = new FlowsealAdapter(new PreparedCommands(), () => Task.FromResult(controlled.State), controlled, engine);
            var started = await session.ExecuteAsync(request);
            try
            {
                if (started.Outcome != FlowsealActionOutcome.Success || controlled.State.State == ServiceState.Running)
                    throw new Exception("Service must be stopped before launching standalone winws");
                var competing = await new FlowsealAdapter(new RejectCommands(), () => Task.FromResult(controlled.State), controlled)
                    .ExecuteAsync(request with { Action = FlowsealAction.InstallService });
                if (competing.Outcome != FlowsealActionOutcome.Conflict) throw new Exception("Whole session must retain shared mutation guard");
                var refusedStop = await session.ExecuteAsync(new(Path.Combine(root, "other"), FlowsealAction.ManualStop));
                if (refusedStop.Outcome != FlowsealActionOutcome.InvalidRequest || !engine.Running)
                    throw new Exception("Another root must not stop the session");
            }
            finally { await session.ExecuteAsync(new(root, FlowsealAction.ManualStop)); }
            if (controlled.State.State != initialState || controlled.State.Configuration != initial.Configuration || engine.Running)
                throw new Exception("Stop must return Running/Stopped/Missing without changing registration");
        }
        Console.WriteLine("PASS manual session restores original service state and retains mutex until cleanup");

        foreach (var stage in new[] { "stop", "launch", "early-exit", "process-identity", "service-edit", "file-edit", "cleanup" })
        {
            var initial = original with { State = ServiceState.Running, Process = new(1, Path.Combine(root, "bin", "winws.exe"), 9) };
            var controlled = new MemoryService(initial) { FailureStage = stage == "stop" ? "stop" : null, FailAfterMutation = true };
            var engine = new MemoryManualProcesses(root) { LaunchFailure = stage == "launch", StopFailure = stage == "cleanup" };
            var session = new FlowsealAdapter(new PreparedCommands(), () => Task.FromResult(controlled.State), controlled, engine);
            var list = Path.Combine(root, "lists", "list-general.txt");
            var bytes = File.ReadAllBytes(list);
            try
            {
                if (stage == "early-exit") engine.Observation = () => new(null, true, true);
                var started = await session.ExecuteAsync(request);
                if (stage is "stop" or "launch" or "early-exit")
                {
                    if (started.Outcome != FlowsealActionOutcome.ApplyFailed || controlled.State.State != ServiceState.Running || engine.Running)
                        throw new Exception("Failed trial must restore previously running service: " + stage + " " + started);
                    if (stage == "early-exit" && (!started.Explanation.Contains("раньше времени") || string.IsNullOrEmpty(started.ApplyError)))
                        throw new Exception("Early exit must retain a plain-language cause and separate diagnostics");
                }
                else
                {
                    if (started.Outcome != FlowsealActionOutcome.Success) throw new Exception("Setup failed: " + started);
                    if (stage == "process-identity") engine.Observation = () => new(new(78, Path.Combine(root, "bin", "winws.exe"), 999), true, false);
                    if (stage == "service-edit") controlled.Replace(controlled.State with { Configuration = controlled.State.Configuration! with { StartType = 3 } });
                    if (stage == "file-edit") File.AppendAllText(list, "external change");
                    if (stage != "cleanup") await Task.Delay(500);
                    var ended = await session.ExecuteAsync(new(root, FlowsealAction.ManualStop));
                    if (ended.Outcome is not (FlowsealActionOutcome.Conflict or FlowsealActionOutcome.ApplyFailed)
                        || engine.Running || controlled.State.State != ServiceState.Stopped)
                        throw new Exception("Unknown identity/external edit/failed cleanup must stop own trial without restoring over conflict: " + stage + " " + ended);
                    if (stage == "file-edit" && !File.ReadAllText(list).EndsWith("external change")) throw new Exception("External file edit lost");
                }
            }
            finally
            {
                await session.ExecuteAsync(new(root, FlowsealAction.ManualStop));
                File.WriteAllBytes(list, bytes);
            }
        }
        Console.WriteLine("PASS partial stop, launch refusal, early exit, identity changes, external edits and cleanup failure preserve safety");
        var flag = Path.Combine(root, "utils", "game_filter.enabled");
        var savedFlag = File.Exists(flag) ? File.ReadAllBytes(flag) : null;
        try
        {
            foreach (var missingFlag in new[] { false, true })
            {
                if (missingFlag) File.Delete(flag); else File.WriteAllText(flag, "mode=disabled\r\n");
                var blocked = false;
                var blockedDuringStop = false;
                var state = new MemoryService(original);
                var engine = new MemoryManualProcesses(root)
                {
                    OnLaunch = () =>
                    {
                        try { File.WriteAllText(flag, "mode=\" & echo unsafe & rem \""); }
                        catch (IOException) { blocked = true; }
                    },
                    OnStop = () =>
                    {
                        try { File.WriteAllText(flag, "mode=\" & echo unsafe & rem \""); }
                        catch (IOException) { blockedDuringStop = true; }
                    }
                };
                var adapterWithLock = new FlowsealAdapter(new PreparedCommands(), () => Task.FromResult(state.State), state, engine);
                var started = await adapterWithLock.ExecuteAsync(request);
                try
                {
                    if (!blocked || started.Outcome != FlowsealActionOutcome.Success)
                        throw new Exception("Existing or initially missing Game Filter must be locked against CMD-input replacement before launch");
                }
                finally { await adapterWithLock.ExecuteAsync(new(root, FlowsealAction.ManualStop)); }
                if (!blockedDuringStop) throw new Exception("Game Filter must remain locked until the original CMD/job has terminated");
            }
        }
        finally { if (savedFlag == null) File.Delete(flag); else File.WriteAllBytes(flag, savedFlag); }
        Console.WriteLine("PASS existing/absent Game Filter cannot become substituted CMD input between preparation and BAT launch");
        await RunNative(root);
    }

    public static Task RunNativeObservation(string sourceRoot) => RunNative(sourceRoot, true);

    private static async Task RunNative(string sourceRoot, bool observationOnly = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "Zapret manual " + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                var copy = Path.Combine(root, Path.GetRelativePath(sourceRoot, file));
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(file, copy);
            }
            // The test apphost becomes inert sc/netsh/winws. No real network tool, driver or engine executes.
            foreach (var destination in new[] { root, Path.Combine(root, "bin") })
            {
                foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory))
                    File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
            }
            File.Copy(Environment.ProcessPath!, Path.Combine(root, "sc.exe"));
            File.Copy(Environment.ProcessPath!, Path.Combine(root, "netsh.exe"));
            File.Copy(Environment.ProcessPath!, Path.Combine(root, "bin", "winws.exe"), true);
            CheckShortLivedHelpers(root);
            if (observationOnly) return;
            var inherited = Environment.GetEnvironmentVariable("NO_UPDATE_CHECK");
            foreach (var name in new[] { "general (ALT5)", "general (FAKE TLS AUTO)" })
            {
                var state = new MemoryService(new(ServiceState.Missing, null));
                var adapter = new FlowsealAdapter(new PreparedCommands(), () => Task.FromResult(state.State), state, new InertWindowsProcesses());
                var result = await adapter.ExecuteAsync(new(root, FlowsealAction.ManualStart, new(name, Path.Combine(root, name + ".bat"))));
                try
                {
                    if (result.Outcome != FlowsealActionOutcome.Success) throw new Exception("Original BAT inert job failed: " + result);
                    var session = await adapter.ReadManualSessionAsync();
                    if (session.Process?.Executable != Path.Combine(root, "bin", "winws.exe") || session.Process.StartedAtUtcTicks == null)
                        throw new Exception("Native detached child must have confirmed path, PID and creation time");
                    using var captured = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "bin", "manual-capture.json")));
                    var arguments = captured.RootElement.GetProperty("Arguments").EnumerateArray().Select(a => a.GetString()).ToArray();
                    if (captured.RootElement.GetProperty("NoUpdate").GetString() != "1"
                        || captured.RootElement.GetProperty("Directory").GetString() != Path.Combine(root, "bin")
                        || !arguments.Contains("--wf-tcp=80,443,2053,2083,2087,2096,8443,12")
                        || (name.Contains("FAKE TLS") && !arguments.Any(a => a?.Contains("!", StringComparison.Ordinal) == true)))
                        throw new Exception("Original BAT must retain arguments/escaping, working directory and child-only NO_UPDATE_CHECK");
                    await Task.Delay(name.Contains("ALT5") ? 36000 : 1500); // Outlives the short-command runner's 35-second timeout.
                    if (!(await adapter.ReadManualSessionAsync()).IsActive) throw new Exception("Launcher exit must not end detached trial");
                }
                finally
                {
                    var stopped = await adapter.ExecuteAsync(new(root, FlowsealAction.ManualStop));
                    if (stopped.Outcome != FlowsealActionOutcome.Success || state.State.State != ServiceState.Missing)
                        throw new Exception("Native job cleanup must confirm exit without registering a service: " + stopped);
                }
            }
            if (Environment.GetEnvironmentVariable("NO_UPDATE_CHECK") != inherited) throw new Exception("Update flag leaked to GUI environment");
            await InterruptedOperationScenarios.RunNativeCrash(root);
            Console.WriteLine("PASS original BAT + native detached Windows job with inert sc/netsh/winws: spaces, parentheses, escaping, child environment and cleanup");
        }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(root).StartsWith("Zapret manual ")) throw new IOException("Unexpected native fixture root");
            // Windows/antivirus can briefly retain a mapped runtime DLL after process teardown.
            var deadline = Environment.TickCount64 + 10000;
            while (Directory.Exists(root))
            {
                try { Directory.Delete(root, true); }
                catch (Exception error) when ((error is IOException or UnauthorizedAccessException) && Environment.TickCount64 < deadline)
                { await Task.Delay(200); }
            }
        }
    }

    private sealed class InertWindowsProcesses : IManualProcessBackend
    {
        public void EnsureNoOtherWinws(ServiceProcess? serviceProcess) { } // Existing workstation processes are outside the fixture job.
        public IManualProcessSession Launch(string root, string path) => new WindowsManualProcessBackend().Launch(root, path);
    }

    private static void CheckShortLivedHelpers(string root)
    {
        var script = Path.Combine(root, "short-helpers.bat");
        File.WriteAllText(script, "@echo off\r\nfor /l %%i in (1,1,20) do start \"\" /b \"%~dp0sc.exe\" >nul\r\nstart \"\" /b \"%~dp0bin\\winws.exe\" >nul\r\n");
        for (var attempt = 0; attempt < 12; attempt++)
        {
            using var session = new WindowsManualProcessBackend().Launch(root, script);
            var deadline = Environment.TickCount64 + 10000;
            ManualProcessObservation observed;
            do
            {
                observed = session.Observe();
                if (observed.Exited || Environment.TickCount64 >= deadline)
                    throw new Exception("Short helper exits must not prevent observing the live fixture winws");
            } while (observed.Process == null || !observed.LauncherExited);
            if (observed.Process.Executable != Path.Combine(root, "bin", "winws.exe") || observed.Process.StartedAtUtcTicks == null)
                throw new Exception("Live fixture winws identity must remain proven while helpers exit");
            // Let the short processes fully leave between launches, matching the original BAT lifetime.
            var stableUntil = Environment.TickCount64 + 2000;
            while (Environment.TickCount64 < stableUntil)
                if (session.Observe().Process != observed.Process) throw new Exception("Live fixture identity changed");
            session.Stop();
            if (!session.Observe().Exited) throw new Exception("Fixture job exit must be confirmed after stop");
        }
        Console.WriteLine("PASS native job observes identified winws across short helper exits and confirms cleanup (12 runs)");
    }
}

sealed class MemoryManualProcesses(string root) : IManualProcessBackend, IManualProcessSession
{
    public bool Running { get; private set; }
    public bool LaunchFailure { get; init; }
    public bool StopFailure { get; init; }
    public Func<ManualProcessObservation>? Observation { get; set; }
    public Action? OnLaunch { get; init; }
    public Action? OnStop { get; init; }
    public void EnsureNoOtherWinws(ServiceProcess? serviceProcess) { }
    public IManualProcessSession Launch(string selectedRoot, string strategyPath)
    {
        if (selectedRoot != root || strategyPath != Path.Combine(root, "general.bat"))
            throw new Exception("Only selected original BAT may be launched in its distribution directory");
        if (LaunchFailure) throw new IOException("Injected launch refusal");
        OnLaunch?.Invoke();
        Running = true;
        return this;
    }
    public ManualProcessObservation Observe() => Observation?.Invoke() ?? new(Running ? new(77, Path.Combine(root, "bin", "winws.exe"), 456) : null, true, !Running);
    public void Stop() { OnStop?.Invoke(); if (StopFailure) throw new IOException("Injected cleanup failure"); Running = false; }
    public void Dispose() => Running = false;
}
