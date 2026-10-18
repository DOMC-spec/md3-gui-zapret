using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using Zapret.Gui.ViewModels;

namespace Zapret.Gui;

public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel = new();
    private MainViewModel Model => (MainViewModel)DataContext;
    private readonly System.Windows.Threading.DispatcherTimer observationTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool refreshingObservedState;
    private bool initialized;
    private string lastPresentedError = "";
    public MainWindow()
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
        observationTimer.Tick += async (_, _) =>
        {
            if (refreshingObservedState || Model.IsBusy) return;
            refreshingObservedState = true;
            try { await Model.RefreshObservedStateAsync(); }
            catch (Exception error) { Model.Error = "Не удалось обновить пробный сеанс: " + error.Message; }
            finally { refreshingObservedState = false; }
        };
        Closed += (_, _) => observationTimer.Stop();
    }
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await Model.InitializeAsync();
        initialized = true;
        observationTimer.Start();
    }
    private async void OnActivated(object? sender, EventArgs e)
    {
        if (initialized) await Model.RefreshObservedStateAsync();
    }
    private void OnErrorTextChanged(object sender, TextChangedEventArgs e) => PresentError((TextBox)sender);
    private void OnErrorVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => PresentError((TextBox)sender);
    private void PresentError(TextBox text)
    {
        if (!text.IsVisible || text.Text.Length == 0 || text.Text == lastPresentedError) return;
        lastPresentedError = text.Text;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!text.IsVisible) return;
            text.BringIntoView();
            UIElementAutomationPeer.CreatePeerForElement(text)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }));
    }
    private async void OnChooseDistribution(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Выберите распакованную папку Flowseal с service.bat",
            InitialDirectory = Directory.Exists(Model.Root) ? Model.Root : AppContext.BaseDirectory,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true) await Model.SelectDistributionCommand.ExecuteAsync(dialog.FolderName);
    }
    private async void OnExportList(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Сохранить черновик в новый файл вне поставки",
            Filter = "Текстовый файл (*.txt)|*.txt", FileName = "my-list-draft.txt"
        };
        if (dialog.ShowDialog(this) == true) await Model.ExportHostlistCommand.ExecuteAsync(dialog.FileName);
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (Model.IsBusy) { e.Cancel = true; return; }
        if (Model.HasUnsavedChanges && MessageBox.Show(this, "Закрыть окно без сохранения изменений списка или Game Filter?", "Несохранённые изменения",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            e.Cancel = true;
        if (e.Cancel) return;
        if (Model.HasManualSession)
        {
            e.Cancel = true;
            try
            {
                if (await Model.PrepareToCloseAsync()) _ = Dispatcher.BeginInvoke(new Action(Close));
            }
            catch (Exception error) { Model.Error = "Пробный сеанс не удалось завершить: " + error.Message; }
            return;
        }
        Model.Notifications.Dispose();
    }
}
