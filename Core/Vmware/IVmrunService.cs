namespace Seebot.WorkerAgent.Core.Vmware;

public interface IVmrunService
{
    Task<IReadOnlyList<string>> ListSnapshotsAsync(string vmxPath, CancellationToken cancellationToken);

    Task<VmrunCommandResult> StopVmAsync(string vmxPath, VmStopMode mode, CancellationToken cancellationToken);

    Task<VmrunCommandResult> RevertToSnapshotAsync(string vmxPath, string snapshotName, CancellationToken cancellationToken);

    Task<VmrunCommandResult> StartVmAsync(string vmxPath, bool noGui, CancellationToken cancellationToken);

    Task<VmrunCommandResult> CopyFileFromGuestToHostAsync(
        string vmxPath,
        string guestUser,
        string guestPassword,
        string guestPath,
        string hostPath,
        CancellationToken cancellationToken);
}
