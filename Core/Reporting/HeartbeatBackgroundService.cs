using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seebot.WorkerAgent.Core.Configuration;
using Seebot.WorkerAgent.Core.Domain;
using Seebot.WorkerAgent.Core.Scheduler;
using Seebot.WorkerAgent.Core.Storage;

namespace Seebot.WorkerAgent.Core.Reporting;

public sealed class HeartbeatBackgroundService : BackgroundService
{
    private readonly ISchedulerClient _schedulerClient;
    private readonly ILocalStore _localStore;
    private readonly WorkerAgentOptions _options;
    private readonly ILogger<HeartbeatBackgroundService> _logger;
    private readonly TimeProvider _timeProvider;

    public HeartbeatBackgroundService(
        ISchedulerClient schedulerClient,
        ILocalStore localStore,
        WorkerAgentOptions options,
        ILogger<HeartbeatBackgroundService> logger,
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
            await _schedulerClient.ReportHeartbeatAsync(new HostAgentHeartbeatRequest
            {
                HostId = _options.Agent.HostId,
                AgentName = _options.Agent.AgentName,
                Status = AgentStatus.RUNNING.ToString(),
                VmCount = _options.VirtualMachines.Count,
                QuarantinedVmCount = vmStates.Count(state => state.IsQuarantined),
                Version = typeof(HeartbeatBackgroundService).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                Timestamp = now.ToString("O", CultureInfo.InvariantCulture)
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to report agent heartbeat.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await ReportOnceAsync(stoppingToken).ConfigureAwait(false);
            await DelayAsync(GetInterval(), stoppingToken).ConfigureAwait(false);
        }
    }

    private TimeSpan GetInterval()
    {
        var seconds = _options.Agent.HeartbeatIntervalSeconds > 0 ? _options.Agent.HeartbeatIntervalSeconds : 15;
        return TimeSpan.FromSeconds(seconds);
    }

    private Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        return Task.Delay(delay, _timeProvider, cancellationToken);
    }
}
