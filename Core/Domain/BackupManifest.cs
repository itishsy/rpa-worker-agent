namespace Seebot.WorkerAgent.Core.Domain;

public sealed class BackupManifest
{
    public string VmName { get; set; } = "";
    public string WorkerId { get; set; } = "";
    public string? SourceProfileId { get; set; }
    public string? SourceSnapshotName { get; set; }
    public string BackupRootPath { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public List<BackupPathRecord> Paths { get; set; } = [];
    public bool Success { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}
