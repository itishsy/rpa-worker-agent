using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seebot.WorkerAgent.Core.Configuration;
using Seebot.WorkerAgent.Core.Scheduler;
using Seebot.WorkerAgent.Core.Startup;

namespace Seebot.WorkerAgent.Core.Reporting;

public sealed class CapabilityReportService : BackgroundService
{
    private readonly ISchedulerClient _schedulerClient;
    private readonly IStartupValidator _startupValidator;
    private readonly WorkerAgentOptions _options;
    private readonly ILogger<CapabilityReportService> _logger;
    private readonly TimeProvider _timeProvider;

    public CapabilityReportService(
        ISchedulerClient schedulerClient,
        IStartupValidator startupValidator,
        WorkerAgentOptions options,
        ILogger<CapabilityReportService> logger,
        TimeProvider? timeProvider = null)
    {
        _schedulerClient = schedulerClient;
        _startupValidator = startupValidator;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task ReportOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var reportedAt = _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
            var validation = await _startupValidator
                .ValidateAndBuildCapabilitiesAsync(_options, reportedAt, cancellationToken)
                .ConfigureAwait(false);

            if (!validation.IsValid)
            {
                _logger.LogWarning("Startup capability validation completed with errors: {Errors}", string.Join("; ", validation.Errors));
            }

            await _schedulerClient.ReportCapabilitiesAsync(validation.Capabilities, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to report VM profile capabilities.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await ReportOnceAsync(stoppingToken).ConfigureAwait(false);
            await Task.Delay(GetInterval(), _timeProvider, stoppingToken).ConfigureAwait(false);
        }
    }

    private TimeSpan GetInterval()
    {
        var seconds = _options.Agent.CapabilityReportIntervalSeconds > 0
            ? _options.Agent.CapabilityReportIntervalSeconds
            : 300;
        return TimeSpan.FromSeconds(seconds);
    }
}
