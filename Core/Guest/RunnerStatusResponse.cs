using Seebot.WorkerAgent.Core.Domain;

namespace Seebot.WorkerAgent.Core.Guest;

public sealed class RunnerStatusResponse
{
    public bool Success { get; set; }
    public string WorkerId { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public RunnerStatusCode RunnerStatusCode { get; set; }
    public string RunnerStatusName { get; set; } = "";
    public string? RunnerStatusDesc { get; set; }
    public long? CurrentTaskId { get; set; }
    public int JavaProcessCount { get; set; }
    public int PythonProcessCount { get; set; }
    public int ChromeProcessCount { get; set; }
    public decimal? DiskFreeGb { get; set; }
    public string? LastHeartbeatTime { get; set; }
    public string? Timestamp { get; set; }
}
