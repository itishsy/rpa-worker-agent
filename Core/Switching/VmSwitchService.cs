using Seebot.WorkerAgent.Core.Backup;
using Seebot.WorkerAgent.Core.Configuration;
using Seebot.WorkerAgent.Core.Domain;
using Seebot.WorkerAgent.Core.Guest;
using Seebot.WorkerAgent.Core.Storage;
using Seebot.WorkerAgent.Core.Vmware;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Seebot.WorkerAgent.Core.Switching;

public sealed class VmSwitchService : IVmSwitchService
{
    // vmrun stop soft 返回后，vmware-vmx.exe 释放 vmdk/lck 文件锁存在短暂延迟，
    // revertToSnapshot 紧接着执行会报“文件正在使用中”，重试可以规避这个瞬时竞争
    private const int SnapshotRevertMaxAttempts = 3;

    private readonly IGuestWorkerClient _guestWorkerClient;
    private readonly ILogBackupService _logBackupService;
    private readonly IVmrunService _vmrunService;
    private readonly ILocalStore _localStore;
    private readonly IVirtualMachineRegistry? _virtualMachineRegistry;
    private readonly WorkerAgentOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _snapshotRevertRetryDelay;
    private readonly ILogger<VmSwitchService> _logger;

    public VmSwitchService(
        IGuestWorkerClient guestWorkerClient,
        ILogBackupService logBackupService,
        IVmrunService vmrunService,
        ILocalStore localStore,
        WorkerAgentOptions options,
        TimeProvider? timeProvider = null,
        TimeSpan? snapshotRevertRetryDelay = null,
        ILogger<VmSwitchService>? logger = null,
        IVirtualMachineRegistry? virtualMachineRegistry = null)
    {
        _guestWorkerClient = guestWorkerClient;
        _logBackupService = logBackupService;
        _vmrunService = vmrunService;
        _localStore = localStore;
        _virtualMachineRegistry = virtualMachineRegistry;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _snapshotRevertRetryDelay = snapshotRevertRetryDelay ?? TimeSpan.FromSeconds(5);
        _logger = logger ?? NullLogger<VmSwitchService>.Instance;
    }

    public async Task<VmSwitchResult> SwitchAsync(VmSwitchRequest request, CancellationToken cancellationToken)
    {
        var tx = CreateTransaction(request);
        _logger.LogInformation(
            "VM switch started. TxId={TxId}, VmName={VmName}, WorkerId={WorkerId}, FromProfileId={FromProfileId}, FromSnapshotName={FromSnapshotName}, TargetProfileId={TargetProfileId}, TargetSnapshotName={TargetSnapshotName}, FirstTaskId={FirstTaskId}",
            tx.TransactionId,
            request.Vm.Name,
            request.Vm.WorkerId,
            request.FromProfileId,
            request.FromSnapshotName,
            request.TargetProfileId,
            request.TargetSnapshotName,
            request.FirstTaskId);
        await _localStore.CreateSwitchTransactionAsync(tx, cancellationToken);

        _logger.LogInformation("Checking runner status before switch. TxId={TxId}, VmName={VmName}", tx.TransactionId, request.Vm.Name);
        var beforeStatus = await _guestWorkerClient.GetRunnerStatusAsync(request.Vm, cancellationToken);
        _logger.LogInformation(
            "Runner status before switch. TxId={TxId}, VmName={VmName}, RunnerStatusCode={RunnerStatusCode}",
            tx.TransactionId,
            request.Vm.Name,
            beforeStatus.RunnerStatusCode);
        if (beforeStatus.RunnerStatusCode == RunnerStatusCode.Running)
        {
            return await FailAsync(tx, ErrorCodes.WorkerRunning, "Runner is Running.", request.Timestamp, cancellationToken);
        }

        if (beforeStatus.RunnerStatusCode == RunnerStatusCode.Upgrading)
        {
            return await FailAsync(tx, ErrorCodes.WorkerUpgrading, "Runner is Upgrading.", request.Timestamp, cancellationToken);
        }

        _logger.LogInformation("Stopping runner before VM switch. TxId={TxId}, VmName={VmName}", tx.TransactionId, request.Vm.Name);
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

        _logger.LogInformation("Starting log backup before VM revert. TxId={TxId}, VmName={VmName}", tx.TransactionId, request.Vm.Name);
        var backup = await _logBackupService.BackupAsync(request.Vm, tx, request.Timestamp, cancellationToken);
        _logger.LogInformation(
            "Log backup result before VM revert. TxId={TxId}, VmName={VmName}, Success={Success}, TargetPath={TargetPath}, ErrorCode={ErrorCode}, ErrorMessage={ErrorMessage}",
            tx.TransactionId,
            request.Vm.Name,
            backup.Success,
            backup.TargetPath,
            backup.ErrorCode,
            backup.ErrorMessage);
        if (!backup.Success && !_options.Agent.ForceRevertWhenBackupFailed)
        {
            return await FailAsync(tx, backup.ErrorCode ?? ErrorCodes.LogBackupFailed, backup.ErrorMessage, request.Timestamp, cancellationToken);
        }

        await UpdateAsync(tx, SwitchTransactionStatus.LOG_BACKUP_DONE, "log-backup-done", backup.ErrorCode, backup.ErrorMessage, request.Timestamp, cancellationToken);

        try
        {
            _logger.LogInformation("Stopping VM before snapshot revert. TxId={TxId}, VmName={VmName}, VmxPath={VmxPath}", tx.TransactionId, request.Vm.Name, request.Vm.VmxPath);
            await _vmrunService.StopVmAsync(request.Vm.VmxPath, VmStopMode.Soft, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return await FailAndMarkVmErrorAsync(tx, request, ErrorCodes.VmStopFailed, exception.Message, cancellationToken);
        }

        await UpdateAsync(tx, SwitchTransactionStatus.VM_STOP_DONE, "vm-stop-done", null, null, request.Timestamp, cancellationToken);

        try
        {
            await RevertToSnapshotWithRetryAsync(tx, request, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return await FailAndMarkVmErrorAsync(tx, request, ErrorCodes.SnapshotRevertFailed, exception.Message, cancellationToken);
        }

        await UpdateAsync(tx, SwitchTransactionStatus.SNAPSHOT_REVERT_DONE, "snapshot-revert-done", null, null, request.Timestamp, cancellationToken);

        try
        {
            _logger.LogInformation("Starting VM after snapshot revert. TxId={TxId}, VmName={VmName}, NoGui={NoGui}", tx.TransactionId, request.Vm.Name, _options.Vmrun.DefaultStartNoGui);
            await _vmrunService.StartVmAsync(request.Vm.VmxPath, _options.Vmrun.DefaultStartNoGui, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return await FailAndMarkVmErrorAsync(tx, request, ErrorCodes.VmStartFailed, exception.Message, cancellationToken);
        }

        await UpdateAsync(tx, SwitchTransactionStatus.VM_START_DONE, "vm-start-done", null, null, request.Timestamp, cancellationToken);

        _logger.LogInformation("Waiting for runner ready after VM start. TxId={TxId}, VmName={VmName}", tx.TransactionId, request.Vm.Name);
        var (readyStatus, ready) = await WaitUntilRunnerReadyAsync(request.Vm, cancellationToken);
        _logger.LogInformation(
            "Runner ready evaluation completed. TxId={TxId}, VmName={VmName}, Evaluation={Evaluation}, ErrorCode={ErrorCode}, RunnerStatusCode={RunnerStatusCode}",
            tx.TransactionId,
            request.Vm.Name,
            ready.Kind,
            ready.ErrorCode,
            readyStatus.RunnerStatusCode);
        if (ready.Kind != WorkerReadyEvaluationKind.Ready)
        {
            return await FailAsync(tx, ready.ErrorCode ?? ErrorCodes.VmReadyTimeout, "Runner was not ready after VM start.", request.Timestamp, cancellationToken);
        }

        if (!MatchesExpectedWorker(request, readyStatus))
        {
            return await FailAsync(tx, ErrorCodes.WorkerProfileMismatch, "VM workerId/profileId did not match target after ready.", request.Timestamp, cancellationToken);
        }

        await UpdateAsync(tx, SwitchTransactionStatus.WORKER_READY_DONE, "worker-ready-done", null, null, request.Timestamp, cancellationToken);

        await _localStore.UpsertVmStateAsync(request.HostId, new VmCurrentState
        {
            VmName = request.Vm.Name,
            WorkerId = request.Vm.WorkerId,
            VmStatus = AgentVmStatus.MONITORING,
            CurrentProfileId = request.TargetProfileId,
            CurrentSnapshotName = request.TargetSnapshotName,
            RunnerStatusCode = readyStatus.RunnerStatusCode,
            UpdatedAt = request.Timestamp
        }, cancellationToken);

        var profile = request.Vm.Profiles.FirstOrDefault(item =>
            string.Equals(item.ProfileId, request.TargetProfileId, StringComparison.OrdinalIgnoreCase));
        if (profile is not null)
        {
            profile.SnapshotName = request.TargetSnapshotName;
        }

        if (_virtualMachineRegistry is not null)
        {
            await _virtualMachineRegistry.UpdateProfileSnapshotAsync(
                request.Vm.Name,
                request.TargetProfileId,
                request.TargetSnapshotName,
                cancellationToken).ConfigureAwait(false);
        }

        await UpdateAsync(tx, SwitchTransactionStatus.SUCCESS, "success", null, null, request.Timestamp, cancellationToken);
        _logger.LogInformation(
            "VM switch completed successfully. TxId={TxId}, VmName={VmName}, TargetProfileId={TargetProfileId}, TargetSnapshotName={TargetSnapshotName}",
            tx.TransactionId,
            request.Vm.Name,
            request.TargetProfileId,
            request.TargetSnapshotName);

        return new VmSwitchResult
        {
            TxId = tx.TransactionId,
            Success = true
        };
    }

    private async Task RevertToSnapshotWithRetryAsync(SwitchTransaction tx, VmSwitchRequest request, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= SnapshotRevertMaxAttempts; attempt++)
        {
            try
            {
                _logger.LogInformation(
                    "Reverting VM to snapshot. TxId={TxId}, VmName={VmName}, TargetSnapshotName={TargetSnapshotName}, Attempt={Attempt}, MaxAttempts={MaxAttempts}",
                    tx.TransactionId,
                    request.Vm.Name,
                    request.TargetSnapshotName,
                    attempt,
                    SnapshotRevertMaxAttempts);
                await _vmrunService.RevertToSnapshotAsync(request.Vm.VmxPath, request.TargetSnapshotName, cancellationToken);
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException && attempt < SnapshotRevertMaxAttempts)
            {
                _logger.LogWarning(
                    exception,
                    "Snapshot revert attempt failed, retrying after delay. TxId={TxId}, VmName={VmName}, Attempt={Attempt}, MaxAttempts={MaxAttempts}",
                    tx.TransactionId,
                    request.Vm.Name,
                    attempt,
                    SnapshotRevertMaxAttempts);
                await Task.Delay(_snapshotRevertRetryDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
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

    private async Task<(RunnerStatusResponse Status, WorkerReadyEvaluation Evaluation)> WaitUntilRunnerReadyAsync(
        VirtualMachineOptions vm,
        CancellationToken cancellationToken)
    {
        var timeoutSeconds = _options.Agent.WaitVmReadyTimeoutSeconds > 0 ? _options.Agent.WaitVmReadyTimeoutSeconds : 180;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);

        while (true)
        {
            var status = await _guestWorkerClient.GetRunnerStatusAsync(vm, cancellationToken).ConfigureAwait(false);
            var evaluation = WorkerStateEvaluator.EvaluateReadyAfterVmStart(status.RunnerStatusCode);
            _logger.LogInformation(
                "Runner ready polling result. VmName={VmName}, RunnerStatusCode={RunnerStatusCode}, Evaluation={Evaluation}",
                vm.Name,
                status.RunnerStatusCode,
                evaluation.Kind);

            if (evaluation.Kind != WorkerReadyEvaluationKind.Wait)
            {
                return (status, evaluation);
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return (status, new WorkerReadyEvaluation(WorkerReadyEvaluationKind.Error, ErrorCodes.VmReadyTimeout));
            }

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        }
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
        _logger.LogWarning(
            "VM switch failed. TxId={TxId}, VmName={VmName}, ErrorCode={ErrorCode}, ErrorMessage={ErrorMessage}",
            tx.TransactionId,
            tx.VmName,
            errorCode,
            errorMessage);
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
