using Zapret.Gui.Services;

namespace Zapret.Gui.ViewModels;

public partial class MainViewModel
{
    private const string PartialObservationNotice = "Часть сведений о службе прочитать не удалось. Проверьте права доступа и обновите состояние; изменение службы может быть недоступно.";
    private ServiceSnapshot observedService = new(ServiceState.Unknown, null);
    private bool refreshPending;
    private Task? observedRefresh;
    private string observationDetails = "";
    private string operationDetails = "";
    private string recoveryDetails = "";
    private string observationNotice = "";
    private bool manualObservationKnown;

    private void SetObservationNotice(string notice)
    {
        var otherErrors = observationNotice.Length == 0 ? Error : Error.Replace(observationNotice, "").Trim();
        observationNotice = notice;
        Error = string.Join("\n", new[] { otherErrors, notice }.Where(text => text.Length != 0));
    }
    public string RegisteredStrategyText => observedService.State == ServiceState.Missing
        ? "Автозапуск: не установлен" : "Автозапуск: " + (observedService.Preset ?? "стратегия неизвестна");
    public string SelectedStrategyText => "Для следующего запуска: " + (SelectedPreset?.Name ?? "не выбрана");
    public string RunningConfigurationText => !manualObservationKnown ? "Пробный процесс: состояние неизвестно. Обновите состояние перед новым запуском."
        : lastManualSnapshot is { State: ManualSessionState.Running, Process.Executable: not null, Process.StartedAtUtcTicks: not null } manual
        ? $"Подтверждённый пробный процесс: {manual.Strategy} · PID {manual.Process.Id}"
        : observedService.State == ServiceState.Stopped || observedService.State == ServiceState.Missing
            ? "Работающая конфигурация службы: процесс не запущен."
            : "Работающая конфигурация службы не подтверждена. Метка автозапуска не доказывает параметры запущенного процесса.";
    public string TechnicalDetails => string.Join("\n\n", new[] { operationDetails, observationDetails, recoveryDetails,
        observedService.Configuration?.BinaryPath,
        observedService.Process is { } process ? $"Служба: PID {process.Id}; {process.Executable ?? "путь неизвестен"}; начало UTC ticks: {process.StartedAtUtcTicks}" : null
    }.Where(text => !string.IsNullOrWhiteSpace(text)));
    private void NotifyObservedState()
    {
        OnPropertyChanged(nameof(RegisteredStrategyText));
        OnPropertyChanged(nameof(RunningConfigurationText));
        OnPropertyChanged(nameof(TechnicalDetails));
    }

    public Task RefreshObservedStateAsync()
    {
        if (IsBusy || observedRefresh != null) { refreshPending = true; return Task.CompletedTask; }
        return observedRefresh = ObserveWithoutInterruptingInputAsync();
    }

    private async Task ObserveWithoutInterruptingInputAsync()
    {
        // Store the task before completing even when an adapter returns synchronously.
        await Task.Yield();
        try
        {
            await ReadStatusAsync();
            await PollManualSessionAsync();
            await ReadInterruptedOperationAsync();
        }
        finally { observedRefresh = null; }
        if (refreshPending && !IsBusy) { refreshPending = false; await RefreshObservedStateAsync(); }
    }

    private async Task<FlowsealActionResult?> ExecuteObservedActionAsync(FlowsealActionRequest request)
    {
        try
        {
            // The adapter repeats observation under its guard immediately before mutation.
            var result = await adapter.ExecuteAsync(request);
            ShowActionResult(result);
            return result;
        }
        catch (Exception error)
        {
            operationDetails = error.ToString();
            Error = "Действие не удалось завершить. Проверьте текущее состояние и журнал восстановления перед повторной попыткой.";
            ActionReadiness = Error;
            return null;
        }
        finally
        {
            await ReadStatusAsync();
            await ReadInterruptedOperationAsync();
            NotifyObservedState();
        }
    }
}
