using Seebot.WorkerAgent.Core.Configuration;
using Seebot.WorkerAgent.Core.Scheduler;
using Seebot.WorkerAgent.Core.Snapshot;
using Seebot.WorkerAgent.Core.Storage;
using Seebot.WorkerAgent.Core.Vmware;

namespace Seebot.WorkerAgent.Core.Startup;

public sealed class StartupValidator : IStartupValidator
{
    private const string Ready = "READY";
    private const string Missing = "MISSING";
    private const string Invalid = "INVALID";

    private readonly IVmrunService _vmrunService;
    private readonly IProfileSnapshotResolver _snapshotResolver;
    private readonly IVirtualMachineRegistry? _virtualMachineRegistry;

    public StartupValidator(
        IVmrunService vmrunService,
        IProfileSnapshotResolver snapshotResolver,
        IVirtualMachineRegistry? virtualMachineRegistry = null)
    {
        _vmrunService = vmrunService;
        _snapshotResolver = snapshotResolver;
        _virtualMachineRegistry = virtualMachineRegistry;
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
            var vmErrors = new List<string>();
            var vmCapability = new VmCapabilityDto
            {
                VmName = vm.Name,
                WorkerId = vm.WorkerId,
                VmxPath = vm.VmxPath,
                BaseSnapshotName = vm.BaseSnapshotName,
                Enabled = vm.Enabled,
                IsQuarantined = false
            };
            capabilities.Vms.Add(vmCapability);

            if (!vm.Enabled)
            {
                vmCapability.Enabled = false;
                foreach (var profile in vm.Profiles)
                {
                    vmCapability.Profiles.Add(new ProfileCapabilityDto
                    {
                        ProfileId = profile.ProfileId,
                        ProfileName = profile.ProfileName,
                        SnapshotName = "",
                        Enabled = false,
                        SnapshotExists = false,
                        ValidationStatus = Invalid,
                        ValidationMessage = vm.DisabledReason ?? "VM is disabled."
                    });
                }

                continue;
            }

            if (!File.Exists(vm.VmxPath))
            {
                vmErrors.Add($"VMX file does not exist for VM {vm.Name}: {vm.VmxPath}");
            }

            var snapshots = await LoadSnapshotsAsync(vm, vmErrors, cancellationToken);
            if (!ContainsSnapshot(snapshots, vm.BaseSnapshotName))
            {
                vmErrors.Add($"BaseSnapshotName does not exist for VM {vm.Name}: {vm.BaseSnapshotName}");
            }

            foreach (var profile in vm.Profiles)
            {
                var resolution = _snapshotResolver.Resolve(vm, profile, snapshots);
                var validationStatus = ToValidationStatus(resolution);
                var snapshotExists = resolution.IsReady;
                if (!snapshotExists)
                {
                    vmErrors.Add($"Profile snapshot is not ready for VM {vm.Name}, profile {profile.ProfileId}: {resolution.Message}");
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

            if (vmErrors.Count > 0)
            {
                var disabledReason = string.Join("; ", vmErrors);
                vm.Enabled = false;
                vm.DisabledReason = disabledReason;
                vmCapability.Enabled = false;
                foreach (var profileCapability in vmCapability.Profiles)
                {
                    profileCapability.Enabled = false;
                    if (string.Equals(profileCapability.ValidationStatus, Ready, StringComparison.OrdinalIgnoreCase))
                    {
                        profileCapability.ValidationStatus = Invalid;
                        profileCapability.ValidationMessage = disabledReason;
                    }
                }

                if (_virtualMachineRegistry is not null)
                {
                    await _virtualMachineRegistry.UpdateVmStatusAsync(vm.Name, enabled: false, disabledReason, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            else if (_virtualMachineRegistry is not null)
            {
                await _virtualMachineRegistry.UpdateVmStatusAsync(vm.Name, enabled: true, disabledReason: null, cancellationToken)
                    .ConfigureAwait(false);
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
