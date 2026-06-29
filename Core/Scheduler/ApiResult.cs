namespace Seebot.WorkerAgent.Core.Scheduler;

internal sealed class ApiResult<T>
{
    public int Code { get; set; }
    public string? Message { get; set; }
    public T? Data { get; set; }
}
