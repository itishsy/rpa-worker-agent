namespace Seebot.WorkerAgent.Core.Vmware;

public sealed record VmrunCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration,
    string CommandName);
