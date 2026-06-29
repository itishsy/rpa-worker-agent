namespace Seebot.WorkerAgent.Core.Configuration;

public sealed class AgentOptions
{
    public string HostId { get; set; } = "";
    public string AgentName { get; set; } = "";
    public string LocalDbPath { get; set; } = "";
    public int PollIntervalSeconds { get; set; }
    public int CapabilityReportIntervalSeconds { get; set; }
    public int SwitchTimeoutSeconds { get; set; }
    public int WaitVmReadyTimeoutSeconds { get; set; }
    public int WaitUpgradeTimeoutSeconds { get; set; }
    public int IdleStableSeconds { get; set; }
    public bool ForceRevertWhenBackupFailed { get; set; }
    public bool AllowRevertWhenRunnerError { get; set; }
    public int MaxSwitchesPerCycle { get; set; }
}
