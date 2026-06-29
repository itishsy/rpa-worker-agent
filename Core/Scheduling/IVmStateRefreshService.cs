namespace Seebot.WorkerAgent.Core.Scheduling;

public interface IVmStateRefreshService
{
    Task RefreshAsync(DateTimeOffset now, CancellationToken cancellationToken);
}
