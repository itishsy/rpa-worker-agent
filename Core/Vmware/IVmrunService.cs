namespace Seebot.WorkerAgent.Core.Vmware;

public sealed record GuestProcess(int Pid, string Owner, string CommandLine);

public interface IVmrunService
{
    Task<IReadOnlyList<string>> ListSnapshotsAsync(string vmxPath, CancellationToken cancellationToken);

    Task<bool> IsVmRunningAsync(string vmxPath, CancellationToken cancellationToken);

    Task<string?> GetCurrentSnapshotAsync(string vmxPath, CancellationToken cancellationToken);

    Task<string> GetGuestIPAddressAsync(string vmxPath, CancellationToken cancellationToken);

    Task<VmrunCommandResult> StopVmAsync(string vmxPath, VmStopMode mode, CancellationToken cancellationToken);

    Task<VmrunCommandResult> RevertToSnapshotAsync(string vmxPath, string snapshotName, CancellationToken cancellationToken);

    Task<VmrunCommandResult> StartVmAsync(string vmxPath, CancellationToken cancellationToken);

    Task<VmrunCommandResult> EnableSharedFoldersAsync(string vmxPath, CancellationToken cancellationToken);

    Task<VmrunCommandResult> AddSharedFolderAsync(string vmxPath, string shareName, string hostPath, CancellationToken cancellationToken);

    Task<VmrunCommandResult> RemoveSharedFolderAsync(string vmxPath, string shareName, CancellationToken cancellationToken);

    Task<VmrunCommandResult> RunProgramInGuestAsync(string vmxPath, string guestUser, string guestPassword, string programPath, IReadOnlyList<string> arguments, CancellationToken cancellationToken);

    Task<VmrunCommandResult> CopyFileFromHostToGuestAsync(string vmxPath, string guestUser, string guestPassword, string hostPath, string guestPath, CancellationToken cancellationToken);

    Task<VmrunCommandResult> CopyFileFromGuestToHostAsync(string vmxPath, string guestUser, string guestPassword, string guestPath, string hostPath, CancellationToken cancellationToken);

    Task<VmrunCommandResult> CreateSnapshotAsync(string vmxPath, string snapshotName, CancellationToken cancellationToken);

    Task<VmrunCommandResult> DeleteSnapshotAsync(string vmxPath, string snapshotName, CancellationToken cancellationToken);

    Task<IReadOnlyList<GuestProcess>> ListProcessesInGuestAsync(string vmxPath, string guestUser, string guestPassword, CancellationToken cancellationToken);
}
