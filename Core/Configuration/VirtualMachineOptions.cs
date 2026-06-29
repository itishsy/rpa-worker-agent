namespace Seebot.WorkerAgent.Core.Configuration;

public sealed class VirtualMachineOptions
{
    public string Name { get; set; } = "";
    public string VmxPath { get; set; } = "";
    public string BaseSnapshotName { get; set; } = "";
    public string GuestUser { get; set; } = "";
    public string GuestPasswordSecret { get; set; } = "";
    public string WorkerId { get; set; } = "";
    public string RunnerControlBaseUrl { get; set; } = "";
    public string RunnerStatusUrl { get; set; } = "";
    public string RunnerKillUrl { get; set; } = "";
    public string HostWorkPath { get; set; } = "";
    public string HostSharedPath { get; set; } = "";
    public string GuestSharedPath { get; set; } = "";
    public GuestBackupPathsOptions GuestBackupPaths { get; set; } = new();
    public List<ProfileOptions> Profiles { get; set; } = [];
}
