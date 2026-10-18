using System.Security.Cryptography;
using Zapret.Core;

namespace Zapret.Gui.Services;

public sealed record FlowsealActionRequest(string Root, FlowsealAction Action, Preset? Strategy = null);
public enum FlowsealActionOutcome { Unavailable, RecoveryRequired, ForeignInstallation, UnknownOwnership, Conflict, UnsupportedStrategy, MissingFiles, InvalidRequest, Ready, Success, ApplyFailed }
public enum FlowsealRecoveryOutcome { NotNeeded, Restored, Failed, Conflict }
public sealed record FlowsealActionResult(FlowsealActionOutcome Outcome, ServiceSnapshot State, string Explanation,
    FlowsealRecoveryOutcome Recovery = FlowsealRecoveryOutcome.NotNeeded, string? ApplyError = null,
    string? RecoveryExplanation = null, string? NextStep = null);

public interface IFlowsealAdapter
{
    Task<DistributionDescription> DescribeAsync(string root);
    Task<ServiceSnapshot> ReadStateAsync(string root);
    Task<FlowsealActionResult> PreflightAsync(FlowsealActionRequest request);
    Task<FlowsealActionResult> ExecuteAsync(FlowsealActionRequest request);
    Task<ManualSessionSnapshot> ReadManualSessionAsync() => Task.FromResult(new ManualSessionSnapshot());
    Task<InterruptedOperationSnapshot> ReadInterruptedOperationAsync(string root) => Task.FromResult(new InterruptedOperationSnapshot());
    Task<GameFilterSnapshot> ReadGameFilterAsync(string root) => throw new NotSupportedException("Настройки Game Filter недоступны.");
    Task<GameFilterSnapshot> SaveGameFilterAsync(string root, GameFilterSettings settings, string revision) => throw new NotSupportedException("Сохранение Game Filter недоступно.");
    Task<IReadOnlyList<DomainListDescription>> ReadDomainListsAsync(string root, Preset? strategy) => Task.FromResult<IReadOnlyList<DomainListDescription>>([]);
    Task<HostlistSnapshot> ReadDomainListAsync(string root, string path) => throw new NotSupportedException("Чтение списков недоступно.");
    Task<HostlistSnapshot> SaveDomainListAsync(string root, HostlistSnapshot original, string text) => throw new NotSupportedException("Сохранение списков недоступно.");
    Task ExportDomainListAsync(string root, string path, string text) => throw new NotSupportedException("Экспорт черновика недоступен.");
}

/// <summary>GUI boundary for Flowseal inspection and guarded transactional service installation.</summary>
public sealed partial class FlowsealAdapter(ICommandRunner runner, Func<Task<ServiceSnapshot>>? readServiceState = null,
    IServiceMutationBackend? mutationBackend = null, IManualProcessBackend? manualBackend = null,
    IOperationJournal? operationJournal = null) : IFlowsealAdapter
{
    // The Windows service is shared by all distributions, sessions and GUI instances.
    private const string GuardName = @"Global\Zapret.Flowseal.ServiceMutation.v1";
    private readonly FlowsealDistributionAdapter distribution = new();
    private readonly ZapretService service = new();
    public Task<DistributionDescription> DescribeAsync(string root) => Task.Run(() =>
    {
        var description = distribution.Describe(root);
        return description with { Explanation = description.ControlFilesVerified && description.MissingComponents.Count == 0
            ? "Исходные файлы Flowseal 1.10.3 проверены. Подготовка и замена автозапуска доступны после проверки принадлежности и снимка службы."
            : description.Explanation, Capabilities = description.Capabilities.Select(capability => capability.Action == FlowsealAction.InstallService
            ? capability with { Available = description.ServiceScriptVerified && description.Strategies.Any(s => s.Verified)
                && description.MissingComponents.Count == 0 && description.PathIssue == null,
                Reason = "Подготовка Flowseal 1.10.3 и установка службы с восстановлением; требуется предварительная проверка снимка. Реальная приёмка на отдельной Windows ещё не завершена." }
            : capability.Action == FlowsealAction.ChangeGameFilter ? capability with {
                Available = description.ServiceScriptVerified && ServiceOwnershipCheck.TryProvePath(Path.Combine(description.Root, "utils"), out _),
                Reason = "Настройки сохраняются в формате Flowseal 1.10.3. Работающий процесс не изменяется; для применения нужен новый пробный запуск или переустановка автозапуска." }
            : capability.Action == FlowsealAction.ManualStart ? capability with {
                Available = (readServiceState == null || (mutationBackend != null && manualBackend != null))
                    && description.ServiceScriptVerified && description.Strategies.Any(s => s.Verified)
                    && description.MissingComponents.Count == 0 && description.PathIssue == null,
                Reason = "Исходный BAT запускается в пробном сеансе. Своя служба временно останавливается и возвращается после сеанса; обновления не проверяются. Реальная приёмка Windows ещё предстоит." }
            : capability.Action == FlowsealAction.ManualStop ? capability with { Available = true,
                Reason = "Завершение только своего пробного сеанса и возврат прежнего состояния службы." }
            : IsServiceControl(capability.Action) ? capability with {
                Available = (readServiceState == null || mutationBackend != null)
                    && ServiceOwnershipCheck.TryProvePath(Path.Combine(description.Root, "bin", "winws.exe"), out _),
                Reason = "Управление только своей службой через Windows с проверкой результата. Подготовка и переустановка стратегии не выполняются; реальная приёмка на отдельной Windows ещё предстоит." }
            : capability).ToArray() };
    });
    public async Task<ServiceSnapshot> ReadStateAsync(string root) => ServiceOwnershipCheck.Classify(root,
        await (readServiceState?.Invoke() ?? service.ReadAsync()));

    public Task<FlowsealActionResult> PreflightAsync(FlowsealActionRequest request) => Task.Factory.StartNew(
        () => GuardedPreflight(request, false), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    // No cached preflight approval: execution repeats observation and all guards.
    public Task<FlowsealActionResult> ExecuteAsync(FlowsealActionRequest request) => request.Action switch
    {
        FlowsealAction.ManualStart => BeginManualAsync(request),
        FlowsealAction.ManualStop => StopManualAsync(request.Root),
        FlowsealAction.RecoverInterrupted => Task.Factory.StartNew(() => InspectInterrupted(request.Root, true).Result,
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default),
        _ => Task.Factory.StartNew(() => GuardedPreflight(request, true), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default)
    };

    private FlowsealActionResult GuardedPreflight(FlowsealActionRequest request, bool execute, ManualRun? manual = null)
    {
        var state = new ServiceSnapshot(ServiceState.Unknown, null);
        try
        {
            var before = Observe(request);
            state = before.State;
            using var guard = new Mutex(false, GuardName);
            var acquired = false;
            try
            {
                try { acquired = guard.WaitOne(0); }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                    return new(FlowsealActionOutcome.Conflict, state,
                        "Предыдущая операция оболочки прервана. Обновите состояние и повторите проверку; изменения не начаты.");
                }
                if (!acquired) return new(FlowsealActionOutcome.Conflict, state,
                    "Другая оболочка проверяет или изменяет общую службу zapret. Дождитесь завершения и обновите состояние.");
                try
                {
                    if (journal.Read() is { Phase: not OperationPhase.Completed })
                        return new(FlowsealActionOutcome.RecoveryRequired, state,
                            "Обнаружена незавершённая операция. Сначала проверьте и восстановите прежнее состояние; новые изменения службы заблокированы.");
                }
                catch (Exception error) when (Recoverable(error))
                { return new(FlowsealActionOutcome.RecoveryRequired, state, "Журнал восстановления недоступен. Изменения запрещены: " + error.Message); }
                var current = Observe(request);
                state = current.State;
                if (before.State != current.State || before.Files != current.Files)
                    return new(FlowsealActionOutcome.Conflict, state,
                        "Служба, её конфигурация или используемые файлы изменились извне. Действие прекращено; обновите состояние перед повторной попыткой.");
                var validation = Validate(request, current);
                if (validation.Outcome != FlowsealActionOutcome.Ready || !execute) return validation;
                return manual != null ? RunManual(request, current, manual)
                    : IsServiceControl(request.Action) ? ControlService(request, current) : PrepareAndReplace(request, current);
            }
            finally { if (acquired) guard.ReleaseMutex(); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
            or ArgumentException or NotSupportedException or FormatException or System.ComponentModel.Win32Exception or TimeoutException or InvalidOperationException)
        {
            if (activeRecord is { Phase: not OperationPhase.Completed })
                return new(FlowsealActionOutcome.RecoveryRequired, SafeRecoveryRead(request.Root),
                    "Операция или её журнал не завершены. Обновите состояние и выполните восстановление: " + ex.Message,
                    FlowsealRecoveryOutcome.Failed, ApplyError: ex.Message);
            return new(FlowsealActionOutcome.InvalidRequest, state,
                "Предварительная проверка не завершена; действие не начато. Проверьте доступ и исходные файлы: " + ex.Message);
        }
    }

    // Runs synchronously on the dedicated guard owner. No await can move ReleaseMutex to another thread.
    private Inputs Observe(FlowsealActionRequest request)
    {
        var state = ReadStateAsync(request.Root).GetAwaiter().GetResult();
        var description = distribution.Describe(request.Root);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Root));
        var selected = request.Strategy == null ? null : Path.GetFullPath(request.Strategy.FilePath);
        var verified = selected != null && string.Equals(Path.GetDirectoryName(selected), root, StringComparison.OrdinalIgnoreCase)
            && description.Strategies.Any(s => s.Verified && Path.GetFullPath(s.Preset.FilePath).Equals(selected, StringComparison.OrdinalIgnoreCase));
        ParsedPreset? parsed = null;
        // Preflight inspects static dependencies only. Actual port values come from upstream preparation.
        if (verified) parsed = PresetParser.Parse(File.ReadAllText(selected!), root);
        var paths = (description.RequiredComponents ?? []).Select(p => Path.Combine(root, p))
            .Concat(parsed?.ReferencedFiles ?? []).Concat(UserListSeeds.Keys.Select(name => Path.Combine(root, "lists", name)))
            .Concat([Path.Combine(root, "service.bat"), Path.Combine(root, "utils", "game_filter.enabled")]);
        if (selected != null) paths = paths.Append(selected);
        var files = string.Join('\n', paths.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Select(FileStamp));
        return new(state, description, selected, verified, parsed, files);
    }

    private static string FileStamp(string path)
    {
        if (!File.Exists(path)) return path + ":missing";
        var file = new FileInfo(path);
        return path + ":" + file.Length + ":" + file.LastWriteTimeUtc.Ticks + ":" + file.Attributes + ":"
            + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    }

    private FlowsealActionResult Validate(FlowsealActionRequest request, Inputs input)
    {
        var state = input.State;
        if (state.Ownership is ServiceOwnership.Foreign or ServiceOwnership.Unknown)
            return new(state.Ownership == ServiceOwnership.Foreign ? FlowsealActionOutcome.ForeignInstallation : FlowsealActionOutcome.UnknownOwnership,
                state, state.OwnershipReason);
        if (IsServiceControl(request.Action))
        {
            if (readServiceState != null && mutationBackend == null)
                return new(FlowsealActionOutcome.Unavailable, state, "Граница изменения службы недоступна.");
            return new(FlowsealActionOutcome.Ready, state,
                "Принадлежность проверена. Действие ограничено службой zapret выбранной поставки; её состояние будет проверено повторно.");
        }
        if (request.Action is not (FlowsealAction.InstallService or FlowsealAction.ManualStart))
            return new(FlowsealActionOutcome.Unavailable, state, request.Action == FlowsealAction.ReadState
                ? "Чтение состояния доступно отдельно; обновите состояние службы."
                : "Это действие через оболочку пока недоступно: управление своей службой и соответствие Flowseal ещё не реализованы.");
        if (request.Strategy == null || !input.Description.ServiceScriptVerified || !input.StrategyVerified
            || !ServiceOwnershipCheck.TryProvePath(input.Selected!, out _)
            || !ServiceOwnershipCheck.TryProvePath(Path.Combine(input.Description.Root, "service.bat"), out _))
            return new(FlowsealActionOutcome.UnsupportedStrategy, state,
                "Стратегия или service.bat не соответствуют проверенной Flowseal 1.10.3. Неподдерживаемый BAT не выполняется; восстановите исходные файлы.");
        if (input.Description.PathIssue != null)
            return new(FlowsealActionOutcome.InvalidRequest, state, input.Description.PathIssue);
        var flag = Path.Combine(input.Description.Root, "utils", "game_filter.enabled");
        if (File.Exists(flag) && (!ServiceOwnershipCheck.TryProvePath(flag, out _)
            || File.ReadAllBytes(flag).Any(b => !(b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z'
                or >= (byte)'0' and <= (byte)'9' or (byte)'=' or (byte)'_' or (byte)',' or (byte)';' or (byte)' '
                or (byte)'-' or (byte)'\t' or (byte)'\r' or (byte)'\n'))))
            return new(FlowsealActionOutcome.InvalidRequest, state,
                "Файл Game Filter содержит неподдерживаемые символы или является ссылкой. Для безопасного вызова Flowseal используйте ASCII-настройки mode/tcp/udp без команд CMD.");
        var missing = input.Description.MissingComponents.Where(p => !p.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            .Concat(input.Parsed!.ReferencedFiles.Where(p => !File.Exists(p) && !CanInitializeUserList(input.Description.Root, p))).ToArray();
        if (missing.Length != 0)
            return new(FlowsealActionOutcome.MissingFiles, state,
                "Не хватает обязательных файлов: " + string.Join(", ", missing) + ". Распакуйте полный архив; действие не начато.");
        if ((input.Description.RequiredComponents ?? []).Select(p => Path.Combine(input.Description.Root, p))
            .Concat(input.Parsed.ReferencedFiles).Any(p => !ServiceOwnershipCheck.TryProvePath(p, out _) && !CanInitializeUserList(input.Description.Root, p)))
            return new(FlowsealActionOutcome.InvalidRequest, state,
                "Используемые файлы проходят через ссылки или недоступны для проверки. Выберите локальную распакованную поставку; действие не начато.");
        if ((request.Action == FlowsealAction.ManualStart && readServiceState != null && (manualBackend == null || mutationBackend == null))
            || (request.Action == FlowsealAction.InstallService && readServiceState != null && mutationBackend == null && state.Ownership == ServiceOwnership.Missing))
            return new(FlowsealActionOutcome.Unavailable, state,
                "Запуск и установка через оболочку пока недоступны: подготовка и соответствие выполнения Flowseal ещё не проверены. Используйте исходную поставку.");
        if ((readServiceState != null && mutationBackend == null) || !SupportsRecovery(state))
            return new(FlowsealActionOutcome.RecoveryRequired, state,
                "Полный поддерживаемый снимок службы получить нельзя. Изменения не начаты: " + (state.ObservationError ?? "параметры восстановления не подтверждены."));
        if (request.Action == FlowsealAction.ManualStart)
        {
            (manualBackend ?? new WindowsManualProcessBackend()).EnsureNoOtherWinws(state.Process);
            return new(FlowsealActionOutcome.Ready, state,
                "Будет выполнен исходный BAT. Своя работающая служба временно остановится и вернётся после завершения сеанса или закрытия окна. Автозапуск не меняется. Обновления не проверяются; TCP timestamps и созданные списки сохранятся.");
        }
        return new(FlowsealActionOutcome.Ready, state,
            "Снимок и стратегия проверены. При установке будут подготовлены пользовательские списки, TCP timestamps и Game Filter из Flowseal 1.10.3.");
    }

    private sealed record Inputs(ServiceSnapshot State, DistributionDescription Description, string? Selected,
        bool StrategyVerified, ParsedPreset? Parsed, string Files);
}
