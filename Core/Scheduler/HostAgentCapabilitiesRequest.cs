namespace Seebot.WorkerAgent.Core.Scheduler;

public sealed class HostAgentCapabilitiesRequest
{
    public string HostId { get; set; } = "";
    public string AgentName { get; set; } = "";
    public string ReportedAt { get; set; } = "";
    public List<VmCapabilityDto> Vms { get; set; } = [];
}
