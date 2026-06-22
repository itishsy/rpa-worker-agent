namespace Seebot.WorkerAgent.Core.Switching;

public interface IVmSwitchService
{
    Task<VmSwitchResult> SwitchAsync(VmSwitchRequest request, CancellationToken cancellationToken);
}
