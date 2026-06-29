namespace Seebot.WorkerAgent.Core.Scheduler;

public sealed class VmStatusReportRequest
{
    public string HostId { get; set; } = "";
    public string VmName { get; set; } = "";
    public string WorkerId { get; set; } = "";
    public string? CurrentProfileId { get; set; }
    public string? CurrentSnapshotName { get; set; }
    public string AgentVmStatus { get; set; } = "";
    public int? RunnerStatusCode { get; set; }
    public string? RunnerStatusName { get; set; }
    public string? RunnerStatusDesc { get; set; }
    public string? CurrentTaskId { get; set; }
    public bool IsQuarantined { get; set; }
    public string? LastSwitchAt { get; set; }
    public string LastHeartbeatTime { get; set; } = "";
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}
