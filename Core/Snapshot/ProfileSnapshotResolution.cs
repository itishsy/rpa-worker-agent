namespace Seebot.WorkerAgent.Core.Snapshot;

public sealed class ProfileSnapshotResolution
{
    public ProfileSnapshotResolutionStatus Status { get; init; }
    public string? SnapshotName { get; init; }
    public IReadOnlyList<string> MatchedSnapshotNames { get; init; } = [];
    public string? Message { get; init; }

    public bool IsReady => Status == ProfileSnapshotResolutionStatus.Ready
        && !string.IsNullOrWhiteSpace(SnapshotName);
}
