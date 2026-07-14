namespace Seebot.WorkerAgent.Core.Snapshot;

public sealed class ProfileInitUpdateResult
{
    public string ProfileId { get; set; } = "";
    public bool Success { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? OldSnapshotName { get; set; }
    public string? NewSnapshotName { get; set; }
}

public sealed class InitFileUpdateResult
{
    public bool Success { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public IReadOnlyList<ProfileInitUpdateResult> Profiles { get; set; } = [];
}
