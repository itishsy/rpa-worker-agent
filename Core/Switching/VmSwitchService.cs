using Seebot.WorkerAgent.Core.Backup;
using Seebot.WorkerAgent.Core.Configuration;
using Seebot.WorkerAgent.Core.Domain;
using Seebot.WorkerAgent.Core.Guest;
using Seebot.WorkerAgent.Core.Storage;
using Seebot.WorkerAgent.Core.Vmware;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seebot.WorkerAgent.Core.Coordination;
using Seebot.WorkerAgent.Core.Operations;

namespace Seebot.WorkerAgent.Core.Switching;

public sealed class VmSwitchService : IVmSwitchService
{
    // vmrun stop soft 返回后，vmware-vmx.exe 释放 vmdk/lck 文件锁存在短暂延迟，
    // revertToSnapshot 紧接着执行会报“文件正在使用中”，重试可以规避这个瞬时竞争
    private const int SnapshotRevertMaxAttempts = 3;
    private static readonly TimeSpan DefaultVmStoppedPollInterval = TimeSpan.FromSeconds(2);

    private readonly IGuestWorkerClient _guestWorkerClient;
    private readonly ILogBackupService _logBackupService;
    private readonly IVmrunService _vmrunService;
    private readonly IGuestTokenProvisioningService? _guestTokenProvisioningService;
    private readonly ILocalStore _localStore;
    private readonly IVirtualMachineRegistry? _virtualMachineRegistry;
    private readonly WorkerAgentOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _snapshotRevertRetryDelay;
    private readonly ILogger<VmSwitchService> _logger;
    private readonly TimeSpan _vmStoppedPollInterval;
    private readonly TimeSpan? _vmStopTimeoutOverride;
    private readonly IVmOperationCoordinator? _operationCoordinator;
    private readonly IVmPowerRecoveryService _powerRecoveryService;

    public VmSwitchService(
        IGuestWorkerClient guestWorkerClient,
        ILogBackupService logBackupService,
        IVmrunService vmrunService,
        ILocalStore localStore,
        WorkerAgentOptions options,
        IVmPowerRecoveryService powerRecoveryService,
        TimeProvider? timeProvider = null,
        TimeSpan? snapshotRevertRetryDelay = null,
        TimeSpan? vmStoppedPollInterval = null,
        TimeSpan? vmStopTimeout = null,
        ILogger<VmSwitchService>? logger = null,
        IVirtualMachineRegistry? virtualMachineRegistry = null,
        IGuestTokenProvisioningService? guestTokenProvisioningService = null,
        IVmOperationCoordinator? operationCoordinator = null)
    {
        _guestWorkerClient = guestWorkerClient;
        _logBackupService = logBackupService;
        _vmrunService = vmrunService;
        _guestTokenProvisioningService = guestTokenProvisioningService;
        _localStore = localStore;
        _virtualMachineRegistry = virtualMachineRegistry;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _snapshotRevertRetryDelay = snapshotRevertRetryDelay ?? TimeSpan.FromSeconds(5);
        _vmStoppedPollInterval = vmStoppedPollInterval ?? DefaultVmStoppedPollInterval;
        _vmStopTimeoutOverride = vmStopTimeout;
        _logger = logger ?? NullLogger<VmSwitchService>.Instance;
        _operationCoordinator = operationCoordinator;
        _powerRecoveryService = powerRecoveryService;
    }

    public async Task<VmSwitchResult> SwitchAsync(VmSwitchRequest request, CancellationToken cancellationToken)
    {
        if (_operationCoordinator is null)
            return await SwitchCoreAsync(request, null, cancellationToken).ConfigureAwait(false);

        await using var operation = await _operationCoordinator.TryAcquireAsync(
            _options.Agent.HostId, request.Vm.Name, VmOperationType.ProfileSwitch, cancellationToken).ConfigureAwait(false);
        if (operation is null)
            return new VmSwitchResult { Success = false, ErrorCode = "VM_OPERATION_BUSY", ErrorMessage = "Another VM operation is in progress." };

        try
        {
            var result = await SwitchCoreAsync(request, operation, cancellationToken).ConfigureAwait(false);
            if (result.Success) await operation.CompleteAsync(cancellationToken).ConfigureAwait(false);
            else await operation.FailAsync(result.ErrorCode ?? "SWITCH_FAILED", result.ErrorMessage ?? "VM switch failed.", cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await operation.FailAsync("SWITCH_UNHANDLED", exception.Message, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<VmSwitchResult> SwitchCoreAsync(VmSwitchRequest request, IVmOperationLease? operation, CancellationToken cancellationToken)
    {
        var tx = CreateTransaction(request);
        _logger.LogInformation(
            "画像切换开始。TxId={TxId}, VmName={VmName}, WorkerId={WorkerId}, FromProfileId={FromProfileId}, FromSnapshotName={FromSnapshotName}, TargetProfileId={TargetProfileId}, TargetSnapshotName={TargetSnapshotName}, FirstTaskId={FirstTaskId}",
            tx.TransactionId,
            request.Vm.Name,
            request.Vm.WorkerId,
            request.FromProfileId,
            request.FromSnapshotName,
            request.TargetProfileId,
            request.TargetSnapshotName,
            request.FirstTaskId);
        await _localStore.CreateSwitchTransactionAsync(tx, cancellationToken);

        if (operation is not null) await operation.SetStatusAsync(VmOperationStatus.CheckingRunner, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("画像切换前检查 runner 状态。TxId={TxId}, VmName={VmName}", tx.TransactionId, request.Vm.Name);
        var recovery = await _powerRecoveryService.EnsureOperationalAsync(request.Vm, operation, cancellationToken).ConfigureAwait(false);
        if (!recovery.Success)
            return await FailAndMarkVmErrorAsync(tx, request, recovery.ErrorCode ?? ErrorCodes.VmStartFailed, recovery.Message, cancellationToken);
        if (recovery.Action == VmPowerRecoveryAction.Started)
            return await SkipAsync(tx, ErrorCodes.SwitchSkippedVmStarted, recovery.Message, "precheck-vm-started", request.Timestamp, cancellationToken);
        if (recovery.Action == VmPowerRecoveryAction.Restarted)
            return await SkipAsync(tx, ErrorCodes.SwitchSkippedVmRestarted, recovery.Message, "precheck-vm-restarted", request.Timestamp, cancellationToken);
        var beforeStatus = recovery.RunnerStatus ?? throw new InvalidOperationException("Power recovery returned responsive without Runner status.");
        _logger.LogInformation(
            "画像切换前 runner 状态。TxId={TxId}, VmName={VmName}, RunnerStatusCode={RunnerStatusCode}",
            tx.TransactionId,
            request.Vm.Name,
            beforeStatus.RunnerStatusCode);
        if (!beforeStatus.Success || beforeStatus.RunnerStatusCode != RunnerStatusCode.Runnable || beforeStatus.CurrentTaskId is not null)
        {
            return await SkipAsync(tx, ErrorCodes.SwitchSkippedRunnerNotIdle,
                $"Runner is not normally idle (status={beforeStatus.RunnerStatusCode}, currentTaskId={beforeStatus.CurrentTaskId?.ToString() ?? "null"}); current switch was skipped.",
                "precheck-runner-not-idle", request.Timestamp, cancellationToken);
        }

        _logger.LogInformation("画像切换前停止 runner。TxId={TxId}, VmName={VmName}", tx.TransactionId, request.Vm.Name);
        if (operation is not null) await operation.SetStatusAsync(VmOperationStatus.StoppingRunner, cancellationToken).ConfigureAwait(false);
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

        // 取消对exe进程判断
        //if (kill.CurrentTaskId is not null)
        //{
        //    return await FailAsync(tx, ErrorCodes.ExecutorStopFailed, "Runner still has currentTaskId after kill.", request.Timestamp, cancellationToken);
        //}

        await UpdateAsync(tx, SwitchTransactionStatus.STOP_RUNNER_DONE, "runner-stopped", null, null, request.Timestamp, cancellationToken);

        _logger.LogInformation("画像切换前开始备份日志。TxId={TxId}, VmName={VmName}", tx.TransactionId, request.Vm.Name);
        if (operation is not null) await operation.SetStatusAsync(VmOperationStatus.BackingUp, cancellationToken).ConfigureAwait(false);
        var backup = await _logBackupService.BackupAsync(request.Vm, tx, request.Timestamp, cancellationToken);
        _logger.LogInformation(
            "画像切换前日志备份结果。TxId={TxId}, VmName={VmName}, Success={Success}, TargetPath={TargetPath}, ErrorCode={ErrorCode}, ErrorMessage={ErrorMessage}",
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

        if (operation is not null) await operation.SetStatusAsync(VmOperationStatus.StoppingVm, cancellationToken).ConfigureAwait(false);
        var stopError = await StopVmSafelyAsync(request.Vm.VmxPath, request.Vm.Name, tx.TransactionId, cancellationToken);
        if (stopError is not null)
        {
            return await FailAndMarkVmErrorAsync(tx, request, ErrorCodes.VmStopFailed, stopError, cancellationToken);
        }

        await UpdateAsync(tx, SwitchTransactionStatus.VM_STOP_DONE, "vm-stop-done", null, null, request.Timestamp, cancellationToken);
        await WaitForVmFilesReleasedAsync(request.Vm, cancellationToken).ConfigureAwait(false);

        try
        {
            if (operation is not null) await operation.SetStatusAsync(VmOperationStatus.RevertingSnapshot, cancellationToken).ConfigureAwait(false);
            await RevertToSnapshotWithRetryAsync(tx, request, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return await FailAndMarkVmErrorAsync(tx, request, ErrorCodes.SnapshotRevertFailed, exception.Message, cancellationToken);
        }

        await UpdateAsync(tx, SwitchTransactionStatus.SNAPSHOT_REVERT_DONE, "snapshot-revert-done", null, null, request.Timestamp, cancellationToken);

        await DelayIfPositiveAsync(_options.Agent.VmPostRevertStabilizationSeconds, cancellationToken).ConfigureAwait(false);

        try
        {
            _logger.LogInformation("快照回滚完成，以 nogui 模式启动 VM。TxId={TxId}, VmName={VmName}", tx.TransactionId, request.Vm.Name);
            if (operation is not null) await operation.SetStatusAsync(VmOperationStatus.StartingVm, cancellationToken).ConfigureAwait(false);
            await _vmrunService.StartVmAsync(request.Vm.VmxPath, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return await FailAndMarkVmErrorAsync(tx, request, ErrorCodes.VmStartFailed, exception.Message, cancellationToken);
        }

        await UpdateAsync(tx, SwitchTransactionStatus.VM_START_DONE, "vm-start-done", null, null, request.Timestamp, cancellationToken);

        _logger.LogInformation("VM 启动后等待 runner 就绪。TxId={TxId}, VmName={VmName}", tx.TransactionId, request.Vm.Name);
        if (_guestTokenProvisioningService is not null)
        {
            var tokenResult = await _guestTokenProvisioningService.ProvisionAsync(request.Vm, cancellationToken).ConfigureAwait(false);
            if (!tokenResult.Success)
            {
                return await FailAsync(tx, tokenResult.ErrorCode ?? ErrorCodes.ConfigUpdateFailed,
                    tokenResult.ErrorMessage ?? "Failed to provision scheduler token in guest.", request.Timestamp, cancellationToken);
            }
        }

        if (operation is not null) await operation.SetStatusAsync(VmOperationStatus.WaitingRunner, cancellationToken).ConfigureAwait(false);
        var (readyStatus, ready) = await WaitUntilRunnerReadyAsync(request.Vm, cancellationToken);
        _logger.LogInformation(
            "runner 就绪评估完成。TxId={TxId}, VmName={VmName}, Evaluation={Evaluation}, ErrorCode={ErrorCode}, RunnerStatusCode={RunnerStatusCode}",
            tx.TransactionId,
            request.Vm.Name,
            ready.Kind,
            ready.ErrorCode,
            readyStatus.RunnerStatusCode);
        if (ready.Kind != WorkerReadyEvaluationKind.Ready)
        {
            return await FailAndMarkVmErrorAsync(tx, request, ready.ErrorCode ?? ErrorCodes.VmReadyTimeout,
                "Runner was not ready after the switched VM started.", cancellationToken);
        }

        if (operation is not null) await operation.SetStatusAsync(VmOperationStatus.VerifyingIdentity, cancellationToken).ConfigureAwait(false);
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
            "画像切换成功。TxId={TxId}, VmName={VmName}, TargetProfileId={TargetProfileId}, TargetSnapshotName={TargetSnapshotName}",
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

    private async Task WaitForVmFilesReleasedAsync(VirtualMachineOptions vm, CancellationToken cancellationToken)
    {
        var vmDirectory = Path.GetDirectoryName(vm.VmxPath);
        if (string.IsNullOrWhiteSpace(vmDirectory) || !Directory.Exists(vmDirectory))
        {
            await DelayIfPositiveAsync(_options.Agent.VmPostStopStabilizationSeconds, cancellationToken).ConfigureAwait(false);
            return;
        }

        var waitSeconds = Math.Max(_options.Agent.VmPostStopStabilizationSeconds, 0);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(waitSeconds);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var hasLocks = Directory.EnumerateFileSystemEntries(vmDirectory, "*.lck", SearchOption.TopDirectoryOnly).Any();
                if (!hasLocks)
                {
                    var remaining = deadline - DateTimeOffset.UtcNow;
                    if (remaining > TimeSpan.Zero)
                    {
                        await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
                    }

                    return;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "Unable to inspect VMware lock files; continuing with vmrun validation. VmName={VmName}, Directory={Directory}", vm.Name, vmDirectory);
                await DelayIfPositiveAsync(waitSeconds, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                _logger.LogWarning(
                    "VMware lock entries remain after stabilization wait; continuing and letting vmrun validate the actual lock state. VmName={VmName}, Directory={Directory}, WaitSeconds={WaitSeconds}",
                    vm.Name,
                    vmDirectory,
                    waitSeconds);
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    private static Task DelayIfPositiveAsync(int seconds, CancellationToken cancellationToken)
    {
        return seconds > 0
            ? Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken)
            : Task.CompletedTask;
    }

    private async Task RevertToSnapshotWithRetryAsync(SwitchTransaction tx, VmSwitchRequest request, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= SnapshotRevertMaxAttempts; attempt++)
        {
            try
            {
                _logger.LogInformation(
                    "正在回滚 VM 到目标快照。TxId={TxId}, VmName={VmName}, TargetSnapshotName={TargetSnapshotName}, Attempt={Attempt}, MaxAttempts={MaxAttempts}",
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
                    "快照回滚失败，稍后重试。TxId={TxId}, VmName={VmName}, Attempt={Attempt}, MaxAttempts={MaxAttempts}",
                    tx.TransactionId,
                    request.Vm.Name,
                    attempt,
                    SnapshotRevertMaxAttempts);
                await Task.Delay(_snapshotRevertRetryDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<string?> StopVmSafelyAsync(
        string vmxPath,
        string vmName,
        string txId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await _vmrunService.IsVmRunningAsync(vmxPath, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogInformation("快照操作前 VM 已停止。TxId={TxId}, VmName={VmName}, VmxPath={VmxPath}", txId, vmName, vmxPath);
                return null;
            }

            _logger.LogInformation("快照操作前停止 VM。TxId={TxId}, VmName={VmName}, VmxPath={VmxPath}, Mode={Mode}", txId, vmName, vmxPath, VmStopMode.Soft);
            await _vmrunService.StopVmAsync(vmxPath, VmStopMode.Soft, cancellationToken).ConfigureAwait(false);

            var softTimeout = GetStopTimeout();
            if (await WaitUntilVmStoppedAsync(vmxPath, vmName, txId, softTimeout, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            if (!_options.Vmrun.AllowHardStopAfterSoftTimeout)
            {
                return $"VM did not power off within {softTimeout.TotalSeconds:0} seconds after soft stop.";
            }

            _logger.LogWarning("软关机超时，尝试硬关机。TxId={TxId}, VmName={VmName}, VmxPath={VmxPath}", txId, vmName, vmxPath);
            await _vmrunService.StopVmAsync(vmxPath, VmStopMode.Hard, cancellationToken).ConfigureAwait(false);

            if (await WaitUntilVmStoppedAsync(vmxPath, vmName, txId, softTimeout, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            return $"VM did not power off within {softTimeout.TotalSeconds:0} seconds after hard stop.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return exception.Message;
        }
    }

    private async Task<bool> WaitUntilVmStoppedAsync(
        string vmxPath,
        string vmName,
        string txId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow().Add(timeout);
        while (true)
        {
            var isRunning = await _vmrunService.IsVmRunningAsync(vmxPath, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("VM 停止轮询结果。TxId={TxId}, VmName={VmName}, IsRunning={IsRunning}", txId, vmName, isRunning);
            if (!isRunning)
            {
                return true;
            }

            if (_timeProvider.GetUtcNow() >= deadline)
            {
                return false;
            }

            await Task.Delay(_vmStoppedPollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private TimeSpan GetStopTimeout()
    {
        if (_vmStopTimeoutOverride is not null)
        {
            return _vmStopTimeoutOverride.Value;
        }

        return TimeSpan.FromSeconds(_options.Vmrun.StopSoftTimeoutSeconds > 0 ? _options.Vmrun.StopSoftTimeoutSeconds : 60);
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
            RunnerStatusResponse status;
            try
            {
                status = await _guestWorkerClient.GetRunnerStatusAsync(vm, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogDebug(exception, "Runner is not reachable while VM is booting. VmName={VmName}", vm.Name);
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    return (
                        new RunnerStatusResponse { Success = false, RunnerStatusCode = RunnerStatusCode.Offline },
                        new WorkerReadyEvaluation(WorkerReadyEvaluationKind.Error, ErrorCodes.VmReadyTimeout));
                }

                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                continue;
            }
            var evaluation = WorkerStateEvaluator.EvaluateReadyAfterVmStart(status.RunnerStatusCode);
            _logger.LogInformation(
                "runner 就绪轮询结果。VmName={VmName}, RunnerStatusCode={RunnerStatusCode}, Evaluation={Evaluation}",
                vm.Name,
                status.RunnerStatusCode,
                evaluation.Kind);

            if (status.RunnerStatusCode == RunnerStatusCode.Offline && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                continue;
            }

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

    private async Task<VmSwitchResult> SkipAsync(
        SwitchTransaction tx,
        string reasonCode,
        string message,
        string step,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        await UpdateAsync(tx, SwitchTransactionStatus.SUCCESS, step, reasonCode, message, timestamp, cancellationToken);
        _logger.LogInformation("Snapshot switch skipped after precheck handling. TxId={TxId}, VmName={VmName}, ReasonCode={ReasonCode}", tx.TransactionId, tx.VmName, reasonCode);
        return new VmSwitchResult
        {
            TxId = tx.TransactionId,
            Success = true,
            Skipped = true,
            ErrorCode = reasonCode,
            ErrorMessage = message
        };
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
            "画像切换失败。TxId={TxId}, VmName={VmName}, ErrorCode={ErrorCode}, ErrorMessage={ErrorMessage}",
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
