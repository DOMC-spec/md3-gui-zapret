using System.IO;
using CommunityToolkit.Mvvm.Input;
using Zapret.Core;
using Zapret.Gui.Services;
using Zapret.Gui.ViewModels;
using Zapret.Gui;

internal static class ManualPresentationChecks
{
    public static void Run(Action<Task> complete)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "flowseal-1.10.3");
        var adapter = new ManualAdapter(root);
        var model = new MainViewModel(adapter, root);
        var closes = 0;
        var closed = false;
        model.CloseRequested += (_, _) => closes++;
        try
        {
            complete(model.InitializeAsync());
            var start = typeof(MainViewModel).GetProperty("StartManualCommand")?.GetValue(model) as IAsyncRelayCommand;
            if (start == null || !start.CanExecute(null)) throw new Exception("GUI must expose separate manual trial");
            complete(start.ExecuteAsync(null));
            if (closes != 0 || model.ApplyCommand.CanExecute(null) || model.RemoveCommand.CanExecute(null))
                throw new Exception("Trial must keep the window open and block service changes");
            complete(model.SelectDistributionCommand.ExecuteAsync(root + " other"));
            if (model.Root != root) throw new Exception("Active trial must retain its distribution");
            var stop = typeof(MainViewModel).GetProperty("StopManualCommand")?.GetValue(model) as IAsyncRelayCommand;
            if (stop == null || !stop.CanExecute(null)) throw new Exception("GUI must offer trial cleanup");
            complete(stop.ExecuteAsync(null));
            if (adapter.Session.IsActive || model.HasError || model.CurrentState != ServiceState.Running || closes != 0)
                throw new Exception("Stopping trial must show restored service without closing GUI");
            complete(start.ExecuteAsync(null));
            var window = new MainWindow { DataContext = model };
            window.Closed += (_, _) => closed = true;
            window.Close();
            complete(Task.Delay(50));
            if (!closed || adapter.Session.IsActive || model.CurrentState != ServiceState.Running)
                throw new Exception("Closing real WPF window must await trial cleanup and service restoration");
        }
        finally { if (!closed) model.Notifications.Dispose(); }
        var failing = new ManualAdapter(root) { FailStop = true };
        var failedModel = new MainViewModel(failing, root);
        try
        {
            complete(failedModel.InitializeAsync());
            complete(failedModel.StartManualCommand.ExecuteAsync(null));
            var close = failedModel.PrepareToCloseAsync();
            complete(close);
            if (close.Result || !failedModel.HasError || failedModel.CurrentState != ServiceState.Stopped)
                throw new Exception("Cleanup failure must keep the window open with observed state and an explanation");
        }
        finally { failedModel.Notifications.Dispose(); }
        Console.WriteLine("PASS GUI manual start/stop, root guard, command exclusion, awaited window cleanup and failed-close explanation");
    }

    private sealed class ManualAdapter(string root) : IFlowsealAdapter
    {
        public ManualSessionSnapshot Session { get; private set; } = new();
        public bool FailStop { get; init; }
        public Task<DistributionDescription> DescribeAsync(string path)
        {
            var description = new FlowsealDistributionAdapter().Describe(root);
            return Task.FromResult(description with { Capabilities = description.Capabilities.Select(c =>
                c.Action == FlowsealAction.ManualStart ? c with { Available = true } : c).ToArray() });
        }
        public Task<ServiceSnapshot> ReadStateAsync(string path) => Task.FromResult(new ServiceSnapshot(Session.IsActive || (FailStop && Session.State == ManualSessionState.Finished) ? ServiceState.Stopped : ServiceState.Running, "original",
            new("inert"), Ownership: ServiceOwnership.Owned));
        public Task<ManualSessionSnapshot> ReadManualSessionAsync() => Task.FromResult(Session);
        public Task<FlowsealActionResult> PreflightAsync(FlowsealActionRequest request) => Task.FromResult(new FlowsealActionResult(
            FlowsealActionOutcome.Ready, new(ServiceState.Running, "original"), "Ready"));
        public Task<FlowsealActionResult> ExecuteAsync(FlowsealActionRequest request)
        {
            var starting = request.Action == FlowsealAction.ManualStart;
            if (starting && request.Strategy == null) throw new Exception("Trial requires selected original strategy");
            Session = new(starting ? ManualSessionState.Running : ManualSessionState.Finished, root, "general",
                starting ? new(77, Path.Combine(root, "bin", "winws.exe"), 456) : null,
                "Обновления не проверялись.");
            return Task.FromResult(new FlowsealActionResult(!starting && FailStop ? FlowsealActionOutcome.ApplyFailed : FlowsealActionOutcome.Success,
                new(starting || FailStop ? ServiceState.Stopped : ServiceState.Running, "original"),
                !starting && FailStop ? "Возврат службы не подтверждён." : "Подтверждено"));
        }
    }
}
