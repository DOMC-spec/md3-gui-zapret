using System.IO;
using Zapret.Core;
using Zapret.Gui.Services;
using Zapret.Gui.ViewModels;

static class DomainListPresentationChecks
{
    public static void Run(Action<Task> complete)
    {
        var root = Path.Combine(Path.GetTempPath(), "Zapret lists UI " + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "lists"));
            File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "flowseal-1.10.3", "service.bat"), Path.Combine(root, "service.bat"));
            File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "flowseal-1.10.3", "general.bat"), Path.Combine(root, "general.bat"));
            var adapter = new FlowsealAdapter(new NoCommands(), () => Task.FromResult(new ServiceSnapshot(ServiceState.Missing, null)));
            var model = new MainViewModel(adapter, root);
            complete(model.InitializeAsync());
            model.SelectedHostlist = model.Hostlists.Single(list => list.Path.EndsWith("list-general-user.txt"));
            complete(model.LoadHostlistCommand.ExecuteAsync(null));
            if (File.Exists(model.SelectedHostlist.Path) || !model.ListUsage.Contains("используется")) throw new Exception("Optional user list must be explained and read without creation");
            model.Domain = "пример.рф";
            complete(model.AddDomainCommand.ExecuteAsync(null));
            if (!File.ReadAllText(model.SelectedHostlist.Path).Contains("xn--e1afmkfd.xn--p1ai") || model.IsDirty)
                throw new Exception("Adding an IDN must create and save the native user list");
            if (!model.ListMessage.Contains("заново")) throw new Exception("Saving must explain reapplication");
            var userPath = model.SelectedHostlist.Path;
            model.Domain = "EXAMPLE.ORG";
            File.WriteAllText(userPath, "external.example\r\n");
            complete(model.AddDomainCommand.ExecuteAsync(null));
            if (!model.Error.Contains("изменился") || !model.HostlistText.Contains("example.org") || !model.IsDirty)
                throw new Exception("Conflict must retain added domain in the draft");
            complete(model.CompareHostlistCommand.ExecuteAsync(null));
            if (!model.ObservedListText.Contains("external.example") || !model.HostlistText.Contains("example.org"))
                throw new Exception("Reread must show external text separately from the draft");
            var export = root + "-draft.txt";
            try
            {
                complete(model.ExportHostlistCommand.ExecuteAsync(export));
                if (!File.ReadAllText(export).Contains("example.org") || File.ReadAllText(userPath) != "external.example\r\n") throw new Exception("Separate export must preserve both versions");
            }
            finally { if (File.Exists(export)) File.Delete(export); }
            complete(model.SelectDistributionCommand.ExecuteAsync(root + "other"));
            if (model.Root != root || !model.IsDirty) throw new Exception("Root change must retain draft");
            model.HostlistText += "external.example\r\n";
            complete(model.SaveComparedDraftCommand.ExecuteAsync(null));
            if (model.IsDirty || !File.ReadAllText(userPath).Contains("external.example") || !File.ReadAllText(userPath + ".bak").Contains("external.example"))
                throw new Exception("Explicit merge must save against reread revision with backup");
            model.SelectedHostlist = model.Hostlists.Single(list => list.Path.EndsWith("list-exclude-user.txt"));
            complete(model.LoadHostlistCommand.ExecuteAsync(null));
            model.Domain = "https://bad.example/path";
            complete(model.AddDomainCommand.ExecuteAsync(null));
            if (!model.Error.Contains("домен") || !model.Domain.Contains("https://") || File.Exists(model.SelectedHostlist.Path))
                throw new Exception("Invalid site must stay visible and not create a file");
            model.Domain = "excluded.example";
            complete(model.AddDomainCommand.ExecuteAsync(null));
            if (!File.ReadAllText(model.SelectedHostlist.Path).Contains("excluded.example")) throw new Exception("User can add an exclusion");
            complete(CheckAdapter(root, adapter));
            model.Notifications.Dispose();
            Console.WriteLine("PASS GUI adds site/exclusion, rejects invalid input, compares conflicts, exports draft, preserves root changes and explicitly merges");
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("Zapret lists UI ")) throw new IOException("Unexpected test directory");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static async Task CheckAdapter(string root, IFlowsealAdapter adapter)
    {
        var path = Path.Combine(root, "lists", "list-extra.txt");
        foreach (var encoding in new System.Text.Encoding[] { new System.Text.UTF8Encoding(true), new System.Text.UTF8Encoding(false),
            new System.Text.UnicodeEncoding(false, true), new System.Text.UnicodeEncoding(true, true), new System.Text.UTF32Encoding(false, true) })
        {
            File.WriteAllText(path, "пример.рф\r\n", encoding);
            var original = File.ReadAllBytes(path);
            var read = await adapter.ReadDomainListAsync(root, path);
            var saved = await adapter.SaveDomainListAsync(root, read, read.Text + "added.example\r\n");
            if (saved.Text != "пример.рф\r\nadded.example\r\n" || !File.ReadAllBytes(path).AsSpan().StartsWith(encoding.GetPreamble())
                || !File.ReadAllBytes(path + ".bak").SequenceEqual(original)) throw new Exception("Encoding/BOM or backup lost");
        }
        var descriptions = await adapter.ReadDomainListsAsync(root, new Preset("general", Path.Combine(root, "general.bat")));
        if (descriptions.Single(item => item.Path == path).UsedByStrategy != false) throw new Exception("Unused list must be described as unused");
        var unknown = await adapter.ReadDomainListsAsync(root, new Preset("changed", Path.Combine(root, "changed.bat")));
        if (unknown.Any(item => item.UsedByStrategy != null)) throw new Exception("Unknown strategy cannot make a usage claim");
        var before = await adapter.ReadDomainListAsync(root, path);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            try { await adapter.SaveDomainListAsync(root, before, "lost.example\n"); throw new Exception("Read-only save succeeded"); }
            catch (UnauthorizedAccessException) { }
            if (File.ReadAllText(path) != before.Text) throw new Exception("Failed save lost original text");
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
        if (HostlistStore.AddDomain("пример.рф\r\n", "xn--e1afmkfd.xn--p1ai") != "пример.рф\r\n")
            throw new Exception("Unicode and ASCII IDN spellings must not create duplicates");
        Console.WriteLine("PASS adapter preserves UTF8/Unicode BOM and exact backup, unused/unknown strategy and refused write");
    }
}
