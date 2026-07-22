namespace Seebot.WorkerAgent.Core.Configuration;

public sealed class VmrunOptions
{
    public string VmrunPath { get; set; } = "";
    public string HostType { get; set; } = "ws";
    public int StopSoftTimeoutSeconds { get; set; }
    public bool AllowHardStopAfterSoftTimeout { get; set; }
    public int FileOperationTimeoutSeconds { get; set; }
}
