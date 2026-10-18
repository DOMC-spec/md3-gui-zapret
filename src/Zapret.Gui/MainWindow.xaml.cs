using System.ComponentModel;
using System.Windows;
using Zapret.Gui.ViewModels;

namespace Zapret.Gui;

public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel = new();
    private MainViewModel Model => (MainViewModel)DataContext;
    private readonly System.Windows.Threading.DispatcherTimer manualTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool pollingManual;
    public MainWindow()
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
        manualTimer.Tick += async (_, _) =>
        {
            if (pollingManual || Model.IsBusy) return;
            pollingManual = true;
            try { await Model.PollManualSessionAsync(); }
            catch (Exception error) { Model.Error = "Не удалось обновить пробный сеанс: " + error.Message; }
            finally { pollingManual = false; }
        };
        Closed += (_, _) => manualTimer.Stop();
    }
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await Model.InitializeAsync();
        manualTimer.Start();
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
