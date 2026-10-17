using Zapret.Core;
using Zapret.Gui.Services;

static class ServiceControlScenarios
{
    public static async Task Run(string root)
    {
        // BAT installation has neither GUI marker nor recoverable ACL snapshot.
        var original = new ServiceSnapshot(ServiceState.Stopped, null,
            new ServiceConfiguration(CommandLine.Quote(Path.Combine(root, "bin", "winws.exe")) + " --wf-tcp=443"));
        var backend = new MemoryService(original);
        var adapter = new FlowsealAdapter(new RejectCommands(), () => Task.FromResult(backend.State), backend);
        var start = await adapter.ExecuteAsync(new(root, FlowsealAction.StartService));
        if (start.Outcome != FlowsealActionOutcome.Success || start.State.State != ServiceState.Running
            || start.State.Configuration != original.Configuration)
            throw new Exception("Start must confirm the BAT-installed service without replacing registration: " + start);
        var stop = await adapter.ExecuteAsync(new(root, FlowsealAction.StopService));
        if (stop.Outcome != FlowsealActionOutcome.Success || stop.State.State != ServiceState.Stopped
            || stop.State.Configuration != original.Configuration)
            throw new Exception("Temporary stop must retain automatic registration: " + stop);
        var remove = await adapter.ExecuteAsync(new(root, FlowsealAction.RemoveService));
        if (remove.Outcome != FlowsealActionOutcome.Success || remove.State.State != ServiceState.Missing)
            throw new Exception("Remove must confirm absence of own service: " + remove);
        Console.WriteLine("PASS BAT-installed service starts, temporarily stops and removes independently of strategy selection");
        foreach (var action in new[] { FlowsealAction.StartService, FlowsealAction.StopService, FlowsealAction.RemoveService })
        {
            var missing = await adapter.ExecuteAsync(new(root, action));
            if (missing.State.State != ServiceState.Missing || missing.Outcome != (action == FlowsealAction.StartService
                ? FlowsealActionOutcome.InvalidRequest : FlowsealActionOutcome.Success))
                throw new Exception("Absent service must give an explicit, non-mutating result: " + action);
            var foreign = original with { Configuration = new(CommandLine.Quote(Environment.ProcessPath!)) };
            foreach (var initial in new[] { foreign, original with { State = ServiceState.Unknown } })
            {
                var refusedBackend = new MemoryService(initial);
                var refused = await new FlowsealAdapter(new RejectCommands(), () => Task.FromResult(refusedBackend.State), refusedBackend)
                    .ExecuteAsync(new(root, action));
                if (refused.Outcome is not (FlowsealActionOutcome.ForeignInstallation or FlowsealActionOutcome.UnknownOwnership)
                    || refusedBackend.State != initial) throw new Exception("Unowned service must remain unchanged: " + action);
            }
        }
        Console.WriteLine("PASS absent, foreign and unknown services have explicit safe results for all controls");
        var running = original with { State = ServiceState.Running, Process = new(1, Path.Combine(root, "bin", "winws.exe"), 9) };
        foreach (var initial in new[] { original, running })
        {
            var unchanged = new MemoryService(initial) { AfterMutation = _ => throw new Exception("Already reached state must not mutate") };
            var noOp = await new FlowsealAdapter(new RejectCommands(), () => Task.FromResult(unchanged.State), unchanged)
                .ExecuteAsync(new(root, initial.State == ServiceState.Running ? FlowsealAction.StartService : FlowsealAction.StopService));
            if (noOp.Outcome != FlowsealActionOutcome.Success || unchanged.State != initial)
                throw new Exception("Already running/stopped must be successful without changing registration");
        }
        var removedRunning = new MemoryService(running);
        var removed = await new FlowsealAdapter(new RejectCommands(), () => Task.FromResult(removedRunning.State), removedRunning)
            .ExecuteAsync(new(root, FlowsealAction.RemoveService));
        if (removed.Outcome != FlowsealActionOutcome.Success || removed.State.State != ServiceState.Missing)
            throw new Exception("Removal must stop own running service and confirm deletion");
        Console.WriteLine("PASS idempotent start/stop and removal of a running owned service");

        foreach (var scenario in new[] { "pending", "timeout", "external", "early-exit", "denied", "partial-stop", "delete-timeout" })
        {
            var control = new MemoryService(scenario is "partial-stop" or "delete-timeout" ? running : original);
            var issued = false;
            var reads = 0;
            control.AfterMutation = stage =>
            {
                issued = true;
                if (scenario is "pending" or "timeout") control.Replace(control.State with { State = ServiceState.Starting });
                if (scenario == "early-exit") control.Replace(original);
                if (scenario == "external") control.Replace(control.State with { Configuration = control.State.Configuration! with { StartType = 3 } });
                if (scenario == "partial-stop") throw new IOException("Injected stop response failure");
                if (scenario == "delete-timeout" && stage == "delete") control.Replace(original);
            };
            var testAdapter = new FlowsealAdapter(new RejectCommands(), () =>
            {
                if (issued && scenario == "pending" && ++reads > 2)
                    control.Replace(control.State with { State = ServiceState.Running });
                if (scenario == "denied") throw new UnauthorizedAccessException("Injected observation denial");
                return Task.FromResult(control.State);
            }, control);
            var result = await testAdapter.ExecuteAsync(new(root, scenario is "partial-stop" or "delete-timeout"
                ? FlowsealAction.RemoveService : FlowsealAction.StartService));
            var wanted = scenario switch { "pending" => FlowsealActionOutcome.Success,
                "external" => FlowsealActionOutcome.Conflict, "denied" => FlowsealActionOutcome.InvalidRequest, _ => FlowsealActionOutcome.ApplyFailed };
            if (result.Outcome != wanted || (scenario == "external" && result.State.Configuration?.StartType != 3)
                || (scenario == "partial-stop" && result.State.State != ServiceState.Stopped)
                || (scenario == "timeout" && result.State.State != ServiceState.Starting))
                throw new Exception("Control must expose actual outcome/state without destructive follow-up: " + scenario + " " + result);
        }
        Console.WriteLine("PASS pending transitions, start/delete timeouts, intervention, early exit, denied observation and partial stop");
        foreach (var stage in new[] { "start", "stop", "delete" })
        foreach (var after in new[] { false, true })
        {
            var fault = new MemoryService(stage == "stop" ? running : original) { FailureStage = stage, FailAfterMutation = after };
            var result = await new FlowsealAdapter(new RejectCommands(), () => Task.FromResult(fault.State), fault)
                .ExecuteAsync(new(root, stage == "start" ? FlowsealAction.StartService : FlowsealAction.RemoveService));
            if (result.Outcome != FlowsealActionOutcome.ApplyFailed || string.IsNullOrEmpty(result.NextStep)
                || (result.State with { Ownership = fault.State.Ownership, OwnershipReason = fault.State.OwnershipReason }) != fault.State)
                throw new Exception("Windows command failure must report actual state including partial effects: " + stage + "/" + after);
        }
        foreach (var editFile in new[] { false, true })
        {
            var list = Path.Combine(root, "lists", "list-general.txt");
            var bytes = File.ReadAllBytes(list);
            try
            {
                var changed = new MemoryService(running);
                changed.AfterMutation = stage =>
                {
                    if (stage != "stop") throw new Exception("External change must prevent deletion");
                    if (editFile) File.AppendAllText(list, "external edit");
                    else changed.Replace(changed.State with { Configuration = changed.State.Configuration! with { StartType = 3 } });
                };
                var result = await new FlowsealAdapter(new RejectCommands(), () => Task.FromResult(changed.State), changed)
                    .ExecuteAsync(new(root, FlowsealAction.RemoveService));
                if (result.Outcome != FlowsealActionOutcome.Conflict || result.State.State != ServiceState.Stopped
                    || (editFile && !File.ReadAllText(list).EndsWith("external edit")))
                    throw new Exception("External change after stop must preserve registration and file edit");
            }
            finally { File.WriteAllBytes(list, bytes); }
        }
        Console.WriteLine("PASS command failures before/after effects expose actual state; external changes after stop prevent deletion");
    }
}
