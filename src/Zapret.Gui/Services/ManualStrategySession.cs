using Zapret.Core;

namespace Zapret.Gui.Services;

public sealed partial class FlowsealAdapter
{
    private readonly object manualGate = new();
    private ManualRun? manualRun;
    private ManualSessionSnapshot manualState = new();
    public Task<ManualSessionSnapshot> ReadManualSessionAsync() => Task.FromResult(Volatile.Read(ref manualState));

    private sealed class ManualRun(string root)
    {
        public string Root { get; } = Path.GetFullPath(root);
        public ManualResetEventSlim StopRequested { get; } = new();
        public TaskCompletionSource<FlowsealActionResult> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<FlowsealActionResult> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private Task<FlowsealActionResult> BeginManualAsync(FlowsealActionRequest request)
    {
        lock (manualGate)
        {
            if (manualRun is { Finished.Task.IsCompleted: false })
                return Task.FromResult(new FlowsealActionResult(FlowsealActionOutcome.Conflict, new(ServiceState.Unknown, null),
                    "Свой пробный сеанс уже выполняется. Сначала завершите его."));
            var run = new ManualRun(request.Root);
            manualRun = run;
            Volatile.Write(ref manualState, new(ManualSessionState.Starting, request.Root, request.Strategy?.Name,
                Explanation: "Подготовка пробного сеанса…"));
            _ = Task.Factory.StartNew(() =>
            {
                var result = GuardedPreflight(request, true, run);
                // GuardedPreflight releases the named mutex on this same thread before completion.
                Volatile.Write(ref manualState, new(result.Outcome == FlowsealActionOutcome.Success
                    ? ManualSessionState.Finished : ManualSessionState.Failed, request.Root, request.Strategy?.Name,
                    Explanation: result.Explanation, Result: result));
                run.Started.TrySetResult(result);
                run.Finished.TrySetResult(result);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            return run.Started.Task;
        }
    }

    private Task<FlowsealActionResult> StopManualAsync(string root)
    {
        lock (manualGate)
        {
            if (manualRun == null)
                return Task.FromResult(new FlowsealActionResult(FlowsealActionOutcome.InvalidRequest, new(ServiceState.Unknown, null),
                    "Свой пробный сеанс не запускался."));
            if (!Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).Equals(
                Path.TrimEndingDirectorySeparator(manualRun.Root), StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new FlowsealActionResult(FlowsealActionOutcome.InvalidRequest, new(ServiceState.Unknown, null),
                    "Пробный сеанс относится к другой папке. Сначала вернитесь к его поставке."));
            manualRun.StopRequested.Set();
            return manualRun.Finished.Task;
        }
    }

    // This worker owns the global service mutex for the whole session, including restoration.
    private FlowsealActionResult RunManual(FlowsealActionRequest request, Inputs input, ManualRun run)
    {
        var backend = mutationBackend ?? service;
        var processes = manualBackend ?? new WindowsManualProcessBackend();
        var expected = input.State;
        var prepared = input;
        var serviceStopAttempted = false;
        IManualProcessSession? process = null;
        FileStream? strategyLock = null;
        FileStream? serviceLock = null;
        FileStream? settingsLock = null;
        Exception? failure = null;
        var processStopped = true;
        try
        {
            strategyLock = File.Open(input.Selected!, FileMode.Open, FileAccess.Read, FileShare.Read);
            serviceLock = File.Open(Path.Combine(input.Description.Root, "service.bat"), FileMode.Open, FileAccess.Read, FileShare.Read);
            settingsLock = LockManualSettings(ref input);
            prepared = input;
            CheckUnchanged();
            prepared = Prepare(request, input);
            CheckUnchanged();
            processes.EnsureNoOtherWinws(expected.Process);
            if (expected.State == ServiceState.Running)
            {
                serviceStopAttempted = true;
                backend.Stop(expected);
                expected = WaitForService(expected, ServiceState.Stopped);
            }
            CheckUnchanged();
            processes.EnsureNoOtherWinws(null);
            process = processes.Launch(input.Description.Root, input.Selected!);
            processStopped = false;
            ServiceProcess? identity = null;
            var stable = 0;
            var deadline = Environment.TickCount64 + 10000;
            while (!run.StopRequested.IsSet)
            {
                CheckUnchanged();
                var observed = process.Observe();
                if (observed.Exited) throw new IOException("Пробная стратегия завершилась. Выход cmd сам по себе не подтверждает работу winws.");
                if (observed.Process is { } found)
                {
                    if (found.Id == 0 || found.StartedAtUtcTicks == null || found.Executable == null
                        || !ServiceOwnershipCheck.TryProvePath(found.Executable, out var actual)
                        || !actual.Equals(Path.Combine(input.Description.Root, "bin", "winws.exe"), StringComparison.OrdinalIgnoreCase)
                        || (identity != null && identity != found))
                        throw new ServiceConflictException("Путь или идентичность пробного winws не подтверждены.");
                    identity = found;
                    if (++stable == 4)
                    {
                        Volatile.Write(ref manualState, new(ManualSessionState.Running, request.Root, request.Strategy?.Name,
                            identity, "Пробный winws работает. Автозапуск не изменён; обновления не проверялись. Работа процесса не подтверждает доступность видео или голоса."));
                        run.Started.TrySetResult(new(FlowsealActionOutcome.Success, expected, manualState.Explanation));
                    }
                }
                else if (identity != null) throw new IOException("Наблюдаемый пробный winws завершился.");
                if (stable < 4 && Environment.TickCount64 >= deadline) throw new TimeoutException("Исходный BAT не запустил наблюдаемый winws за 10 секунд.");
                run.StopRequested.Wait(250);
            }
        }
        catch (Exception error) when (Recoverable(error)) { failure = error; }
        finally
        {
            Volatile.Write(ref manualState, manualState with { State = ManualSessionState.Stopping,
                Explanation = "Завершение своего пробного процесса и возврат службы…" });
            if (process != null)
            {
                try { process.Stop(); processStopped = true; }
                catch (Exception error) when (Recoverable(error)) { failure = error; }
                finally
                {
                    try { process.Dispose(); }
                    catch (Exception error) when (Recoverable(error)) { failure = error; processStopped = false; }
                }
            }
            // Keep CMD inputs frozen until even a launcher stopped during startup is no longer executing.
            settingsLock?.Dispose();
            serviceLock?.Dispose();
            strategyLock?.Dispose();
        }
        var recovery = FlowsealRecoveryOutcome.NotNeeded;
        string recoveryText;
        try
        {
            if (!processStopped) throw new IOException("Завершение своего процесса не подтверждено; служба автоматически не запускается.");
            if (failure is ServiceConflictException) throw failure;
            if (serviceStopAttempted)
            {
                if (expected.State is ServiceState.Running or ServiceState.Stopping)
                {
                    // Stop can fail after its side effect. Observe before deciding whether to restore.
                    var actual = ReadStateAsync(request.Root).GetAwaiter().GetResult();
                    if (!SameEntry(actual, input.State) || (actual.State == ServiceState.Running && actual.Process != input.State.Process))
                        throw new ServiceConflictException("Служба изменилась после остановки; восстановление прекращено.");
                    expected = actual.State == ServiceState.Stopping ? WaitForService(actual, ServiceState.Stopped) : actual;
                }
                if (expected.State == ServiceState.Stopped)
                {
                    CheckUnchanged();
                    processes.EnsureNoOtherWinws(null);
                    backend.Start(expected);
                    expected = WaitForService(expected, ServiceState.Running);
                    for (var sample = 0; sample < 3; sample++) { Thread.Sleep(400); CheckUnchanged(); }
                }
                recovery = FlowsealRecoveryOutcome.Restored;
            }
            else if (!SameObservation(input.State, SafeRead()))
                throw new ServiceConflictException("Исходная служба изменилась извне; автоматические действия прекращены.");
            recoveryText = "Исходная регистрация и состояние службы сохранены" + (serviceStopAttempted ? "; прежняя служба снова работает." : ".");
        }
        catch (Exception error) when (Recoverable(error))
        {
            recovery = error is ServiceConflictException ? FlowsealRecoveryOutcome.Conflict : FlowsealRecoveryOutcome.Failed;
            recoveryText = "Возврат состояния не подтверждён: " + error.Message;
            failure ??= error;
        }
        var finalState = SafeRead();
        return new(failure == null ? FlowsealActionOutcome.Success
            : recovery == FlowsealRecoveryOutcome.Conflict ? FlowsealActionOutcome.Conflict : FlowsealActionOutcome.ApplyFailed,
            finalState, failure == null ? "Пробный сеанс завершён. Обновления не проверялись." : "Пробный сеанс завершён с ошибкой: " + failure.Message,
            recovery, failure?.Message, recoveryText,
            recovery is FlowsealRecoveryOutcome.Failed or FlowsealRecoveryOutcome.Conflict
                ? "Обновите состояние и проверьте службу вручную. Сторонние изменения не перезаписываются."
                : "TCP timestamps и созданные пользовательские списки сохраняются. Автозапуск не изменён.");

        ServiceSnapshot SafeRead()
        {
            try { return ReadStateAsync(request.Root).GetAwaiter().GetResult(); }
            catch (Exception error) when (Recoverable(error)) { return new(ServiceState.Unknown, null, ObservationError: error.Message); }
        }
        void CheckUnchanged()
        {
            var current = Observe(request);
            if (current.Files != prepared.Files || !SameObservation(expected, current.State))
                throw new ServiceConflictException("Служба или используемые файлы изменились извне. Стороннее изменение сохранено.");
        }
        ServiceSnapshot WaitForService(ServiceSnapshot entry, ServiceState target)
        {
            var deadline = Environment.TickCount64 + 10000;
            while (true)
            {
                var current = Observe(request);
                if (current.Files != prepared.Files || !SameEntry(current.State, entry))
                    throw new ServiceConflictException("Служба или файлы изменились во время перехода.");
                if (current.State.State == target && current.State.Ownership == ServiceOwnership.Owned) return current.State;
                if (current.State.State != (target == ServiceState.Running ? ServiceState.Starting : ServiceState.Stopping))
                    throw new IOException("Служба не достигла состояния " + target);
                if (Environment.TickCount64 >= deadline) throw new TimeoutException("Истекло время ожидания службы: " + target);
                Thread.Sleep(100);
            }
        }
    }

    // Upstream interpolates this data into CALL. Keep validated bytes immutable through its entire execution.
    private static FileStream LockManualSettings(ref Inputs input)
    {
        var path = Path.Combine(input.Description.Root, "utils", "game_filter.enabled");
        var wasMissing = input.Files.Split('\n').Contains(path + ":missing");
        if (wasMissing)
        {
            if (!ServiceOwnershipCheck.TryProvePath(Path.GetDirectoryName(path)!, out _))
                throw new IOException("Папка настроек Game Filter недоступна или проходит через ссылку.");
            // Empty and absent both mean disabled in pinned 1.10.3. Reserve the absent name without overwriting a raced creation.
            using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { }
        }
        var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (!ServiceOwnershipCheck.TryProvePath(path, out _) || (wasMissing && stream.Length != 0))
                throw new ServiceConflictException("Настройки Game Filter изменились до блокировки чтения.");
            if (wasMissing)
                input = input with { Files = string.Join('\n', input.Files.Split('\n').Select(stamp =>
                    stamp == path + ":missing" ? FileStamp(path) : stamp)) };
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }
}
