using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaterialDesignThemes.Wpf;
using Zapret.Core;
using Zapret.Gui.Services;
using Zapret.Gui.Theming;

namespace Zapret.Gui.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IFlowsealAdapter adapter;
    private bool installReady;
    public event EventHandler? CloseRequested;
    public MainViewModel() : this(new FlowsealAdapter(new ProcessRunner()), AppContext.BaseDirectory) { }
    public MainViewModel(IFlowsealAdapter adapter, string applicationDirectory)
    {
        this.adapter = adapter;
        root = applicationDirectory;
    }
    [ObservableProperty] private string root;
    [ObservableProperty] private DistributionDescription? distribution;
    public ObservableCollection<Preset> Presets { get; } = [];
    public ObservableCollection<HostlistItem> Hostlists { get; } = [];
    public SnackbarMessageQueue Notifications { get; } = new(TimeSpan.FromSeconds(3));

    private string originalText = "";
    private string? loadedPath;

    [ObservableProperty] private Preset? selectedPreset;
    [ObservableProperty] private string statusText = "Проверка…";
    [ObservableProperty] private string activePreset = "—";
    [ObservableProperty] private string serviceOwnershipText = "Принадлежность службы не проверена.";
    [ObservableProperty] private string autostartText = "Автозапуск не проверен.";
    private ServiceOwnership serviceOwnership = ServiceOwnership.Unknown;
    [ObservableProperty] private string actionReadiness = "Выберите стратегию и проверьте действие.";
    [ObservableProperty] private ServiceState currentState = ServiceState.Missing;
    [ObservableProperty] private string error = "";
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool closeAfterApply = true;
    [ObservableProperty] private HostlistItem? selectedHostlist;
    [ObservableProperty] private string hostlistText = "";
    [ObservableProperty] private string domain = "";
    [ObservableProperty] private bool isDark;
    [ObservableProperty] private bool hasManualSession;
    [ObservableProperty] private string manualStatusText = "Пробный сеанс не запущен.";
    [ObservableProperty] private string manualProcessText = "";
    private ManualSessionSnapshot? lastManualSnapshot;
    [ObservableProperty] private bool hasInterruptedOperation;
    [ObservableProperty] private string interruptedOperationText = "Незавершённых операций нет.";
    private InterruptedOperationSnapshot interruptedOperation = new();
    public string ManualConsequences => "Пробный запуск не устанавливает автозапуск. Своя работающая служба временно остановится и вернётся после завершения сеанса или закрытия окна. Обновления не проверяются. TCP timestamps и созданные списки сохранятся. Отсутствующие настройки Game Filter сохраняются как отключённый режим.";

    public string DistributionVersion => Distribution?.DeclaredVersion is { } version ? "Заявленная версия поставки: " + version : "Версия поставки неизвестна";
    public string ReadinessText => Distribution?.Explanation ?? "Выберите папку поставки или обновите состояние.";
    public string MissingComponentsText => Distribution?.MissingComponents.Count > 0 ? "Отсутствуют: " + string.Join(", ", Distribution.MissingComponents) : "";
    public string PathLimitations => Distribution?.PathIssue ?? "Автозапуск продолжает работать после закрытия окна.";
    public string StrategyReadiness => Distribution?.Strategies.FirstOrDefault(s => s.Preset.FilePath == SelectedPreset?.FilePath)?.Explanation ?? "Выберите стратегию из поставки.";
    public string CapabilitySummary => Distribution == null ? "" : string.Join("\n\n", Distribution.Capabilities.Select(c => ActionName(c.Action) + ": " + c.Reason));
    private static string ActionName(FlowsealAction action) => action switch
    {
        FlowsealAction.ReadState => "Чтение состояния", FlowsealAction.ManualStart => "Пробный запуск", FlowsealAction.InstallService => "Установка автозапуска",
        FlowsealAction.ManualStop => "Завершение пробного сеанса",
        FlowsealAction.RecoverInterrupted => "Восстановление после сбоя",
        FlowsealAction.StartService => "Запуск службы", FlowsealAction.StopService => "Остановка службы", FlowsealAction.RemoveService => "Удаление автозапуска",
        FlowsealAction.ChangeGameFilter => "Game Filter", FlowsealAction.EditLists => "Изменение списков", FlowsealAction.Tests => "Тесты Flowseal",
        FlowsealAction.ChangeIpSet => "IPSet Filter", FlowsealAction.UpdateIpSet => "Обновление IPSet", FlowsealAction.UpdateHosts => "Обновление hosts", FlowsealAction.Diagnostics => "Диагностика",
        FlowsealAction.CheckUpdates => "Проверка обновлений", FlowsealAction.ChangeUpdateChecks => "Настройка проверки обновлений", _ => "Замена активных fake-файлов"
    };
    private bool Allows(FlowsealAction action) => Distribution?.Capabilities.Any(c => c.Action == action && c.Available) == true;
    private string Unavailable(FlowsealAction action) => Distribution?.Capabilities.FirstOrDefault(c => c.Action == action)?.Reason ?? "Сначала выберите поставку Flowseal.";
    public bool CanEditLists => !IsBusy && !HasManualSession && !HasInterruptedOperation && Allows(FlowsealAction.EditLists);
    private bool CanControlService(FlowsealAction action) => !IsBusy && manualObservationKnown && !HasManualSession && !HasInterruptedOperation && Allows(action)
        && serviceOwnership == ServiceOwnership.Owned && CurrentState is ServiceState.Running or ServiceState.Stopped;
    private bool CanRemove() => CanControlService(FlowsealAction.RemoveService);
    private bool CanStartService() => CanControlService(FlowsealAction.StartService) && CurrentState == ServiceState.Stopped;
    private bool CanStopService() => CanControlService(FlowsealAction.StopService) && CurrentState == ServiceState.Running;
    private void NotifyServiceControls()
    {
        RemoveCommand.NotifyCanExecuteChanged();
        StartServiceCommand.NotifyCanExecuteChanged();
        StopServiceCommand.NotifyCanExecuteChanged();
    }
    public bool CanInteract => !IsBusy;
    public bool CanChooseDistribution => !IsBusy && !HasManualSession;
    private bool CanStartManual() => !IsBusy && manualObservationKnown && !HasManualSession && !HasInterruptedOperation && SelectedPreset != null && Allows(FlowsealAction.ManualStart);
    private bool CanStopManual() => !IsBusy && HasManualSession;
    private void NotifyManualControls()
    {
        NotifyDomainListControls();
        NotifyGameFilter();
        OnPropertyChanged(nameof(CanChooseDistribution));
        OnPropertyChanged(nameof(CanEditLists));
        StartManualCommand.NotifyCanExecuteChanged();
        StopManualCommand.NotifyCanExecuteChanged();
        SelectDistributionCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
        CheckActionCommand.NotifyCanExecuteChanged();
        SaveHostlistCommand.NotifyCanExecuteChanged();
        AddDomainCommand.NotifyCanExecuteChanged();
        NotifyServiceControls();
        RecoverInterruptedCommand.NotifyCanExecuteChanged();
        OpenRecoveryDistributionCommand.NotifyCanExecuteChanged();
    }
    partial void OnHasManualSessionChanged(bool value) => NotifyManualControls();
    partial void OnHasInterruptedOperationChanged(bool value) => NotifyManualControls();
    partial void OnDistributionChanged(DistributionDescription? value)
    {
        installReady = false;
        foreach (var name in new[] { nameof(DistributionVersion), nameof(ReadinessText), nameof(MissingComponentsText), nameof(PathLimitations), nameof(StrategyReadiness), nameof(CapabilitySummary), nameof(CanEditLists) }) OnPropertyChanged(name);
        ApplyCommand.NotifyCanExecuteChanged();
        NotifyServiceControls();
        SaveHostlistCommand.NotifyCanExecuteChanged();
        AddDomainCommand.NotifyCanExecuteChanged();
        NotifyManualControls();
    }
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public bool IsDirty => loadedPath != null && (HostlistText != originalText || Domain.Length != 0);
    public bool HasLoadedHostlist => loadedPath != null;
    public string LoadedFileName => loadedPath == null ? "Список не открыт" : System.IO.Path.GetRelativePath(Root, loadedPath);
    public string FileSummary => $"Найдено стратегий: {Presets.Count} · списков: {Hostlists.Count}";

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInteract));
        OnPropertyChanged(nameof(CanEditLists));
        SelectDistributionCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
        NotifyServiceControls();
        RefreshCommand.NotifyCanExecuteChanged();
        CheckActionCommand.NotifyCanExecuteChanged();
        LoadHostlistCommand.NotifyCanExecuteChanged();
        SaveHostlistCommand.NotifyCanExecuteChanged();
        AddDomainCommand.NotifyCanExecuteChanged();
        NotifyManualControls();
    }
    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnSelectedPresetChanged(Preset? value)
    {
        OnPropertyChanged(nameof(SelectedStrategyText));
        if (!IsBusy) _ = RefreshDomainListsAsync();
        installReady = false;
        ApplyCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(StrategyReadiness));
        ActionReadiness = "Стратегия выбрана. Нажмите «Проверить действие», чтобы проверить возможность замены автозапуска.";
        CheckActionCommand.NotifyCanExecuteChanged();
        StartManualCommand.NotifyCanExecuteChanged();
    }
    partial void OnHostlistTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }
    partial void OnIsDarkChanged(bool value) => M3Theme.Apply(value);

    public async Task InitializeAsync() => await RefreshAsync();

    private async Task PerformAsync(Func<Task> action, bool clearError = true)
    {
        if (IsBusy) return;
        IsBusy = true;
        if (clearError) Error = "";
        try { await action(); }
        catch (Exception ex) { Error = ex.Message; }
        finally
        {
            IsBusy = false;
            if (refreshPending) { refreshPending = false; await RefreshObservedStateAsync(); }
        }
    }

    private async Task ReadStatusAsync()
    {
        SetObservationNotice("");
        ServiceSnapshot snapshot;
        try { snapshot = await adapter.ReadStateAsync(Root); }
        catch (Exception ex)
        {
            snapshot = new ServiceSnapshot(ServiceState.Unknown, null);
            observationDetails = ex.ToString();
            SetObservationNotice("Состояние службы прочитать не удалось. Проверьте права доступа и нажмите «Обновить».");
        }
        ShowObservedState(snapshot);
    }

    private void ShowObservedState(ServiceSnapshot snapshot)
    {
        if (snapshot != observedService) installReady = false;
        observedService = snapshot;
        if (snapshot.State != ServiceState.Unknown || snapshot.ObservationError != null) observationDetails = snapshot.ObservationError ?? "";
        if (snapshot.ObservationError != null)
            SetObservationNotice("Часть сведений о службе прочитать не удалось. Проверьте права доступа и обновите состояние; изменение службы может быть недоступно.");
        serviceOwnership = snapshot.Ownership;
        AutostartText = snapshot.State == ServiceState.Missing ? "Автозапуск не установлен." : snapshot.Configuration?.StartType switch
        {
            2 => "Автозапуск при загрузке Windows включён.",
            3 => "Автозапуск выключен; служба зарегистрирована для запуска вручную.",
            4 => "Запуск зарегистрированной службы отключён в Windows.",
            _ => "Тип запуска службы неизвестен."
        };
        ServiceOwnershipText = snapshot.OwnershipReason;
        StatusText = snapshot.State switch
        {
            ServiceState.Missing => "Не установлена", ServiceState.Running => "Работает",
            ServiceState.Stopped => "Остановлена", ServiceState.Starting => "Запускается…",
            ServiceState.Stopping => "Останавливается…", ServiceState.Paused => "Приостановлена", _ => "Неизвестно"
        };
        CurrentState = snapshot.State;
        NotifyServiceControls();
        ActivePreset = snapshot.Preset ?? (snapshot.State == ServiceState.Missing ? "Стратегия ещё не применена" : "Стратегия не указана");
        NotifyObservedState();
    }

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private Task RefreshAsync() => PerformAsync(LoadDistributionAsync);

    [RelayCommand(CanExecute = nameof(CanChooseDistribution))]
    private Task SelectDistributionAsync(string path) => PerformAsync(async () =>
    {
        if (HasManualSession) throw new IOException("Сначала завершите свой пробный сеанс перед сменой поставки.");
        if (IsDirty) throw new IOException("Сохраните или отмените изменения списка перед выбором другой поставки.");
        if (IsGameFilterDirty) throw new IOException("Сохраните или отмените изменения Game Filter перед выбором другой поставки.");
        ResetGameFilter();
        Distribution = null;
        Presets.Clear();
        Hostlists.Clear();
        Root = path;
        loadedPath = null;
        originalText = "";
        listSnapshot = null;
        ObservedListText = "";
        comparedList = null;
        HasListComparison = false;
        HostlistText = "";
        SelectedHostlist = null;
        SelectedPreset = null;
        Domain = "";
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(HasLoadedHostlist));
        OnPropertyChanged(nameof(LoadedFileName));
        await LoadDistributionAsync();
    });

    private async Task LoadDistributionAsync()
    {
        installReady = false;
        var name = SelectedPreset?.Name;
        Distribution = await adapter.DescribeAsync(Root);
        Root = Distribution.Root;
        Presets.Clear();
        foreach (var strategy in Distribution.Strategies) Presets.Add(strategy.Preset);
        SelectedPreset = Presets.FirstOrDefault(p => p.Name == name) ?? Presets.FirstOrDefault();
        await RefreshDomainListsAsync();
        if (!IsDirty)
        {
            var path = SelectedHostlist?.Path;
            Hostlists.Clear();
            foreach (var list in domainLists) Hostlists.Add(new HostlistItem(list.Path, list.Name));
            SelectedHostlist = Hostlists.FirstOrDefault(p => p.Path == path) ?? Hostlists.FirstOrDefault();
        }
        OnPropertyChanged(nameof(FileSummary));
        await ReadStatusAsync();
        await PollManualSessionAsync();
        await ReadInterruptedOperationAsync();
        if (!HasManualSession && !HasInterruptedOperation && CurrentState != ServiceState.Unknown) await CheckInstallReadinessAsync();
    }
    private bool CanApply() => !IsBusy && manualObservationKnown && !HasManualSession && !HasInterruptedOperation && SelectedPreset != null && installReady;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        var succeeded = false;
        await PerformAsync(async () =>
        {
            installReady = false;
            ActionReadiness = "Замена автозапуска… При ошибке оболочка попытается вернуть прежнюю службу. Дождитесь результата.";
            var result = await ExecuteObservedActionAsync(new(Root, FlowsealAction.InstallService, SelectedPreset));
            succeeded = result?.Outcome == FlowsealActionOutcome.Success && CurrentState != ServiceState.Unknown && !HasInterruptedOperation && !HasError;
            if (succeeded) Notifications.Enqueue("Автозапуск установлен. Работа службы не подтверждает доступность видео или голоса.");
        });
        if (succeeded && CloseAfterApply && !HasUnsavedChanges) CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private Task RemoveAsync() => PerformAsync(() => ExecuteServiceControlAsync(FlowsealAction.RemoveService));

    [RelayCommand(CanExecute = nameof(CanStartService))]
    private Task StartServiceAsync() => PerformAsync(() => ExecuteServiceControlAsync(FlowsealAction.StartService));

    [RelayCommand(CanExecute = nameof(CanStopService))]
    private Task StopServiceAsync() => PerformAsync(() => ExecuteServiceControlAsync(FlowsealAction.StopService));

    private bool CanCheckAction() => !IsBusy && !HasManualSession && !HasInterruptedOperation && SelectedPreset != null;
    [RelayCommand(CanExecute = nameof(CanCheckAction))]
    private Task CheckActionAsync() => PerformAsync(CheckInstallReadinessAsync);

    private async Task CheckInstallReadinessAsync()
    {
        installReady = false;
        if (SelectedPreset == null) { ActionReadiness = "Сначала выберите стратегию из поставки."; return; }
        await ReadStatusAsync();
        if (CurrentState == ServiceState.Unknown) { ActionReadiness = "Состояние службы неизвестно. Обновите состояние перед проверкой действия."; return; }
        FlowsealActionResult result;
        try { result = await adapter.PreflightAsync(new(Root, FlowsealAction.InstallService, SelectedPreset)); }
        catch (Exception error)
        {
            result = new(FlowsealActionOutcome.InvalidRequest, observedService,
                "Предварительная проверка не завершена. Проверьте доступ к поставке и права управления службой; обновите состояние.", ApplyError: error.ToString());
        }
        ShowActionResult(result);
        installReady = result.Outcome == FlowsealActionOutcome.Ready;
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private async Task ExecuteServiceControlAsync(FlowsealAction action)
    {
        installReady = false;
        ApplyCommand.NotifyCanExecuteChanged();
        ActionReadiness = ActionName(action) + "… Дождитесь подтверждения состояния Windows.";
        await ExecuteObservedActionAsync(new(Root, action));
    }

    private bool CanRecoverInterrupted() => !IsBusy && manualObservationKnown && !HasManualSession && HasInterruptedOperation && interruptedOperation.CanRecover;
    private bool CanOpenRecoveryDistribution() => !IsBusy && !HasManualSession && HasInterruptedOperation
        && interruptedOperation.Root != null && !Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar)
            .Equals(Path.GetFullPath(interruptedOperation.Root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private async Task ReadInterruptedOperationAsync()
    {
        try
        {
            interruptedOperation = await adapter.ReadInterruptedOperationAsync(Root);
            recoveryDetails = interruptedOperation.TechnicalDetails ?? "";
        }
        catch (Exception error)
        {
            recoveryDetails = error.ToString();
            interruptedOperation = new(true, false, Explanation: "Журнал восстановления прочитать не удалось. Изменения заблокированы. Проверьте права доступа и обновите состояние.");
            NotifyObservedState();
        }
        HasInterruptedOperation = interruptedOperation.Pending;
        InterruptedOperationText = interruptedOperation.Explanation;
        OnPropertyChanged(nameof(TechnicalDetails));
        if (HasInterruptedOperation) installReady = false;
        NotifyManualControls();
    }

    [RelayCommand(CanExecute = nameof(CanRecoverInterrupted))]
    private Task RecoverInterruptedAsync() => PerformAsync(async () =>
    {
        installReady = false;
        ActionReadiness = "Проверка сохранённой операции и возврат прежнего состояния…";
        await ExecuteObservedActionAsync(new(Root, FlowsealAction.RecoverInterrupted));
    });

    [RelayCommand(CanExecute = nameof(CanOpenRecoveryDistribution))]
    private Task OpenRecoveryDistributionAsync() => SelectDistributionAsync(interruptedOperation.Root!);

    [RelayCommand(CanExecute = nameof(CanStartManual))]
    private Task StartManualAsync() => PerformAsync(async () =>
    {
        installReady = false;
        ActionReadiness = "Подготовка пробного сеанса… Дождитесь наблюдения собственного winws.";
        await ExecuteObservedActionAsync(new(Root, FlowsealAction.ManualStart, SelectedPreset));
        await PollManualSessionAsync();
    });

    [RelayCommand(CanExecute = nameof(CanStopManual))]
    private Task StopManualAsync() => PerformAsync(async () =>
    {
        ActionReadiness = "Завершение пробного сеанса и возврат службы… Дождитесь результата.";
        await ExecuteObservedActionAsync(new(Root, FlowsealAction.ManualStop));
        await PollManualSessionAsync();
    });

    public async Task PollManualSessionAsync()
    {
        ManualSessionSnapshot snapshot;
        try { snapshot = await adapter.ReadManualSessionAsync(); }
        catch (Exception error)
        {
            manualObservationKnown = false;
            lastManualSnapshot = null;
            ManualProcessText = "";
            operationDetails = error.ToString();
            installReady = false;
            ManualStatusText = "Состояние пробного процесса неизвестно. Обновите состояние; перед новым запуском убедитесь, что прежний процесс завершён.";
            Error = ManualStatusText;
            NotifyObservedState();
            NotifyManualControls();
            return;
        }
        manualObservationKnown = true;
        NotifyManualControls();
        if (ReferenceEquals(snapshot, lastManualSnapshot)) return;
        lastManualSnapshot = snapshot;
        HasManualSession = snapshot.IsActive;
        ManualStatusText = snapshot.Explanation;
        ManualProcessText = snapshot.Process is { } process
            ? $"{snapshot.Strategy} · PID {process.Id} · начало: {(process.StartedAtUtcTicks is { } ticks ? new DateTime(ticks, DateTimeKind.Utc).ToLocalTime().ToString("G") : "неизвестно")}\n{process.Executable}" : "";
        NotifyObservedState();
        if (snapshot.Result != null)
        {
            ShowActionResult(snapshot.Result);
            await ReadStatusAsync();
            await ReadInterruptedOperationAsync();
        }
    }

    public async Task<bool> PrepareToCloseAsync()
    {
        if (IsBusy) return false;
        await PollManualSessionAsync();
        if (!HasManualSession) return true;
        var canClose = false;
        await PerformAsync(async () =>
        {
            var result = await ExecuteObservedActionAsync(new(Root, FlowsealAction.ManualStop));
            await PollManualSessionAsync();
            canClose = result?.Outcome == FlowsealActionOutcome.Success && !HasManualSession;
        });
        return canClose;
    }

    private void ShowActionResult(FlowsealActionResult result)
    {
        ShowObservedState(result.State);
        operationDetails = string.Join("\n\n", new[] { result.Outcome.ToString(), result.Explanation, result.ApplyError,
            result.RecoveryExplanation, result.NextStep }.Where(text => !string.IsNullOrWhiteSpace(text)).Distinct());
        var explanation = result.Explanation;
        var recovery = result.Recovery switch
        {
            FlowsealRecoveryOutcome.Restored => "Восстановление: прежнее состояние службы возвращено.",
            FlowsealRecoveryOutcome.Failed => "Восстановление не подтверждено. Проверьте службу и сохранённую операцию перед повтором.",
            FlowsealRecoveryOutcome.Conflict => "Восстановление остановлено: обнаружено стороннее изменение. Сравните текущее состояние с сохранённым; не перезаписывайте внешние изменения.",
            _ => "Восстановление не требовалось."
        };
        ActionReadiness = string.Join("\n\n", new[] { explanation, recovery,
            result.NextStep ?? (result.Outcome == FlowsealActionOutcome.Ready
                ? "Проверка завершена. Можно выполнить выбранное действие; перед изменением состояние будет проверено повторно."
                : result.Outcome == FlowsealActionOutcome.Success
                ? "Проверьте нужные функции сервисов: работа службы не подтверждает доступность видео или голоса."
                : "Обновите состояние, проверьте поставку и права доступа перед повторной попыткой.") });
        Error = result.Outcome is FlowsealActionOutcome.Success or FlowsealActionOutcome.Ready ? "" : string.Join("\n\n",
            new[] { explanation, recovery, result.NextStep }.Where(text => !string.IsNullOrWhiteSpace(text)));
        NotifyObservedState();
    }

}

public sealed record HostlistItem(string Path, string Name);
