namespace Seebot.WorkerAgent.Core.Snapshot;

public interface IConfigFileUpdater
{
    Task UpdateSnapshotNameAsync(
        string vmName,
        string profileId,
        string newSnapshotName,
        CancellationToken cancellationToken);
}
