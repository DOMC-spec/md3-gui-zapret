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
    private readonly ZapretService service = new(new ProcessRunner());
    public string Root { get; } = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    public ObservableCollection<Preset> Presets { get; } = [];
    public ObservableCollection<HostlistItem> Hostlists { get; } = [];
    public SnackbarMessageQueue Notifications { get; } = new(TimeSpan.FromSeconds(3));
    private HostlistStore Store => new(Root);
    private string originalText = "";
    private string? loadedPath;

    [ObservableProperty] private Preset? selectedPreset;
    [ObservableProperty] private string statusText = "Проверка…";
    [ObservableProperty] private string activePreset = "—";
    [ObservableProperty] private ServiceState currentState = ServiceState.Missing;
    [ObservableProperty] private string error = "";
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool closeAfterApply = true;
    [ObservableProperty] private HostlistItem? selectedHostlist;
    [ObservableProperty] private string hostlistText = "";
    [ObservableProperty] private string domain = "";
    [ObservableProperty] private bool isDark;

    public bool CanInteract => !IsBusy;
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public bool IsDirty => loadedPath != null && HostlistText != originalText;
    public bool HasLoadedHostlist => loadedPath != null;
    public string LoadedFileName => loadedPath == null ? "Список не открыт" : System.IO.Path.GetRelativePath(Root, loadedPath);
    public string FileSummary => $"Найдено стратегий: {Presets.Count} · списков: {Hostlists.Count}";

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInteract));
        ApplyCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
        LoadHostlistCommand.NotifyCanExecuteChanged();
        SaveHostlistCommand.NotifyCanExecuteChanged();
        AddDomainCommand.NotifyCanExecuteChanged();
    }
    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnSelectedPresetChanged(Preset? value) => ApplyCommand.NotifyCanExecuteChanged();
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
        var snapshot = await service.ReadAsync();
        StatusText = snapshot.State switch
        {
            ServiceState.Missing => "Не установлена", ServiceState.Running => "Работает",
            ServiceState.Stopped => "Остановлена", ServiceState.Starting => "Запускается…",
            ServiceState.Stopping => "Останавливается…", ServiceState.Paused => "Приостановлена", _ => "Неизвестно"
        };
        CurrentState = snapshot.State;
        ActivePreset = snapshot.Preset ?? (snapshot.State == ServiceState.Missing ? "Пресет ещё не применён" : "Пресет не указан");
    }

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task RefreshAsync() => await PerformAsync(async () =>
    {
        var name = SelectedPreset?.Name;
        var presets = await Task.Run(() => PresetParser.Discover(Root));
        Presets.Clear();
        foreach (var preset in presets) Presets.Add(preset);
        SelectedPreset = Presets.FirstOrDefault(p => p.Name == name) ?? Presets.FirstOrDefault();
        if (!IsDirty)
        {
            var path = SelectedHostlist?.Path;
            var lists = await Task.Run(Store.Discover);
            Hostlists.Clear();
            foreach (var file in lists) Hostlists.Add(new HostlistItem(file, System.IO.Path.GetRelativePath(Root, file)));
            SelectedHostlist = Hostlists.FirstOrDefault(p => p.Path == path) ?? Hostlists.FirstOrDefault();
        }
        OnPropertyChanged(nameof(FileSummary));
        await ReadStatusAsync();
        if (!File.Exists(System.IO.Path.Combine(Root, "bin", "winws.exe")))
            Error = "Не найден bin\\winws.exe. Поместите EXE в папку распакованного zapret рядом с service.bat.";
        else if (Presets.Count == 0) Error = "В папке программы не найдены BAT-пресеты с запуском winws.exe.";
    });

    private bool CanApply() => !IsBusy && SelectedPreset != null;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        var success = false;
        await PerformAsync(async () =>
        {
            if (IsDirty) throw new IOException("Сохраните изменения списка перед применением стратегии.");
            try { await service.ApplyAsync(Root, SelectedPreset!); success = true; }
            finally { await ReadStatusAsync(); }
            Notifications.Enqueue("Служба zapret запущена");
        });
        if (success && CloseAfterApply)
        {
            IsBusy = true;
            await Task.Delay(1800);
            IsBusy = false;
            Application.Current.Shutdown();
        }
    }

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task RemoveAsync() => await PerformAsync(async () =>
    {
        try { await service.RemoveAsync(); }
        finally { await ReadStatusAsync(); }
        Notifications.Enqueue("Служба удалена, драйвер WinDivert остановлен");
    });

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

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task SaveHostlistAsync() => await PerformAsync(async () =>
    {
        if (loadedPath == null) throw new IOException("Откройте список для редактирования.");
        await Store.SaveAsync(loadedPath, HostlistText, originalText);
        originalText = HostlistText;
        OnPropertyChanged(nameof(IsDirty));
        Notifications.Enqueue("Список сохранён. Для работающей службы примените стратегию заново.");
    });

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task AddDomainAsync() => await PerformAsync(async () =>
    {
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
