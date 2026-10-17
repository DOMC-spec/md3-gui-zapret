using Zapret.Core;
using Zapret.Gui.Services;

static class RecoveryScenarios
{
    public static async Task RunReview(string root)
    {
        var original = new ServiceSnapshot(ServiceState.Stopped, "original.bat",
            new ServiceConfiguration(CommandLine.Quote(Path.Combine(root, "bin", "winws.exe")) + " --wf-tcp=80"),
            RecoveryData: new("Original description", "AQID"));
        var request = new FlowsealActionRequest(root, FlowsealAction.InstallService, new("general", Path.Combine(root, "general.bat")));
        var restarted = new MemoryService(original);
        var started = false;
        var runningObserved = false;
        var restartInjected = false;
        restarted.AfterMutation = stage => { if (stage == "start") started = true; };
        var result = await new FlowsealAdapter(new RejectCommands(), () =>
        {
            if (started && runningObserved && !restartInjected)
            {
                restartInjected = true;
                restarted.Replace(restarted.State with { Process = restarted.State.Process! with { Id = 88, StartedAtUtcTicks = 456 } });
            }
            if (started) runningObserved = true;
            return Task.FromResult(restarted.State);
        }, restarted).ExecuteAsync(request);
        if (result.Outcome != FlowsealActionOutcome.ApplyFailed || result.Recovery != FlowsealRecoveryOutcome.Conflict
            || result.State.State != ServiceState.Running || result.State.Preset != "general.bat" || result.State.Process?.Id != 88
            || restarted.State != (result.State with { Ownership = restarted.State.Ownership, OwnershipReason = restarted.State.OwnershipReason }))
            throw new Exception("External process restart with unchanged configuration and operation marker must remain untouched: " + result.Recovery);
        Console.WriteLine("PASS external process restart during stability verification is preserved");

        var pending = new MemoryService(original);
        long? startAt = null;
        pending.AfterMutation = stage =>
        {
            if (stage != "start") return;
            startAt = Environment.TickCount64;
            pending.Replace(pending.State with { State = ServiceState.Starting });
        };
        var timedOut = await new FlowsealAdapter(new RejectCommands(), () =>
        {
            if (startAt != null && Environment.TickCount64 - startAt.Value >= 11000 && pending.State.State == ServiceState.Starting)
                pending.Replace(pending.State with { State = ServiceState.Running });
            return Task.FromResult(pending.State);
        }, pending).ExecuteAsync(request);
        if (timedOut.Outcome != FlowsealActionOutcome.ApplyFailed || timedOut.Recovery != FlowsealRecoveryOutcome.Restored
            || timedOut.State.State != ServiceState.Stopped || timedOut.State.Configuration != original.Configuration
            || timedOut.State.RecoveryData != original.RecoveryData)
            throw new Exception("Own pending start settling after the apply timeout must still restore the original stopped service: " + timedOut.Recovery);
        Console.WriteLine("PASS timed-out own start settles under recovery guard and restores original configuration/state");
    }

    public static async Task Run(string root)
    {
        var original = new ServiceSnapshot(ServiceState.Stopped, "original.bat",
            new ServiceConfiguration(CommandLine.Quote(Path.Combine(root, "bin", "winws.exe")) + " --wf-tcp=80"),
            RecoveryData: new("Original description", "AQID"));
        var backend = new MemoryService(original);
        var adapter = new FlowsealAdapter(new RejectCommands(), () => Task.FromResult(backend.State), backend);
        var request = new FlowsealActionRequest(root, FlowsealAction.InstallService, new("general", Path.Combine(root, "general.bat")));
        var preflight = await adapter.PreflightAsync(request);
        if (preflight.Outcome != FlowsealActionOutcome.Ready || backend.State != original)
            throw new Exception("Complete supported snapshot must allow a read-only service replacement preflight");
        Console.WriteLine("PASS complete recovery snapshot permits read-only preflight");
        var applied = await adapter.ExecuteAsync(request);
        if (applied.Outcome != FlowsealActionOutcome.Success || applied.State.State != ServiceState.Running
            || applied.State.Configuration?.StartType != 2 || applied.State.Preset != "general.bat"
            || !applied.State.Configuration.BinaryPath.Contains("--wf-tcp=80,443"))
            throw new Exception("Verified strategy must replace service and report observed stable running automatic configuration: " + applied);
        Console.WriteLine("PASS verified replacement reports stable running automatic service and exact strategy arguments");
        foreach (var initialState in new[] { ServiceState.Running, ServiceState.Stopped, ServiceState.Missing })
        foreach (var stage in new[] { "stop", "delete", "create", "start" })
        foreach (var after in new[] { false, true })
        {
            if ((stage == "stop" && initialState != ServiceState.Running) || (stage == "delete" && initialState == ServiceState.Missing)) continue;
            var initial = initialState == ServiceState.Missing ? new ServiceSnapshot(ServiceState.Missing, null)
                : original with { State = initialState, Process = initialState == ServiceState.Running ? new(1, Path.Combine(root, "bin", "winws.exe"), 9) : null };
            var fault = new MemoryService(initial) { FailureStage = stage, FailAfterMutation = after };
            var guarded = new FlowsealAdapter(new RejectCommands(), () => Task.FromResult(fault.State), fault);
            var result = await guarded.ExecuteAsync(request);
            if (result.Outcome != FlowsealActionOutcome.ApplyFailed || result.Recovery != FlowsealRecoveryOutcome.Restored
                || result.State.State != initialState || result.State.Configuration != initial.Configuration
                || result.State.Preset != initial.Preset || result.State.RecoveryData != initial.RecoveryData
                || string.IsNullOrEmpty(result.ApplyError) || string.IsNullOrEmpty(result.NextStep))
                throw new Exception($"{initialState}/{stage}/{after}: failed replacement must restore configuration, metadata and original state: {result}");
        }
        Console.WriteLine("PASS stop/delete/create/start failures before and after side effects restore running/stopped/missing state");
        foreach (var unsupported in new[]
        {
            original with { RecoveryData = null }, original with { ObservationError = "incomplete snapshot" },
            original with { Configuration = original.Configuration! with { Account = "custom-user" } },
            original with { Configuration = original.Configuration! with { TagId = 1 } },
            original with { Configuration = original.Configuration! with { LoadOrderGroup = "custom" } },
            original with { State = ServiceState.Running, Process = new(1, Path.Combine(root, "bin", "winws.exe"), 9),
                Configuration = original.Configuration! with { StartType = 4 } }
        })
        {
            var refusedBackend = new MemoryService(unsupported);
            var refused = await new FlowsealAdapter(new RejectCommands(), () => Task.FromResult(refusedBackend.State), refusedBackend).ExecuteAsync(request);
            if (refused.Outcome != FlowsealActionOutcome.RecoveryRequired || refusedBackend.State != unsupported)
                throw new Exception("Unsupported/incomplete snapshot must leave service untouched: " + refused);
        }
        Console.WriteLine("PASS incomplete or unsupported restoration profile refuses before mutation");
        foreach (var intervention in new[] { "command", "marker", "file" })
        {
            var originalList = File.ReadAllBytes(Path.Combine(root, "lists", "list-general.txt"));
            try
            {
                var foreign = new MemoryService(original) { FailureStage = "start", FailAfterMutation = true };
                foreign.AfterMutation = stage =>
                {
                    if (stage != "start") return;
                    if (intervention == "command") foreign.Replace(foreign.State with { Configuration = foreign.State.Configuration! with { BinaryPath = foreign.State.Configuration.BinaryPath + " --external" } });
                    if (intervention == "marker") foreign.Replace(foreign.State with { RecoveryData = foreign.State.RecoveryData! with { OperationId = null } });
                    if (intervention == "file") File.AppendAllText(Path.Combine(root, "lists", "list-general.txt"), "external edit");
                };
                var result = await new FlowsealAdapter(new RejectCommands(), () => Task.FromResult(foreign.State), foreign).ExecuteAsync(request);
                if (result.Outcome != FlowsealActionOutcome.ApplyFailed || result.Recovery != FlowsealRecoveryOutcome.Conflict
                    || result.State.State != ServiceState.Running || result.State.Preset != "general.bat")
                    throw new Exception("External intervention must be preserved, including same-path edits or missing generation marker: " + intervention + " " + result.Recovery);
            }
            finally { File.WriteAllBytes(Path.Combine(root, "lists", "list-general.txt"), originalList); }
        }
        Console.WriteLine("PASS same-path command edits, generation marker removal and file edits prohibit recovery overwrites");
        var recoveryFailure = new MemoryService(original) { FailureStage = "start" };
        recoveryFailure.AfterMutation = stage => { if (stage == "delete" && recoveryFailure.HasFailed) throw new IOException("Recovery delete failed"); };
        var failedRecovery = await new FlowsealAdapter(new RejectCommands(), () => Task.FromResult(recoveryFailure.State), recoveryFailure).ExecuteAsync(request);
        if (failedRecovery.Outcome != FlowsealActionOutcome.ApplyFailed || failedRecovery.Recovery != FlowsealRecoveryOutcome.Failed
            || !failedRecovery.ApplyError!.Contains("start") || !failedRecovery.RecoveryExplanation!.Contains("Recovery delete failed"))
            throw new Exception("Application error and failed recovery must be reported separately");
        Console.WriteLine("PASS failed recovery reports original apply error and independent recovery failure");
        foreach (var faultMode in new[] { "early-exit", "wait-error", "pending" })
        {
            var waiting = new MemoryService(original);
            var startSeen = false;
            var observationFailed = false;
            var pendingSeen = false;
            waiting.AfterMutation = stage =>
            {
                if (stage != "start") return;
                startSeen = true;
                if (faultMode == "early-exit") waiting.Replace(waiting.State with { State = ServiceState.Stopped, Process = null });
                if (faultMode == "pending") waiting.Replace(waiting.State with { State = ServiceState.Starting });
            };
            var waitingAdapter = new FlowsealAdapter(new RejectCommands(), () =>
            {
                if (startSeen && faultMode == "wait-error" && !observationFailed)
                { observationFailed = true; throw new TimeoutException("Injected status wait failure"); }
                if (waiting.State.State == ServiceState.Starting)
                {
                    if (pendingSeen) waiting.Replace(waiting.State with { State = ServiceState.Running });
                    pendingSeen = true;
                }
                return Task.FromResult(waiting.State);
            }, waiting);
            var result = await waitingAdapter.ExecuteAsync(request);
            if (faultMode == "pending" ? result.Outcome != FlowsealActionOutcome.Success
                : result.Outcome != FlowsealActionOutcome.ApplyFailed || result.Recovery != FlowsealRecoveryOutcome.Restored || result.State.State != ServiceState.Stopped)
                throw new Exception("Transition/status wait/early exit must produce verified success or original stopped service: " + faultMode + " " + result.Recovery);
        }
        Console.WriteLine("PASS pending start completes, early exit and observation wait errors restore original state");
        var locked = new MemoryService(original) { FailureStage = "start" };
        var checkedRecoveryLock = false;
        locked.AfterMutation = stage =>
        {
            if (stage != "delete" || !locked.HasFailed) return;
            var other = new FlowsealAdapter(new RejectCommands(), () => Task.FromResult(locked.State));
            if (other.PreflightAsync(request).GetAwaiter().GetResult().Outcome != FlowsealActionOutcome.Conflict)
                throw new IOException("Mutex released before recovery completed");
            checkedRecoveryLock = true;
        };
        var lockResult = await new FlowsealAdapter(new RejectCommands(), () => Task.FromResult(locked.State), locked).ExecuteAsync(request);
        if (!checkedRecoveryLock || lockResult.Recovery != FlowsealRecoveryOutcome.Restored)
            throw new Exception("Mutation guard must remain held through rollback");
        Console.WriteLine("PASS shared guard remains held for the complete recovery transaction");
        await RunReview(root);
    }
}

sealed class MemoryService(ServiceSnapshot initial) : IServiceMutationBackend
{
    public ServiceSnapshot State { get; private set; } = initial;
    public string? FailureStage { get; init; }
    public bool FailAfterMutation { get; init; }
    public Action<string>? AfterMutation { get; set; }
    public bool HasFailed => failed;
    public void Replace(ServiceSnapshot state) => State = state;
    private bool failed;
    private void Command(string stage, ServiceSnapshot? expected, Action mutate)
    {
        if (expected != null && (State.State != expected.State || State.Configuration != expected.Configuration || State.RecoveryData != expected.RecoveryData || State.Process != expected.Process))
            throw new IOException("Fake Windows refused to mutate changed generation");
        var fail = stage == FailureStage && !failed;
        if (fail) failed = true;
        if (fail && !FailAfterMutation) throw new IOException("Injected " + stage);
        mutate();
        AfterMutation?.Invoke(stage);
        if (fail) throw new IOException("Injected partial " + stage);
    }
    public void Stop(ServiceSnapshot expected) => Command("stop", expected, () => State = State with { State = ServiceState.Stopped, Process = null });
    public void Delete(ServiceSnapshot expected) => Command("delete", expected, () => State = new(ServiceState.Missing, null));
    public void Create(ServiceSnapshot desired) => Command("create", null, () =>
    { if (State.State != ServiceState.Missing) throw new IOException("Occupied service"); State = desired; });
    public void Start(ServiceSnapshot expected) => Command("start", expected, () => State = State with { State = ServiceState.Running,
        Process = new(42, Path.Combine(Path.GetDirectoryName(CommandLine.Split(State.Configuration!.BinaryPath)[0])!, "winws.exe"), 123) });
}
