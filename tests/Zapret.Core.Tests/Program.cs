using Zapret.Core;

var failures = 0;
var passed = 0;
var root = Path.Combine(Path.GetTempPath(), "Zapret tests " + Guid.NewGuid());
Directory.CreateDirectory(Path.Combine(root, "bin"));
Directory.CreateDirectory(Path.Combine(root, "lists"));
Directory.CreateDirectory(Path.Combine(root, "utils"));
try
{
    Test("Missing distribution explains readiness and leaves inspection available", () =>
    {
        var result = new FlowsealDistributionAdapter().Describe(Path.Combine(root, "missing distribution"));
        Equal("Папка поставки не найдена. Выберите распакованную папку Flowseal.", result.Explanation);
        Equal("0", result.Strategies.Count.ToString());
        Equal("True", result.Capabilities.Single(c => c.Action == FlowsealAction.ReadState).Available.ToString());
        Equal("False", result.Capabilities.Single(c => c.Action == FlowsealAction.InstallService).Available.ToString());
    });
    Test("Pinned Flowseal sources expose all 22 verified strategies without executing BAT", () =>
    {
        var result = new FlowsealDistributionAdapter().Describe(Path.Combine(AppContext.BaseDirectory, "fixtures", "flowseal-1.10.3"));
        Equal("1.10.3", result.DeclaredVersion ?? "unknown");
        Equal("True", result.ControlFilesVerified.ToString());
        Equal("22", result.Strategies.Count.ToString());
        Equal("22", result.Strategies.Count(s => s.Verified).ToString());
        Equal("True", result.MissingComponents.Contains("bin/winws.exe").ToString());
        Equal("False", result.Capabilities.Single(c => c.Action == FlowsealAction.InstallService).Available.ToString());
    });
    Test("User list and setting changes preserve source verification and safe list editing", () =>
    {
        var distribution = CopyReference("user data with spaces");
        Directory.CreateDirectory(Path.Combine(distribution, "lists"));
        File.WriteAllText(Path.Combine(distribution, "lists", "list-general.txt"), "user.example\n");
        File.WriteAllText(Path.Combine(distribution, "utils", "game_filter.enabled"), "mode=udp\ntcp=1234\nudp=5678");
        Directory.CreateDirectory(Path.Combine(distribution, "bin"));
        File.WriteAllText(Path.Combine(distribution, "bin", "ACTIVE_GAME_UDP.bin"), "user selected fake");
        var result = new FlowsealDistributionAdapter().Describe(distribution);
        Equal("True", result.ControlFilesVerified.ToString());
        Equal("True", result.Capabilities.Single(c => c.Action == FlowsealAction.EditLists).Available.ToString());
        Equal("False", result.Capabilities.Single(c => c.Action == FlowsealAction.ManualStart).Available.ToString());
    });
    Test("Changing one strategy does not mislabel the other 21 original strategies", () =>
    {
        var distribution = CopyReference("changed strategy");
        File.AppendAllText(Path.Combine(distribution, "general.bat"), "\r\nrem user change\r\n");
        var result = new FlowsealDistributionAdapter().Describe(distribution);
        Equal("21", result.Strategies.Count(s => s.Verified).ToString());
        Equal("False", result.Strategies.Single(s => s.Preset.Name == "general").Verified.ToString());
        Equal("False", result.ControlFilesVerified.ToString());
        Equal("True", result.Capabilities.Single(c => c.Action == FlowsealAction.ReadState).Available.ToString());
        Equal("False", result.Capabilities.Single(c => c.Action == FlowsealAction.ManualStart).Available.ToString());
    });
    Test("An unknown same-version service is unverified while original strategies remain readable", () =>
    {
        var distribution = CopyReference("same version unknown service");
        File.AppendAllText(Path.Combine(distribution, "service.bat"), "\r\nrem changed control code\r\n");
        var result = new FlowsealDistributionAdapter().Describe(distribution);
        Equal("1.10.3", result.DeclaredVersion ?? "unknown");
        Equal("False", result.ControlFilesVerified.ToString());
        Equal("22", result.Strategies.Count(s => s.Verified).ToString());
        Equal("False", result.Capabilities.Single(c => c.Action == FlowsealAction.InstallService).Available.ToString());
    });
    Test("Changing the optional test tool affects its capability reason only", () =>
    {
        var distribution = CopyReference("changed tool");
        File.AppendAllText(Path.Combine(distribution, "utils", "test zapret.ps1"), "\n# changed tool\n");
        var result = new FlowsealDistributionAdapter().Describe(distribution);
        Equal("True", result.ControlFilesVerified.ToString());
        Equal("True", result.Capabilities.Single(c => c.Action == FlowsealAction.Tests).Reason.Contains("отсутствует или изменён").ToString());
    });
    Test("Paths with spaces are inspected and unsupported CMD or Cyrillic paths are explained", () =>
    {
        foreach (var folder in new[] { "plain distribution with spaces", "unsafe & path", "Поставка" })
        {
            var distribution = CopyReference(folder);
            var result = new FlowsealDistributionAdapter().Describe(distribution);
            Equal("22", result.Strategies.Count.ToString());
            Equal("True", result.ControlFilesVerified.ToString());
            Equal((folder != "plain distribution with spaces").ToString(), (result.PathIssue != null).ToString());
        }
    });
    if (args.Length == 2 && args[0] == "--inspect")
        Test("Actual complete Flowseal distribution is inspected read-only", () =>
        {
            var result = new FlowsealDistributionAdapter().Describe(args[1]);
            Equal("1.10.3", result.DeclaredVersion ?? "unknown");
            Equal("True", result.ControlFilesVerified.ToString());
            Equal("0", result.MissingComponents.Count.ToString());
            Equal("22", result.Strategies.Count(s => s.Verified).ToString());
            Equal("False", result.Capabilities.Single(c => c.Action == FlowsealAction.InstallService).Available.ToString());
        });
    Test("Windows argument quoting round-trips spaces, quotes and trailing backslashes", () =>
    {
        string[] arguments = ["", "abc", @"C:\Program Files\zapret\", "a\"b", @"--hostlist=C:\Мои списки\a.txt", "--new"];
        Equal(string.Join('|', arguments), string.Join('|', CommandLine.Split(CommandLine.Join(arguments))));
    });
    const string bat = """
        @echo off
        call service.bat load_game_filter
        set "BIN=%~dp0bin\"
        set "LISTS=%~dp0lists\"
        cd /d %BIN%
        start "zapret: %~n0" /min "%BIN%winws.exe" --wf-tcp=80,443,%GameFilterTCP% ^
        --hostlist="%LISTS%list-general.txt" --dpi-desync-fake-tls="%BIN%tls.bin" ^
        --new --filter-tcp=443
        pause
        """;
    Test("Flowseal multiline preset resolves variables without executing commands", () =>
    {
        var preset = PresetParser.Parse(bat, root);
        Equal("--wf-tcp=80,443,12", preset.Arguments[0]);
        Equal(Path.Combine(root, "lists", "list-general.txt"), preset.ReferencedFiles[0]);
        Equal(Path.Combine(root, "bin", "tls.bin"), preset.ReferencedFiles[1]);
        Equal("--filter-tcp=443", preset.Arguments[^1]);
        var command = preset.ServiceCommand(Path.Combine(root, "bin", "winws.exe"));
        Equal(Path.Combine(root, "bin", "winws.exe"), CommandLine.Split(command)[0]);
    });
    Test("Unknown variables fail before service mutation", () => Throws<FormatException>(() => PresetParser.Parse(bat.Replace("%GameFilterTCP%", "%Unsupported%"), root)));
    Test("Batch command chaining is rejected", () => Throws<FormatException>(() => PresetParser.Parse("winws.exe --new & echo oops", root)));
    Test("Multiple winws launches are rejected", () => Throws<FormatException>(() => PresetParser.Parse("winws.exe --new\nwinws.exe --new", root)));
    Test("Unterminated quote is rejected", () => Throws<FormatException>(() => CommandLine.Split("--hostlist=\"oops")));
    Test("Escaped exclamation in Flowseal TLS fake is a literal argument", () =>
    {
        var preset = PresetParser.Parse("winws.exe --dpi-desync-fake-tls=^! --new --dpi-desync-fake-tls=^!", root);
        Equal("--dpi-desync-fake-tls=!", preset.Arguments[0]);
        Equal("--dpi-desync-fake-tls=!", preset.Arguments[2]);
    });
    Test("Relative file arguments resolve against bin", () =>
    {
        var preset = PresetParser.Parse("winws.exe --hostlist=../lists/list-general.txt", root);
        Equal(Path.Combine(root, "lists", "list-general.txt"), preset.ReferencedFiles[0]);
    });
    Test("Game filter mode and port ranges follow flag file", () =>
    {
        File.WriteAllText(Path.Combine(root, "utils", "game_filter.enabled"), "mode=udp\ntcp=2000-3000\nudp=4000-5000");
        var vars = PresetParser.ReadGameFilter(root);
        Equal("12", vars["GameFilterTCP"]);
        Equal("4000-5000", vars["GameFilterUDP"]);
    });
    Test("Domain validation supports IDN and rejects URLs", () =>
    {
        Equal("example.org", HostlistStore.NormalizeDomain(" Example.ORG. "));
        Equal("xn--e1afmkfd.xn--p1ai", HostlistStore.NormalizeDomain("пример.рф"));
        Throws<FormatException>(() => HostlistStore.NormalizeDomain("https://example.org/a"));
        Throws<FormatException>(() => HostlistStore.NormalizeDomain("-bad.example"));
    });
    Test("Adding domains is idempotent and preserves line endings", () =>
    {
        Equal("example.org\r\n", HostlistStore.AddDomain("example.org\r\n", "EXAMPLE.ORG"));
        Equal("# comment\nexample.org\n", HostlistStore.AddDomain("# comment\n", "example.org"));
    });
    Test("Atomic save writes backup and detects external edits", () =>
    {
        var path = Path.Combine(root, "lists", "list-general.txt");
        File.WriteAllText(path, "original\n");
        var store = new HostlistStore(root);
        store.SaveAsync(path, "updated\n", "original\n").GetAwaiter().GetResult();
        Equal("original\n", File.ReadAllText(path + ".bak"));
        Equal("updated\n", File.ReadAllText(path));
        Throws<IOException>(() => store.SaveAsync(path, "lost update", "original\n").GetAwaiter().GetResult());
    });
    Test("Hostlist store rejects paths outside root", () =>
        Throws<IOException>(() => new HostlistStore(root).ReadAsync(Path.Combine(Path.GetTempPath(), "outside.txt")).GetAwaiter().GetResult()));
    if (args.Length == 2 && args[0] == "--upstream")
    {
        foreach (var file in Directory.GetFiles(args[1], "*.bat"))
            Test("Upstream " + Path.GetFileName(file), () =>
            {
                var parsed = PresetParser.Parse(File.ReadAllText(file), root);
                if (parsed.Arguments.Count == 0) throw new Exception("No arguments parsed");
            });
    }
}
finally
{
    var resolved = Path.GetFullPath(root);
    if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
        || !Path.GetFileName(resolved).StartsWith("Zapret tests ")) throw new IOException("Unexpected test directory");
    Directory.Delete(resolved, true);
}
Console.WriteLine($"{passed} passed, {failures} failed");
return failures == 0 ? 0 : 1;

string CopyReference(string folder)
{
    var destination = Path.Combine(root, folder);
    var reference = Path.Combine(AppContext.BaseDirectory, "fixtures", "flowseal-1.10.3");
    foreach (var file in Directory.EnumerateFiles(reference, "*", SearchOption.AllDirectories))
    {
        var copy = Path.Combine(destination, Path.GetRelativePath(reference, file));
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.Copy(file, copy);
    }
    return destination;
}

void Test(string name, Action action)
{
    try { action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
static void Equal(string expected, string actual)
{
    if (expected != actual) throw new Exception($"Expected '{expected}', got '{actual}'");
}
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
