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
            M3Theme.Apply(false);
            var bindingErrors = new BindingErrors();
            PresentationTraceSources.DataBindingSource.Listeners.Add(bindingErrors);
            var window = new MainWindow();
            var viewModel = (MainViewModel)window.DataContext;
            viewModel.StatusText = "Не установлена";
            viewModel.ActivePreset = "Пресет ещё не применён";
            viewModel.Presets.Add(new Zapret.Core.Preset("general (ALT)", "general (ALT).bat"));
            viewModel.SelectedPreset = viewModel.Presets[0];
            viewModel.Hostlists.Add(new HostlistItem("lists/list-general.txt", "lists/list-general.txt"));
            viewModel.SelectedHostlist = viewModel.Hostlists[0];
            if (args.Contains("--running"))
            {
                viewModel.CurrentState = ServiceState.Running;
                viewModel.StatusText = "Работает";
                viewModel.ActivePreset = "general (ALT)";
            }
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

    private static T? Find<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (Find<T>(child) is { } nested) return nested;
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
