namespace Seebot.WorkerAgent.Core.Snapshot;

public interface IInitFileUpdateService
{
    Task<InitFileUpdateResult> UpdateWorkerIdInSnapshotsAsync(string vmName, CancellationToken cancellationToken);
}
