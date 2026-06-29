namespace Seebot.WorkerAgent.Core.Snapshot;

public interface ISnapshotUpdateService
{
    Task<SnapshotUpdateResult> UpdateSnapshotAsync(
        string vmName,
        string profileId,
        CancellationToken cancellationToken);
}
