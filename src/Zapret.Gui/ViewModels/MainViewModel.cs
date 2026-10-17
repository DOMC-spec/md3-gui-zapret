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
    private HostlistStore Store => new(Root);
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
        FlowsealAction.StartService => "Запуск службы", FlowsealAction.StopService => "Остановка службы", FlowsealAction.RemoveService => "Удаление автозапуска",
        FlowsealAction.ChangeGameFilter => "Game Filter", FlowsealAction.EditLists => "Изменение списков", FlowsealAction.Tests => "Тесты Flowseal",
        FlowsealAction.ChangeIpSet => "IPSet Filter", FlowsealAction.UpdateIpSet => "Обновление IPSet", FlowsealAction.UpdateHosts => "Обновление hosts", FlowsealAction.Diagnostics => "Диагностика",
        FlowsealAction.CheckUpdates => "Проверка обновлений", FlowsealAction.ChangeUpdateChecks => "Настройка проверки обновлений", _ => "Замена активных fake-файлов"
    };
    private bool Allows(FlowsealAction action) => Distribution?.Capabilities.Any(c => c.Action == action && c.Available) == true;
    private string Unavailable(FlowsealAction action) => Distribution?.Capabilities.FirstOrDefault(c => c.Action == action)?.Reason ?? "Сначала выберите поставку Flowseal.";
    public bool CanEditLists => !IsBusy && !HasManualSession && Allows(FlowsealAction.EditLists);
    private bool CanControlService(FlowsealAction action) => !IsBusy && !HasManualSession && Allows(action)
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
    private bool CanStartManual() => !IsBusy && !HasManualSession && SelectedPreset != null && Allows(FlowsealAction.ManualStart);
    private bool CanStopManual() => !IsBusy && HasManualSession;
    private void NotifyManualControls()
    {
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
    }
    partial void OnHasManualSessionChanged(bool value) => NotifyManualControls();
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
    public bool IsDirty => loadedPath != null && HostlistText != originalText;
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
        installReady = false;
        ApplyCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(StrategyReadiness));
        ActionReadiness = "Стратегия выбрана. Нажмите «Проверить действие», чтобы проверить возможность замены автозапуска.";
        CheckActionCommand.NotifyCanExecuteChanged();
        StartManualCommand.NotifyCanExecuteChanged();
    }
    partial void OnHostlistTextChanged(string value) => OnPropertyChanged(nameof(IsDirty));
    partial void OnIsDarkChanged(bool value) => M3Theme.Apply(value);

    public async Task InitializeAsync() => await RefreshAsync();

    private async Task PerformAsync(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        Error = "";
        try { await action(); }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsBusy = false; }
    }

    private async Task ReadStatusAsync()
    {
        ServiceSnapshot snapshot;
        try { snapshot = await adapter.ReadStateAsync(Root); }
        catch (Exception ex)
        {
            snapshot = new ServiceSnapshot(ServiceState.Unknown, null);
            Error += (Error.Length == 0 ? "" : "\n") + "Состояние службы прочитать не удалось. " + ex.Message;
        }
        ShowObservedState(snapshot);
    }

    private void ShowObservedState(ServiceSnapshot snapshot)
    {
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
    }

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private Task RefreshAsync() => PerformAsync(LoadDistributionAsync);

    [RelayCommand(CanExecute = nameof(CanChooseDistribution))]
    private Task SelectDistributionAsync(string path) => PerformAsync(async () =>
    {
        if (HasManualSession) throw new IOException("Сначала завершите свой пробный сеанс перед сменой поставки.");
        if (IsDirty) throw new IOException("Сохраните или отмените изменения списка перед выбором другой поставки.");
        Distribution = null;
        Presets.Clear();
        Hostlists.Clear();
        Root = path;
        loadedPath = null;
        originalText = "";
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
        if (!IsDirty)
        {
            var path = SelectedHostlist?.Path;
            IReadOnlyList<string> lists = [];
            try { if (Directory.Exists(Root)) lists = await Task.Run(Store.Discover); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Error = "Списки прочитать не удалось. " + ex.Message; }
            Hostlists.Clear();
            foreach (var file in lists) Hostlists.Add(new HostlistItem(file, System.IO.Path.GetRelativePath(Root, file)));
            SelectedHostlist = Hostlists.FirstOrDefault(p => p.Path == path) ?? Hostlists.FirstOrDefault();
        }
        OnPropertyChanged(nameof(FileSummary));
        await ReadStatusAsync();
        await PollManualSessionAsync();
        if (!HasManualSession) await CheckInstallReadinessAsync();
    }
    private bool CanApply() => !IsBusy && !HasManualSession && SelectedPreset != null && installReady;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        var succeeded = false;
        await PerformAsync(async () =>
        {
            installReady = false;
            ActionReadiness = "Замена автозапуска… При ошибке оболочка попытается вернуть прежнюю службу. Дождитесь результата.";
            var result = await adapter.ExecuteAsync(new(Root, FlowsealAction.InstallService, SelectedPreset));
            ShowActionResult(result);
            succeeded = result.Outcome == FlowsealActionOutcome.Success;
            if (succeeded) Notifications.Enqueue("Автозапуск установлен. Работа службы не подтверждает доступность видео или голоса.");
        });
        if (succeeded && CloseAfterApply && !IsDirty) CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private Task RemoveAsync() => PerformAsync(() => ExecuteServiceControlAsync(FlowsealAction.RemoveService));

    [RelayCommand(CanExecute = nameof(CanStartService))]
    private Task StartServiceAsync() => PerformAsync(() => ExecuteServiceControlAsync(FlowsealAction.StartService));

    [RelayCommand(CanExecute = nameof(CanStopService))]
    private Task StopServiceAsync() => PerformAsync(() => ExecuteServiceControlAsync(FlowsealAction.StopService));

    private bool CanCheckAction() => !IsBusy && !HasManualSession && SelectedPreset != null;
    [RelayCommand(CanExecute = nameof(CanCheckAction))]
    private Task CheckActionAsync() => PerformAsync(CheckInstallReadinessAsync);

    private async Task CheckInstallReadinessAsync()
    {
        installReady = false;
        if (SelectedPreset == null) { ActionReadiness = "Сначала выберите стратегию из поставки."; return; }
        var result = await adapter.PreflightAsync(new(Root, FlowsealAction.InstallService, SelectedPreset));
        installReady = result.Outcome == FlowsealActionOutcome.Ready;
        ApplyCommand.NotifyCanExecuteChanged();
        ShowObservedState(result.State);
        ActionReadiness = result.Explanation == result.State.OwnershipReason ? "Изменение службы недоступно." : result.Explanation;
    }

    private async Task ExecuteServiceControlAsync(FlowsealAction action)
    {
        installReady = false;
        ApplyCommand.NotifyCanExecuteChanged();
        ActionReadiness = ActionName(action) + "… Дождитесь подтверждения состояния Windows.";
        var result = await adapter.ExecuteAsync(new(Root, action));
        ShowActionResult(result);
    }

    [RelayCommand(CanExecute = nameof(CanStartManual))]
    private Task StartManualAsync() => PerformAsync(async () =>
    {
        installReady = false;
        ActionReadiness = "Подготовка пробного сеанса… Дождитесь наблюдения собственного winws.";
        var result = await adapter.ExecuteAsync(new(Root, FlowsealAction.ManualStart, SelectedPreset));
        ShowActionResult(result);
        await PollManualSessionAsync();
    });

    [RelayCommand(CanExecute = nameof(CanStopManual))]
    private Task StopManualAsync() => PerformAsync(async () =>
    {
        var result = await adapter.ExecuteAsync(new(Root, FlowsealAction.ManualStop));
        ShowActionResult(result);
        await PollManualSessionAsync();
    });

    public async Task PollManualSessionAsync()
    {
        var snapshot = await adapter.ReadManualSessionAsync();
        if (ReferenceEquals(snapshot, lastManualSnapshot)) return;
        lastManualSnapshot = snapshot;
        HasManualSession = snapshot.IsActive;
        ManualStatusText = snapshot.Explanation;
        ManualProcessText = snapshot.Process is { } process
            ? $"{snapshot.Strategy} · PID {process.Id} · создан {new DateTime(process.StartedAtUtcTicks!.Value, DateTimeKind.Utc).ToLocalTime():G}\n{process.Executable}" : "";
        if (snapshot.Result != null) ShowActionResult(snapshot.Result);
    }

    public async Task<bool> PrepareToCloseAsync()
    {
        if (IsBusy) return false;
        await PollManualSessionAsync();
        if (!HasManualSession) return true;
        var canClose = false;
        await PerformAsync(async () =>
        {
            var result = await adapter.ExecuteAsync(new(Root, FlowsealAction.ManualStop));
            ShowActionResult(result);
            await PollManualSessionAsync();
            canClose = result.Outcome == FlowsealActionOutcome.Success;
        });
        return canClose;
    }

    private void ShowActionResult(FlowsealActionResult result)
    {
        ShowObservedState(result.State);
        ActionReadiness = string.Join("\n\n", new[] { result.Explanation,
            result.RecoveryExplanation, result.NextStep }.Where(text => !string.IsNullOrWhiteSpace(text)));
        Error = result.Outcome == FlowsealActionOutcome.Success ? "" : string.Join("\n\n",
            new[] { result.ApplyError, result.Explanation, result.RecoveryExplanation, result.NextStep }
                .Where(text => !string.IsNullOrWhiteSpace(text)).Distinct());
    }
    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task LoadHostlistAsync() => await PerformAsync(async () =>
    {
        if (SelectedHostlist == null) throw new IOException("Сначала выберите список.");
        if (IsDirty) throw new IOException("Сначала сохраните текущий список или отмените изменения.");
        var text = await Store.ReadAsync(SelectedHostlist.Path);
        loadedPath = SelectedHostlist.Path;
        originalText = text;
        HostlistText = text;
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(HasLoadedHostlist));
        OnPropertyChanged(nameof(LoadedFileName));
    });

    [RelayCommand(CanExecute = nameof(CanEditLists))]
    private async Task SaveHostlistAsync() => await PerformAsync(async () =>
    {
        if (!Allows(FlowsealAction.EditLists)) throw new IOException(Unavailable(FlowsealAction.EditLists));
        if (loadedPath == null) throw new IOException("Откройте список для редактирования.");
        await Store.SaveAsync(loadedPath, HostlistText, originalText);
        originalText = HostlistText;
        OnPropertyChanged(nameof(IsDirty));
        Notifications.Enqueue("Список сохранён. Для работающей службы примените стратегию заново.");
    });

    [RelayCommand(CanExecute = nameof(CanEditLists))]
    private async Task AddDomainAsync() => await PerformAsync(async () =>
    {
        if (!Allows(FlowsealAction.EditLists)) throw new IOException(Unavailable(FlowsealAction.EditLists));
        if (loadedPath == null) throw new IOException("Откройте список для редактирования.");
        var updated = HostlistStore.AddDomain(HostlistText, Domain);
        await Store.SaveAsync(loadedPath, updated, originalText);
        originalText = updated;
        HostlistText = updated;
        Domain = "";
        OnPropertyChanged(nameof(IsDirty));
        Notifications.Enqueue("Домен добавлен, список сохранён");
    });

    [RelayCommand]
    private void DiscardChanges()
    {
        HostlistText = originalText;
        OnPropertyChanged(nameof(IsDirty));
    }
}

public sealed record HostlistItem(string Path, string Name);
