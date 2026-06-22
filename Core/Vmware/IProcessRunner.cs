namespace Seebot.WorkerAgent.Core.Vmware;

public interface IProcessRunner
{
    Task<VmrunCommandResult> RunAsync(ProcessCommand command, CancellationToken cancellationToken);
}
