namespace Seebot.WorkerAgent.Core.Configuration;

public sealed class VirtualMachineOptions
{
    public string Name { get; set; } = "";
    public string VmxPath { get; set; } = "";
    public string BaseSnapshotName { get; set; } = "";
    public string GuestUser { get; set; } = "";
    public string GuestPasswordSecret { get; set; } = "";
    public string WorkerId { get; set; } = "";
    public string GuestWorkPath { get; set; } = "";
    public string GuestBackupPaths { get; set; } = "cache,db,file,logs";
    public bool Enabled { get; set; } = true;
    public string? DisabledReason { get; set; }
    public List<ProfileOptions> Profiles { get; set; } = [];
}
