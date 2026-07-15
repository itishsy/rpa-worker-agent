using Seebot.WorkerAgent.Core.Configuration;

namespace Seebot.WorkerAgent.Core.Guest;

public interface IGuestTokenProvisioningService
{
    Task<GuestTokenProvisioningResult> ProvisionAsync(
        VirtualMachineOptions vm,
        CancellationToken cancellationToken);
}
