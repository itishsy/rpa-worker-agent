using Seebot.WorkerAgent.Core.Configuration;

namespace Seebot.WorkerAgent.Core.Startup;

public interface IStartupValidator
{
    Task<StartupValidationResult> ValidateAndBuildCapabilitiesAsync(
        WorkerAgentOptions options,
        string reportedAt,
        CancellationToken cancellationToken);
}
