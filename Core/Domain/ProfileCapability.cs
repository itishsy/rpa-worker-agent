namespace Seebot.WorkerAgent.Core.Domain;

public sealed class ProfileCapability
{
    public string VmName { get; set; } = "";
    public string WorkerId { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public string SnapshotName { get; set; } = "";
    public string BaseSnapshotName { get; set; } = "";
    public bool Enabled { get; set; } = true;
}
