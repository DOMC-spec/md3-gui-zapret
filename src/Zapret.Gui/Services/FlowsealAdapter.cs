using System.Security.Cryptography;
using Zapret.Core;

namespace Zapret.Gui.Services;

public sealed record FlowsealActionRequest(string Root, FlowsealAction Action, Preset? Strategy = null);
public enum FlowsealActionOutcome { Unavailable, RecoveryRequired, ForeignInstallation, UnknownOwnership, Conflict, UnsupportedStrategy, MissingFiles, InvalidRequest }
public sealed record FlowsealActionResult(FlowsealActionOutcome Outcome, ServiceSnapshot State, string Explanation);

public interface IFlowsealAdapter
{
    Task<DistributionDescription> DescribeAsync(string root);
    Task<ServiceSnapshot> ReadStateAsync(string root);
    Task<FlowsealActionResult> PreflightAsync(FlowsealActionRequest request);
    Task<FlowsealActionResult> ExecuteAsync(FlowsealActionRequest request);
}

/// <summary>GUI boundary for read-only Flowseal inspection and guarded refusal of unsupported actions.</summary>
public sealed class FlowsealAdapter(ICommandRunner runner, Func<Task<ServiceSnapshot>>? readServiceState = null) : IFlowsealAdapter
{
    // The Windows service is shared by all distributions, sessions and GUI instances.
    private const string GuardName = @"Global\Zapret.Flowseal.ServiceMutation.v1";
    private readonly FlowsealDistributionAdapter distribution = new();
    private readonly ZapretService service = new();
    public Task<DistributionDescription> DescribeAsync(string root) => Task.Run(() => distribution.Describe(root));
    public async Task<ServiceSnapshot> ReadStateAsync(string root) => ServiceOwnershipCheck.Classify(root,
        await (readServiceState?.Invoke() ?? service.ReadAsync()));

    public Task<FlowsealActionResult> PreflightAsync(FlowsealActionRequest request) => Task.Factory.StartNew(
        () => GuardedPreflight(request), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    // No cached preflight approval: the public action boundary always re-observes before refusing.
    public Task<FlowsealActionResult> ExecuteAsync(FlowsealActionRequest request) => PreflightAsync(request);

    private FlowsealActionResult GuardedPreflight(FlowsealActionRequest request)
    {
        _ = runner; // Retained OS command boundary; this stage has no execution capability.
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
                var current = Observe(request);
                state = current.State;
                if (before.State != current.State || before.Files != current.Files)
                    return new(FlowsealActionOutcome.Conflict, state,
                        "Служба, её конфигурация или используемые файлы изменились извне. Действие прекращено; обновите состояние перед повторной попыткой.");
                return Validate(request, current);
            }
            finally { if (acquired) guard.ReleaseMutex(); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
            or ArgumentException or NotSupportedException or FormatException or System.ComponentModel.Win32Exception)
        {
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
        if (verified) parsed = PresetParser.Parse(File.ReadAllText(selected!), root, PresetParser.ReadGameFilter(root));
        var paths = (description.RequiredComponents ?? []).Select(p => Path.Combine(root, p))
            .Concat(parsed?.ReferencedFiles ?? []).Concat([Path.Combine(root, "service.bat"), Path.Combine(root, "utils", "game_filter.enabled")]);
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

    private static FlowsealActionResult Validate(FlowsealActionRequest request, Inputs input)
    {
        var state = input.State;
        if (state.Ownership is ServiceOwnership.Foreign or ServiceOwnership.Unknown)
            return new(state.Ownership == ServiceOwnership.Foreign ? FlowsealActionOutcome.ForeignInstallation : FlowsealActionOutcome.UnknownOwnership,
                state, state.OwnershipReason);
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
        var missing = input.Description.MissingComponents.Where(p => !p.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            .Concat(input.Parsed!.ReferencedFiles.Where(p => !File.Exists(p))).ToArray();
        if (missing.Length != 0)
            return new(FlowsealActionOutcome.MissingFiles, state,
                "Не хватает обязательных файлов: " + string.Join(", ", missing) + ". Распакуйте полный архив; действие не начато.");
        if ((input.Description.RequiredComponents ?? []).Select(p => Path.Combine(input.Description.Root, p))
            .Concat(input.Parsed.ReferencedFiles).Any(p => !ServiceOwnershipCheck.TryProvePath(p, out _)))
            return new(FlowsealActionOutcome.InvalidRequest, state,
                "Используемые файлы проходят через ссылки или недоступны для проверки. Выберите локальную распакованную поставку; действие не начато.");
        if (request.Action != FlowsealAction.InstallService || state.Ownership == ServiceOwnership.Missing)
            return new(FlowsealActionOutcome.Unavailable, state,
                "Запуск и установка через оболочку пока недоступны: подготовка и соответствие выполнения Flowseal ещё не проверены. Используйте исходную поставку.");
        return new(FlowsealActionOutcome.RecoveryRequired, state,
            "Замена автозапуска недоступна в этой версии: восстановление прежней службы при ошибке ещё не поддерживается.");
    }

    private sealed record Inputs(ServiceSnapshot State, DistributionDescription Description, string? Selected,
        bool StrategyVerified, ParsedPreset? Parsed, string Files);
}
