using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;
using Zapret.Core;
using Zapret.Gui;
using Zapret.Gui.Services;
using Zapret.Gui.ViewModels;

internal static class ExpressivePresentationChecks
{
    public static IFlowsealAdapter Preview(string root, bool conflict = false) => new PresentationAdapter(root) { Conflict = conflict };

    public static void Run(Action<Task> complete)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "flowseal-1.10.3");
        var adapter = new PresentationAdapter(root);
        var model = new MainViewModel(adapter, root) { CloseAfterApply = false };
        var window = new MainWindow { DataContext = model };
        var shutdownMode = Application.Current.ShutdownMode;
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            complete(model.InitializeAsync());
            var content = (FrameworkElement)window.Content;
            content.LayoutTransform = new ScaleTransform(2, 2);
            content.Measure(new Size(720, 682));
            content.Arrange(new Rect(0, 0, 720, 682));
            content.UpdateLayout();
            var apply = Descendants<Button>(content).Single(button => ReferenceEquals(button.Command, model.ApplyCommand));
            var remove = Descendants<Button>(content).Single(button => ReferenceEquals(button.Command, model.RemoveCommand));
            var applyBounds = new Rect(apply.TranslatePoint(new Point(), content), apply.RenderSize);
            var removeBounds = new Rect(remove.TranslatePoint(new Point(), content), remove.RenderSize);
            if (applyBounds.IntersectsWith(removeBounds) || applyBounds.Right > content.ActualWidth + 1 || removeBounds.Right > content.ActualWidth + 1)
                throw new Exception("At 200% scale, service actions must fit without overlap or horizontal clipping");
            adapter.State = adapter.State with { State = ServiceState.Stopping };
            complete(model.RefreshObservedStateAsync());
            content.UpdateLayout();
            var headline = Descendants<TextBlock>(content).Single(text => text.Text == "Останавливается…");
            var refresh = Descendants<Button>(content).Single(button => ReferenceEquals(button.Command, model.RefreshCommand));
            if (headline.TranslatePoint(new Point(headline.ActualWidth, 0), content).X > refresh.TranslatePoint(new Point(), content).X)
                throw new Exception("Long transitional state must wrap before the refresh button at 200% scale");
            adapter.State = adapter.State with { State = ServiceState.Stopped };
            content.LayoutTransform = Transform.Identity;
            model.ReduceMotion = true;
            window.Show();
            complete(Task.Delay(80));
            if (!TransitionAssist.GetDisableTransitions(window)) throw new Exception("Reduced motion must disable toolkit transitions");
            var tabs = Descendants<TabControl>(content).Single();
            var tab = (TabItem)tabs.Items[0];
            Keyboard.Focus(tab);
            if (!tab.IsKeyboardFocused || !Descendants<Border>(tab).Any(border => border.Visibility == Visibility.Visible && border.BorderThickness.Left >= 3))
                throw new Exception("Keyboard focus on navigation must have a visible outline");
            tab.MoveFocus(new TraversalRequest(FocusNavigationDirection.Right));
            if (tabs.SelectedIndex != 1) throw new Exception("Arrow keys must switch tabs");
            tabs.SelectedIndex = 0;
            content.UpdateLayout();
            var combo = Descendants<ComboBox>(content).Single();
            Keyboard.Focus(combo);
            var held = new TaskCompletionSource<ServiceSnapshot>();
            adapter.HeldRead = held;
            var refreshing = model.RefreshObservedStateAsync();
            complete(Task.Delay(20));
            var focusLost = !combo.IsEnabled || Keyboard.FocusedElement != combo;
            held.SetResult(adapter.State);
            complete(refreshing);
            if (focusLost) throw new Exception("Background observation must preserve keyboard focus and editable controls");
            if (!TransitionAssist.GetDisableTransitions(combo)) throw new Exception("Reduced motion must reach child controls");
            adapter.PendingAction = new TaskCompletionSource<FlowsealActionResult>();
            complete(model.CheckActionCommand.ExecuteAsync(null));
            var applying = model.ApplyCommand.ExecuteAsync(null);
            content.UpdateLayout();
            var progress = Descendants<ProgressBar>(content).Single();
            if (!model.IsBusy || progress.IsIndeterminate) throw new Exception("Reduced motion must show static progress while retaining operation state");
            adapter.PendingAction.SetResult(new(FlowsealActionOutcome.Success, adapter.State, "Автозапуск установлен."));
            complete(applying);
            adapter.PendingAction = null;
            adapter.Conflict = true;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                complete(model.CheckActionCommand.ExecuteAsync(null));
                var viewer = Descendants<ScrollViewer>(content).First();
                viewer.ScrollToBottom();
                content.UpdateLayout();
                complete(model.ApplyCommand.ExecuteAsync(null));
                complete(Task.Delay(50));
                var error = Descendants<TextBox>(content).Single(box => System.Windows.Automation.AutomationProperties.GetName(box) == "Ошибка и следующий шаг");
                var errorBottom = error.TranslatePoint(new Point(0, error.ActualHeight), viewer).Y;
                if (errorBottom < 0 || errorBottom > viewer.ActualHeight + 1)
                    throw new Exception("Each failed user attempt must bring its error into view, including identical repeated errors");
            }
            adapter.State = adapter.State with { ObservationError = "native observation details" };
            complete(model.RefreshObservedStateAsync());
            complete(Task.Delay(50));
            var scroller = Descendants<ScrollViewer>(content).First();
            scroller.ScrollToBottom();
            content.UpdateLayout();
            var offset = scroller.VerticalOffset;
            complete(model.RefreshObservedStateAsync());
            complete(Task.Delay(50));
            if (Math.Abs(scroller.VerticalOffset - offset) > 1)
                throw new Exception("Unchanged background read errors must not repeatedly scroll away from user input");
        }
        finally { window.Close(); Application.Current.ShutdownMode = shutdownMode; }
        Console.WriteLine("PASS expressive 200% layout, keyboard focus/navigation, reduced motion, repeated errors and non-disruptive observation");
    }

    public static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private sealed class PresentationAdapter(string root) : IFlowsealAdapter
    {
        public ServiceSnapshot State { get; set; } = new(ServiceState.Stopped, "general (ALT) · зарегистрированная стратегия",
            new("inert"), Ownership: ServiceOwnership.Owned, OwnershipReason: "Своя служба выбранной поставки.");
        public TaskCompletionSource<ServiceSnapshot>? HeldRead;
        public TaskCompletionSource<FlowsealActionResult>? PendingAction;
        public bool Conflict;
        public Task<DistributionDescription> DescribeAsync(string path) => Task.FromResult(new DistributionDescription(root, "1.10.3", true,
            [new(new("general (FAKE TLS AUTO) · очень длинное имя исходной стратегии Flowseal для проверки переноса текста", Path.Combine(root, "general.bat")), true, "Исходная стратегия проверена.")],
            [], null, "Поставка готова.", Enum.GetValues<FlowsealAction>().Select(action => new FlowsealCapability(action, true, "Доступно после проверки.")).ToArray()));
        public Task<ServiceSnapshot> ReadStateAsync(string path)
        {
            if (HeldRead is { } held) { HeldRead = null; return held.Task; }
            return Task.FromResult(State);
        }
        public Task<FlowsealActionResult> PreflightAsync(FlowsealActionRequest request) => Task.FromResult(new FlowsealActionResult(FlowsealActionOutcome.Ready, State, "Можно установить выбранную стратегию в автозапуск."));
        public Task<FlowsealActionResult> ExecuteAsync(FlowsealActionRequest request) => PendingAction?.Task ?? Task.FromResult(Conflict
            ? new FlowsealActionResult(FlowsealActionOutcome.Conflict, State, "Служба изменена извне. Сторонняя конфигурация сохранена.", FlowsealRecoveryOutcome.Conflict,
                ApplyError: "native conflict details", NextStep: "Обновите состояние и сравните внешние изменения перед повторной попыткой.")
            : new FlowsealActionResult(FlowsealActionOutcome.Success, State, "Действие выполнено."));
    }
}
