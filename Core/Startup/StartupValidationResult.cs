using Seebot.WorkerAgent.Core.Scheduler;

namespace Seebot.WorkerAgent.Core.Startup;

public sealed class StartupValidationResult
{
    public StartupValidationResult(HostAgentCapabilitiesRequest capabilities, IReadOnlyList<string> errors)
    {
        Capabilities = capabilities;
        Errors = errors;
    }

    public bool IsValid => Errors.Count == 0;
    public HostAgentCapabilitiesRequest Capabilities { get; }
    public IReadOnlyList<string> Errors { get; }
}
