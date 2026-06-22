namespace Seebot.WorkerAgent.Core.Configuration;

public sealed class VmrunOptions
{
    public string VmrunPath { get; set; } = "";
    public bool DefaultStartNoGui { get; set; }
    public int StopSoftTimeoutSeconds { get; set; }
    public bool AllowHardStopAfterSoftTimeout { get; set; }
}
