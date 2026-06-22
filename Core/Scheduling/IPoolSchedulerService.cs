namespace Seebot.WorkerAgent.Core.Scheduling;

public interface IPoolSchedulerService
{
    Task<PoolSchedulerCycleResult> RunOneCycleAsync(CancellationToken cancellationToken);
}
