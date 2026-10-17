using System.IO;
using Zapret.Core;
using Zapret.Gui.Services;
using Zapret.Gui.ViewModels;

internal static class ServiceRecoveryPresentationChecks
{
    public static void Run(Action<Task> complete)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "flowseal-1.10.3");
        foreach (var recovery in Enum.GetValues<FlowsealRecoveryOutcome>())
        {
            var adapter = new PresentationAdapter(root, new(FlowsealActionOutcome.ApplyFailed,
                new(ServiceState.Stopped, "previous strategy"), "Применение не завершено.", recovery,
                "Ошибка запуска новой стратегии.", "Восстановление: " + recovery, "Проверьте состояние и повторите проверку."));
            var model = new MainViewModel(adapter, root);
            var closes = 0;
            model.CloseRequested += (_, _) => closes++;
            try
            {
                complete(model.InitializeAsync());
                Check(model.ApplyCommand.CanExecute(null), "Ready preflight must enable the GUI action");
                complete(model.ApplyCommand.ExecuteAsync(null));
                Check(adapter.Executions == 1 && model.ActivePreset == "previous strategy" && model.StatusText == "Остановлена",
                    "GUI must present the observed result, including the previous strategy after recovery");
                Check(model.Error.Contains("Ошибка запуска") && model.ActionReadiness.Contains("Восстановление: " + recovery)
                    && model.ActionReadiness.Contains("Повторите", StringComparison.OrdinalIgnoreCase),
                    "Application error, recovery result and next step must remain visible together");
                Check(closes == 0 && !model.ApplyCommand.CanExecute(null), "Failure must keep the window open and require a new preflight");
            }
            finally { model.Notifications.Dispose(); }
        }
        foreach (var close in new[] { true, false })
        {
            var adapter = new PresentationAdapter(root, new(FlowsealActionOutcome.Success,
                new(ServiceState.Running, "new strategy"), "Автозапуск установлен."));
            var model = new MainViewModel(adapter, root) { CloseAfterApply = close };
            var closes = 0;
            model.CloseRequested += (_, _) => { Check(!model.IsBusy, "Close must occur after the action finishes"); closes++; };
            try
            {
                complete(model.InitializeAsync());
                complete(model.ApplyCommand.ExecuteAsync(null));
                Check(!model.HasError && model.CurrentState == ServiceState.Running && model.ActivePreset == "new strategy",
                    "Confirmed success must show the actual running strategy");
                Check(closes == (close ? 1 : 0), "Close-after-apply preference must apply only to success");
                complete(model.RefreshCommand.ExecuteAsync(null));
                model.SelectedPreset = model.Presets.Last();
                Check(!model.ApplyCommand.CanExecute(null), "Selecting another strategy must invalidate preflight approval");
            }
            finally { model.Notifications.Dispose(); }
        }
        Console.WriteLine("PASS GUI → adapter recovery: error, restoration, conflict, next step, observed state and close only on success");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class PresentationAdapter(string root, FlowsealActionResult result) : IFlowsealAdapter
    {
        public int Executions { get; private set; }
        public Task<DistributionDescription> DescribeAsync(string selectedRoot) =>
            Task.FromResult(new FlowsealDistributionAdapter().Describe(root));
        public Task<ServiceSnapshot> ReadStateAsync(string selectedRoot) => Task.FromResult(new ServiceSnapshot(ServiceState.Stopped, "previous strategy"));
        public Task<FlowsealActionResult> PreflightAsync(FlowsealActionRequest request) =>
            Task.FromResult(new FlowsealActionResult(FlowsealActionOutcome.Ready, new(ServiceState.Stopped, "previous strategy"), "Готово к замене."));
        public Task<FlowsealActionResult> ExecuteAsync(FlowsealActionRequest request)
        {
            Executions++;
            return Task.FromResult(result);
        }
    }
}
