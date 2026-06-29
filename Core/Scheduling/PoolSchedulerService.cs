using Seebot.WorkerAgent.Core.Configuration;
using Seebot.WorkerAgent.Core.Domain;
using Seebot.WorkerAgent.Core.Guest;
using Seebot.WorkerAgent.Core.Scheduler;
using Seebot.WorkerAgent.Core.Storage;
using Seebot.WorkerAgent.Core.Switching;

namespace Seebot.WorkerAgent.Core.Scheduling;

public sealed class PoolSchedulerService : IPoolSchedulerService
{
    private readonly ISchedulerClient _schedulerClient;
    private readonly IGuestWorkerClient _guestWorkerClient;
    private readonly IVmStateRefreshService _vmStateRefreshService;
    private readonly IVmSwitchService _vmSwitchService;
    private readonly ILocalStore _localStore;
    private readonly WorkerAgentOptions _options;
    private readonly TimeProvider _timeProvider;

    public PoolSchedulerService(
        ISchedulerClient schedulerClient,
        IGuestWorkerClient guestWorkerClient,
        IVmStateRefreshService vmStateRefreshService,
        IVmSwitchService vmSwitchService,
        ILocalStore localStore,
        WorkerAgentOptions options,
        TimeProvider? timeProvider = null)
    {
        _schedulerClient = schedulerClient;
        _guestWorkerClient = guestWorkerClient;
        _vmStateRefreshService = vmStateRefreshService;
        _vmSwitchService = vmSwitchService;
        _localStore = localStore;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<PoolSchedulerCycleResult> RunOneCycleAsync(CancellationToken cancellationToken)
    {
        if (_options.VirtualMachines.Count == 0)
        {
            return NoSwitch("No configured VMs.");
        }

        var targetProfiles = await QueryPendingProfilesAsync(cancellationToken).ConfigureAwait(false);

        var now = _timeProvider.GetUtcNow();
        await _vmStateRefreshService.RefreshAsync(now, cancellationToken).ConfigureAwait(false);

        var vmStates = await _localStore.GetVmStatesAsync(_options.Agent.HostId, cancellationToken).ConfigureAwait(false);
        var activeTxVmNames = await GetActiveSwitchVmNamesAsync(cancellationToken).ConfigureAwait(false);

        if (targetProfiles.Count == 0)
        {
            var generalTargets = BuildGeneralRevertTargets(vmStates);
            if (generalTargets.Count == 0)
            {
                return NoSwitch("No pending profile tasks.");
            }

            targetProfiles = generalTargets;
        }

        var pendingProfileIds = targetProfiles.Select(r => r.ProfileId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var s in vmStates)
        {
            s.HasActiveSwitchTransaction = activeTxVmNames.Contains(s.VmName);
        }

        var stateByVmName = vmStates.ToDictionary(state => state.VmName, StringComparer.OrdinalIgnoreCase);

        foreach (var target in targetProfiles)
        {
            var candidate = FindCandidate(target.ProfileId, pendingProfileIds, stateByVmName, now);
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

    private IReadOnlyList<ProfilePendingTaskResponse> BuildGeneralRevertTargets(IReadOnlyList<VmCurrentState> vmStates)
    {
        var generalProfileId = _options.Agent.GeneralProfileId;
        if (string.IsNullOrWhiteSpace(generalProfileId))
        {
            return [];
        }

        var targets = new List<ProfilePendingTaskResponse>();
        foreach (var state in vmStates)
        {
            if (string.Equals(state.CurrentProfileId, generalProfileId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var vm = _options.VirtualMachines.FirstOrDefault(v =>
                string.Equals(v.Name, state.VmName, StringComparison.OrdinalIgnoreCase));
            if (vm is null)
            {
                continue;
            }

            var generalProfile = vm.Profiles.FirstOrDefault(p =>
                string.Equals(p.ProfileId, generalProfileId, StringComparison.OrdinalIgnoreCase));
            if (generalProfile is null)
            {
                continue;
            }

            targets.Add(new ProfilePendingTaskResponse
            {
                HasTask = true,
                ProfileId = generalProfileId,
                PendingCount = 0,
                FirstTaskId = null,
                Priority = 0,
                OldestQueuedAt = null
            });

            break;
        }

        return targets;
    }

    private async Task<IReadOnlyList<ProfilePendingTaskResponse>> QueryPendingProfilesAsync(
        CancellationToken cancellationToken)
    {
        var workerIds = _options.VirtualMachines
            .Select(vm => vm.WorkerId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var results = new List<ProfilePendingTaskResponse>();
        foreach (var workerId in workerIds)
        {
            var profiles = await _schedulerClient.QueryPendingTasksAsync(workerId, cancellationToken).ConfigureAwait(false);
            results.AddRange(profiles);
        }

        return results
            .Where(profile => profile.HasTask && !string.IsNullOrWhiteSpace(profile.ProfileId))
            .OrderByDescending(profile => profile.Priority)
            .ThenBy(profile => profile.OldestQueuedAt, StringComparer.Ordinal)
            .ToList();
    }

    private (VirtualMachineOptions Vm, VmCurrentState State, ProfileOptions Profile)? FindCandidate(
        string targetProfileId,
        IReadOnlySet<string> pendingProfileIds,
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

            if (string.Equals(state.CurrentProfileId, targetProfileId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(state.CurrentSnapshotName, profile.SnapshotName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var currentProfilePending = !string.IsNullOrWhiteSpace(state.CurrentProfileId)
                && pendingProfileIds.Contains(state.CurrentProfileId);
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

    private async Task<HashSet<string>> GetActiveSwitchVmNamesAsync(CancellationToken cancellationToken)
    {
        var txList = await _localStore.GetIncompleteSwitchTransactionsAsync(_options.Agent.HostId, cancellationToken).ConfigureAwait(false);
        if (txList.Count == 0)
        {
            return [];
        }

        var now = _timeProvider.GetUtcNow();
        var activeVmNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tx in txList)
        {
            var elapsed = now - (tx.UpdatedAt ?? tx.CreatedAt);
            if (elapsed < TimeSpan.FromSeconds(20))
            {
                activeVmNames.Add(tx.VmName);
                continue;
            }

            var vm = _options.VirtualMachines.FirstOrDefault(v =>
                string.Equals(v.Name, tx.VmName, StringComparison.OrdinalIgnoreCase));
            if (vm is null)
            {
                activeVmNames.Add(tx.VmName);
                continue;
            }

            bool runnerResponded;
            try
            {
                var status = await _guestWorkerClient.GetRunnerStatusAsync(vm, cancellationToken).ConfigureAwait(false);
                runnerResponded = status.Success;
            }
            catch (Exception)
            {
                runnerResponded = false;
            }

            if (runnerResponded)
            {
                await _localStore.DeleteSwitchTransactionAsync(tx.TransactionId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                activeVmNames.Add(tx.VmName);
            }
        }

        return activeVmNames;
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
