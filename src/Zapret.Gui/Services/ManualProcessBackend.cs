namespace Zapret.Gui.Services;

public enum ManualSessionState { Inactive, Starting, Running, Stopping, Finished, Failed }
public sealed record ManualSessionSnapshot(ManualSessionState State = ManualSessionState.Inactive,
    string? Root = null, string? Strategy = null, ServiceProcess? Process = null,
    string Explanation = "Пробный сеанс не запущен.", FlowsealActionResult? Result = null)
{
    public bool IsActive => State is ManualSessionState.Starting or ManualSessionState.Running or ManualSessionState.Stopping;
}

public sealed record ManualProcessObservation(ServiceProcess? Process, bool LauncherExited, bool Exited);

/// <summary>System boundary. The returned session owns only the launched BAT and its descendants.</summary>
public interface IManualProcessBackend
{
    void EnsureNoOtherWinws(ServiceProcess? serviceProcess);
    IManualProcessSession Launch(string root, string strategyPath);
}

public interface IManualProcessSession : IDisposable
{
    ManualProcessObservation Observe();
    void Stop();
}
