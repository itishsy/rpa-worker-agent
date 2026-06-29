namespace Seebot.WorkerAgent.Core.Vmware;

public interface IVmrunService
{
    Task<IReadOnlyList<string>> ListSnapshotsAsync(string vmxPath, CancellationToken cancellationToken);

    Task<string?> GetCurrentSnapshotAsync(string vmxPath, CancellationToken cancellationToken);

    Task<VmrunCommandResult> StopVmAsync(string vmxPath, VmStopMode mode, CancellationToken cancellationToken);

    Task<VmrunCommandResult> RevertToSnapshotAsync(string vmxPath, string snapshotName, CancellationToken cancellationToken);

    Task<VmrunCommandResult> StartVmAsync(string vmxPath, bool noGui, CancellationToken cancellationToken);

    Task<VmrunCommandResult> EnableSharedFoldersAsync(string vmxPath, CancellationToken cancellationToken);

    Task<VmrunCommandResult> AddSharedFolderAsync(string vmxPath, string shareName, string hostPath, CancellationToken cancellationToken);

    Task<VmrunCommandResult> RemoveSharedFolderAsync(string vmxPath, string shareName, CancellationToken cancellationToken);

    Task<VmrunCommandResult> CreateSnapshotAsync(string vmxPath, string snapshotName, CancellationToken cancellationToken);

    Task<VmrunCommandResult> DeleteSnapshotAsync(string vmxPath, string snapshotName, CancellationToken cancellationToken);
}
