using System.ComponentModel;
using Zapret.Core;
using Zapret.Gui.Services;

internal static class ActualStateScenarios
{
    public static async Task Run(string root)
    {
        var request = new FlowsealActionRequest(root, FlowsealAction.InstallService, new("general", Path.Combine(root, "general.bat")));
        foreach (var error in new Exception[] { new UnauthorizedAccessException("native denied 5"), new Win32Exception(5) })
        {
            IFlowsealAdapter adapter = new FlowsealAdapter(new RejectCommands(), () => Task.FromException<ServiceSnapshot>(error));
            foreach (var result in new[] { await adapter.PreflightAsync(request), await adapter.ExecuteAsync(request) })
                if (result.Outcome != FlowsealActionOutcome.InvalidRequest || result.State.State != ServiceState.Unknown
                    || !result.Explanation.Contains("доступ", StringComparison.OrdinalIgnoreCase)
                    || result.Explanation.Contains(error.Message) || result.ApplyError != error.Message)
                    throw new Exception("Denied observation must have a plain-language cause, unknown state and separate native details: " + result);
        }
        var state = new ServiceSnapshot(ServiceState.Stopped, "external BAT",
            new(CommandLine.Quote(Path.Combine(root, "bin", "winws.exe"))), ObservationError: "QueryServiceConfig: 1734");
        IFlowsealAdapter partial = new FlowsealAdapter(new RejectCommands(), () => Task.FromResult(state), new MemoryService(state));
        var preflight = await partial.PreflightAsync(request);
        if (preflight.Outcome != FlowsealActionOutcome.RecoveryRequired || preflight.Explanation.Contains("QueryServiceConfig")
            || preflight.ApplyError != "QueryServiceConfig: 1734")
            throw new Exception("Incomplete snapshot must separate system diagnostics from recovery guidance: " + preflight);
        Console.WriteLine("PASS adapter denied observation and incomplete recovery snapshot separate human reasons from native details");
    }
}
