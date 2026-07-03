namespace Seebot.WorkerAgent.Core.Scheduler;

public interface ISchedulerTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken);

    Task<string> RefreshAccessTokenAsync(CancellationToken cancellationToken);
}
