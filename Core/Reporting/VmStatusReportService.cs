using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seebot.WorkerAgent.Core.Configuration;
using Seebot.WorkerAgent.Core.Scheduler;
using Seebot.WorkerAgent.Core.Storage;

namespace Seebot.WorkerAgent.Core.Reporting;

public sealed class VmStatusReportService : BackgroundService
{
    private readonly ISchedulerClient _schedulerClient;
    private readonly ILocalStore _localStore;
    private readonly WorkerAgentOptions _options;
    private readonly ILogger<VmStatusReportService> _logger;
    private readonly TimeProvider _timeProvider;

    public VmStatusReportService(
        ISchedulerClient schedulerClient,
        ILocalStore localStore,
        WorkerAgentOptions options,
        ILogger<VmStatusReportService> logger,
        TimeProvider? timeProvider = null)
    {
        _schedulerClient = schedulerClient;
        _localStore = localStore;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task ReportOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var vmStates = await _localStore.GetVmStatesAsync(_options.Agent.HostId, cancellationToken).ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();
            foreach (var state in vmStates)
            {
                var request = VmStatusReportBuilder.Build(_options, state, now);
                await _schedulerClient.ReportVmStatusAsync(request, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to report VM current status.");
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
        var seconds = _options.Agent.HeartbeatIntervalSeconds > 0 ? _options.Agent.HeartbeatIntervalSeconds : 15;
        return TimeSpan.FromSeconds(seconds);
    }
}
