using Microsoft.Extensions.Logging;

namespace Seebot.WorkerAgent.Core.Logging;

public sealed class FileLoggerOptions
{
    public bool Enabled { get; set; } = true;
    public string DirectoryPath { get; set; } = "";
    public string FileNamePrefix { get; set; } = "rpa-worker-agent";
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;
}
