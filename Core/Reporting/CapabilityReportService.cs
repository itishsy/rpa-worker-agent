using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seebot.WorkerAgent.Core.Configuration;
using Seebot.WorkerAgent.Core.Scheduler;

namespace Seebot.WorkerAgent.Core.Reporting;

public sealed class CapabilityReportService : BackgroundService
{
    private readonly ISchedulerClient _schedulerClient;
    private readonly WorkerAgentOptions _options;
    private readonly ILogger<CapabilityReportService> _logger;

    public CapabilityReportService(
        ISchedulerClient schedulerClient,
        WorkerAgentOptions options,
        ILogger<CapabilityReportService> logger)
    {
        _schedulerClient = schedulerClient;
        _options = options;
        _logger = logger;
    }

    public async Task ReportOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var capabilities = BuildProfileCapabilities();
            await _schedulerClient.ReportCapabilitiesAsync(capabilities, cancellationToken).ConfigureAwait(false);
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
            try
            {
                await Task.Delay(GetInterval(), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private TimeSpan GetInterval()
    {
        var seconds = _options.Agent.CapabilityReportIntervalSeconds > 0
            ? _options.Agent.CapabilityReportIntervalSeconds
            : 300;
        return TimeSpan.FromSeconds(seconds);
    }

    private IReadOnlyList<HostProfileCapabilityRequest> BuildProfileCapabilities()
    {
        var hostName = FirstNonEmpty(_options.Agent.AgentName, _options.Agent.HostId);

        return _options.VirtualMachines
            .SelectMany(vm =>
            {
                var machineCode = FirstNonEmpty(vm.WorkerId, vm.Name);
                return vm.Profiles.Select(profile => new HostProfileCapabilityRequest
                {
                    HostName = hostName,
                    MachineCode = machineCode,
                    ProfileId = profile.ProfileId,
                    ProfileName = FirstNonEmpty(profile.ProfileName, profile.ProfileId),
                    SnapshotName = profile.SnapshotName
                });
            })
            .ToList();
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
    }
}
