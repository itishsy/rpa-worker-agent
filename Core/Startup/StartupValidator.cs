using Seebot.WorkerAgent.Core.Configuration;
using Seebot.WorkerAgent.Core.Scheduler;
using Seebot.WorkerAgent.Core.Vmware;

namespace Seebot.WorkerAgent.Core.Startup;

public sealed class StartupValidator : IStartupValidator
{
    private const string Ready = "READY";
    private const string Missing = "MISSING";
    private const string Invalid = "INVALID";

    private readonly IVmrunService _vmrunService;

    public StartupValidator(IVmrunService vmrunService)
    {
        _vmrunService = vmrunService;
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

            if (!string.Equals(vm.BaseSnapshotName, vm.Name, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"BaseSnapshotName for VM {vm.Name} must match VM name.");
            }

            var snapshots = await LoadSnapshotsAsync(vm, errors, cancellationToken);
            if (!ContainsSnapshot(snapshots, vm.BaseSnapshotName))
            {
                errors.Add($"BaseSnapshotName does not exist for VM {vm.Name}: {vm.BaseSnapshotName}");
            }

            foreach (var profile in vm.Profiles)
            {
                var snapshotExists = ContainsSnapshot(snapshots, profile.SnapshotName);
                if (!snapshotExists)
                {
                    errors.Add($"Profile snapshot does not exist for VM {vm.Name}, profile {profile.ProfileId}: {profile.SnapshotName}");
                }

                vmCapability.Profiles.Add(new ProfileCapabilityDto
                {
                    ProfileId = profile.ProfileId,
                    SnapshotName = profile.SnapshotName,
                    Enabled = true,
                    SnapshotExists = snapshotExists,
                    ValidationStatus = snapshotExists ? Ready : Missing,
                    ValidationMessage = snapshotExists ? null : "Configured snapshot was not returned by vmrun listSnapshots."
                });
            }

            if (!string.Equals(vm.BaseSnapshotName, vm.Name, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var profileCapability in vmCapability.Profiles)
                {
                    if (profileCapability.ValidationStatus == Ready)
                    {
                        profileCapability.ValidationStatus = Invalid;
                        profileCapability.ValidationMessage = "BaseSnapshotName does not match VM name.";
                    }
                }
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
}
