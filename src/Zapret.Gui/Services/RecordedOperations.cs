using Zapret.Core;

namespace Zapret.Gui.Services;

public sealed partial class FlowsealAdapter
{
    private readonly IOperationJournal journal = operationJournal ?? (readServiceState == null
        ? new WindowsOperationJournal() : new MemoryOperationJournal());
    private OperationRecord? activeRecord;

    private void BeginRecordedOperation(FlowsealActionRequest request, Inputs input)
    {
        activeRecord = null;
        if (journal.Read() is { Phase: not OperationPhase.Completed })
            throw new IOException("Сначала завершите восстановление предыдущей операции.");
        var record = new OperationRecord(Guid.NewGuid().ToString("N"), request.Action, input.Description.Root,
            request.Strategy!, input.State, input.State, input.Files, UpdatedAtUtcTicks: DateTime.UtcNow.Ticks);
        journal.Write(record);
        activeRecord = record;
    }
    private void SaveRecordedOperation(OperationRecord record)
    {
        if (journal.Read()?.Id != record.Id) throw new ServiceConflictException("Запись незавершённой операции изменилась извне.");
        journal.Write(record);
        activeRecord = record;
    }
    private void RecordPrepared(Inputs prepared, ServiceSnapshot? desired = null)
    {
        if (activeRecord != null) SaveRecordedOperation(activeRecord with { Files = prepared.Files,
            Desired = desired ?? activeRecord.Desired, UpdatedAtUtcTicks = DateTime.UtcNow.Ticks });
    }
    private void RecordObserved(ServiceSnapshot observed, OperationPhase phase = OperationPhase.Observed, ServiceProcess? trial = null)
    {
        if (activeRecord != null) SaveRecordedOperation(activeRecord with { Expected = observed, Next = null,
            Phase = phase, Trial = trial ?? activeRecord.Trial, UpdatedAtUtcTicks = DateTime.UtcNow.Ticks });
    }
    private FlowsealActionResult FinishRecordedOperation(FlowsealActionResult result)
    {
        if (activeRecord != null && (result.Outcome == FlowsealActionOutcome.Success || result.Recovery == FlowsealRecoveryOutcome.Restored
            || (result.Recovery == FlowsealRecoveryOutcome.NotNeeded && SameObservation(result.State, activeRecord.Original))))
            RecordObserved(result.State, OperationPhase.Completed);
        return result;
    }
    private IServiceMutationBackend RecordingBackend(IServiceMutationBackend backend, string root) => new RecordedBackend(backend,
        () => ReadStateAsync(root).GetAwaiter().GetResult(), (phase, before, after) =>
        {
            if (activeRecord == null) throw new IOException("Нет сохранённого исходного состояния; команда Windows запрещена.");
            SaveRecordedOperation(activeRecord with { Phase = phase, Expected = before, Next = after,
                UpdatedAtUtcTicks = DateTime.UtcNow.Ticks });
        });

    private sealed class RecordedBackend(IServiceMutationBackend backend, Func<ServiceSnapshot> read,
        Action<OperationPhase, ServiceSnapshot, ServiceSnapshot> record) : IServiceMutationBackend
    {
        public void Stop(ServiceSnapshot expected)
        { record(OperationPhase.Stop, expected, expected with { State = ServiceState.Stopped, Process = null }); backend.Stop(expected); }
        public void Delete(ServiceSnapshot expected)
        { record(OperationPhase.Delete, expected, new(ServiceState.Missing, null)); backend.Delete(expected); }
        public void Start(ServiceSnapshot expected)
        { record(OperationPhase.Start, expected, expected with { State = ServiceState.Running, Process = null }); backend.Start(expected); }
        public void Create(ServiceSnapshot desired)
        {
            var before = read();
            if (before.State != ServiceState.Missing) throw new ServiceConflictException("Имя службы занято перед созданием.");
            record(OperationPhase.Create, before, desired);
            backend.Create(desired);
        }
    }
}
