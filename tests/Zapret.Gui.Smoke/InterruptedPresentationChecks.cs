using System.IO;
using CommunityToolkit.Mvvm.Input;
using Zapret.Core;
using Zapret.Gui.Services;
using Zapret.Gui.ViewModels;

internal static class InterruptedPresentationChecks
{
    public static IFlowsealAdapter Preview(string root) => new RecoveryAdapter(root);

    public static void Run(Action<Task> complete)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "flowseal-1.10.3");
        var adapter = new RecoveryAdapter(root);
        var model = new MainViewModel(adapter, root);
        var closes = 0;
        model.CloseRequested += (_, _) => closes++;
        try
        {
            complete(model.InitializeAsync());
            var command = typeof(MainViewModel).GetProperty("RecoverInterruptedCommand")?.GetValue(model) as IAsyncRelayCommand;
            if (command == null || !command.CanExecute(null)) throw new Exception("Reopened GUI must expose explicit interrupted recovery");
            if (model.ApplyCommand.CanExecute(null) || model.StartManualCommand.CanExecute(null) || model.RemoveCommand.CanExecute(null))
                throw new Exception("Unfinished recovery must block new service/manual mutations");
            complete(command.ExecuteAsync(null));
            if (adapter.Pending || model.CurrentState != ServiceState.Running || model.HasError || closes != 0)
                throw new Exception("Recovery must show observed restored state, close the record and keep GUI open");
        }
        finally { model.Notifications.Dispose(); }
        var conflicting = new RecoveryAdapter(root) { Conflict = true };
        var conflictModel = new MainViewModel(conflicting, root);
        try
        {
            complete(conflictModel.InitializeAsync());
            var command = (IAsyncRelayCommand)typeof(MainViewModel).GetProperty("RecoverInterruptedCommand")!.GetValue(conflictModel)!;
            complete(command.ExecuteAsync(null));
            if (!conflicting.Pending || !conflictModel.HasError || !conflictModel.Error.Contains("Стороннее изменение")
                || conflictModel.StartManualCommand.CanExecute(null)) throw new Exception("Conflict must remain visible and keep operation pending");
        }
        finally { conflictModel.Notifications.Dispose(); }
        Console.WriteLine("PASS GUI reopened-operation recovery, mutation exclusion, observed state and preserved conflict");
    }

    private sealed class RecoveryAdapter(string root) : IFlowsealAdapter
    {
        public bool Pending { get; private set; } = true;
        public bool Conflict { get; init; }
        public Task<DistributionDescription> DescribeAsync(string selectedRoot)
        {
            var description = new FlowsealDistributionAdapter().Describe(root);
            return Task.FromResult(description with { Capabilities = description.Capabilities.Select(c => c with { Available = true }).ToArray() });
        }
        public Task<ServiceSnapshot> ReadStateAsync(string selectedRoot) => Task.FromResult(new ServiceSnapshot(
            Pending ? ServiceState.Stopped : ServiceState.Running, "original", new("inert"), Ownership: ServiceOwnership.Owned));
        public async Task<InterruptedOperationSnapshot> ReadInterruptedOperationAsync(string selectedRoot) => new(Pending, Pending, root,
            Pending ? "Обнаружена незавершённая замена автозапуска." : "Незавершённых операций нет.", await ReadStateAsync(selectedRoot));
        public async Task<FlowsealActionResult> PreflightAsync(FlowsealActionRequest request) => new(FlowsealActionOutcome.Ready,
            await ReadStateAsync(request.Root), "Готово");
        public async Task<FlowsealActionResult> ExecuteAsync(FlowsealActionRequest request)
        {
            if (request.Action != FlowsealAction.RecoverInterrupted) throw new Exception("Only explicit recovery may execute");
            if (!Conflict) Pending = false;
            return new(Conflict ? FlowsealActionOutcome.Conflict : FlowsealActionOutcome.Success, await ReadStateAsync(request.Root),
                Conflict ? "Стороннее изменение сохранено." : "Прежнее состояние восстановлено.");
        }
    }
}
