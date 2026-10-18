using Zapret.Gui.Services;

static class GameFilterScenarios
{
    public static async Task Run(string root)
    {
        var adapter = new FlowsealAdapter(new ProcessRunner(), operationJournal: new MemoryOperationJournal());
        var original = await adapter.ReadGameFilterAsync(root);
        if (original.Settings != new GameFilterSettings("disabled", "1024-65535", "1024-65535"))
            throw new Exception("Absent settings must display upstream defaults");
        var saved = await adapter.SaveGameFilterAsync(root, new("all", "1, 22, 65535", "2000 - 3000"), original.Revision);
        if (saved.Settings != new GameFilterSettings("all", "1,22,65535", "2000-3000")
            || saved.Tcp != "1,22,65535" || saved.Udp != "2000-3000")
            throw new Exception("Saved settings must roundtrip through the original Flowseal helper");
        var flag = Path.Combine(root, "utils", "game_filter.enabled");
        if (File.ReadAllText(flag) != "mode=all\r\ntcp=1,22,65535\r\nudp=2000-3000\r\n")
            throw new Exception("Settings must use the native ASCII/CRLF format");
        File.Delete(flag);
        Console.WriteLine("PASS Game Filter adapter loads defaults and saves normalized settings through upstream CMD");
        foreach (var item in new (string? Text, string Mode, string TcpRange, string UdpRange, string Tcp, string Udp)[]
        {
            (null, "disabled", "1024-65535", "1024-65535", "12", "12"),
            ("", "disabled", "1024-65535", "1024-65535", "12", "12"),
            ("all", "all", "1024-65535", "1024-65535", "1024-65535", "1024-65535"),
            ("tcp", "tcp", "1024-65535", "1024-65535", "1024-65535", "12"),
            ("udp", "udp", "1024-65535", "1024-65535", "12", "1024-65535"),
            ("MODE=AlL\nTCP=1, 22, 65535\nUDP=2000-3000", "all", "1,22,65535", "2000-3000", "1,22,65535", "2000-3000"),
            ("mode=unknown\ntcp=2000\nudp=4000", "disabled", "2000", "4000", "12", "12"),
            ("mode=all\ntcp=0001\nudp=65536", "all", "1024-65535", "1024-65535", "1024-65535", "1024-65535"),
            ("mode=all\ntcp=2000\ntcp=0\nudp=7000\nudp=6000-5000", "all", "1024-65535", "1024-65535", "1024-65535", "1024-65535"),
            ("mode=all\nmode=udp\ntcp=80\nudp=4000", "udp", "80", "4000", "12", "4000")
        })
        {
            if (File.Exists(flag)) File.Delete(flag);
            if (item.Text != null) File.WriteAllText(flag, item.Text);
            var read = await adapter.ReadGameFilterAsync(root);
            if (read.Settings != new GameFilterSettings(item.Mode, item.TcpRange, item.UdpRange) || read.Tcp != item.Tcp || read.Udp != item.Udp)
                throw new Exception("Native read matrix failed: " + item.Text);
            var roundtrip = await adapter.SaveGameFilterAsync(root, read.Settings, read.Revision);
            if (roundtrip.Settings != read.Settings || roundtrip.Tcp != read.Tcp || roundtrip.Udp != read.Udp)
                throw new Exception("Native read/write matrix failed: " + item.Text);
        }
        foreach (var mode in new[] { "disabled", "all", "tcp", "udp" })
        {
            var read = await adapter.ReadGameFilterAsync(root);
            var write = await adapter.SaveGameFilterAsync(root, new(mode, "1-65535", "53,443"), read.Revision);
            if (write.Settings != new GameFilterSettings(mode, "1-65535", "53,443")
                || write.Tcp != (mode is "all" or "tcp" ? "1-65535" : "12")
                || write.Udp != (mode is "all" or "udp" ? "53,443" : "12")) throw new Exception("Mode write matrix failed");
        }
        Console.WriteLine("PASS native read/write matrix: legacy, absent, empty, duplicates, defaults, case and all four modes");
        var current = await adapter.ReadGameFilterAsync(root);
        var unchanged = File.ReadAllBytes(flag);
        foreach (var invalid in new[] { "", " ", "0", "65536", "01", "2-1", "80,,443", ",80", "80,", "1-2-3", "80;443", "80\t", "80\n", "80&echo x", "８０", "1,1," + new string('1', 1200) })
        {
            foreach (var tcp in new[] { true, false })
            {
                try
                {
                    await adapter.SaveGameFilterAsync(root, tcp ? new("all", invalid, "80") : new("all", "80", invalid), current.Revision);
                    throw new Exception("Invalid range accepted: " + invalid);
                }
                catch (FormatException) { }
                if (!File.ReadAllBytes(flag).SequenceEqual(unchanged)) throw new Exception("Invalid input changed settings");
            }
        }
        File.WriteAllText(flag, "mode=disabled\r\n");
        try { await adapter.SaveGameFilterAsync(root, new("all", "5000", "6000"), current.Revision); throw new Exception("External edit overwritten"); }
        catch (IOException error) when (error.Message.Contains("изменился")) { }
        if (File.ReadAllText(flag) != "mode=disabled\r\n") throw new Exception("External edit lost");
        File.Delete(flag);
        try { await adapter.SaveGameFilterAsync(root, new("all", "5000", "6000"), current.Revision); throw new Exception("External deletion ignored"); }
        catch (IOException error) when (error.Message.Contains("изменился")) { }
        File.WriteAllText(flag, "mode=all&echo unsafe\r\n");
        try { await adapter.ReadGameFilterAsync(root); throw new Exception("Unsafe CMD input accepted"); }
        catch (IOException error) when (error.Message.Contains("символы")) { }
        File.Delete(flag);
        Console.WriteLine("PASS invalid ranges preserve file; external edit/deletion conflict and unsafe input are refused");
        var longRange = string.Join(',', Enumerable.Repeat("80", 360));
        File.WriteAllText(flag, "mode=tcp\r\ntcp=" + longRange + "\r\nudp=53\r\n");
        var longRead = await adapter.ReadGameFilterAsync(root);
        var longSaved = await adapter.SaveGameFilterAsync(root, longRead.Settings, longRead.Revision);
        if (longSaved.Settings.TcpRange != longRange || longSaved.Tcp != longRange) throw new Exception("Native ranges over 1024 characters must roundtrip");
        File.Delete(flag);
        foreach (var exists in new[] { true, false })
        {
            if (exists) File.WriteAllText(flag, "mode=tcp\r\ntcp=80\r\nudp=53\r\n");
            var beforeFailure = await adapter.ReadGameFilterAsync(root);
            var faulted = new FlowsealAdapter(new FailReadBack(), operationJournal: new MemoryOperationJournal());
            try { await faulted.SaveGameFilterAsync(root, new("all", "5000", "6000"), beforeFailure.Revision); throw new Exception("Readback failure ignored"); }
            catch (IOException error) when (error.Message.Contains("прежние настройки возвращены")) { }
            var restored = await adapter.ReadGameFilterAsync(root);
            if (restored.Settings != beforeFailure.Settings) throw new Exception("Readback failure lost previous configuration");
            if (exists && File.ReadAllText(flag) != "mode=tcp\r\ntcp=80\r\nudp=53\r\n") throw new Exception("Readback failure must restore exact bytes");
            File.Delete(flag);
        }
        Console.WriteLine("PASS long native ranges and failed post-save helper restore previous/absent settings");
        File.WriteAllText(flag, string.Concat(Enumerable.Repeat("; native comment\r\n", 600)) + "mode=tcp\r\ntcp=80\r\nudp=53\r\n");
        if ((await adapter.ReadGameFilterAsync(root)).Settings != new GameFilterSettings("tcp", "80", "53"))
            throw new Exception("Native settings with comments over 8192 bytes must remain readable");
        File.Delete(flag);
        Console.WriteLine("PASS native settings with many ignored comments remain readable");
    }

    private sealed class FailReadBack : ICommandRunner
    {
        private int exports;
        public Task<CommandResult> RunAsync(string executable, params string[] arguments) => ++exports == 2
            ? Task.FromException<CommandResult>(new IOException("Injected native readback failure"))
            : new ProcessRunner().RunAsync(executable, arguments);
    }
}
