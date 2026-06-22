namespace Seebot.WorkerAgent.Core.Domain;

public enum AgentVmStatus
{
    UNKNOWN,
    POWERED_OFF,
    POWERED_ON,
    STOPPING,
    REVERTING,
    STARTING,
    WAIT_READY,
    MONITORING,
    ERROR,
    QUARANTINED
}
