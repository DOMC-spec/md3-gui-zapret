using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Zapret.Gui;
using Zapret.Gui.ViewModels;
using Zapret.Gui.Theming;
using Zapret.Gui.Services;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var app = new App();
            app.InitializeComponent();
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(app.Dispatcher));
            M3Theme.Apply(false);
            if (args.Contains("--manual-presentation-only"))
            {
                ManualPresentationChecks.Run(Complete);
                app.Dispatcher.InvokeShutdown();
                return 0;
            }
            var bindingErrors = new BindingErrors();
            PresentationTraceSources.DataBindingSource.Listeners.Add(bindingErrors);
            CheckDistributionSelection();
            CheckFolderChangeResetsEditor();
            CheckServiceSafetyPresentation();
            ServiceRecoveryPresentationChecks.Run(Complete);
            ServiceControlPresentationChecks.Run(Complete);
            ManualPresentationChecks.Run(Complete);
            var window = new MainWindow();
            var selectedRoot = args.FirstOrDefault(a => a.StartsWith("--distribution="))?["--distribution=".Length..]
                ?? Path.Combine(AppContext.BaseDirectory, "fixtures", "flowseal-1.10.3");
            var snapshot = args.Contains("--owned")
                ? new ServiceSnapshot(ServiceState.Stopped, null, new ServiceConfiguration(Zapret.Core.CommandLine.Quote(Path.Combine(selectedRoot, "bin", "winws.exe"))))
                : args.Contains("--foreign")
                ? new ServiceSnapshot(ServiceState.Stopped, "external BAT", new ServiceConfiguration(Zapret.Core.CommandLine.Quote(Environment.ProcessPath!)))
                : args.Contains("--unknown") ? new ServiceSnapshot(ServiceState.Unknown, null)
                : new ServiceSnapshot(ServiceState.Missing, null);
            if (args.Contains("--running")) snapshot = new(ServiceState.Running, "general (ALT)",
                new ServiceConfiguration(Zapret.Core.CommandLine.Quote(Path.Combine(selectedRoot, "bin", "winws.exe"))),
                new ServiceProcess(19, Path.Combine(selectedRoot, "bin", "winws.exe"), 100));
            var viewModel = new MainViewModel(new FlowsealAdapter(new NoCommands(), () => Task.FromResult(snapshot)), selectedRoot);
            window.DataContext = viewModel;
            Complete(viewModel.InitializeAsync());
            if (args.Contains("--error")) viewModel.Error = "Не найден bin\\winws.exe. Поместите конфигуратор в корневую папку zapret.";
            if (args.Contains("--dark")) viewModel.IsDark = true;
            CheckContrast(app, "OnSurface", "Surface", 4.5);
            CheckContrast(app, "OnSurfaceVariant", "SurfaceContainerHighest", 4.5);
            CheckContrast(app, "OnPrimary", "Primary", 4.5);
            CheckContrast(app, "OnSecondaryContainer", "SecondaryContainer", 4.5);
            CheckContrast(app, "OnErrorContainer", "ErrorContainer", 4.5);
            var width = args.Contains("--small") ? 720 : 760;
            var height = args.Contains("--small") ? 682 : 722;
            var grid = (FrameworkElement)window.Content;
            grid.Measure(new Size(width, height));
            grid.Arrange(new Rect(0, 0, width, height));
            grid.UpdateLayout();
            // Build both tabs to catch deferred StaticResource and binding errors.
            var tabs = Find<TabControl>(grid) ?? throw new Exception("Tab control missing");
            tabs.SelectedIndex = 1;
            grid.UpdateLayout();
            tabs.SelectedIndex = 0;
            if (args.Contains("--lists")) tabs.SelectedIndex = 1;
            grid.UpdateLayout();
            if (args.Contains("--manual-view"))
            {
                var button = Find<Button>(grid, b => ReferenceEquals(b.Command, viewModel.StartManualCommand))
                    ?? throw new Exception("Manual trial button missing");
                for (DependencyObject? parent = button; parent != null; parent = VisualTreeHelper.GetParent(parent))
                    if (parent is ScrollViewer viewer) { viewer.ScrollToBottom(); break; }
                grid.UpdateLayout();
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle((Brush)app.FindResource("M3.Surface"), null, new Rect(0, 0, width, height));
                dc.DrawRectangle(new VisualBrush(grid), null, new Rect(grid.Margin.Left, grid.Margin.Top, grid.ActualWidth, grid.ActualHeight));
            }
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "artifacts/ui-light.png";
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using (var output = File.Create(path)) encoder.Save(output);
            if (bindingErrors.Errors.Count != 0) throw new Exception(string.Join("\n", bindingErrors.Errors));
            window.Close();
            app.Dispatcher.InvokeShutdown();
            Console.WriteLine("PASS WPF resources, both tabs, bindings, M3 contrast and layout: " + Path.GetFullPath(path));
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Complete(Task task)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        _ = task.ContinueWith(_ => Application.Current.Dispatcher.BeginInvoke(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static FlowsealAdapter ReadOnlyAdapter() => new(new ProcessRunner(),
        () => Task.FromException<ServiceSnapshot>(new IOException("Нет доступа к диспетчеру служб")));

    private static void CheckDistributionSelection()
    {
        var reference = Path.Combine(AppContext.BaseDirectory, "fixtures", "flowseal-1.10.3");
        var model = new MainViewModel(ReadOnlyAdapter(), reference);
        Complete(model.InitializeAsync());
        if (model.Root != reference || model.DistributionVersion != "Заявленная версия поставки: 1.10.3" || model.Presets.Count != 22)
            throw new Exception("Adjacent distribution was not inspected through the real adapter");
        var missing = Path.Combine(Path.GetTempPath(), "Chosen missing distribution " + Guid.NewGuid());
        Complete(model.SelectDistributionCommand.ExecuteAsync(missing));
        if (model.Root != missing || model.Presets.Count != 0 || model.CurrentState != ServiceState.Unknown
            || !model.ReadinessText.Contains("Папка поставки не найдена") || !model.Error.Contains("Состояние службы")
            || model.ApplyCommand.CanExecute(null) || model.RemoveCommand.CanExecute(null))
            throw new Exception("Folder selection must keep distribution readiness visible after service observation fails and block mutations");
        Complete(model.SelectDistributionCommand.ExecuteAsync(reference));
        if (model.Presets.Count != 22 || !model.Distribution!.ControlFilesVerified)
            throw new Exception("Choosing the official fixture must restore source readiness through the real adapter");
        model.Notifications.Dispose();
    }

    private static void CopyReference(string destination)
    {
        var reference = Path.Combine(AppContext.BaseDirectory, "fixtures", "flowseal-1.10.3");
        foreach (var file in Directory.EnumerateFiles(reference, "*", SearchOption.AllDirectories))
        {
            var copy = Path.Combine(destination, Path.GetRelativePath(reference, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
        }
    }
    private static void CheckFolderChangeResetsEditor()
    {
        var temporary = Path.Combine(Path.GetTempPath(), "Zapret selection " + Guid.NewGuid());
        try
        {
            var first = Path.Combine(temporary, "first distribution");
            var second = Path.Combine(temporary, "second distribution");
            CopyReference(first);
            CopyReference(second);
            Directory.CreateDirectory(Path.Combine(first, "lists"));
            Directory.CreateDirectory(Path.Combine(second, "lists"));
            File.WriteAllText(Path.Combine(first, "lists", "list-general.txt"), "first.example\n");
            File.WriteAllText(Path.Combine(second, "lists", "list-general.txt"), "second.example\n");
            var model = new MainViewModel(ReadOnlyAdapter(), first);
            Complete(model.InitializeAsync());
            Complete(model.LoadHostlistCommand.ExecuteAsync(null));
            model.HostlistText = "unsaved.example\n";
            Complete(model.SelectDistributionCommand.ExecuteAsync(second));
            if (model.Root != first || !model.IsDirty || !model.Error.Contains("отмените"))
                throw new Exception("Choosing a folder must preserve unsaved list changes");
            model.DiscardChangesCommand.Execute(null);
            Complete(model.SelectDistributionCommand.ExecuteAsync(second));
            if (model.Root != second || model.HasLoadedHostlist || model.HostlistText != "" || model.Hostlists.Single().Path != Path.Combine(second, "lists", "list-general.txt"))
                throw new Exception("Choosing a new folder must reset the loaded editor and discover only its lists");
            Complete(model.LoadHostlistCommand.ExecuteAsync(null));
            if (model.HostlistText != "second.example\n") throw new Exception("Newly loaded list must come from selected distribution");
            model.Notifications.Dispose();
        }
        finally
        {
            var resolved = Path.GetFullPath(temporary);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("Zapret selection ")) throw new IOException("Unexpected test directory");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }

    private static void CheckServiceSafetyPresentation()
    {
        var temporary = Path.Combine(Path.GetTempPath(), "Zapret selection " + Guid.NewGuid());
        try
        {
            CopyReference(temporary);
            var description = new Zapret.Core.FlowsealDistributionAdapter().Describe(temporary);
            foreach (var relative in description.RequiredComponents!)
            {
                var file = Path.Combine(temporary, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, "inert fixture");
            }
            foreach (var list in new[] { "list-general-user.txt", "list-exclude-user.txt", "ipset-exclude-user.txt" })
                File.WriteAllText(Path.Combine(temporary, "lists", list), "fixture.example\n");
            var snapshot = new ServiceSnapshot(ServiceState.Stopped, null,
                new ServiceConfiguration(Zapret.Core.CommandLine.Quote(Path.Combine(temporary, "bin", "winws.exe"))));
            var model = new MainViewModel(new FlowsealAdapter(new NoCommands(), () => Task.FromResult(snapshot)), temporary);
            Complete(model.InitializeAsync());
            Complete(model.CheckActionCommand.ExecuteAsync(null));
            if (!model.ServiceOwnershipText.Contains("Своя служба") || !model.ActionReadiness.Contains("Полный поддерживаемый снимок")
                || model.ApplyCommand.CanExecute(null) || model.RemoveCommand.CanExecute(null))
                throw new Exception("GUI must show verified ownership and refuse replacement when a full recovery snapshot is unavailable");
            model.Notifications.Dispose();
        }
        finally
        {
            if (!Path.GetFileName(temporary).StartsWith("Zapret selection ")
                || !Path.GetFullPath(temporary).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new IOException("Unexpected test folder");
            if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
        }
    }
    private static void CheckContrast(Application app, string foreground, string background, double minimum)
    {
        static double Luminance(Color color)
        {
            static double Channel(byte c) { var value = c / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
            return .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
        }
        var a = Luminance(((SolidColorBrush)app.FindResource("M3." + foreground)).Color);
        var b = Luminance(((SolidColorBrush)app.FindResource("M3." + background)).Color);
        var contrast = (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
        if (contrast < minimum) throw new Exception($"{foreground}/{background}: contrast {contrast:F2} < {minimum}");
    }

    private static T? Find<T>(DependencyObject parent, Func<T, bool>? match = null) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found && (match == null || match(found))) return found;
            if (Find(child, match) is { } nested) return nested;
        }
        return null;
    }
}

internal sealed class BindingErrors : TraceListener
{
    public List<string> Errors { get; } = [];
    public override void Write(string? message) { }
    public override void WriteLine(string? message)
    {
        if (message != null) Errors.Add(message);
    }
}


internal sealed class NoCommands : ICommandRunner
{
    public Task<CommandResult> RunAsync(string executable, params string[] arguments) => throw new Exception("WPF smoke must not execute OS commands");
}
