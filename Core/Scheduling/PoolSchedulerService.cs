using Seebot.WorkerAgent.Core.Configuration;
using Seebot.WorkerAgent.Core.Domain;
using Seebot.WorkerAgent.Core.Guest;
using Seebot.WorkerAgent.Core.Scheduler;
using Seebot.WorkerAgent.Core.Snapshot;
using Seebot.WorkerAgent.Core.Storage;
using Seebot.WorkerAgent.Core.Switching;
using Seebot.WorkerAgent.Core.Vmware;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Seebot.WorkerAgent.Core.Scheduling;

public sealed class PoolSchedulerService : IPoolSchedulerService
{
    private readonly ISchedulerClient _schedulerClient;
    private readonly IGuestWorkerClient _guestWorkerClient;
    private readonly IVmStateRefreshService _vmStateRefreshService;
    private readonly IVmSwitchService _vmSwitchService;
    private readonly IVmrunService _vmrunService;
    private readonly IProfileSnapshotResolver _snapshotResolver;
    private readonly IVmOperationLock _vmOperationLock;
    private readonly ILocalStore _localStore;
    private readonly WorkerAgentOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PoolSchedulerService> _logger;

    public PoolSchedulerService(
        ISchedulerClient schedulerClient,
        IGuestWorkerClient guestWorkerClient,
        IVmStateRefreshService vmStateRefreshService,
        IVmSwitchService vmSwitchService,
        IVmrunService vmrunService,
        IProfileSnapshotResolver snapshotResolver,
        IVmOperationLock vmOperationLock,
        ILocalStore localStore,
        WorkerAgentOptions options,
        TimeProvider? timeProvider = null,
        ILogger<PoolSchedulerService>? logger = null)
    {
        _schedulerClient = schedulerClient;
        _guestWorkerClient = guestWorkerClient;
        _vmStateRefreshService = vmStateRefreshService;
        _vmSwitchService = vmSwitchService;
        _vmrunService = vmrunService;
        _snapshotResolver = snapshotResolver;
        _vmOperationLock = vmOperationLock;
        _localStore = localStore;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<PoolSchedulerService>.Instance;
    }

    public async Task<PoolSchedulerCycleResult> RunOneCycleAsync(CancellationToken cancellationToken)
    {
        var enabledVms = EnabledVirtualMachines();
        if (enabledVms.Count == 0)
        {
            _logger.LogInformation("调度周期已跳过：当前未配置可用的 VM。");
            return NoSwitch("No configured VMs.");
        }

        _logger.LogInformation("调度周期开始。HostId={HostId}, VmCount={VmCount}", _options.Agent.HostId, enabledVms.Count);
        var targetProfiles = await QueryPendingProfilesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("调度器待处理 Profile 查询完成。PendingProfileCount={PendingProfileCount}", targetProfiles.Count);

        var now = _timeProvider.GetUtcNow();
        _logger.LogInformation("候选 VM 评估前开始刷新 VM 状态。HostId={HostId}", _options.Agent.HostId);
        await _vmStateRefreshService.RefreshAsync(now, cancellationToken).ConfigureAwait(false);

        var vmStates = await _localStore.GetVmStatesAsync(_options.Agent.HostId, cancellationToken).ConfigureAwait(false);
        var activeTxVmNames = await GetActiveSwitchVmNamesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "调度周期已加载 VM 状态。VmStateCount={VmStateCount}, ActiveSwitchVmCount={ActiveSwitchVmCount}",
            vmStates.Count,
            activeTxVmNames.Count);

        if (targetProfiles.Count == 0)
        {
            _logger.LogInformation("调度周期结束，本次未执行切换。Reason={Reason}", "云端没有返回待处理的 Profile。");
            return NoSwitch("No pending profile tasks returned by scheduler.");
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
                _logger.LogInformation("目标 Profile 未找到可用的候选 VM。TargetProfileId={TargetProfileId}", target.ProfileId);
                continue;
            }

            var (vm, state, profile) = candidate.Value;
            _logger.LogInformation(
                "已选择候选 VM。VmName={VmName}, WorkerId={WorkerId}, CurrentProfileId={CurrentProfileId}, CurrentSnapshotName={CurrentSnapshotName}, TargetProfileId={TargetProfileId}",
                vm.Name,
                vm.WorkerId,
                state.CurrentProfileId,
                state.CurrentSnapshotName,
                target.ProfileId);
            await using var vmLock = await _vmOperationLock.AcquireAsync(vm.VmxPath, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<string> snapshots;
            try
            {
                _logger.LogInformation("切换候选项校验前开始读取快照列表。VmName={VmName}, VmxPath={VmxPath}", vm.Name, vm.VmxPath);
                snapshots = await _vmrunService.ListSnapshotsAsync(vm.VmxPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "读取候选 VM 的快照列表失败。VmName={VmName}, TargetProfileId={TargetProfileId}", vm.Name, target.ProfileId);
                continue;
            }

            var resolution = _snapshotResolver.Resolve(vm, profile, snapshots);
            if (!resolution.IsReady)
            {
                _logger.LogWarning(
                    "目标 Profile 快照尚未就绪。VmName={VmName}, TargetProfileId={TargetProfileId}, Status={Status}, Message={Message}",
                    vm.Name,
                    target.ProfileId,
                    resolution.Status,
                    resolution.Message);
                continue;
            }

            if (string.Equals(state.CurrentProfileId, target.ProfileId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(state.CurrentSnapshotName, resolution.SnapshotName, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "候选 VM 已处于目标 Profile 快照，无需切换。VmName={VmName}, TargetProfileId={TargetProfileId}, SnapshotName={SnapshotName}",
                    vm.Name,
                    target.ProfileId,
                    resolution.SnapshotName);
                continue;
            }

            _logger.LogInformation(
                "调度器开始执行 VM 切换。VmName={VmName}, FromProfileId={FromProfileId}, TargetProfileId={TargetProfileId}, TargetSnapshotName={TargetSnapshotName}, FirstTaskId={FirstTaskId}",
                vm.Name,
                state.CurrentProfileId,
                target.ProfileId,
                resolution.SnapshotName,
                target.FirstTaskId);
            var result = await _vmSwitchService.SwitchAsync(new VmSwitchRequest
            {
                HostId = _options.Agent.HostId,
                Vm = vm,
                FromProfileId = state.CurrentProfileId,
                FromSnapshotName = state.CurrentSnapshotName,
                TargetProfileId = target.ProfileId,
                TargetSnapshotName = resolution.SnapshotName!,
                FirstTaskId = target.FirstTaskId,
                Timestamp = now
            }, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "调度器 VM 切换请求执行完成。VmName={VmName}, TargetProfileId={TargetProfileId}, Success={Success}, ErrorCode={ErrorCode}, ErrorMessage={ErrorMessage}",
                vm.Name,
                target.ProfileId,
                result.Success,
                result.ErrorCode,
                result.ErrorMessage);
            return new PoolSchedulerCycleResult
            {
                SwitchStarted = result.Success && !result.Skipped,
                TargetProfileId = target.ProfileId,
                VmName = vm.Name,
                Reason = result.Skipped ? result.ErrorCode ?? "Switch skipped." : result.Success ? "Switch completed." : result.ErrorCode ?? result.ErrorMessage
            };
        }

        _logger.LogInformation("调度周期结束，本次未执行切换。Reason={Reason}", "没有兼容且处于空闲状态的候选 VM。");
        return NoSwitch("No compatible idle VM candidate.");
    }

    private async Task<IReadOnlyList<ProfilePendingTaskResponse>> QueryPendingProfilesAsync(
        CancellationToken cancellationToken)
    {
        var results = new List<ProfilePendingTaskResponse>();
        var vmStates = await _localStore.GetVmStatesAsync(_options.Agent.HostId, cancellationToken).ConfigureAwait(false);
        var stateByVmName = vmStates.ToDictionary(state => state.VmName, StringComparer.OrdinalIgnoreCase);

        foreach (var vm in EnabledVirtualMachines())
        {
            if (string.IsNullOrWhiteSpace(vm.WorkerId)
                || !stateByVmName.TryGetValue(vm.Name, out var state)
                || string.IsNullOrWhiteSpace(state.CurrentProfileId))
            {
                continue;
            }

            var profiles = await _schedulerClient.QueryPendingTasksAsync(vm.WorkerId, state.CurrentProfileId, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "待处理任务查询完成。VmName={VmName}, WorkerId={WorkerId}, CurrentProfileId={CurrentProfileId}, ReturnedProfileCount={ReturnedProfileCount}",
                vm.Name,
                vm.WorkerId,
                state.CurrentProfileId,
                profiles.Count);
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
        foreach (var vm in EnabledVirtualMachines())
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

            _logger.LogInformation(
                "VM 未通过切换候选条件校验。VmName={VmName}, TargetProfileId={TargetProfileId}, RunnerStatusCode={RunnerStatusCode}, ErrorCode={ErrorCode}, Reason={Reason}",
                vm.Name,
                targetProfileId,
                state.RunnerStatusCode,
                evaluation.ErrorCode,
                evaluation.Reason);
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

            var vm = EnabledVirtualMachines().FirstOrDefault(v =>
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

    private IReadOnlyList<VirtualMachineOptions> EnabledVirtualMachines()
    {
        return _options.VirtualMachines.Where(vm => vm.Enabled).ToList();
    }
}
