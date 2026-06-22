namespace Seebot.WorkerAgent.Core.Scheduler;

public sealed class VmCapabilityDto
{
    public string VmName { get; set; } = "";
    public string WorkerId { get; set; } = "";
    public string VmxPath { get; set; } = "";
    public string BaseSnapshotName { get; set; } = "";
    public bool Enabled { get; set; }
    public bool IsQuarantined { get; set; }
    public List<ProfileCapabilityDto> Profiles { get; set; } = [];
}
