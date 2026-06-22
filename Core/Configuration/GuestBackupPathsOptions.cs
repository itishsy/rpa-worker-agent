namespace Seebot.WorkerAgent.Core.Configuration;

public sealed class GuestBackupPathsOptions
{
    public string Cache { get; set; } = "";
    public string Db { get; set; } = "";
    public string File { get; set; } = "";
    public string Logs { get; set; } = "";
}
