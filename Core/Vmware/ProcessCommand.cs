namespace Seebot.WorkerAgent.Core.Vmware;

public sealed class ProcessCommand
{
    public ProcessCommand(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        string commandName)
    {
        FileName = fileName;
        Arguments = arguments;
        Timeout = timeout;
        CommandName = commandName;
    }

    public string FileName { get; }
    public IReadOnlyList<string> Arguments { get; }
    public TimeSpan Timeout { get; }
    public string CommandName { get; }
}
