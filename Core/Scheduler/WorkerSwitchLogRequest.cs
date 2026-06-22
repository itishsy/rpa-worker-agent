namespace Seebot.WorkerAgent.Core.Scheduler;

public sealed class WorkerSwitchLogRequest
{
    public string TxId { get; set; } = "";
    public string HostId { get; set; } = "";
    public string VmName { get; set; } = "";
    public string WorkerId { get; set; } = "";
    public string? FromProfileId { get; set; }
    public string? FromSnapshotName { get; set; }
    public string ToProfileId { get; set; } = "";
    public string ToSnapshotName { get; set; } = "";
    public long? FirstTaskId { get; set; }
    public string Status { get; set; } = "";
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string StartedAt { get; set; } = "";
    public string? FinishedAt { get; set; }
    public int? DurationSeconds { get; set; }
}
