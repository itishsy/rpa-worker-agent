using Seebot.WorkerAgent.Core.Configuration;

namespace Seebot.WorkerAgent.Core.Vmware;

public interface IVmxNetworkConfigurationService
{
    Task<VmxNetworkConfigurationResult> ApplyAsync(
        string vmxPath,
        VmrunOptions options,
        CancellationToken cancellationToken);
}

public sealed record VmxNetworkConfigurationResult(bool Success, string? ErrorMessage = null);
