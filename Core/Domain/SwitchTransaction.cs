namespace Seebot.WorkerAgent.Core.Domain;

public sealed class SwitchTransaction
{
    public string TransactionId { get; set; } = "";
    public string HostId { get; set; } = "";
    public string VmName { get; set; } = "";
    public string WorkerId { get; set; } = "";
    public string? FromProfileId { get; set; }
    public string? FromSnapshotName { get; set; }
    public string TargetProfileId { get; set; } = "";
    public string TargetSnapshotName { get; set; } = "";
    public long? FirstTaskId { get; set; }
    public string? TriggerReason { get; set; }
    public SwitchTransactionStatus Status { get; set; } = SwitchTransactionStatus.CREATED;
    public string? Step { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? BackupPath { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}
