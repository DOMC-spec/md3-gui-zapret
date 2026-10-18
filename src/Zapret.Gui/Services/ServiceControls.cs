using Zapret.Core;

namespace Zapret.Gui.Services;

public sealed partial class FlowsealAdapter
{
    private static bool IsServiceControl(FlowsealAction action) => action is
        FlowsealAction.StartService or FlowsealAction.StopService or FlowsealAction.RemoveService;

    private FlowsealActionResult ControlService(FlowsealActionRequest request, Inputs input)
    {
        var backend = mutationBackend ?? service;
        var expected = input.State;
        var deleting = false;
        try
        {
            CheckUnchanged();
            if (expected.State == ServiceState.Missing)
                return new(request.Action == FlowsealAction.StartService ? FlowsealActionOutcome.InvalidRequest : FlowsealActionOutcome.Success,
                    expected, "Служба отсутствует; автозапуск не установлен. Для запуска сначала включите автозапуск выбранной стратегии.");
            if (expected.State is not (ServiceState.Stopped or ServiceState.Running))
                return new(FlowsealActionOutcome.InvalidRequest, expected, "Служба не находится в поддерживаемом устойчивом состоянии. Обновите состояние.");
            if (request.Action == FlowsealAction.StartService)
            {
                if (expected.State == ServiceState.Stopped)
                {
                    backend.Start(expected);
                    expected = Wait(ServiceState.Running);
                }
                // Detect early exit and another process taking over the same configuration.
                for (var sample = 0; sample < 3; sample++) { Thread.Sleep(400); CheckUnchanged(); }
                return new(FlowsealActionOutcome.Success, expected,
                    "Служба работает. Регистрация автозапуска сохранена. Работа службы не подтверждает доступность видео или голоса.");
            }
            if (expected.State == ServiceState.Running)
            {
                backend.Stop(expected);
                expected = Wait(ServiceState.Stopped);
            }
            CheckUnchanged();
            if (request.Action == FlowsealAction.StopService)
                return new(FlowsealActionOutcome.Success, expected,
                    "Служба остановлена временно. Регистрация и тип запуска сохранены; автоматический запуск при загрузке Windows остаётся включён, если был включён ранее.");
            backend.Delete(expected);
            deleting = true;
            expected = Wait(ServiceState.Missing);
            CheckUnchanged();
            return new(FlowsealActionOutcome.Success, expected,
                "Автозапуск удалён: отсутствие службы zapret подтверждено. Другие процессы, службы и общий драйвер не изменялись.");
        }
        catch (Exception error) when (Recoverable(error))
        {
            ServiceSnapshot observed;
            try { observed = ReadStateAsync(request.Root).GetAwaiter().GetResult(); }
            catch (Exception readError) when (Recoverable(readError))
            { observed = new(ServiceState.Unknown, null, ObservationError: readError.Message); }
            return new(error is ServiceConflictException ? FlowsealActionOutcome.Conflict : FlowsealActionOutcome.ApplyFailed,
                observed, "Действие со службой не подтверждено. " + ExplainFailure(error), ApplyError: error.Message,
                NextStep: "Обновите состояние. При тайм-ауте Windows может ещё завершать операцию; проверьте результат перед повтором.");
        }

        void CheckUnchanged()
        {
            var current = Observe(request);
            if (current.Files != input.Files || !SameObservation(expected, current.State)
                || (current.State.State != ServiceState.Missing && current.State.Ownership != ServiceOwnership.Owned))
                throw new ServiceConflictException("Обнаружено внешнее изменение. Дальнейшие действия прекращены; сторонние изменения сохранены.");
        }
        ServiceSnapshot Wait(ServiceState target)
        {
            var deadline = Environment.TickCount64 + 10000;
            while (true)
            {
                Inputs current;
                try { current = Observe(request); }
                catch (System.ComponentModel.Win32Exception error) when (deleting && error.NativeErrorCode == 1072)
                {
                    if (Environment.TickCount64 >= deadline) throw new TimeoutException("Windows удерживает удаляемую запись службы.");
                    Thread.Sleep(100); continue;
                }
                var state = current.State;
                if (current.Files != input.Files || (state.State == ServiceState.Missing ? !deleting :
                    state.Configuration != expected.Configuration || state.Preset != expected.Preset
                    || state.RecoveryData != expected.RecoveryData || state.ObservationError != expected.ObservationError))
                    throw new ServiceConflictException("Конфигурация или файлы изменились во время ожидания. Дальнейшие действия прекращены.");
                if (state.State == target)
                {
                    if (target != ServiceState.Missing && state.Ownership != ServiceOwnership.Owned)
                        throw new ServiceConflictException("Принадлежность конечного состояния службы не подтверждена.");
                    return state;
                }
                if (!deleting && state.State != (target == ServiceState.Running ? ServiceState.Starting : ServiceState.Stopping))
                    throw new IOException("Служба не достигла ожидаемого состояния: " + target + "; наблюдается: " + state.State);
                if (Environment.TickCount64 >= deadline) throw new TimeoutException("Истекло время ожидания службы: " + target);
                Thread.Sleep(100);
            }
        }
    }
}
