using Seebot.WorkerAgent.Core.Domain;

namespace Seebot.WorkerAgent.Core.Guest;

public sealed class KillRunnerResponse
{
    public bool Success { get; set; }
    public string? ErrorCode { get; set; }
    public string? Message { get; set; }
    public RunnerStatusCode? BeforeRunnerStatusCode { get; set; }
    public string? BeforeRunnerStatusName { get; set; }
    public RunnerStatusCode? AfterRunnerStatusCode { get; set; }
    public string? AfterRunnerStatusName { get; set; }
    public long? CurrentTaskId { get; set; }
    public bool LogFlushed { get; set; }
}
