using Seebot.WorkerAgent.Core.Configuration;

namespace Seebot.WorkerAgent.Core.Storage;

public interface IVirtualMachineRegistry
{
    Task<IReadOnlyList<VirtualMachineOptions>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<VirtualMachineOptions?> GetByNameAsync(string vmName, CancellationToken cancellationToken = default);

    Task UpsertVmAsync(VirtualMachineOptions vm, CancellationToken cancellationToken = default);

    Task UpdateVmStatusAsync(string vmName, bool enabled, string? disabledReason, CancellationToken cancellationToken = default);

    Task DeleteVmAsync(string vmName, CancellationToken cancellationToken = default);

    Task UpsertProfileAsync(string vmName, ProfileOptions profile, CancellationToken cancellationToken = default);

    Task UpdateProfileSnapshotAsync(string vmName, string profileId, string snapshotName, CancellationToken cancellationToken = default);

    Task DeleteProfileAsync(string vmName, string profileId, CancellationToken cancellationToken = default);
}
