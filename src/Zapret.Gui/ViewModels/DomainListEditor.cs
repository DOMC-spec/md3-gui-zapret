using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zapret.Core;
using Zapret.Gui.Services;

namespace Zapret.Gui.ViewModels;

public partial class MainViewModel
{
    private HostlistSnapshot? listSnapshot;
    private IReadOnlyList<DomainListDescription> domainLists = [];
    [ObservableProperty] private string listMessage = "Откройте список и добавьте домен без https:// и пути.";
    [ObservableProperty] private string observedListText = "";
    [ObservableProperty] private bool hasListComparison;
    private DomainListDescription? LoadedList => domainLists.FirstOrDefault(list => list.Path == (loadedPath ?? SelectedHostlist?.Path));
    public string ListPurpose => LoadedList?.Purpose ?? "Выберите список для сайтов или исключений.";
    public string ListTargetName => loadedPath == null ? "Список ещё не открыт" : LoadedList?.Name ?? "Открытый список";
    public string ListUsage => LoadedList?.UsedByStrategy switch
    {
        true => $"Список используется выбранной стратегией «{SelectedPreset?.Name}» при новом запуске. Это не подтверждает конфигурацию работающего процесса.",
        false => $"Список не используется выбранной стратегией «{SelectedPreset?.Name}». Его правка не изменит её обработку сайтов.",
        _ => "Связь со стратегией неизвестна: выберите исходную проверенную стратегию. Сохранение списка не подтверждает изменение работающего обхода."
    };

    private async Task RefreshDomainListsAsync()
    {
        var requestedRoot = Root;
        var strategy = SelectedPreset;
        try
        {
            var lists = await adapter.ReadDomainListsAsync(requestedRoot, strategy);
            if (Root != requestedRoot || SelectedPreset != strategy) return;
            domainLists = lists;
            NotifyListDescription();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { ListMessage = "Назначения списков прочитать не удалось: " + error.Message; }
    }
    private void NotifyListDescription()
    {
        OnPropertyChanged(nameof(ListPurpose));
        OnPropertyChanged(nameof(ListUsage));
        OnPropertyChanged(nameof(ListTargetName));
    }
    partial void OnSelectedHostlistChanged(HostlistItem? value) => NotifyListDescription();
    partial void OnDomainChanged(string value) { OnPropertyChanged(nameof(IsDirty)); OnPropertyChanged(nameof(HasUnsavedChanges)); }

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private Task LoadHostlistAsync() => PerformAsync(async () =>
    {
        if (SelectedHostlist == null) throw new IOException("Сначала выберите список.");
        if (IsDirty) throw new IOException("Сначала сохраните текущий список, сохраните черновик отдельно или отмените изменения.");
        ShowList(await adapter.ReadDomainListAsync(Root, SelectedHostlist.Path));
    });

    private void ShowList(HostlistSnapshot snapshot)
    {
        listSnapshot = snapshot;
        loadedPath = snapshot.Path;
        originalText = snapshot.Text;
        HostlistText = snapshot.Text;
        Domain = "";
        HasListComparison = false;
        ObservedListText = "";
        ListMessage = snapshot.Revision == "missing"
            ? "Пользовательского файла ещё нет. При первом сохранении он будет создан со штатным содержимым Flowseal и вашим дополнением."
            : "Список открыт. Сохранение не перезапускает процессы.";
        NotifyLoadedList();
    }
    private void NotifyLoadedList()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(HasLoadedHostlist));
        OnPropertyChanged(nameof(LoadedFileName));
        NotifyListDescription();
    }

    [RelayCommand(CanExecute = nameof(CanEditLists))]
    private Task SaveHostlistAsync() => PerformAsync(SaveListDraftAsync);

    private async Task SaveListDraftAsync()
    {
        if (!Allows(FlowsealAction.EditLists)) throw new IOException(Unavailable(FlowsealAction.EditLists));
        if (listSnapshot == null) throw new IOException("Откройте список для редактирования.");
        if (Domain.Length != 0) HostlistText = HostlistStore.AddDomain(HostlistText, Domain);
        var snapshot = await adapter.SaveDomainListAsync(Root, listSnapshot, HostlistText);
        ShowList(snapshot);
        ListMessage = "Список сохранён с резервной копией существовавшего файла. Работающий процесс не изменён. Для применения запустите выбранную стратегию заново. " + ListUsage;
        installReady = false;
        ApplyCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanEditLists))]
    private Task AddDomainAsync() => PerformAsync(async () =>
    {
        if (listSnapshot == null) throw new IOException("Откройте список для добавления сайта или исключения.");
        HostlistText = HostlistStore.AddDomain(HostlistText, Domain);
        await SaveListDraftAsync();
    });

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private Task CompareHostlistAsync() => PerformAsync(async () =>
    {
        if (loadedPath == null) throw new IOException("Сначала откройте список.");
        var observed = await adapter.ReadDomainListAsync(Root, loadedPath);
        ObservedListText = observed.Text;
        HasListComparison = true;
        ListMessage = "Файл перечитан для сравнения. Ваш черновик и введённый домен сохранены. «Загрузить прочитанное» заменит черновик; для объединения сохраните свои правки отдельно и отредактируйте текст в расширенном режиме.";
        // Keep the original revision: rereading alone must never authorize overwriting an external edit.
        comparedList = observed;
    });
    private HostlistSnapshot? comparedList;

    [RelayCommand(CanExecute = nameof(CanEditLists))]
    private Task SaveComparedDraftAsync() => PerformAsync(async () =>
    {
        if (!HasListComparison || comparedList == null) throw new IOException("Сначала перечитайте файл для сравнения.");
        listSnapshot = comparedList;
        await SaveListDraftAsync();
    });

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private void UseComparedHostlist()
    {
        if (!HasListComparison || comparedList == null) return;
        ShowList(comparedList);
    }

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private Task ExportHostlistAsync(string path) => PerformAsync(async () =>
    {
        if (listSnapshot == null) throw new IOException("Сначала откройте список.");
        var text = HostlistText + (Domain.Length == 0 ? "" : "\r\n" + Domain + "\r\n");
        await adapter.ExportDomainListAsync(Root, path, text);
        ListMessage = "Черновик и введённый домен сохранены отдельно в UTF-8: " + path + ". Файл поставки и редактор не изменены.";
    });

    [RelayCommand]
    private void DiscardChanges()
    {
        if (listSnapshot != null) ShowList(listSnapshot);
        else { HostlistText = originalText; Domain = ""; NotifyLoadedList(); }
    }
}
