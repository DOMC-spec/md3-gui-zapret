using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zapret.Core;

namespace Zapret.Gui.Services;

public enum OperationPhase { Preparing, Observed, Stop, Delete, Create, Start, ManualLaunch, ManualActive, Completed }
public sealed record OperationRecord(string Id, FlowsealAction Action, string Root, Preset Strategy,
    ServiceSnapshot Original, ServiceSnapshot Expected, string Files, OperationPhase Phase = OperationPhase.Preparing,
    ServiceSnapshot? Desired = null, ServiceSnapshot? Next = null, ServiceProcess? Trial = null,
    long UpdatedAtUtcTicks = 0, int Version = 1);
public sealed record InterruptedOperationSnapshot(bool Pending = false, bool CanRecover = false, string? Root = null,
    string Explanation = "Незавершённых операций нет.", ServiceSnapshot? State = null);

/// <summary>Durable system boundary. Write must complete its durable flush before returning.</summary>
public interface IOperationJournal
{
    OperationRecord? Read();
    void Write(OperationRecord record);
}

/// <summary>Used with injected service observation; never writes the workstation's journal.</summary>
public sealed class MemoryOperationJournal : IOperationJournal
{
    private OperationRecord? record;
    public OperationRecord? Read() => Volatile.Read(ref record);
    public void Write(OperationRecord value) => Volatile.Write(ref record, value);
}

public static class OperationJournalCodec
{
    private sealed record Envelope(string Payload, string Hash);
    public static byte[] Encode(OperationRecord record)
    {
        var payload = JsonSerializer.Serialize(record);
        var encoded = JsonSerializer.SerializeToUtf8Bytes(new Envelope(payload, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))));
        if (encoded.Length > 1024 * 1024) throw new IOException("Запись восстановления превышает поддерживаемый размер.");
        return encoded;
    }
    public static OperationRecord Decode(byte[] bytes)
    {
        try
        {
            if (bytes.Length > 1024 * 1024) throw new IOException("Журнал восстановления слишком большой.");
            var envelope = JsonSerializer.Deserialize<Envelope>(bytes) ?? throw new IOException("Пустой журнал восстановления.");
            if (envelope.Payload == null || envelope.Hash != Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(envelope.Payload))))
                throw new IOException("Контрольная сумма журнала не совпадает; изменения запрещены.");
            var record = JsonSerializer.Deserialize<OperationRecord>(envelope.Payload) ?? throw new IOException("Пустая операция.");
            if (record.Version != 1 || !Guid.TryParseExact(record.Id, "N", out _) || !Enum.IsDefined(record.Phase)
                || record.Action is not (FlowsealAction.InstallService or FlowsealAction.ManualStart)
                || record.Root == null || !Path.IsPathFullyQualified(record.Root) || record.Strategy == null
                || record.Original == null || record.Expected == null || record.Files == null
                || (record.Phase is OperationPhase.Stop or OperationPhase.Delete or OperationPhase.Create or OperationPhase.Start && record.Next == null))
                throw new IOException("Формат или версия записи восстановления не поддерживается.");
            return record;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException)
        { throw new IOException("Журнал восстановления повреждён: " + error.Message, error); }
    }
}
