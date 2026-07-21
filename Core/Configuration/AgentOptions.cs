namespace Seebot.WorkerAgent.Core.Configuration;

public sealed class AgentOptions
{
    public string HostId { get; set; } = "";
    public string AgentName { get; set; } = "";
    public string GeneralProfileId { get; set; } = "General";
    public string HostWorkPath { get; set; } = "";
    public int PollIntervalSeconds { get; set; }
    public int SwitchTimeoutSeconds { get; set; }
    public int WaitVmReadyTimeoutSeconds { get; set; }
    public int WaitUpgradeTimeoutSeconds { get; set; }
    public int IdleStableSeconds { get; set; }
    public bool ForceRevertWhenBackupFailed { get; set; }
    public bool AllowRevertWhenRunnerError { get; set; }
    public int MaxSwitchesPerCycle { get; set; }
    public int VmPostStopStabilizationSeconds { get; set; } = 5;
    public int VmPostRevertStabilizationSeconds { get; set; } = 3;
    public int VmPowerCycleStopTimeoutSeconds { get; set; } = 30;
    public int ManualPowerOnRunnerProbeTimeoutSeconds { get; set; } = 5;
    public int ManualPowerOnStartMaxAttempts { get; set; } = 3;
    public int ManualPowerOnRunnerReadyTimeoutSeconds { get; set; } = 180;
    public int VmOperationLeaseSeconds { get; set; } = 600;
    public int VmOperationHeartbeatSeconds { get; set; } = 30;
    public int VmRecoveryWindowMinutes { get; set; } = 30;
    public int VmMaxPowerCyclesPerWindow { get; set; } = 2;
    public int VmRecoveryCooldownMinutes { get; set; } = 10;
    public int VmMaxConsecutiveRecoveryFailures { get; set; } = 2;
    public int VmRecoveryFailureCooldownMinutes { get; set; } = 60;
}
