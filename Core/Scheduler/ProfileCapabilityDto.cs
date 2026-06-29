namespace Seebot.WorkerAgent.Core.Scheduler;

public sealed class ProfileCapabilityDto
{
    public string ProfileId { get; set; } = "";
    public string ProfileName { get; set; } = "";
    public string SnapshotName { get; set; } = "";
    public bool Enabled { get; set; }
    public bool SnapshotExists { get; set; }
    public string ValidationStatus { get; set; } = "";
    public string? ValidationMessage { get; set; }
}
