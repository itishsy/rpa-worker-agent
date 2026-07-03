using Seebot.WorkerAgent.Core.Configuration;
using Seebot.WorkerAgent.Core.Scheduler;
using Seebot.WorkerAgent.Core.Snapshot;
using Seebot.WorkerAgent.Core.Vmware;

namespace Seebot.WorkerAgent.Core.Startup;

public sealed class StartupValidator : IStartupValidator
{
    private const string Ready = "READY";
    private const string Missing = "MISSING";
    private const string Invalid = "INVALID";

    private readonly IVmrunService _vmrunService;
    private readonly IProfileSnapshotResolver _snapshotResolver;

    public StartupValidator(IVmrunService vmrunService, IProfileSnapshotResolver snapshotResolver)
    {
        _vmrunService = vmrunService;
        _snapshotResolver = snapshotResolver;
    }

    public async Task<StartupValidationResult> ValidateAndBuildCapabilitiesAsync(
        WorkerAgentOptions options,
        string reportedAt,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var capabilities = new HostAgentCapabilitiesRequest
        {
            HostId = options.Agent.HostId,
            AgentName = options.Agent.AgentName,
            ReportedAt = reportedAt
        };

        if (!File.Exists(options.Vmrun.VmrunPath))
        {
            errors.Add($"Vmrun.VmrunPath does not exist: {options.Vmrun.VmrunPath}");
        }

        foreach (var vm in options.VirtualMachines)
        {
            var vmCapability = new VmCapabilityDto
            {
                VmName = vm.Name,
                WorkerId = vm.WorkerId,
                VmxPath = vm.VmxPath,
                BaseSnapshotName = vm.BaseSnapshotName,
                Enabled = true,
                IsQuarantined = false
            };
            capabilities.Vms.Add(vmCapability);

            if (!File.Exists(vm.VmxPath))
            {
                errors.Add($"VMX file does not exist for VM {vm.Name}: {vm.VmxPath}");
            }

            var snapshots = await LoadSnapshotsAsync(vm, errors, cancellationToken);
            if (!ContainsSnapshot(snapshots, vm.BaseSnapshotName))
            {
                errors.Add($"BaseSnapshotName does not exist for VM {vm.Name}: {vm.BaseSnapshotName}");
            }

            foreach (var profile in vm.Profiles)
            {
                var resolution = _snapshotResolver.Resolve(vm, profile, snapshots);
                var validationStatus = ToValidationStatus(resolution);
                var snapshotExists = resolution.IsReady;
                if (!snapshotExists)
                {
                    errors.Add($"Profile snapshot is not ready for VM {vm.Name}, profile {profile.ProfileId}: {resolution.Message}");
                }

                vmCapability.Profiles.Add(new ProfileCapabilityDto
                {
                    ProfileId = profile.ProfileId,
                    ProfileName = profile.ProfileName,
                    SnapshotName = resolution.SnapshotName ?? "",
                    Enabled = true,
                    SnapshotExists = snapshotExists,
                    ValidationStatus = validationStatus,
                    ValidationMessage = resolution.IsReady ? null : resolution.Message
                });
            }
        }

        return new StartupValidationResult(capabilities, errors);
    }

    private async Task<IReadOnlyList<string>> LoadSnapshotsAsync(
        VirtualMachineOptions vm,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _vmrunService.ListSnapshotsAsync(vm.VmxPath, cancellationToken);
        }
        catch (Exception exception)
        {
            errors.Add($"Failed to list snapshots for VM {vm.Name}: {exception.Message}");
            return [];
        }
    }

    private static bool ContainsSnapshot(IReadOnlyList<string> snapshots, string snapshotName)
    {
        return snapshots.Any(snapshot => string.Equals(snapshot, snapshotName, StringComparison.OrdinalIgnoreCase));
    }

    private static string ToValidationStatus(ProfileSnapshotResolution resolution)
    {
        return resolution.Status switch
        {
            ProfileSnapshotResolutionStatus.Ready => Ready,
            ProfileSnapshotResolutionStatus.Missing => Missing,
            _ => Invalid
        };
    }
}
