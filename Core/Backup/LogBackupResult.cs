namespace Seebot.WorkerAgent.Core.Backup;

public sealed class LogBackupResult
{
    public string TxId { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public IReadOnlyList<string> Directories { get; set; } = [];
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
    public bool Success { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}
