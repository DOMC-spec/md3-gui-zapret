using System.IO;
using Zapret.Gui.Services;
using Zapret.Gui.ViewModels;

static class GameFilterPresentationChecks
{
    public static void Run(Action<Task> complete)
    {
        var root = Path.Combine(Path.GetTempPath(), "Zapret game UI " + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "utils"));
            File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "flowseal-1.10.3", "service.bat"), Path.Combine(root, "service.bat"));
            var adapter = new FlowsealAdapter(new ProcessRunner(), () => Task.FromResult(new ServiceSnapshot(ServiceState.Missing, null)));
            var model = new MainViewModel(adapter, root);
            complete(model.InitializeAsync());
            complete(model.LoadGameFilterCommand.ExecuteAsync(null));
            if (model.GameTcpRange != "1024-65535" || model.GameMode != "disabled") throw new Exception("GUI must load native defaults");
            model.GameMode = "tcp";
            model.GameTcpRange = "80, 443";
            complete(model.SaveGameFilterCommand.ExecuteAsync(null));
            if (model.IsGameFilterDirty || model.GameTcpRange != "80,443" || !model.GameFilterMessage.Contains("не изменён"))
                throw new Exception("GUI must normalize, save and distinguish process application");
            model.GameTcpRange = "5000";
            var flag = Path.Combine(root, "utils", "game_filter.enabled");
            File.WriteAllText(flag, "mode=udp\r\ntcp=9000\r\nudp=10000\r\n");
            complete(model.SaveGameFilterCommand.ExecuteAsync(null));
            if (!model.Error.Contains("изменился") || model.GameTcpRange != "5000" || !model.IsGameFilterDirty)
                throw new Exception("GUI must retain input after external edit conflict");
            complete(model.LoadGameFilterCommand.ExecuteAsync(null));
            if (model.GameTcpRange != "5000" || !model.GameFilterObserved.Contains("9000"))
                throw new Exception("Reread must show external settings for comparison without losing the draft");
            complete(model.SelectDistributionCommand.ExecuteAsync(root + " other"));
            if (model.Root != root) throw new Exception("Dirty Game Filter must prevent changing distribution");
            model.DiscardGameFilterCommand.Execute(null);
            if (model.IsGameFilterDirty || model.GameTcpRange != "9000") throw new Exception("Discard must restore latest observed settings");
            model.Notifications.Dispose();
            Console.WriteLine("PASS Game Filter GUI saves, preserves conflicts, compares reread and discards to current settings");
        }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(root).StartsWith("Zapret game UI ")) throw new IOException("Unexpected test folder");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
