namespace Seebot.WorkerAgent.Core.Scheduling;

public sealed class PoolSchedulerCycleResult
{
    public bool SwitchStarted { get; set; }
    public string? TargetProfileId { get; set; }
    public string? VmName { get; set; }
    public string? Reason { get; set; }
}
