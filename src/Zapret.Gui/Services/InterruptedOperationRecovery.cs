using Zapret.Core;

namespace Zapret.Gui.Services;

public sealed partial class FlowsealAdapter
{
    public Task<InterruptedOperationSnapshot> ReadInterruptedOperationAsync(string root) => Task.Factory.StartNew(() =>
    {
        var inspection = InspectInterrupted(root, false);
        return new InterruptedOperationSnapshot(inspection.Pending, inspection.Result.Outcome == FlowsealActionOutcome.Ready,
            inspection.Record?.Root, inspection.Result.Explanation, inspection.Result.State);
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private sealed record InterruptedInspection(bool Pending, OperationRecord? Record, FlowsealActionResult Result);
    private InterruptedInspection InspectInterrupted(string root, bool execute)
    {
        OperationRecord? record = null;
        var current = new ServiceSnapshot(ServiceState.Unknown, null);
        using var guard = new Mutex(false, GuardName);
        var acquired = false;
        try
        {
            try { acquired = guard.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return new(false, null, new(FlowsealActionOutcome.Conflict, current,
                "Другая оболочка выполняет операцию. Дождитесь завершения и обновите состояние."));
            record = journal.Read();
            if (record == null || record.Phase == OperationPhase.Completed)
                return new(false, null, new(FlowsealActionOutcome.Success, SafeRecoveryRead(root), "Незавершённых операций нет."));
            // Validate schema even when storage is injected. The production store additionally verifies its ACL and checksum.
            record = OperationJournalCodec.Decode(OperationJournalCodec.Encode(record));
            if (!SameRoot(root, record.Root)) return new(true, record, new(FlowsealActionOutcome.InvalidRequest, current,
                "Незавершённая операция относится к папке " + record.Root + ". Выберите эту поставку для восстановления."));
            current = RecoveryRead(record, execute);
            ValidateInterrupted(record, current);
            if (!execute) return new(true, record, new(FlowsealActionOutcome.Ready, current,
                $"Незавершённая операция: {(record.Action == FlowsealAction.ManualStart ? "пробный сеанс" : "замена автозапуска")}; этап {record.Phase}. "
                + "Исходное состояние сохранено. Нажмите «Восстановить прежнее состояние»; перед изменением всё будет проверено повторно."));
            activeRecord = record;
            return new(true, record, RestoreInterrupted(record, current));
        }
        catch (Exception error) when (Recoverable(error))
        {
            return new(true, record, new(error is ServiceConflictException ? FlowsealActionOutcome.Conflict : FlowsealActionOutcome.RecoveryRequired,
                SafeRecoveryRead(record?.Root ?? root), "Восстановление не начато или не завершено: " + error.Message,
                error is ServiceConflictException ? FlowsealRecoveryOutcome.Conflict : FlowsealRecoveryOutcome.Failed,
                ApplyError: error.Message, NextStep: "Обновите состояние. Не перезаписывайте сторонние изменения; при неизвестном состоянии проверьте службу и журнал вручную."));
        }
        finally { if (acquired) guard.ReleaseMutex(); }
    }
    private static bool SameRoot(string left, string right) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(left))
        .Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
    private ServiceSnapshot SafeRecoveryRead(string root)
    {
        try { return ReadStateAsync(root).GetAwaiter().GetResult(); }
        catch (Exception error) when (Recoverable(error)) { return new(ServiceState.Unknown, null, ObservationError: error.Message); }
    }
    private ServiceSnapshot RecoveryRead(OperationRecord record, bool wait)
    {
        var deadline = Environment.TickCount64 + 10000;
        while (true)
        {
            try { return ReadStateAsync(record.Root).GetAwaiter().GetResult(); }
            catch (System.ComponentModel.Win32Exception error) when (wait && record.Phase == OperationPhase.Delete && error.NativeErrorCode == 1072)
            {
                if (Environment.TickCount64 >= deadline) throw new TimeoutException("Windows ещё удерживает удаляемую запись службы.");
                Thread.Sleep(100);
            }
        }
    }
    private Inputs RecoveryInputs(OperationRecord record) => Observe(new(record.Root, record.Action, record.Strategy));
    private void ValidateInterrupted(OperationRecord record, ServiceSnapshot current)
    {
        var original = ServiceOwnershipCheck.Classify(record.Root, record.Original);
        if (original.Ownership is not (ServiceOwnership.Owned or ServiceOwnership.Missing) || !SupportsRecovery(original))
            throw new ServiceConflictException("Принадлежность и полный исходный снимок нельзя подтвердить.");
        if (record.Desired != null && (record.Desired.RecoveryData?.OperationId != record.Id
            || ServiceOwnershipCheck.Classify(record.Root, record.Desired).Ownership != ServiceOwnership.Owned))
            throw new ServiceConflictException("Созданная конфигурация не подтверждена записью операции.");
        if (record.Phase == OperationPhase.Preparing)
        {
            if (!SameObservation(current, original)) throw new ServiceConflictException("До изменения службы обнаружено внешнее изменение её состояния.");
            return; // Preparation can leave lists/TCP settings behind; it never calls SCM mutations.
        }
        var observed = RecoveryInputs(record);
        if (observed.Files != record.Files || !SameObservation(current, observed.State))
            throw new ServiceConflictException("Поставка или состояние изменились после прерывания; изменения сохранены.");
        if (current.State is not (ServiceState.Starting or ServiceState.Stopping)
            && current.Ownership is not (ServiceOwnership.Owned or ServiceOwnership.Missing))
            throw new ServiceConflictException("Текущая принадлежность службы или процесса не подтверждена.");
        bool Known(ServiceSnapshot state) => state.State == ServiceState.Missing
            || SameEntry(state, original) || (record.Desired != null && SameEntry(state, record.Desired));
        if (!Known(record.Expected) || (record.Next != null && !Known(record.Next)))
            throw new ServiceConflictException("Записанный переход не относится к исходной или созданной своей службе.");
        if (SameObservation(current, record.Expected)) return;
        var next = record.Next;
        var validAfter = record.Phase switch
        {
            OperationPhase.Delete => current.State == ServiceState.Missing,
            OperationPhase.Stop => next != null && current.State is ServiceState.Stopped or ServiceState.Stopping && SameEntry(current, next),
            OperationPhase.Create => next != null && current.State == ServiceState.Stopped && SameEntry(current, next),
            OperationPhase.Start => next != null && current.State is ServiceState.Running or ServiceState.Starting && SameEntry(current, next),
            _ => false
        };
        if (!validAfter) throw new ServiceConflictException("Текущее состояние или идентичность процесса не соответствуют сохранённому этапу. Автоматическая перезапись запрещена.");
        if (current.State is not (ServiceState.Starting or ServiceState.Stopping)
            && current.Ownership is not (ServiceOwnership.Owned or ServiceOwnership.Missing))
            throw new ServiceConflictException("Текущая принадлежность службы не подтверждена.");
    }

    private FlowsealActionResult RestoreInterrupted(OperationRecord record, ServiceSnapshot current)
    {
        var backend = RecordingBackend(mutationBackend ?? service, record.Root);
        var expected = current;
        if (record.Phase == OperationPhase.Preparing) return Complete();
        // A former job is not reconstructed or killed by a stored PID. Its owner process closed it on death.
        if (readServiceState == null || manualBackend != null)
            (manualBackend ?? new WindowsManualProcessBackend()).EnsureNoOtherWinws(expected.Process);
        if (expected.State is ServiceState.Starting or ServiceState.Stopping)
            expected = Wait(record.Next!, record.Next!.State);
        if (!SameObservation(expected, record.Original))
        {
            if (record.Desired != null && SameEntry(expected, record.Desired))
            {
                if (expected.State == ServiceState.Running)
                { Check(); backend.Stop(expected); expected = Wait(expected, ServiceState.Stopped); }
                if (expected.State != ServiceState.Stopped) throw new ServiceConflictException("Созданная запись не находится в устойчивом состоянии.");
                Check(); backend.Delete(expected); expected = WaitMissing(expected);
            }
            if (expected.State == ServiceState.Missing && record.Original.State != ServiceState.Missing)
            {
                Check(); backend.Create(record.Original with { State = ServiceState.Stopped, Process = null });
                expected = Wait(record.Original, ServiceState.Stopped);
            }
            if (record.Original.State != ServiceState.Missing)
            {
                if (!SameEntry(expected, record.Original)) throw new ServiceConflictException("Исходная запись службы не подтверждена.");
                if (expected.State == ServiceState.Stopped && record.Original.State == ServiceState.Running)
                {
                    Check();
                    if (readServiceState == null || manualBackend != null)
                        (manualBackend ?? new WindowsManualProcessBackend()).EnsureNoOtherWinws(null);
                    backend.Start(expected); expected = Wait(expected, ServiceState.Running);
                    for (var sample = 0; sample < 3; sample++) { Thread.Sleep(400); Check(); }
                }
                else if (expected.State != record.Original.State)
                    throw new ServiceConflictException("Состояние изменено извне; восстановление прекращено.");
            }
        }
        return Complete();

        FlowsealActionResult Complete()
        {
            if (record.Phase != OperationPhase.Preparing) Check();
            RecordObserved(expected, OperationPhase.Completed);
            return new(FlowsealActionOutcome.Success, expected, "Незавершённая операция закрыта. Исходная конфигурация и состояние службы подтверждены.",
                FlowsealRecoveryOutcome.Restored, RecoveryExplanation: "Сторонние процессы и службы не изменялись. Списки, TCP timestamps и настройки подготовки сохраняются.");
        }
        void Check()
        {
            var observed = RecoveryInputs(record);
            if (observed.Files != record.Files || !SameObservation(observed.State, expected))
                throw new ServiceConflictException("Внешнее изменение во время восстановления сохранено.");
        }
        ServiceSnapshot Wait(ServiceSnapshot entry, ServiceState state)
        {
            var deadline = Environment.TickCount64 + 10000;
            while (true)
            {
                var observed = RecoveryInputs(record);
                if (observed.Files != record.Files || !SameEntry(observed.State, entry))
                    throw new ServiceConflictException("Файлы или конфигурация изменились во время ожидания восстановления.");
                if (observed.State.State == state && observed.State.Ownership == ServiceOwnership.Owned)
                { RecordObserved(observed.State); return observed.State; }
                if (observed.State.State != (state == ServiceState.Running ? ServiceState.Starting : ServiceState.Stopping))
                    throw new IOException("Служба не достигла состояния " + state);
                if (Environment.TickCount64 >= deadline) throw new TimeoutException("Истекло время ожидания восстановления службы.");
                Thread.Sleep(100);
            }
        }
        ServiceSnapshot WaitMissing(ServiceSnapshot entry)
        {
            var deadline = Environment.TickCount64 + 10000;
            while (true)
            {
                var observed = RecoveryRead(activeRecord!, true);
                if (observed.State == ServiceState.Missing) { RecordObserved(observed); return observed; }
                if (!SameEntry(observed, entry)) throw new ServiceConflictException("После удаления обнаружена другая служба.");
                if (Environment.TickCount64 >= deadline) throw new TimeoutException("Удаление своей записи ещё не завершилось.");
                Thread.Sleep(100);
            }
        }
    }
}
