namespace Seebot.WorkerAgent.Core.Scheduler;

public sealed class HostAgentHeartbeatRequest
{
    public string HostId { get; set; } = "";
    public string AgentName { get; set; } = "";
    public string Status { get; set; } = "";
    public int VmCount { get; set; }
    public int QuarantinedVmCount { get; set; }
    public string Version { get; set; } = "";
    public string Timestamp { get; set; } = "";
}
