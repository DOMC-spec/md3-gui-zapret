using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zapret.Core;
using Zapret.Gui.Services;

namespace Zapret.Gui.ViewModels;

public partial class MainViewModel
{
    private GameFilterSnapshot? gameFilterSnapshot;
    [ObservableProperty] private string gameMode = "disabled";
    [ObservableProperty] private string gameTcpRange = "1024-65535";
    [ObservableProperty] private string gameUdpRange = "1024-65535";
    [ObservableProperty] private string gameFilterMessage = "Нажмите «Прочитать настройки»: будет вызван проверенный helper Flowseal в отдельной временной папке.";
    [ObservableProperty] private string gameFilterObserved = "Настройки из файла ещё не прочитаны.";
    public IReadOnlyList<GameModeChoice> GameModes { get; } = [new("disabled", "Отключён"), new("all", "TCP и UDP"), new("tcp", "Только TCP"), new("udp", "Только UDP")];
    private GameFilterSettings GameDraft => new(GameMode, GameTcpRange, GameUdpRange);
    public bool IsGameFilterDirty => gameFilterSnapshot != null && GameDraft != gameFilterSnapshot.Settings;
    public bool HasUnsavedChanges => IsDirty || IsGameFilterDirty;
    public bool CanEditGameFilter => CanLoadGameFilter() && gameFilterSnapshot != null;
    private bool CanLoadGameFilter() => !IsBusy && !HasManualSession && !HasInterruptedOperation && Allows(FlowsealAction.ChangeGameFilter);
    private bool CanSaveGameFilter() => CanEditGameFilter && IsGameFilterDirty;

    partial void OnGameModeChanged(string value) => NotifyGameFilter();
    partial void OnGameTcpRangeChanged(string value) => NotifyGameFilter();
    partial void OnGameUdpRangeChanged(string value) => NotifyGameFilter();
    private void NotifyGameFilter()
    {
        OnPropertyChanged(nameof(CanEditGameFilter));
        OnPropertyChanged(nameof(IsGameFilterDirty));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        LoadGameFilterCommand.NotifyCanExecuteChanged();
        SaveGameFilterCommand.NotifyCanExecuteChanged();
        DiscardGameFilterCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanLoadGameFilter))]
    private Task LoadGameFilterAsync() => PerformAsync(async () =>
    {
        var keepDraft = IsGameFilterDirty;
        var snapshot = await adapter.ReadGameFilterAsync(Root);
        ShowGameFilterSnapshot(snapshot, !keepDraft);
        GameFilterMessage = keepDraft
            ? "Файл перечитан для сравнения. Ваш ввод сохранён; сохраните его поверх показанных настроек или нажмите «Отменить правки фильтра»."
            : "Настройки прочитаны через Flowseal. Их сохранение не меняет работающий процесс.";
    });

    [RelayCommand(CanExecute = nameof(CanSaveGameFilter))]
    private Task SaveGameFilterAsync() => PerformAsync(async () =>
    {
        var saved = await adapter.SaveGameFilterAsync(Root, GameDraft, gameFilterSnapshot!.Revision);
        ShowGameFilterSnapshot(saved, true);
        installReady = false;
        ApplyCommand.NotifyCanExecuteChanged();
        GameFilterMessage = "Настройки сохранены и перечитаны Flowseal. Работающий процесс не изменён. Для применения завершите пробный сеанс и запустите стратегию заново либо переустановите автозапуск кнопкой «Включить при загрузке Windows». Обычный перезапуск службы сохраняет прежние порты.";
    });

    [RelayCommand(CanExecute = nameof(CanSaveGameFilter))]
    private void DiscardGameFilter()
    {
        if (gameFilterSnapshot != null) ShowGameFilterSnapshot(gameFilterSnapshot, true);
        GameFilterMessage = "Правки отменены. Показаны последние прочитанные настройки файла.";
    }

    private void ShowGameFilterSnapshot(GameFilterSnapshot snapshot, bool updateDraft)
    {
        gameFilterSnapshot = snapshot;
        GameFilterObserved = $"Последнее чтение файла: {GameModes.First(m => m.Value == snapshot.Settings.Mode).Name}; TCP: {snapshot.Settings.TcpRange}; UDP: {snapshot.Settings.UdpRange}.";
        if (updateDraft)
        {
            GameMode = snapshot.Settings.Mode;
            GameTcpRange = snapshot.Settings.TcpRange;
            GameUdpRange = snapshot.Settings.UdpRange;
        }
        NotifyGameFilter();
    }

    private void ResetGameFilter()
    {
        gameFilterSnapshot = null;
        GameMode = "disabled";
        GameTcpRange = GameUdpRange = "1024-65535";
        GameFilterObserved = "Настройки из файла ещё не прочитаны.";
        GameFilterMessage = "Нажмите «Прочитать настройки»: будет вызван проверенный helper Flowseal в отдельной временной папке.";
        NotifyGameFilter();
    }
}

public sealed record GameModeChoice(string Value, string Name);
