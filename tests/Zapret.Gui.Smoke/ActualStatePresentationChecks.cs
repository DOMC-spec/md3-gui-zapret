using System.IO;
using Zapret.Core;
using Zapret.Gui.Services;
using Zapret.Gui.ViewModels;

internal static class ActualStatePresentationChecks
{
    public static void Run(Action<Task> complete)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "flowseal-1.10.3");
        var adapter = new StateAdapter(root);
        var model = new MainViewModel(adapter, root);
        try
        {
            complete(model.InitializeAsync());
            var selected = model.SelectedPreset;
            complete(model.RefreshObservedStateAsync());
            if (!model.ApplyCommand.CanExecute(null)) throw new Exception("Unchanged observation must retain preflight readiness");
            if (model.RegisteredStrategyText != "Автозапуск: external BAT" || !model.RunningConfigurationText.Contains("не подтверждена"))
                throw new Exception("A registry label must never prove the running configuration");
            adapter.State = adapter.State with { State = ServiceState.Stopped, Preset = "changed outside" };
            complete(model.RefreshObservedStateAsync());
            if (model.CurrentState != ServiceState.Stopped || model.RegisteredStrategyText != "Автозапуск: changed outside" || model.SelectedPreset != selected)
                throw new Exception("Returning to the window must observe external changes without changing selection");
            if (model.ApplyCommand.CanExecute(null)) throw new Exception("External changes must invalidate preflight approval");
            var held = new TaskCompletionSource<ServiceSnapshot>();
            adapter.HeldRead = held;
            var refreshing = model.RefreshObservedStateAsync();
            complete(model.RefreshObservedStateAsync());
            adapter.State = adapter.State with { Preset = "latest outside" };
            held.SetResult(new(ServiceState.Running, "stale"));
            complete(refreshing);
            if (model.RegisteredStrategyText != "Автозапуск: latest outside")
                throw new Exception("An activation while busy must reread after the operation finishes");
            adapter.PreflightFailure = true;
            complete(model.CheckActionCommand.ExecuteAsync(null));
            if (!model.HasError || model.ActionReadiness.Contains("QueryServiceConfig") || !model.TechnicalDetails.Contains("QueryServiceConfig: 1734"))
                throw new Exception("Preflight must show a human-readable cause and separate technical diagnostics");
            adapter.PreflightFailure = false;
            adapter.FailRead = true;
            complete(model.RefreshObservedStateAsync());
            if (model.CurrentState != ServiceState.Unknown || model.StopServiceCommand.CanExecute(null)
                || !model.TechnicalDetails.Contains("native read 5") || model.Error.Contains("native read 5"))
                throw new Exception("Read failure must replace stale success and separate technical details");
            adapter.FailRead = false;
            complete(model.RefreshObservedStateAsync());
            complete(model.StartManualCommand.ExecuteAsync(null));
            if (!model.HasManualSession || !model.RunningConfigurationText.Contains("trial BAT"))
                throw new Exception("An observed owned trial must identify its running strategy");
            adapter.FailManualRead = true;
            complete(model.RefreshObservedStateAsync());
            if (model.RunningConfigurationText.Contains("trial BAT") || model.StartManualCommand.CanExecute(null)
                || model.ApplyCommand.CanExecute(null) || model.ManualProcessText.Length != 0)
                throw new Exception("Failed process observation must clear proof and block mutations");
            adapter.FailManualRead = false;
            adapter.Session = new(ManualSessionState.Failed, Result: new(FlowsealActionOutcome.ApplyFailed,
                adapter.State, "Процесс завершился раньше времени.", FlowsealRecoveryOutcome.Conflict,
                "native exit 123", "native recovery 456"));
            complete(model.RefreshObservedStateAsync());
            if (model.HasManualSession || model.RunningConfigurationText.Contains("trial BAT")
                || !model.ActionReadiness.Contains("Восстановление остановлено") || !model.TechnicalDetails.Contains("native exit 123")
                || model.Error.Contains("native exit 123") || !model.HasInterruptedOperation)
                throw new Exception("Early exit and recovery conflict must clear live proof, retain details and explain next step");
            complete(model.RefreshObservedStateAsync());
            if (!model.HasError) throw new Exception("Periodic observations must preserve the last failed action");
        }
        finally { model.Notifications.Dispose(); }
        Console.WriteLine("PASS GUI separate selection, registration and runtime; external refresh and failed observation");
    }

    private sealed class StateAdapter(string root) : IFlowsealAdapter
    {
        public ServiceSnapshot State = new(ServiceState.Running, "external BAT", new("inert"),
            new(42, "inert", 123), ServiceOwnership.Owned);
        public bool FailRead;
        public bool FailManualRead;
        public bool PreflightFailure;
        public TaskCompletionSource<ServiceSnapshot>? HeldRead;
        public ManualSessionSnapshot Session = new();
        public Task<DistributionDescription> DescribeAsync(string path)
        {
            var description = new FlowsealDistributionAdapter().Describe(root);
            return Task.FromResult(description with { Capabilities = description.Capabilities.Select(c =>
                c.Action == FlowsealAction.ManualStart ? c with { Available = true } : c).ToArray() });
        }
        public Task<ManualSessionSnapshot> ReadManualSessionAsync() => FailManualRead
            ? Task.FromException<ManualSessionSnapshot>(new IOException("native process 5")) : Task.FromResult(Session);
        public Task<InterruptedOperationSnapshot> ReadInterruptedOperationAsync(string path) => Task.FromResult(
            new InterruptedOperationSnapshot(Session.State == ManualSessionState.Failed, false, Explanation: "Проверьте внешнее изменение перед восстановлением."));
        public Task<ServiceSnapshot> ReadStateAsync(string path)
        {
            if (HeldRead is { } held) { HeldRead = null; return held.Task; }
            return FailRead ? Task.FromException<ServiceSnapshot>(new IOException("native read 5")) : Task.FromResult(State);
        }
        public Task<FlowsealActionResult> PreflightAsync(FlowsealActionRequest request) => Task.FromResult(PreflightFailure
            ? new FlowsealActionResult(FlowsealActionOutcome.RecoveryRequired, State, "Снимок восстановления прочитать не удалось. Проверьте права доступа.", ApplyError: "QueryServiceConfig: 1734")
            : new FlowsealActionResult(FlowsealActionOutcome.Ready, State, "Готово"));
        public Task<FlowsealActionResult> ExecuteAsync(FlowsealActionRequest request)
        {
            Session = new(ManualSessionState.Running, root, "trial BAT", new(77, "inert", 123));
            return Task.FromResult(new FlowsealActionResult(FlowsealActionOutcome.Success, State, "Готово"));
        }
    }
}
