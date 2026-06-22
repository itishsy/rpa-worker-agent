namespace Seebot.WorkerAgent.Core.Guest;

public sealed class GuestWorkerClientException : Exception
{
    public GuestWorkerClientException(string operationName, string requestUrl, string message, Exception? innerException = null)
        : base($"Guest worker operation '{operationName}' failed for {requestUrl}: {message}", innerException)
    {
        OperationName = operationName;
        RequestUrl = requestUrl;
    }

    public string OperationName { get; }
    public string RequestUrl { get; }
}
