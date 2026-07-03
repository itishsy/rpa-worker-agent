namespace Seebot.WorkerAgent.Core.Vmware;

public interface IVmOperationLock
{
    Task<IAsyncDisposable> AcquireAsync(string vmxPath, CancellationToken cancellationToken);
}
