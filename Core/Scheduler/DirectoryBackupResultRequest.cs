namespace Seebot.WorkerAgent.Core.Scheduler;

public sealed class DirectoryBackupResultRequest
{
    public string TxId { get; set; } = "";
    public string HostId { get; set; } = "";
    public string VmName { get; set; } = "";
    public string WorkerId { get; set; } = "";
    public string? FromProfileId { get; set; }
    public string? ToProfileId { get; set; }
    public long? FirstTaskId { get; set; }
    public bool Success { get; set; }
    public string BackupPath { get; set; } = "";
    public List<string> BackedUpDirectories { get; set; } = [];
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}
