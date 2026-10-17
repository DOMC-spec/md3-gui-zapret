using Zapret.Core;

namespace Zapret.Gui.Services;

public sealed partial class FlowsealAdapter
{
    internal static bool SameObservation(ServiceSnapshot left, ServiceSnapshot right) =>
        left.State == right.State && left.Configuration == right.Configuration && left.Process == right.Process
        && left.Preset == right.Preset && left.RecoveryData == right.RecoveryData && left.ObservationError == right.ObservationError;

    private static bool SupportsRecovery(ServiceSnapshot state) => state.State == ServiceState.Missing
        || state is { State: ServiceState.Stopped or ServiceState.Running, ObservationError: null,
            RecoveryData: { SecurityDescriptor.Length: > 0 }, Configuration: { ServiceType: 16, TagId: 0, LoadOrderGroup: "" } config }
            && config.Account.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase) && config.StartType is 2 or 3 or 4
            && !(state.State == ServiceState.Running && config.StartType == 4);

    private static bool SameEntry(ServiceSnapshot actual, ServiceSnapshot expected) =>
        actual.Configuration == expected.Configuration && actual.Preset == expected.Preset
        && actual.RecoveryData?.Description == expected.RecoveryData?.Description
        && actual.RecoveryData?.OperationId == expected.RecoveryData?.OperationId
        && (expected.RecoveryData?.SecurityDescriptor is not { Length: > 0 } security || actual.RecoveryData?.SecurityDescriptor == security)
        && actual.ObservationError == null;

    private FlowsealActionResult Replace(FlowsealActionRequest request, Inputs input, IServiceMutationBackend backend)
    {
        var original = input.State;
        var desired = new ServiceSnapshot(ServiceState.Stopped, Path.GetFileNameWithoutExtension(input.Selected!),
            new ServiceConfiguration(input.Parsed!.ServiceCommand(Path.Combine(input.Description.Root, "bin", "winws.exe"))),
            RecoveryData: new("Zapret DPI bypass software", original.RecoveryData?.SecurityDescriptor ?? "", Guid.NewGuid().ToString("N")));
        var expected = original;
        ServiceSnapshot? deleting = null;
        var changed = false;
        try
        {
            CheckUnchanged();
            if (expected.State == ServiceState.Running)
            {
                changed = true; backend.Stop(expected);
                expected = WaitFor(original, ServiceState.Stopped);
            }
            if (expected.State != ServiceState.Missing)
            {
                CheckUnchanged(); changed = true; deleting = expected; backend.Delete(expected);
                expected = WaitForMissing(original);
            }
            CheckUnchanged(); changed = true; backend.Create(desired);
            expected = WaitFor(desired, ServiceState.Stopped);
            CheckUnchanged(); backend.Start(expected);
            expected = WaitFor(desired, ServiceState.Running);
            VerifyStable(expected);
            return new(FlowsealActionOutcome.Success, expected,
                "Стратегия установлена в автозапуск. Состояние, команда, автоматический тип запуска и отсутствие раннего выхода проверены. Работа службы не подтверждает доступность сервисов.",
                NextStep: "Проверьте нужные функции сервисов. Работа службы не подтверждает доступность видео или голоса.");
        }
        catch (Exception applyError) when (Recoverable(applyError))
        {
            var recovery = applyError is ServiceConflictException
                ? (FlowsealRecoveryOutcome.Conflict, SafeRead(), "Обнаруженное стороннее изменение сохранено; автоматическое восстановление прекращено.")
                : changed ? Recover() : (FlowsealRecoveryOutcome.NotNeeded, SafeRead(), "Изменения не начаты.");
            return new(FlowsealActionOutcome.ApplyFailed, recovery.Item2, "Не удалось применить стратегию автозапуска.",
                recovery.Item1, applyError.Message, recovery.Item3,
                recovery.Item1 == FlowsealRecoveryOutcome.Restored
                    ? "Прежнее состояние восстановлено. Проверьте причину ошибки перед повторной попыткой."
                    : "Обновите состояние. Проверьте службу zapret и её конфигурацию вручную; автоматическая перезапись запрещена.");
        }

        void CheckUnchanged()
        {
            var current = Observe(request);
            if (current.Files != input.Files || !SameObservation(expected, current.State))
                throw new ServiceConflictException("Служба или используемые файлы изменились извне.");
        }
        ServiceSnapshot SafeRead()
        {
            try { return ReadStateAsync(request.Root).GetAwaiter().GetResult(); }
            catch (Exception error) when (Recoverable(error))
            { return new(ServiceState.Unknown, null, ObservationError: error.Message); }
        }
        ServiceSnapshot WaitFor(ServiceSnapshot entry, ServiceState state)
        {
            var deadline = Environment.TickCount64 + 10000;
            while (true)
            {
                var current = ReadStateAsync(request.Root).GetAwaiter().GetResult();
                if (!SameEntry(current, entry)) throw new ServiceConflictException("Конфигурация службы изменилась во время ожидания.");
                if (current.State == state) return current;
                if (current.State is not (ServiceState.Starting or ServiceState.Stopping))
                    throw new IOException("Служба не достигла ожидаемого состояния " + state + ": " + current.State);
                if (Environment.TickCount64 >= deadline) throw new TimeoutException("Истекло время ожидания службы: " + state);
                Thread.Sleep(100);
            }
        }
        ServiceSnapshot WaitForMissing(ServiceSnapshot entry)
        {
            var deadline = Environment.TickCount64 + 10000;
            while (true)
            {
                ServiceSnapshot current;
                try { current = ReadStateAsync(request.Root).GetAwaiter().GetResult(); }
                catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 1072)
                {
                    if (Environment.TickCount64 >= deadline) throw new TimeoutException("Windows ещё удерживает удаляемую запись службы.");
                    Thread.Sleep(100); continue;
                }
                if (current.State == ServiceState.Missing) return current;
                if (!SameEntry(current, entry)) throw new ServiceConflictException("После удаления обнаружена другая запись службы.");
                if (Environment.TickCount64 >= deadline) throw new TimeoutException("Удаление службы не завершилось.");
                Thread.Sleep(100);
            }
        }
        void VerifyStable(ServiceSnapshot running)
        {
            for (var sample = 0; sample < 3; sample++)
            {
                Thread.Sleep(400);
                var observed = Observe(request);
                if (observed.Files != input.Files || !SameEntry(observed.State, running))
                    throw new ServiceConflictException("При проверке работы изменились файлы или конфигурация службы.");
                if (observed.State.State != ServiceState.Running) throw new IOException("Служба завершилась вскоре после запуска.");
                if (observed.State.Process != running.Process || observed.State.Ownership != ServiceOwnership.Owned)
                    throw new ServiceConflictException("Процесс службы изменился во время проверки работы.");
            }
        }
        (FlowsealRecoveryOutcome, ServiceSnapshot, string) Recover()
        {
            try
            {
                Inputs currentInput;
                try { currentInput = Observe(request); }
                catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 1072 && deleting != null)
                { _ = WaitForMissing(deleting); currentInput = Observe(request); }
                var current = currentInput.State;
                if (currentInput.Files != input.Files || current.ObservationError != null
                    || (current.State != ServiceState.Missing && !SameEntry(current, desired) && !SameEntry(current, original)))
                    throw new ServiceConflictException("Обнаружены сторонние изменения или неполное наблюдение; они не перезаписаны.");
                if (SameEntry(current, original) && current.State == ServiceState.Running && original.State == ServiceState.Running
                    && current.Process != original.Process)
                    throw new ServiceConflictException("Исходный процесс заменён извне; восстановление не перезаписывает это изменение.");
                if (SameEntry(current, original) && current.State == original.State
                    && (original.State != ServiceState.Running || original.Process == current.Process))
                    return (FlowsealRecoveryOutcome.Restored, current, "Прежняя конфигурация и исходное состояние сохранены.");
                if (current.State != ServiceState.Missing && !SameEntry(current, original))
                {
                    if (current.State is ServiceState.Starting or ServiceState.Stopping)
                        current = WaitForRecoverySettle(desired);
                    if (current.State == ServiceState.Running)
                    { CheckRecovery(current); backend.Stop(current); current = WaitFor(desired, ServiceState.Stopped); }
                    else if (current.State != ServiceState.Stopped) throw new ServiceConflictException("Неизвестное состояние созданной службы.");
                    CheckRecovery(current); backend.Delete(current); current = WaitForMissing(desired);
                }
                if (original.State == ServiceState.Missing)
                    return (FlowsealRecoveryOutcome.Restored, current, "Запись своей неудачной операции удалена; служба снова отсутствует.");
                if (current.State == ServiceState.Missing)
                { CheckRecovery(current); backend.Create(original with { State = ServiceState.Stopped, Process = null }); current = WaitFor(original, ServiceState.Stopped); }
                if (current.State is ServiceState.Starting or ServiceState.Stopping)
                    current = WaitFor(original, ServiceState.Stopped);
                if (original.State == ServiceState.Running && current.State == ServiceState.Stopped)
                { CheckRecovery(current); backend.Start(current); current = WaitFor(original, ServiceState.Running); VerifyStable(current); }
                if (!SameEntry(current, original) || current.State != original.State)
                    throw new IOException("Исходное состояние не подтверждено.");
                return (FlowsealRecoveryOutcome.Restored, current, original.State == ServiceState.Running
                    ? "Прежняя конфигурация восстановлена; служба снова работает." : "Прежняя конфигурация восстановлена; служба остаётся остановленной.");
            }
            catch (ServiceConflictException error)
            { return (FlowsealRecoveryOutcome.Conflict, SafeRead(), error.Message); }
            catch (Exception error) when (Recoverable(error))
            { return (FlowsealRecoveryOutcome.Failed, SafeRead(), "Восстановление не подтверждено: " + error.Message); }
        }
        void CheckRecovery(ServiceSnapshot recoveryExpected)
        {
            var observed = Observe(request);
            if (observed.Files != input.Files || !SameObservation(recoveryExpected, observed.State))
                throw new ServiceConflictException("Стороннее изменение во время восстановления сохранено.");
        }
        ServiceSnapshot WaitForRecoverySettle(ServiceSnapshot entry)
        {
            // Recovery has its own bounded wait: an apply timeout must not abandon an owned transition.
            var deadline = Environment.TickCount64 + 10000;
            while (true)
            {
                var observed = Observe(request);
                if (observed.Files != input.Files || !SameEntry(observed.State, entry))
                    throw new ServiceConflictException("Стороннее изменение во время ожидания восстановления сохранено.");
                if (observed.State.State is not (ServiceState.Starting or ServiceState.Stopping)) return observed.State;
                if (Environment.TickCount64 >= deadline) throw new TimeoutException("Переходное состояние своей службы не завершилось за время восстановления.");
                Thread.Sleep(100);
            }
        }
    }
    private static bool Recoverable(Exception error) => error is IOException or UnauthorizedAccessException or System.Security.SecurityException
        or ArgumentException or NotSupportedException or FormatException or System.ComponentModel.Win32Exception or TimeoutException or InvalidOperationException;
    private sealed class ServiceConflictException(string message) : IOException(message);
}
