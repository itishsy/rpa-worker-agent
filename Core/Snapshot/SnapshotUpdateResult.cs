namespace Seebot.WorkerAgent.Core.Snapshot;

public sealed class SnapshotUpdateResult
{
    public bool Success { get; set; }
    public string? NewSnapshotName { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string Step { get; set; } = "";
}
