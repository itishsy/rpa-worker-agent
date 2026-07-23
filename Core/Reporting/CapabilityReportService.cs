using Microsoft.Extensions.Logging;
using Seebot.WorkerAgent.Core.Configuration;
using Seebot.WorkerAgent.Core.Domain;
using Seebot.WorkerAgent.Core.Scheduler;
using Seebot.WorkerAgent.Core.Snapshot;
using Seebot.WorkerAgent.Core.Storage;
using Seebot.WorkerAgent.Core.Vmware;

namespace Seebot.WorkerAgent.Core.Reporting;

public sealed class CapabilityReportService
{
    private readonly ISchedulerClient _schedulerClient;
    private readonly IVmrunService _vmrunService;
    private readonly IProfileSnapshotResolver _snapshotResolver;
    private readonly WorkerAgentOptions _options;
    private readonly ILogger<CapabilityReportService> _logger;
    private readonly ILocalStore? _localStore;

    public CapabilityReportService(
        ISchedulerClient schedulerClient,
        IVmrunService vmrunService,
        IProfileSnapshotResolver snapshotResolver,
        WorkerAgentOptions options,
        ILogger<CapabilityReportService> logger,
        ILocalStore? localStore = null)
    {
        _schedulerClient = schedulerClient;
        _vmrunService = vmrunService;
        _snapshotResolver = snapshotResolver;
        _options = options;
        _logger = logger;
        _localStore = localStore;
    }

    public async Task ReportOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Capability report started. HostId={HostId}, VmCount={VmCount}", _options.Agent.HostId, _options.VirtualMachines.Count);
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
        var stateByVmName = await LoadVmStatesAsync(cancellationToken).ConfigureAwait(false);

        foreach (var vm in _options.VirtualMachines)
        {
            IReadOnlyList<string> snapshots = [];
            if (vm.Enabled)
            {
                try
                {
                    _logger.LogInformation("Listing snapshots for capability report. VmName={VmName}, VmxPath={VmxPath}", vm.Name, vm.VmxPath);
                    snapshots = await _vmrunService.ListSnapshotsAsync(vm.VmxPath, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("Snapshots listed for capability report. VmName={VmName}, SnapshotCount={SnapshotCount}", vm.Name, snapshots.Count);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogWarning(exception, "Failed to list snapshots for VM {VmName} while building capabilities.", vm.Name);
                }
            }

            var machineCode = FirstNonEmpty(vm.WorkerId, vm.Name);
            var status = ResolveStatus(vm, stateByVmName);
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
                    SnapshotName = resolution.SnapshotName ?? "",
                    Status = status
                });
            }
        }

        return capabilities;
    }

    private async Task<IReadOnlyDictionary<string, VmCurrentState>> LoadVmStatesAsync(
        CancellationToken cancellationToken)
    {
        if (_localStore is null)
        {
            return new Dictionary<string, VmCurrentState>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var states = await _localStore
                .GetVmStatesAsync(_options.Agent.HostId, cancellationToken)
                .ConfigureAwait(false);
            return states.ToDictionary(state => state.VmName, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Failed to load VM quarantine states for capability report.");
            return new Dictionary<string, VmCurrentState>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string ResolveStatus(
        VirtualMachineOptions vm,
        IReadOnlyDictionary<string, VmCurrentState> stateByVmName)
    {
        if (stateByVmName.TryGetValue(vm.Name, out var state) && state.IsQuarantined)
        {
            return "Quarantined";
        }

        return vm.Enabled ? "Enabled" : "Disabled";
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
    }

}
