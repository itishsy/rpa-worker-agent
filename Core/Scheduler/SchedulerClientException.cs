using System.Net;

namespace Seebot.WorkerAgent.Core.Scheduler;

public sealed class SchedulerClientException : Exception
{
    public SchedulerClientException(string operationName, HttpStatusCode statusCode, string responseBody)
        : base($"Scheduler operation '{operationName}' failed with HTTP {(int)statusCode} ({statusCode}): {responseBody}")
    {
        OperationName = operationName;
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public string OperationName { get; }
    public HttpStatusCode StatusCode { get; }
    public string ResponseBody { get; }
}
