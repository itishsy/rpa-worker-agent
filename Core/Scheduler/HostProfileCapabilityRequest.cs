namespace Seebot.WorkerAgent.Core.Scheduler;

public sealed class HostProfileCapabilityRequest
{
    public string HostName { get; set; } = "";
    public string MachineCode { get; set; } = "";
    public string ProfileCode { get; set; } = "";
    public string ProfileName { get; set; } = "";
    public string SnapshotName { get; set; } = "";
    public string Status { get; set; } = "";
}
