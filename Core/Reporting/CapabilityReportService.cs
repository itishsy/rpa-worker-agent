using Microsoft.Extensions.Logging;
using Seebot.WorkerAgent.Core.Configuration;
using Seebot.WorkerAgent.Core.Scheduler;
using Seebot.WorkerAgent.Core.Snapshot;
using Seebot.WorkerAgent.Core.Vmware;

namespace Seebot.WorkerAgent.Core.Reporting;

public sealed class CapabilityReportService
{
    private readonly ISchedulerClient _schedulerClient;
    private readonly IVmrunService _vmrunService;
    private readonly IProfileSnapshotResolver _snapshotResolver;
    private readonly WorkerAgentOptions _options;
    private readonly ILogger<CapabilityReportService> _logger;

    public CapabilityReportService(
        ISchedulerClient schedulerClient,
        IVmrunService vmrunService,
        IProfileSnapshotResolver snapshotResolver,
        WorkerAgentOptions options,
        ILogger<CapabilityReportService> logger)
    {
        _schedulerClient = schedulerClient;
        _vmrunService = vmrunService;
        _snapshotResolver = snapshotResolver;
        _options = options;
        _logger = logger;
    }

    public async Task ReportOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Capability report started. HostId={HostId}, VmCount={VmCount}", _options.Agent.HostId, EnabledVirtualMachines().Count);
            var capabilities = await BuildProfileCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Capability report payload built. HostId={HostId}, CapabilityCount={CapabilityCount}", _options.Agent.HostId, capabilities.Count);
            await _schedulerClient.ReportCapabilitiesAsync(capabilities, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Capability report completed. HostId={HostId}, CapabilityCount={CapabilityCount}", _options.Agent.HostId, capabilities.Count);
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

    private async Task<IReadOnlyList<HostProfileCapabilityRequest>> BuildProfileCapabilitiesAsync(CancellationToken cancellationToken)
    {
        var hostName = FirstNonEmpty(_options.Agent.AgentName, _options.Agent.HostId);
        var capabilities = new List<HostProfileCapabilityRequest>();

        foreach (var vm in EnabledVirtualMachines())
        {
            IReadOnlyList<string> snapshots;
            try
            {
                _logger.LogInformation("Listing snapshots for capability report. VmName={VmName}, VmxPath={VmxPath}", vm.Name, vm.VmxPath);
                snapshots = await _vmrunService.ListSnapshotsAsync(vm.VmxPath, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Snapshots listed for capability report. VmName={VmName}, SnapshotCount={SnapshotCount}", vm.Name, snapshots.Count);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Failed to list snapshots for VM {VmName} while building capabilities.", vm.Name);
                snapshots = [];
            }

            var machineCode = FirstNonEmpty(vm.WorkerId, vm.Name);
            foreach (var profile in vm.Profiles)
            {
                var resolution = _snapshotResolver.Resolve(vm, profile, snapshots);
                _logger.LogInformation(
                    "Profile capability resolved. VmName={VmName}, ProfileId={ProfileId}, SnapshotName={SnapshotName}, Status={Status}",
                    vm.Name,
                    profile.ProfileId,
                    resolution.SnapshotName,
                    resolution.Status);
                capabilities.Add(new HostProfileCapabilityRequest
                {
                    HostName = hostName,
                    MachineCode = machineCode,
                    ProfileCode = profile.ProfileId,
                    ProfileName = FirstNonEmpty(profile.ProfileName, profile.ProfileId),
                    SnapshotName = resolution.SnapshotName ?? ""
                });
            }
        }

        return capabilities;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
    }

    private IReadOnlyList<VirtualMachineOptions> EnabledVirtualMachines()
    {
        return _options.VirtualMachines.Where(vm => vm.Enabled).ToList();
    }
}
