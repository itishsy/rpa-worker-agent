using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Seebot.WorkerAgent.Service;

public sealed class WorkerAgent : BackgroundService
{
    private readonly ILogger<WorkerAgent> _logger;

    public WorkerAgent(ILogger<WorkerAgent> logger)
    {
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RPA Worker Agent service skeleton started.");
        return Task.CompletedTask;
    }
}
