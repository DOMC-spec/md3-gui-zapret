using System.Text.RegularExpressions;
using Zapret.Core;

namespace Zapret.Gui.Services;

public sealed partial class FlowsealAdapter
{
    private static IReadOnlyDictionary<string, string> UserListSeeds => HostlistStore.UserListSeeds;

    private static bool CanInitializeUserList(string root, string path) =>
        UserListSeeds.Keys.Any(name => Path.Combine(root, "lists", name).Equals(path, StringComparison.OrdinalIgnoreCase))
        && !File.Exists(path) && !Directory.Exists(path)
        && ServiceOwnershipCheck.TryProvePath(Path.GetDirectoryName(path)!, out _);

    private FlowsealActionResult PrepareAndReplace(FlowsealActionRequest request, Inputs input)
    {
        Inputs prepared;
        try { BeginRecordedOperation(request, input); prepared = Prepare(request, input); RecordPrepared(prepared); }
        catch (Exception error) when (Recoverable(error))
        {
            ServiceSnapshot observed;
            try { observed = ReadStateAsync(request.Root).GetAwaiter().GetResult(); }
            catch (Exception observationError) when (Recoverable(observationError))
            { observed = new(ServiceState.Unknown, null, ObservationError: observationError.Message); }
            return FinishRecordedOperation(new(FlowsealActionOutcome.ApplyFailed, observed, "Подготовка запуска не завершена. Оболочка не изменяла службу. " + ExplainFailure(error),
                error is ServiceConflictException ? FlowsealRecoveryOutcome.Conflict : FlowsealRecoveryOutcome.NotNeeded,
                error.Message, "Восстановление службы не требовалось: остановка и замена ещё не начинались.",
                "Проверьте причину и обновите состояние. Созданные пользовательские списки сохранены; TCP timestamps могли быть включены."));
        }
        return FinishRecordedOperation(Replace(request, prepared, RecordingBackend(mutationBackend ?? service, request.Root)));
    }

    private Inputs Prepare(FlowsealActionRequest request, Inputs input)
    {
        var result = ExportGameFilter(request, input);
        if (result.ExitCode != 0) throw new IOException("Не удалось получить настройки Game Filter из Flowseal: " + result.Output);
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = line.Split('=', 2);
            if (pair.Length != 2 || pair[0] is not ("GameFilter" or "GameFilterTCP" or "GameFilterUDP")) continue;
            if (!Regex.IsMatch(pair[1], @"^\d+(?:-\d+)?(?:,\d+(?:-\d+)?)*$") || !variables.TryAdd(pair[0], pair[1]))
                throw new IOException("Flowseal вернул непроверяемые настройки Game Filter.");
        }
        if (variables.Count != 3) throw new IOException("Flowseal не экспортировал все настройки Game Filter.");
        var expected = input;
        CheckUnchanged();
        foreach (var seed in UserListSeeds)
        {
            var path = Path.Combine(input.Description.Root, "lists", seed.Key);
            if (File.Exists(path)) continue;
            CheckUnchanged();
            if (!CanInitializeUserList(input.Description.Root, path)) throw new IOException("Нельзя безопасно создать пользовательский список: " + path);
            var bytes = System.Text.Encoding.ASCII.GetBytes(seed.Value);
            try
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                stream.Write(bytes);
            }
            catch (IOException) when (File.Exists(path))
            { throw new ServiceConflictException("Пользовательский список появился или изменился во время подготовки; перезапись запрещена."); }
            var stamp = FileStamp(path);
            if (!stamp.EndsWith(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), StringComparison.Ordinal))
                throw new ServiceConflictException("Созданный пользовательский список изменён извне.");
            expected = expected with { Files = string.Join('\n', expected.Files.Split('\n').Select(line => line == path + ":missing" ? stamp : line)) };
        }
        CheckUnchanged();
        var tcp = runner.RunAsync(Path.Combine(Environment.SystemDirectory, "netsh.exe"),
            "interface", "tcp", "set", "global", "timestamps=enabled").GetAwaiter().GetResult();
        if (tcp.ExitCode != 0) throw new IOException("Не удалось включить TCP timestamps: " + tcp.Output);
        CheckUnchanged();
        return expected with { Parsed = PresetParser.Parse(File.ReadAllText(input.Selected!), input.Description.Root, variables) };

        void CheckUnchanged()
        {
            var observed = Observe(request);
            if (observed.Files != expected.Files || !SameObservation(input.State, observed.State))
                throw new ServiceConflictException("Служба или файлы изменились во время подготовки.");
        }
    }

    private CommandResult ExportGameFilter(FlowsealActionRequest request, Inputs input)
    {
        // Freeze the verified script and data before CMD reads them. A newly appearing flag in the
        // selected distribution cannot become executable CMD input between checking and reading.
        using var source = new FileStream(Path.Combine(input.Description.Root, "service.bat"), FileMode.Open, FileAccess.Read, FileShare.Read);
        var flag = Path.Combine(input.Description.Root, "utils", "game_filter.enabled");
        using var settings = File.Exists(flag) ? new FileStream(flag, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
        var observed = Observe(request);
        if (observed.Files != input.Files || !SameObservation(input.State, observed.State))
            throw new ServiceConflictException("Исходные файлы изменились перед подготовкой.");
        return ExportFrozenGameFilter(source, settings);
    }

    private CommandResult ExportFrozenGameFilter(Stream source, Stream? settings)
    {
        var stage = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Zapret.Flowseal.Prepare." + Guid.NewGuid().ToString("N")));
        if (stage.Any(c => c > 127 || "&|<>^%!\"\r\n".Contains(c)))
            throw new IOException("Временная папка не подходит для безопасного вызова CMD. Используйте локальный ASCII-путь TEMP.");
        try
        {
            Directory.CreateDirectory(Path.Combine(stage, "utils"));
            using (var target = File.Create(Path.Combine(stage, "service.bat"))) source.CopyTo(target);
            using (var target = File.Create(Path.Combine(stage, "utils", "game_filter.enabled"))) settings?.CopyTo(target);
            using var frozenScript = new FileStream(Path.Combine(stage, "service.bat"), FileMode.Open, FileAccess.Read, FileShare.Read);
            using var frozenSettings = new FileStream(Path.Combine(stage, "utils", "game_filter.enabled"), FileMode.Open, FileAccess.Read, FileShare.Read);
            // CALL and SET run in one CMD: values are explicitly exported from its environment.
            return runner.RunAsync(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d", "/v:off", "/c",
                $"set \"GameFilter=\" & set \"GameFilterTCP=\" & set \"GameFilterUDP=\" & cd /d \"{stage}\" && set \"PATH={Environment.SystemDirectory};{Environment.GetFolderPath(Environment.SpecialFolder.Windows)}\" && call \"{Path.Combine(stage, "service.bat")}\" load_game_filter >nul & set GameFilter").GetAwaiter().GetResult();
        }
        finally
        {
            var temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!stage.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(stage).StartsWith("Zapret.Flowseal.Prepare."))
                throw new IOException("Непроверяемый путь временной подготовки.");
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
        }
    }
}
