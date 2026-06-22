namespace Seebot.WorkerAgent.Core.Domain;

public sealed class BackupPathRecord
{
    public string Name { get; set; } = "";
    public string GuestPath { get; set; } = "";
    public string HostPath { get; set; } = "";
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}
