using Seebot.WorkerAgent.Core.Backup;
using Seebot.WorkerAgent.Core.Configuration;
using Seebot.WorkerAgent.Core.Domain;
using Seebot.WorkerAgent.Core.Guest;
using Seebot.WorkerAgent.Core.Storage;
using Seebot.WorkerAgent.Core.Vmware;

namespace Seebot.WorkerAgent.Core.Switching;

public sealed class VmSwitchService : IVmSwitchService
{
    private readonly IGuestWorkerClient _guestWorkerClient;
    private readonly ILogBackupService _logBackupService;
    private readonly IVmrunService _vmrunService;
    private readonly ILocalStore _localStore;
    private readonly WorkerAgentOptions _options;

    public VmSwitchService(
        IGuestWorkerClient guestWorkerClient,
        ILogBackupService logBackupService,
        IVmrunService vmrunService,
        ILocalStore localStore,
        WorkerAgentOptions options)
    {
        _guestWorkerClient = guestWorkerClient;
        _logBackupService = logBackupService;
        _vmrunService = vmrunService;
        _localStore = localStore;
        _options = options;
    }

    public async Task<VmSwitchResult> SwitchAsync(VmSwitchRequest request, CancellationToken cancellationToken)
    {
        var tx = CreateTransaction(request);
        await _localStore.CreateSwitchTransactionAsync(tx, cancellationToken);

        var beforeStatus = await _guestWorkerClient.GetRunnerStatusAsync(request.Vm, cancellationToken);
        if (beforeStatus.RunnerStatusCode == RunnerStatusCode.Running)
        {
            return await FailAsync(tx, ErrorCodes.WorkerRunning, "Runner is Running.", request.Timestamp, cancellationToken);
        }

        if (beforeStatus.RunnerStatusCode == RunnerStatusCode.Upgrading)
        {
            return await FailAsync(tx, ErrorCodes.WorkerUpgrading, "Runner is Upgrading.", request.Timestamp, cancellationToken);
        }

        var kill = await _guestWorkerClient.KillRunnerAsync(
            request.Vm,
            tx.TransactionId,
            "SNAPSHOT_SWITCH",
            deadlineSeconds: 30,
            cancellationToken);

        if (!kill.Success)
        {
            var errorCode = string.IsNullOrWhiteSpace(kill.ErrorCode) ? ErrorCodes.ExecutorStopFailed : kill.ErrorCode;
            return await FailAsync(tx, errorCode!, kill.Message, request.Timestamp, cancellationToken);
        }

        if (kill.CurrentTaskId is not null)
        {
            return await FailAsync(tx, ErrorCodes.ExecutorStopFailed, "Runner still has currentTaskId after kill.", request.Timestamp, cancellationToken);
        }

        await UpdateAsync(tx, SwitchTransactionStatus.STOP_RUNNER_DONE, "runner-stopped", null, null, request.Timestamp, cancellationToken);

        var backup = await _logBackupService.BackupAsync(request.Vm, tx, request.Timestamp, cancellationToken);
        if (!backup.Success && !_options.Agent.ForceRevertWhenBackupFailed)
        {
            return await FailAsync(tx, backup.ErrorCode ?? ErrorCodes.LogBackupFailed, backup.ErrorMessage, request.Timestamp, cancellationToken);
        }

        await UpdateAsync(tx, SwitchTransactionStatus.LOG_BACKUP_DONE, "log-backup-done", backup.ErrorCode, backup.ErrorMessage, request.Timestamp, cancellationToken);

        try
        {
            await _vmrunService.StopVmAsync(request.Vm.VmxPath, VmStopMode.Soft, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return await FailAndMarkVmErrorAsync(tx, request, ErrorCodes.VmStopFailed, exception.Message, cancellationToken);
        }

        await UpdateAsync(tx, SwitchTransactionStatus.VM_STOP_DONE, "vm-stop-done", null, null, request.Timestamp, cancellationToken);

        try
        {
            await _vmrunService.RevertToSnapshotAsync(request.Vm.VmxPath, request.TargetSnapshotName, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return await FailAndMarkVmErrorAsync(tx, request, ErrorCodes.SnapshotRevertFailed, exception.Message, cancellationToken);
        }

        await UpdateAsync(tx, SwitchTransactionStatus.SNAPSHOT_REVERT_DONE, "snapshot-revert-done", null, null, request.Timestamp, cancellationToken);

        try
        {
            await _vmrunService.StartVmAsync(request.Vm.VmxPath, _options.Vmrun.DefaultStartNoGui, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return await FailAndMarkVmErrorAsync(tx, request, ErrorCodes.VmStartFailed, exception.Message, cancellationToken);
        }

        await UpdateAsync(tx, SwitchTransactionStatus.VM_START_DONE, "vm-start-done", null, null, request.Timestamp, cancellationToken);

        var readyStatus = await _guestWorkerClient.GetRunnerStatusAsync(request.Vm, cancellationToken);
        var ready = WorkerStateEvaluator.EvaluateReadyAfterVmStart(readyStatus.RunnerStatusCode);
        if (ready.Kind != WorkerReadyEvaluationKind.Ready)
        {
            return await FailAsync(tx, ready.ErrorCode ?? ErrorCodes.VmReadyTimeout, "Runner was not ready after VM start.", request.Timestamp, cancellationToken);
        }

        if (!MatchesExpectedWorker(request, readyStatus))
        {
            return await FailAsync(tx, ErrorCodes.WorkerProfileMismatch, "VM workerId/profileId did not match target after ready.", request.Timestamp, cancellationToken);
        }

        await UpdateAsync(tx, SwitchTransactionStatus.WORKER_READY_DONE, "worker-ready-done", null, null, request.Timestamp, cancellationToken);
        await UpdateAsync(tx, SwitchTransactionStatus.SUCCESS, "success", null, null, request.Timestamp, cancellationToken);

        return new VmSwitchResult
        {
            TxId = tx.TransactionId,
            Success = true
        };
    }

    private SwitchTransaction CreateTransaction(VmSwitchRequest request)
    {
        return new SwitchTransaction
        {
            TransactionId = string.IsNullOrWhiteSpace(request.TxId) ? $"SWITCH-{Guid.NewGuid():N}" : request.TxId,
            HostId = request.HostId,
            VmName = request.Vm.Name,
            WorkerId = request.Vm.WorkerId,
            FromProfileId = request.FromProfileId,
            FromSnapshotName = request.FromSnapshotName,
            TargetProfileId = request.TargetProfileId,
            TargetSnapshotName = request.TargetSnapshotName,
            FirstTaskId = request.FirstTaskId,
            TriggerReason = "profile-switch",
            Status = SwitchTransactionStatus.CREATED,
            Step = "created",
            CreatedAt = request.Timestamp,
            UpdatedAt = request.Timestamp
        };
    }

    private static bool MatchesExpectedWorker(VmSwitchRequest request, RunnerStatusResponse readyStatus)
    {
        return string.Equals(readyStatus.WorkerId, request.Vm.WorkerId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(readyStatus.ProfileId, request.TargetProfileId, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<VmSwitchResult> FailAsync(
        SwitchTransaction tx,
        string errorCode,
        string? errorMessage,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        await UpdateAsync(tx, SwitchTransactionStatus.FAILED, "failed", errorCode, errorMessage, timestamp, cancellationToken);
        return new VmSwitchResult
        {
            TxId = tx.TransactionId,
            Success = false,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage
        };
    }

    private async Task<VmSwitchResult> FailAndMarkVmErrorAsync(
        SwitchTransaction tx,
        VmSwitchRequest request,
        string errorCode,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        await _localStore.UpsertVmStateAsync(request.HostId, new VmCurrentState
        {
            VmName = request.Vm.Name,
            VmxPath = request.Vm.VmxPath,
            WorkerId = request.Vm.WorkerId,
            VmStatus = AgentVmStatus.ERROR,
            CurrentProfileId = request.FromProfileId,
            CurrentSnapshotName = request.FromSnapshotName,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            UpdatedAt = request.Timestamp
        }, cancellationToken);

        return await FailAsync(tx, errorCode, errorMessage, request.Timestamp, cancellationToken);
    }

    private async Task UpdateAsync(
        SwitchTransaction tx,
        SwitchTransactionStatus status,
        string step,
        string? errorCode,
        string? errorMessage,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        tx.Status = status;
        tx.Step = step;
        tx.ErrorCode = errorCode;
        tx.ErrorMessage = errorMessage;
        tx.UpdatedAt = timestamp;
        if (status is SwitchTransactionStatus.SUCCESS or SwitchTransactionStatus.FAILED)
        {
            tx.CompletedAt = timestamp;
        }

        await _localStore.UpdateSwitchTransactionAsync(
            tx.TransactionId,
            status,
            step,
            errorCode,
            errorMessage,
            timestamp,
            cancellationToken);
    }
}
