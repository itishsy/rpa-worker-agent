namespace Seebot.WorkerAgent.Core.Health;

public interface IVmHealthCheckService
{
    Task CheckAsync(CancellationToken cancellationToken);
}
