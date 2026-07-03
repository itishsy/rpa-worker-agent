using Seebot.WorkerAgent.Core.Configuration;
using Seebot.WorkerAgent.Core.Domain;
using Seebot.WorkerAgent.Core.Guest;
using Seebot.WorkerAgent.Core.Snapshot;
using Seebot.WorkerAgent.Core.Storage;
using Seebot.WorkerAgent.Core.Vmware;

namespace Seebot.WorkerAgent.Core.Scheduling;

public sealed class VmStateRefreshService : IVmStateRefreshService
{
    private readonly IGuestWorkerClient _guestWorkerClient;
    private readonly IVmrunService _vmrunService;
    private readonly IProfileSnapshotResolver _snapshotResolver;
    private readonly ILocalStore _localStore;
    private readonly WorkerAgentOptions _options;

    public VmStateRefreshService(
        IGuestWorkerClient guestWorkerClient,
        IVmrunService vmrunService,
        IProfileSnapshotResolver snapshotResolver,
        ILocalStore localStore,
        WorkerAgentOptions options)
    {
        _guestWorkerClient = guestWorkerClient;
        _vmrunService = vmrunService;
        _snapshotResolver = snapshotResolver;
        _localStore = localStore;
        _options = options;
    }

    public async Task RefreshAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var vm in _options.VirtualMachines)
        {
            var existing = await _localStore.GetVmStateAsync(_options.Agent.HostId, vm.Name, cancellationToken).ConfigureAwait(false)
                           ?? new VmCurrentState
                           {
                               VmName = vm.Name,
                               WorkerId = vm.WorkerId,
                               VmStatus = AgentVmStatus.UNKNOWN,
                               UpdatedAt = now
                           };

            // 第一步：读取 VM 当前快照，更新 snapshot/profile
            string? currentSnapshotName;
            try
            {
                currentSnapshotName = await _vmrunService.GetCurrentSnapshotAsync(vm.VmxPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                currentSnapshotName = existing.CurrentSnapshotName;
            }

            var currentProfileId = _snapshotResolver.ResolveProfileId(vm, currentSnapshotName) ?? existing.CurrentProfileId;

            // 第二步：查询 runner 状态，更新运行时字段
            RunnerStatusResponse status;
            try
            {
                status = await _guestWorkerClient.GetRunnerStatusAsync(vm, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                status = new RunnerStatusResponse
                {
                    Success = false,
                    RunnerStatusCode = RunnerStatusCode.Offline
                };
            }

            DateTimeOffset? idleSince = existing.IdleSince;
            if (status.RunnerStatusCode == RunnerStatusCode.Runnable && status.CurrentTaskId is null)
            {
                idleSince ??= now;
            }
            else
            {
                idleSince = null;
            }

            await _localStore.UpsertVmStateAsync(_options.Agent.HostId, new VmCurrentState
            {
                VmName = vm.Name,
                WorkerId = vm.WorkerId,
                VmStatus = existing.VmStatus,
                CurrentProfileId = currentProfileId,
                CurrentSnapshotName = currentSnapshotName,
                RunnerStatusCode = status.RunnerStatusCode,
                IsQuarantined = existing.IsQuarantined,
                IdleSince = idleSince,
                ErrorCode = existing.ErrorCode,
                ErrorMessage = existing.ErrorMessage,
                UpdatedAt = now
            }, cancellationToken).ConfigureAwait(false);
        }
    }

}
