namespace Seebot.WorkerAgent.Core.Vmware;

public sealed class VmrunCommandException : Exception
{
    public VmrunCommandException(VmrunCommandResult result)
        : base($"vmrun command '{result.CommandName}' failed with exit code {result.ExitCode}: {result.StandardError}")
    {
        Result = result;
    }

    public VmrunCommandResult Result { get; }
}
