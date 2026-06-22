namespace Seebot.WorkerAgent.Core.Configuration;

public sealed class WorkerAgentOptions
{
    public AgentOptions Agent { get; set; } = new();
    public OperationsApiOptions OperationsApi { get; set; } = new();
    public SchedulerOptions Scheduler { get; set; } = new();
    public VmrunOptions Vmrun { get; set; } = new();
    public List<VirtualMachineOptions> VirtualMachines { get; set; } = [];
}
