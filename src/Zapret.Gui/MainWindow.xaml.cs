using System.ComponentModel;
using System.Windows;
using Zapret.Gui.ViewModels;

namespace Zapret.Gui;

public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel = new();
    public MainWindow()
    {
        InitializeComponent();
        DataContext = viewModel;
    }
    private async void OnLoaded(object sender, RoutedEventArgs e) => await viewModel.InitializeAsync();
    private async void OnChooseDistribution(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Выберите распакованную папку Flowseal с service.bat",
            InitialDirectory = Directory.Exists(viewModel.Root) ? viewModel.Root : AppContext.BaseDirectory,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true) await viewModel.SelectDistributionCommand.ExecuteAsync(dialog.FolderName);
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (viewModel.IsBusy) { e.Cancel = true; return; }
        if (viewModel.IsDirty && MessageBox.Show(this, "Закрыть окно без сохранения изменений списка?", "Несохранённые изменения",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            e.Cancel = true;
        if (!e.Cancel) viewModel.Notifications.Dispose();
    }
}
