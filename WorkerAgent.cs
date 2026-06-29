using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seebot.WorkerAgent.Core.Configuration;
using Seebot.WorkerAgent.Core.Reporting;
using Seebot.WorkerAgent.Core.Scheduling;

namespace Seebot.WorkerAgent.Service;

public sealed class WorkerAgent : BackgroundService
{
    private readonly IPoolSchedulerService _poolSchedulerService;
    private readonly CapabilityReportService _capabilityReportService;
    private readonly WorkerAgentOptions _options;
    private readonly ILogger<WorkerAgent> _logger;

    public WorkerAgent(
        IPoolSchedulerService poolSchedulerService,
        CapabilityReportService capabilityReportService,
        WorkerAgentOptions options,
        ILogger<WorkerAgent> logger)
    {
        _poolSchedulerService = poolSchedulerService;
        _capabilityReportService = capabilityReportService;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RPA Worker Agent started.");

        await _capabilityReportService.ReportOnceAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await _poolSchedulerService.RunOneCycleAsync(stoppingToken).ConfigureAwait(false);
                if (result.SwitchStarted)
                {
                    _logger.LogInformation(
                        "Switch started: VM={VmName}, Profile={ProfileId}.",
                        result.VmName, result.TargetProfileId);
                }
                else
                {
                    _logger.LogDebug("Scheduler cycle: {Reason}", result.Reason);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Unhandled error in scheduler cycle.");
            }

            var interval = GetPollInterval();
            await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
        }

        _logger.LogInformation("RPA Worker Agent stopped.");
    }

    private TimeSpan GetPollInterval()
    {
        var seconds = _options.Agent.PollIntervalSeconds > 0 ? _options.Agent.PollIntervalSeconds : 30;
        return TimeSpan.FromSeconds(seconds);
    }
}
