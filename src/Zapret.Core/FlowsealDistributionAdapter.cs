using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Zapret.Core;

public enum FlowsealAction { ReadState, ManualStart, InstallService, StartService, StopService, RemoveService, ChangeGameFilter, EditLists, Tests, ChangeIpSet, UpdateIpSet, UpdateHosts, Diagnostics, CheckUpdates, ChangeUpdateChecks, ReplaceActiveFakes }
public sealed record FlowsealCapability(FlowsealAction Action, bool Available, string Reason);
public sealed record DistributionStrategy(Preset Preset, bool Verified, string Explanation);
public sealed record DistributionDescription(string Root, string? DeclaredVersion, bool ControlFilesVerified,
    IReadOnlyList<DistributionStrategy> Strategies, IReadOnlyList<string> MissingComponents,
    string? PathIssue, string Explanation, IReadOnlyList<FlowsealCapability> Capabilities, bool ServiceScriptVerified = false, IReadOnlyList<string>? RequiredComponents = null);

/// <summary>Inspects release files only. It never executes BAT or changes settings.</summary>
public sealed class FlowsealDistributionAdapter
{
    private static readonly ReleaseManifest Manifest = LoadManifest();

    public DistributionDescription Describe(string root)
    {
        try { root = Path.GetFullPath(root); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return Result(root, null, false, [], [], null, "Некорректный путь. Выберите распакованную папку Flowseal."); }
        if (!Directory.Exists(root)) return Result(root, null, false, [], [], null,
            "Папка поставки не найдена. Выберите распакованную папку Flowseal.");
        try
        {
            var service = Path.Combine(root, "service.bat");
            var version = File.Exists(service) ? Regex.Match(File.ReadAllText(service), "(?im)^set \\\"LOCAL_VERSION=([^\\\"]+)").Groups[1].Value : null;
            var missing = Manifest.ControlFiles.Where(c => c.Kind != "tool").Select(c => c.Path)
                .Concat(Manifest.RequiredComponents).Where(path => !File.Exists(Path.Combine(root, path))).ToArray();
            var strategies = Directory.EnumerateFiles(root, "*.bat")
                .Where(file => !Path.GetFileName(file).Equals("service.bat", StringComparison.OrdinalIgnoreCase))
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Select(file =>
                {
                    var verified = Matches(file, Manifest.ControlFiles.FirstOrDefault(c => c.Kind == "strategy" && c.Path.Equals(Path.GetFileName(file), StringComparison.OrdinalIgnoreCase)));
                    return new DistributionStrategy(new Preset(Path.GetFileNameWithoutExtension(file), file), verified,
                        verified ? "Исходный файл стратегии соответствует Flowseal 1.10.3." : "Стратегия изменена или неизвестна. Выполнение не проверено.");
                }).ToArray();
            var controlsVerified = Matches(service, Manifest.ControlFiles.Single(c => c.Kind == "service"))
                && strategies.Count(s => s.Verified) == Manifest.ControlFiles.Count(c => c.Kind == "strategy") && strategies.All(s => s.Verified);
            var pathIssue = root.Any(c => c > 127) ? "Путь содержит кириллицу или другие не-ASCII символы: upstream предупреждает об ограничении. Для выполнения выберите простой путь, например C:\\Zapret. Чтение доступно."
                : root.IndexOfAny(['&', '|', '<', '>', '^', '%', '!', '\r', '\n']) >= 0 ? "Путь содержит специальные символы CMD. Для выполнения выберите путь без них. Чтение доступно."
                : root.Contains("OneDrive", StringComparison.OrdinalIgnoreCase) ? "Поставка находится в OneDrive: upstream предупреждает об ограничении. Для выполнения выберите локальную папку. Чтение доступно." : null;
            var explanation = !controlsVerified ? "Управляющий скрипт или стратегии отсутствуют, изменены либо неизвестны. Выберите исходную поставку Flowseal 1.10.3; чтение состояния доступно."
                : missing.Length != 0 ? "Не хватает компонентов поставки. Распакуйте полный архив Flowseal в выбранную папку."
                : "Файлы запуска и 22 стратегии Flowseal 1.10.3 соответствуют эталону. Запуск и управление службой пока недоступны: их совместимость ещё не проверена.";
            var result = Result(root, string.IsNullOrEmpty(version) ? null : version, controlsVerified, strategies, missing, pathIssue, explanation);
            var editableLists = Matches(service, Manifest.ControlFiles.Single(c => c.Kind == "service")) && Directory.Exists(Path.Combine(root, "lists"));
            var testsVerified = Matches(Path.Combine(root, "utils", "test zapret.ps1"), Manifest.ControlFiles.Single(c => c.Kind == "tool"));
            return result with { ServiceScriptVerified = Matches(service, Manifest.ControlFiles.Single(c => c.Kind == "service")), Capabilities = result.Capabilities.Select(capability => capability.Action switch
            {
                FlowsealAction.EditLists => new FlowsealCapability(capability.Action, editableLists, editableLists
                    ? "Редактирование пользовательских списков доступно с резервной копией и проверкой внешних изменений."
                    : "Для редактирования списков выберите исходную поставку с папкой lists."),
                FlowsealAction.Tests when !testsVerified => capability with { Reason = "utils/test zapret.ps1 отсутствует или изменён. Инструмент не проверен; графическое выполнение тестов пока недоступно." },
                _ => capability
            }).ToArray() };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return Result(root, null, false, [], [], null, "Не удалось прочитать поставку. Проверьте доступ к папке: " + ex.Message); }
    }

    private static DistributionDescription Result(string root, string? version, bool verified,
        IReadOnlyList<DistributionStrategy> strategies, IReadOnlyList<string> missing, string? pathIssue, string explanation) =>
        new(root, version, verified, strategies, missing, pathIssue, explanation,
            Enum.GetValues<FlowsealAction>().Select(action => new FlowsealCapability(action,
                action == FlowsealAction.ReadState, action == FlowsealAction.ReadState
                    ? "Чтение состояния доступно без выполнения BAT."
                    : pathIssue ?? (missing.Count > 0 ? "Отсутствуют компоненты поставки; распакуйте полный архив. " : "")
                        + (!verified ? "Исходные управляющие файлы не подтверждены. " : "")
                        + "Действие пока недоступно: соответствие выполнения Flowseal ещё не проверено.")).ToArray(), RequiredComponents: Manifest.RequiredComponents);

    private static bool Matches(string path, ControlFile? reference) => reference != null && File.Exists(path)
        && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).Equals(reference.Sha256, StringComparison.OrdinalIgnoreCase);
    private static ReleaseManifest LoadManifest()
    {
        using var stream = typeof(FlowsealDistributionAdapter).Assembly.GetManifestResourceStream("Zapret.Core.Compatibility.flowseal-1.10.3.json")!;
        return JsonSerializer.Deserialize<ReleaseManifest>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
    private sealed record ControlFile(string Path, string Kind, string Sha256);
    private sealed record ReleaseManifest(IReadOnlyList<ControlFile> ControlFiles, IReadOnlyList<string> RequiredComponents);
}
