namespace Seebot.WorkerAgent.Core.Scheduler;

public sealed class ProfilePendingTaskResponse
{
    public bool HasTask { get; set; }
    public string ProfileId { get; set; } = "";
    public int PendingCount { get; set; }
    public long? FirstTaskId { get; set; }
    public string? ExecutionCode { get; set; }
    public int Priority { get; set; }
    public string? OldestQueuedAt { get; set; }
}
