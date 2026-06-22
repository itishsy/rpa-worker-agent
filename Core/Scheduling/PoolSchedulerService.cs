using System.Globalization;
using Seebot.WorkerAgent.Core.Configuration;
using Seebot.WorkerAgent.Core.Domain;
using Seebot.WorkerAgent.Core.Reporting;
using Seebot.WorkerAgent.Core.Scheduler;
using Seebot.WorkerAgent.Core.Storage;
using Seebot.WorkerAgent.Core.Switching;

namespace Seebot.WorkerAgent.Core.Scheduling;

public sealed class PoolSchedulerService : IPoolSchedulerService
{
    private readonly ISchedulerClient _schedulerClient;
    private readonly IVmSwitchService _vmSwitchService;
    private readonly ILocalStore _localStore;
    private readonly WorkerAgentOptions _options;
    private readonly TimeProvider _timeProvider;

    public PoolSchedulerService(
        ISchedulerClient schedulerClient,
        IVmSwitchService vmSwitchService,
        ILocalStore localStore,
        WorkerAgentOptions options,
        TimeProvider? timeProvider = null)
    {
        _schedulerClient = schedulerClient;
        _vmSwitchService = vmSwitchService;
        _localStore = localStore;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<PoolSchedulerCycleResult> RunOneCycleAsync(CancellationToken cancellationToken)
    {
        var profileIds = GetConfiguredProfileIds();
        if (profileIds.Count == 0)
        {
            return NoSwitch("No configured profiles.");
        }

        var pendingByProfile = await QueryPendingProfilesAsync(profileIds, cancellationToken).ConfigureAwait(false);
        var targetProfiles = pendingByProfile.Values
            .Where(response => response.HasTask)
            .OrderByDescending(response => response.Priority)
            .ThenBy(response => ParseOldestQueuedAt(response.OldestQueuedAt))
            .ThenBy(response => response.ProfileId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var vmStates = await _localStore.GetVmStatesAsync(_options.Agent.HostId, cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        await ReportVmStatesAsync(vmStates, now, cancellationToken).ConfigureAwait(false);

        if (targetProfiles.Count == 0)
        {
            return NoSwitch("No pending profile tasks.");
        }

        var stateByVmName = vmStates.ToDictionary(state => state.VmName, StringComparer.OrdinalIgnoreCase);

        foreach (var target in targetProfiles)
        {
            var candidate = FindCandidate(target.ProfileId, pendingByProfile, stateByVmName, now);
            if (candidate is null)
            {
                continue;
            }

            var (vm, state, profile) = candidate.Value;
            var result = await _vmSwitchService.SwitchAsync(new VmSwitchRequest
            {
                HostId = _options.Agent.HostId,
                Vm = vm,
                FromProfileId = state.CurrentProfileId,
                FromSnapshotName = state.CurrentSnapshotName,
                TargetProfileId = target.ProfileId,
                TargetSnapshotName = profile.SnapshotName,
                FirstTaskId = target.FirstTaskId,
                Timestamp = now
            }, cancellationToken).ConfigureAwait(false);

            return new PoolSchedulerCycleResult
            {
                SwitchStarted = true,
                TargetProfileId = target.ProfileId,
                VmName = vm.Name,
                Reason = result.Success ? "Switch started." : result.ErrorCode ?? result.ErrorMessage
            };
        }

        return NoSwitch("No compatible idle VM candidate.");
    }

    private async Task ReportVmStatesAsync(
        IReadOnlyList<VmCurrentState> vmStates,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var state in vmStates)
        {
            await _schedulerClient
                .ReportVmStatusAsync(VmStatusReportBuilder.Build(_options, state, now), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<Dictionary<string, ProfilePendingTaskResponse>> QueryPendingProfilesAsync(
        IReadOnlyList<string> profileIds,
        CancellationToken cancellationToken)
    {
        var pendingByProfile = new Dictionary<string, ProfilePendingTaskResponse>(StringComparer.OrdinalIgnoreCase);
        foreach (var profileId in profileIds)
        {
            var response = await _schedulerClient.QueryPendingTasksAsync(profileId, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(response.ProfileId))
            {
                response.ProfileId = profileId;
            }

            pendingByProfile[profileId] = response;
        }

        return pendingByProfile;
    }

    private (VirtualMachineOptions Vm, VmCurrentState State, ProfileOptions Profile)? FindCandidate(
        string targetProfileId,
        IReadOnlyDictionary<string, ProfilePendingTaskResponse> pendingByProfile,
        IReadOnlyDictionary<string, VmCurrentState> stateByVmName,
        DateTimeOffset now)
    {
        foreach (var vm in _options.VirtualMachines)
        {
            var profile = vm.Profiles.FirstOrDefault(item =>
                string.Equals(item.ProfileId, targetProfileId, StringComparison.OrdinalIgnoreCase));
            if (profile is null)
            {
                continue;
            }

            if (!stateByVmName.TryGetValue(vm.Name, out var state))
            {
                continue;
            }

            if (string.Equals(state.CurrentProfileId, targetProfileId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var currentProfilePending = IsCurrentProfilePending(state.CurrentProfileId, pendingByProfile);
            var evaluation = WorkerStateEvaluator.EvaluateSwitchCandidate(
                state,
                currentProfilePending,
                _options.Agent.IdleStableSeconds,
                now);
            if (evaluation.CanSwitch)
            {
                return (vm, state, profile);
            }
        }

        return null;
    }

    private static bool IsCurrentProfilePending(
        string? currentProfileId,
        IReadOnlyDictionary<string, ProfilePendingTaskResponse> pendingByProfile)
    {
        if (string.IsNullOrWhiteSpace(currentProfileId))
        {
            return false;
        }

        return pendingByProfile.TryGetValue(currentProfileId, out var pending) && pending.HasTask;
    }

    private IReadOnlyList<string> GetConfiguredProfileIds()
    {
        return _options.VirtualMachines
            .SelectMany(vm => vm.Profiles)
            .Select(profile => profile.ProfileId)
            .Where(profileId => !string.IsNullOrWhiteSpace(profileId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static DateTimeOffset ParseOldestQueuedAt(string? oldestQueuedAt)
    {
        if (DateTimeOffset.TryParse(oldestQueuedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
        {
            return parsed;
        }

        return DateTimeOffset.MaxValue;
    }

    private static PoolSchedulerCycleResult NoSwitch(string reason)
    {
        return new PoolSchedulerCycleResult
        {
            SwitchStarted = false,
            Reason = reason
        };
    }
}
