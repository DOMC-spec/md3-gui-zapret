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
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (viewModel.IsBusy) { e.Cancel = true; return; }
        if (viewModel.IsDirty && MessageBox.Show(this, "Закрыть окно без сохранения изменений списка?", "Несохранённые изменения",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            e.Cancel = true;
        if (!e.Cancel) viewModel.Notifications.Dispose();
    }
}
