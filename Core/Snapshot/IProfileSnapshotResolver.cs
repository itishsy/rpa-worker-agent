using Seebot.WorkerAgent.Core.Configuration;

namespace Seebot.WorkerAgent.Core.Snapshot;

public interface IProfileSnapshotResolver
{
    ProfileSnapshotResolution Resolve(
        VirtualMachineOptions vm,
        ProfileOptions profile,
        IReadOnlyList<string> snapshots);

    string? ResolveProfileId(VirtualMachineOptions vm, string? snapshotName);

    bool IsProfileSnapshotName(string profileId, string snapshotName);
}
