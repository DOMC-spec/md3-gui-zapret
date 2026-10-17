using Zapret.Core;
using Zapret.Gui.Services;
using System.Diagnostics;
using System.Text.Json;

static class InterruptedOperationScenarios
{
    public static Task RunNativeCrash(string root) => CrashAndRecover(root, "native-manual-active");
    public static async Task Run(string root)
    {
        var original = new ServiceSnapshot(ServiceState.Stopped, "original",
            new ServiceConfiguration(CommandLine.Quote(Path.Combine(root, "bin", "winws.exe")) + " --wf-tcp=443"),
            RecoveryData: new("Original", "AQID"));
        var state = new MemoryService(original);
        var commands = new PreparedCommands();
        var adapter = new FlowsealAdapter(commands, () => Task.FromResult(state.State), state, operationJournal: new UnwritableJournal());
        var result = await adapter.ExecuteAsync(new(root, FlowsealAction.InstallService, new("general", Path.Combine(root, "general.bat"))));
        if (result.Outcome != FlowsealActionOutcome.ApplyFailed || state.State != original || commands.Prepared)
            throw new Exception("A durable journal must be saved before preparation or service mutation: " + result);
        Console.WriteLine("PASS unavailable durable journal refuses before preparation and all service changes");
        foreach (var stage in new[] { "preparing", "intent-stop", "stop", "delete", "create", "start", "manual-stop", "manual-launch", "manual-active" })
            await CrashAndRecover(root, stage);
        foreach (var stateBefore in new[] { ServiceState.Stopped, ServiceState.Missing })
        {
            await CrashAndRecover(root, "create", stateBefore);
            await CrashAndRecover(root, "manual-active", stateBefore);
        }
        foreach (var conflict in new[] { "configuration", "process", "files", "corrupt" })
            await CrashAndRecover(root, "stop", conflict: conflict);
        await CrashAndRecover(root, "observed-unknown", conflict: "unknown");
    }

    public static async Task Child(string root, string crashAt)
    {
        var state = new MemoryService(JsonSerializer.Deserialize<ServiceSnapshot>(File.ReadAllText(Path.Combine(root, "service-state.json")))!);
        var journal = new FixtureJournal(Path.Combine(root, "operation.bin"));
        journal.AfterWrite = record =>
        {
            if ((crashAt == "preparing" && record.Phase == OperationPhase.Preparing)
                || (crashAt == "intent-stop" && record.Phase == OperationPhase.Stop)) Halt(root);
            if (crashAt == "observed-unknown" && record.Phase == OperationPhase.Observed && record.Expected.State == ServiceState.Running)
                Halt(root);
        };
        state.AfterMutation = stage =>
        {
            if (stage == "start" && crashAt == "observed-unknown")
            {
                var other = Path.Combine(root, "other.exe");
                File.WriteAllText(other, "inert foreign executable");
                state.Replace(state.State with { Process = state.State.Process! with { Executable = other } });
            }
            SaveState(root, state.State);
            if (stage == crashAt || (crashAt == "manual-stop" && stage == "stop")) Halt(root);
        };
        var processes = new MemoryManualProcesses(root) { OnLaunch = () => { if (crashAt == "manual-launch") Halt(root); } };
        var adapter = new FlowsealAdapter(new PreparedCommands(), () => Task.FromResult(state.State), state,
            crashAt.StartsWith("native") ? new FixtureNativeProcesses() : processes, journal);
        var result = await adapter.ExecuteAsync(new(root, crashAt.Contains("manual") ? FlowsealAction.ManualStart : FlowsealAction.InstallService,
            new("general", Path.Combine(root, "general.bat"))));
        if (crashAt.EndsWith("manual-active") && result.Outcome == FlowsealActionOutcome.Success) Halt(root);
        throw new Exception("Crash stage was not reached: " + crashAt + "; " + result);
    }

    private static void Halt(string root)
    {
        File.WriteAllText(Path.Combine(root, "crash-ready"), "ready");
        Thread.Sleep(Timeout.Infinite);
    }

    private static async Task CrashAndRecover(string root, string stage, ServiceState stateBefore = ServiceState.Running, string? conflict = null)
    {
        File.Delete(Path.Combine(root, "operation.bin"));
        File.Delete(Path.Combine(root, "crash-ready"));
        var initial = stateBefore == ServiceState.Missing ? new ServiceSnapshot(ServiceState.Missing, null) : new ServiceSnapshot(stateBefore, "original",
            new ServiceConfiguration(CommandLine.Quote(Path.Combine(root, "bin", "winws.exe")) + " --wf-tcp=443"),
            stateBefore == ServiceState.Running ? new(1, Path.Combine(root, "bin", "winws.exe"), 9) : null, RecoveryData: new("Original", "AQID"));
        SaveState(root, initial);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "--interrupted-child", root, stage }) start.ArgumentList.Add(argument);
        using var child = Process.Start(start)!;
        Process? trial = null;
        var deadline = Environment.TickCount64 + 15000;
        try
        {
            while (!File.Exists(Path.Combine(root, "crash-ready")))
            {
                if (child.HasExited) throw new Exception("Crash child failed: " + await child.StandardError.ReadToEndAsync());
                if (Environment.TickCount64 >= deadline) throw new TimeoutException("Crash child did not reach " + stage);
                await Task.Delay(50);
            }
            if (stage.StartsWith("native"))
            {
                var observed = new FixtureJournal(Path.Combine(root, "operation.bin")).Read()!.Trial!;
                trial = Process.GetProcessById((int)observed.Id);
                _ = trial.Handle; // Retain this process generation, not a later PID reuse.
                if (trial.MainModule?.FileName != Path.Combine(root, "bin", "winws.exe")
                    || trial.StartTime.ToUniversalTime().Ticks != observed.StartedAtUtcTicks)
                    throw new Exception("Native crash fixture must observe its own inert engine generation");
            }
        }
        finally { if (!child.HasExited) child.Kill(!stage.StartsWith("native")); await child.WaitForExitAsync(); }
        if (trial != null)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await trial.WaitForExitAsync(timeout.Token);
            }
            finally { if (!trial.HasExited) trial.Kill(); trial.Dispose(); }
        }
        var state = new MemoryService(JsonSerializer.Deserialize<ServiceSnapshot>(File.ReadAllText(Path.Combine(root, "service-state.json")))!);
        var journal = new FixtureJournal(Path.Combine(root, "operation.bin"));
        if (conflict == "configuration") state.Replace(state.State with { Configuration = state.State.Configuration! with { StartType = 3 } });
        if (conflict == "process") state.Replace(initial with { Process = initial.Process! with { Id = 99, StartedAtUtcTicks = 999 } });
        var list = Path.Combine(root, "lists", "list-general.txt");
        var originalList = File.ReadAllBytes(list);
        if (conflict == "files") File.AppendAllText(list, "external edit");
        if (conflict == "corrupt") File.WriteAllText(Path.Combine(root, "operation.bin"), "broken journal");
        IFlowsealAdapter reopened = new FlowsealAdapter(new RejectCommands(), () => Task.FromResult(state.State), state, operationJournal: journal);
        var pending = await reopened.ReadInterruptedOperationAsync(root);
        if (conflict != null)
        {
            var before = state.State;
            var denied = await reopened.ExecuteAsync(new(root, FlowsealAction.RecoverInterrupted));
            var blocked = await reopened.ExecuteAsync(new(root, FlowsealAction.StartService));
            if (!pending.Pending || pending.CanRecover || denied.Outcome is not (FlowsealActionOutcome.Conflict or FlowsealActionOutcome.RecoveryRequired)
                || blocked.Outcome != FlowsealActionOutcome.RecoveryRequired || state.State != before)
                throw new Exception("External changes/corruption must preserve current service and block new mutations: " + conflict + " " + denied);
            if (conflict == "files")
            {
                if (!File.ReadAllText(list).EndsWith("external edit")) throw new Exception("External edit lost");
                File.WriteAllBytes(list, originalList);
            }
            Console.WriteLine("PASS interrupted operation preserves " + conflict + " and refuses new mutation");
            return;
        }
        if (!pending.Pending || !pending.CanRecover)
            throw new Exception("Reopened GUI must detect durable interrupted stop without silently restoring: " + pending);
        var wrongRoot = await reopened.ReadInterruptedOperationAsync(Path.Combine(root, "other"));
        if (!wrongRoot.Pending || wrongRoot.CanRecover || wrongRoot.Root != root) throw new Exception("Recovery must identify the original distribution");
        var recovered = await reopened.ExecuteAsync(new(root, FlowsealAction.RecoverInterrupted));
        if (recovered.Outcome != FlowsealActionOutcome.Success || state.State.State != stateBefore
            || state.State.Configuration != initial.Configuration || (await reopened.ReadInterruptedOperationAsync(root)).Pending)
            throw new Exception("Explicit interrupted recovery must restore original service and close its record: " + recovered);
        var stable = state.State;
        await reopened.ExecuteAsync(new(root, FlowsealAction.RecoverInterrupted));
        if (state.State != stable) throw new Exception("Repeated recovery must not restart the already restored service");
        Console.WriteLine("PASS killed GUI after " + stage + " / " + stateBefore + ": durable discovery, explicit restoration and idempotent repeat");
    }

    private static void SaveState(string root, ServiceSnapshot snapshot)
    {
        using var stream = new FileStream(Path.Combine(root, "service-state.json"), FileMode.Create, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(stream, snapshot);
        stream.Flush(true);
    }

    private sealed class FixtureJournal(string path) : IOperationJournal
    {
        public Action<OperationRecord>? AfterWrite { get; set; }
        public OperationRecord? Read() => File.Exists(path) ? OperationJournalCodec.Decode(File.ReadAllBytes(path)) : null;
        public void Write(OperationRecord record)
        {
            var temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            { stream.Write(OperationJournalCodec.Encode(record)); stream.Flush(true); }
            File.Move(temporary, path, true);
            AfterWrite?.Invoke(record);
        }
    }

    private sealed class FixtureNativeProcesses : IManualProcessBackend
    {
        public void EnsureNoOtherWinws(ServiceProcess? serviceProcess) { } // Workstation engines are unrelated to the inert fixture.
        public IManualProcessSession Launch(string root, string strategyPath) => new WindowsManualProcessBackend().Launch(root, strategyPath);
    }

    private sealed class UnwritableJournal : IOperationJournal
    {
        public OperationRecord? Read() => null;
        public void Write(OperationRecord record) => throw new IOException("Injected durable journal write failure");
    }
}
