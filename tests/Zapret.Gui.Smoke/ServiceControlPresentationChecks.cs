using System.IO;
using System.Windows.Input;
using Zapret.Core;
using Zapret.Gui.Services;
using Zapret.Gui.ViewModels;

internal static class ServiceControlPresentationChecks
{
    public static void Run(Action<Task> complete)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "flowseal-1.10.3");
        var adapter = new ControlsAdapter(root);
        var model = new MainViewModel(adapter, root);
        var closes = 0;
        model.CloseRequested += (_, _) => closes++;
        try
        {
            complete(model.InitializeAsync());
            model.SelectedPreset = null;
            foreach (var (name, action, state) in new[] {
                ("StartServiceCommand", FlowsealAction.StartService, ServiceState.Running),
                ("StopServiceCommand", FlowsealAction.StopService, ServiceState.Stopped),
                ("RemoveCommand", FlowsealAction.RemoveService, ServiceState.Missing) })
            {
                var command = typeof(MainViewModel).GetProperty(name)?.GetValue(model) as CommunityToolkit.Mvvm.Input.IAsyncRelayCommand;
                if (command == null || !command.CanExecute(null)) throw new Exception("GUI must expose independent service control: " + name);
                complete(command.ExecuteAsync(null));
                if (adapter.LastAction != action || model.CurrentState != state || model.HasError || closes != 0)
                    throw new Exception("GUI must show observed control result and keep the window open: " + action);
                if (model.ApplyCommand.CanExecute(null)) throw new Exception("Service controls must invalidate installation approval");
            }
        }
        finally { model.Notifications.Dispose(); }
        Console.WriteLine("PASS GUI independent service controls without strategy, observed state and no automatic close");
    }

    private sealed class ControlsAdapter(string root) : IFlowsealAdapter
    {
        public FlowsealAction? LastAction { get; private set; }
        private ServiceSnapshot state = new(ServiceState.Stopped, null, new("inert"), Ownership: ServiceOwnership.Owned);
        public Task<DistributionDescription> DescribeAsync(string selectedRoot)
        {
            var description = new FlowsealDistributionAdapter().Describe(root);
            return Task.FromResult(description with { Capabilities = description.Capabilities.Select(c =>
                c.Action is FlowsealAction.StartService or FlowsealAction.StopService or FlowsealAction.RemoveService
                ? c with { Available = true } : c).ToArray() });
        }
        public Task<ServiceSnapshot> ReadStateAsync(string selectedRoot) => Task.FromResult(state);
        public Task<FlowsealActionResult> PreflightAsync(FlowsealActionRequest request) =>
            Task.FromResult(new FlowsealActionResult(FlowsealActionOutcome.Ready, state, "Готово"));
        public Task<FlowsealActionResult> ExecuteAsync(FlowsealActionRequest request)
        {
            if (request.Strategy != null) throw new Exception("Controls must not depend on a selected BAT");
            LastAction = request.Action;
            state = state with { State = request.Action switch { FlowsealAction.StartService => ServiceState.Running,
                FlowsealAction.StopService => ServiceState.Stopped, _ => ServiceState.Missing } };
            return Task.FromResult(new FlowsealActionResult(FlowsealActionOutcome.Success, state, "Подтверждено"));
        }
    }
}
