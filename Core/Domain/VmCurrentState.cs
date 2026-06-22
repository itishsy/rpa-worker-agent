namespace Seebot.WorkerAgent.Core.Domain;

public sealed class VmCurrentState
{
    public string VmName { get; set; } = "";
    public string VmxPath { get; set; } = "";
    public string WorkerId { get; set; } = "";
    public AgentVmStatus VmStatus { get; set; } = AgentVmStatus.UNKNOWN;
    public string? CurrentProfileId { get; set; }
    public string? CurrentSnapshotName { get; set; }
    public RunnerStatusCode? RunnerStatusCode { get; set; }
    public string? CurrentTaskId { get; set; }
    public bool IsQuarantined { get; set; }
    public bool HasActiveSwitchTransaction { get; set; }
    public DateTimeOffset? IdleSince { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
