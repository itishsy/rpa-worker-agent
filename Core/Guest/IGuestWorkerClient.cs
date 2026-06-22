using Seebot.WorkerAgent.Core.Configuration;

namespace Seebot.WorkerAgent.Core.Guest;

public interface IGuestWorkerClient
{
    Task<RunnerStatusResponse> GetRunnerStatusAsync(VirtualMachineOptions vm, CancellationToken cancellationToken);

    Task<KillRunnerResponse> KillRunnerAsync(
        VirtualMachineOptions vm,
        string txId,
        string reason,
        int deadlineSeconds,
        CancellationToken cancellationToken);
}
