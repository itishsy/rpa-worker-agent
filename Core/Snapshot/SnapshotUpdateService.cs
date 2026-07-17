using Seebot.WorkerAgent.Core.Configuration;
using Seebot.WorkerAgent.Core.Domain;
using Seebot.WorkerAgent.Core.Guest;
using Seebot.WorkerAgent.Core.Storage;
using Seebot.WorkerAgent.Core.Vmware;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Seebot.WorkerAgent.Core.Snapshot;

public sealed class SnapshotUpdateService : ISnapshotUpdateService
{
    private const int DefaultRunnerStatusCheckMaxAttempts = 30;
    private static readonly TimeSpan DefaultInitialRunnerStatusDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DefaultRunnerStatusCheckInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultVmStoppedPollInterval = TimeSpan.FromSeconds(2);

    private readonly IVmrunService _vmrunService;
    private readonly IGuestWorkerClient _guestWorkerClient;
    private readonly IGuestTokenProvisioningService? _guestTokenProvisioningService;
    private readonly IProfileSnapshotResolver _snapshotResolver;
    private readonly IVmOperationLock _vmOperationLock;
    private readonly IVirtualMachineRegistry? _virtualMachineRegistry;
    private readonly WorkerAgentOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _initialRunnerStatusDelay;
    private readonly TimeSpan _runnerStatusCheckInterval;
    private readonly int _runnerStatusCheckMaxAttempts;
    private readonly ILogger<SnapshotUpdateService> _logger;
    private readonly TimeSpan _vmStoppedPollInterval;

    public SnapshotUpdateService(
        IVmrunService vmrunService,
        IGuestWorkerClient guestWorkerClient,
        IProfileSnapshotResolver snapshotResolver,
        IVmOperationLock vmOperationLock,
        WorkerAgentOptions options,
        TimeProvider? timeProvider = null,
        ILogger<SnapshotUpdateService>? logger = null,
        IVirtualMachineRegistry? virtualMachineRegistry = null,
        TimeSpan? initialRunnerStatusDelay = null,
        TimeSpan? runnerStatusCheckInterval = null,
        int? runnerStatusCheckMaxAttempts = null,
        TimeSpan? vmStoppedPollInterval = null,
        IGuestTokenProvisioningService? guestTokenProvisioningService = null)
    {
        _vmrunService = vmrunService;
        _guestWorkerClient = guestWorkerClient;
        _guestTokenProvisioningService = guestTokenProvisioningService;
        _snapshotResolver = snapshotResolver;
        _vmOperationLock = vmOperationLock;
        _virtualMachineRegistry = virtualMachineRegistry;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _initialRunnerStatusDelay = initialRunnerStatusDelay ?? DefaultInitialRunnerStatusDelay;
        _runnerStatusCheckInterval = runnerStatusCheckInterval ?? DefaultRunnerStatusCheckInterval;
        _runnerStatusCheckMaxAttempts = runnerStatusCheckMaxAttempts.GetValueOrDefault(DefaultRunnerStatusCheckMaxAttempts);
        _logger = logger ?? NullLogger<SnapshotUpdateService>.Instance;
        _vmStoppedPollInterval = vmStoppedPollInterval ?? DefaultVmStoppedPollInterval;
    }

    public async Task<SnapshotUpdateResult> UpdateSnapshotAsync(
        string vmName,
        string profileId,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("快照画像升级开始。VmName={VmName}, ProfileId={ProfileId}", vmName, profileId);
        var vm = _options.VirtualMachines
            .Where(v => v.Enabled)
            .FirstOrDefault(v => string.Equals(v.Name, vmName, StringComparison.OrdinalIgnoreCase));
        if (vm is null)
        {
            return Fail(ErrorCodes.VmNotFound, $"VM '{vmName}' not found in configuration.", "lookup");
        }

        var profile = vm.Profiles
            .FirstOrDefault(p => string.Equals(p.ProfileId, profileId, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            return Fail(ErrorCodes.ProfileNotFound, $"Profile '{profileId}' not found in VM '{vmName}'.", "lookup");
        }

        await using var vmLock = await _vmOperationLock.AcquireAsync(vm.VmxPath, cancellationToken)
            .ConfigureAwait(false);
        _logger.LogInformation("快照画像升级已获取 VM 操作锁。VmName={VmName}, VmxPath={VmxPath}", vm.Name, vm.VmxPath);

        IReadOnlyList<string> existingSnapshots;
        try
        {
            _logger.LogInformation("快照画像升级开始读取快照列表。VmName={VmName}, ProfileId={ProfileId}", vm.Name, profileId);
            existingSnapshots = await _vmrunService.ListSnapshotsAsync(vm.VmxPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(ErrorCodes.SnapshotNotFound, ex.Message, "list-snapshots");
        }

        var currentResolution = _snapshotResolver.Resolve(vm, profile, existingSnapshots);
        if (!currentResolution.IsReady)
        {
            var errorCode = currentResolution.Status == ProfileSnapshotResolutionStatus.Duplicate
                ? ErrorCodes.SnapshotAmbiguous
                : ErrorCodes.SnapshotNotFound;
            return Fail(errorCode, currentResolution.Message ?? "Profile snapshot is not ready.", "resolve-snapshot");
        }

        var currentSnapshotName = currentResolution.SnapshotName!;
        _logger.LogInformation(
            "已解析当前画像快照。VmName={VmName}, ProfileId={ProfileId}, CurrentSnapshotName={CurrentSnapshotName}",
            vm.Name,
            profileId,
            currentSnapshotName);

        if (!await CanCreateSnapshotFromCurrentVmStateAsync(vm, profileId, cancellationToken))
        {
            var stopFailure = await StopVmSafelyAsync(vm, "stop-before-revert", cancellationToken).ConfigureAwait(false);
            if (stopFailure is not null)
            {
                return stopFailure;
            }

            try
            {
                _logger.LogInformation("快照画像升级前回滚 VM。VmName={VmName}, SnapshotName={SnapshotName}", vm.Name, currentSnapshotName);
                await _vmrunService.RevertToSnapshotAsync(vm.VmxPath, currentSnapshotName, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Fail(ErrorCodes.SnapshotRevertFailed, ex.Message, "revert");
            }

            try
            {
                _logger.LogInformation("快照画像升级校验前启动 VM。VmName={VmName}", vm.Name);
                await _vmrunService.StartVmAsync(vm.VmxPath, noGui: false, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Fail(ErrorCodes.VmStartFailed, ex.Message, "start");
            }

            var tokenFailure = await ProvisionGuestTokenAsync(vm, "write-token-before-validation", cancellationToken)
                .ConfigureAwait(false);
            if (tokenFailure is not null)
            {
                return tokenFailure;
            }

            await Task.Delay(_initialRunnerStatusDelay, _timeProvider, cancellationToken);
            _logger.LogInformation("runner 状态检查前初始等待完成。VmName={VmName}, WaitSeconds={WaitSeconds}", vm.Name, _initialRunnerStatusDelay.TotalSeconds);

            var runnerReadyFailure = await WaitForRunnerReadyAfterStartAsync(vm, cancellationToken);
            if (runnerReadyFailure is not null)
            {
                return runnerReadyFailure;
            }
        }

        return await CreateUpdatedSnapshotAsync(vm, profile, profileId, currentSnapshotName, cancellationToken);
    }

    private async Task<SnapshotUpdateResult> CreateUpdatedSnapshotAsync(
        VirtualMachineOptions vm,
        ProfileOptions profile,
        string profileId,
        string currentSnapshotName,
        CancellationToken cancellationToken)
    {
        var stopFailure = await StopVmSafelyAsync(vm, "stop-before-create", cancellationToken).ConfigureAwait(false);
        if (stopFailure is not null)
        {
            return stopFailure;
        }

        var existingSnapshots = await _vmrunService.ListSnapshotsAsync(vm.VmxPath, cancellationToken);
        var now = _timeProvider.GetUtcNow().ToLocalTime();
        var newSnapshotName = SnapshotNameGenerator.Generate(
            profileId,
            DateOnly.FromDateTime(now.DateTime),
            existingSnapshots);
        _logger.LogInformation(
            "已生成新快照名称。VmName={VmName}, ProfileId={ProfileId}, NewSnapshotName={NewSnapshotName}",
            vm.Name,
            profileId,
            newSnapshotName);

        try
        {
            _logger.LogInformation("正在创建新快照。VmName={VmName}, SnapshotName={SnapshotName}", vm.Name, newSnapshotName);
            await _vmrunService.CreateSnapshotAsync(vm.VmxPath, newSnapshotName, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(ErrorCodes.SnapshotCreateFailed, ex.Message, "create-snapshot");
        }

        try
        {
            _logger.LogInformation("正在删除旧画像快照。VmName={VmName}, SnapshotName={SnapshotName}", vm.Name, currentSnapshotName);
            await _vmrunService.DeleteSnapshotAsync(vm.VmxPath, currentSnapshotName, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try
            {
                await _vmrunService.DeleteSnapshotAsync(vm.VmxPath, newSnapshotName, cancellationToken);
            }
            catch (Exception rollbackEx) when (rollbackEx is not OperationCanceledException)
            {
                return Fail(
                    ErrorCodes.SnapshotDeleteFailed,
                    $"{ex.Message}; rollback delete of new snapshot failed: {rollbackEx.Message}",
                    "delete-snapshot");
            }

            return Fail(ErrorCodes.SnapshotDeleteFailed, ex.Message, "delete-snapshot");
        }

        try
        {
            _logger.LogInformation("快照画像升级完成后启动 VM。VmName={VmName}", vm.Name);
            await _vmrunService.StartVmAsync(vm.VmxPath, noGui: false, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(ErrorCodes.VmStartFailed, ex.Message, "start-after-update");
        }

        var tokenFailure = await ProvisionGuestTokenAsync(vm, "write-token-after-update", cancellationToken)
            .ConfigureAwait(false);
        if (tokenFailure is not null)
        {
            return tokenFailure;
        }

        profile.SnapshotName = newSnapshotName;
        if (_virtualMachineRegistry is not null)
        {
            await _virtualMachineRegistry.UpdateProfileSnapshotAsync(vm.Name, profileId, newSnapshotName, cancellationToken)
                .ConfigureAwait(false);
        }

        return new SnapshotUpdateResult
        {
            Success = true,
            NewSnapshotName = newSnapshotName,
            Step = "done"
        };
    }

    private async Task<SnapshotUpdateResult?> ProvisionGuestTokenAsync(
        VirtualMachineOptions vm,
        string step,
        CancellationToken cancellationToken)
    {
        if (_guestTokenProvisioningService is null)
        {
            return null;
        }

        var result = await _guestTokenProvisioningService.ProvisionAsync(vm, cancellationToken)
            .ConfigureAwait(false);
        return result.Success
            ? null
            : Fail(
                result.ErrorCode ?? ErrorCodes.ConfigUpdateFailed,
                result.ErrorMessage ?? "Failed to provision scheduler token in guest.",
                step);
    }

    private async Task<bool> CanCreateSnapshotFromCurrentVmStateAsync(
        VirtualMachineOptions vm,
        string profileId,
        CancellationToken cancellationToken)
    {
        string? currentVmSnapshotName;
        try
        {
            _logger.LogInformation("快照画像升级前读取当前 VM 快照。VmName={VmName}", vm.Name);
            currentVmSnapshotName = await _vmrunService.GetCurrentSnapshotAsync(vm.VmxPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "快照画像升级前读取当前 VM 快照失败，回退到回滚路径。VmName={VmName}", vm.Name);
            return false;
        }

        if (!_snapshotResolver.IsProfileSnapshotName(profileId, currentVmSnapshotName ?? ""))
        {
            _logger.LogInformation(
                "当前 VM 快照不匹配目标画像，回退到回滚路径。VmName={VmName}, ProfileId={ProfileId}, CurrentVmSnapshotName={CurrentVmSnapshotName}",
                vm.Name,
                profileId,
                currentVmSnapshotName);
            return false;
        }

        RunnerStatusResponse status;
        try
        {
            _logger.LogInformation("快速快照画像升级前检查 runner 状态。VmName={VmName}, ProfileId={ProfileId}", vm.Name, profileId);
            status = await _guestWorkerClient.GetRunnerStatusAsync(vm, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "快速快照画像升级前检查 runner 状态失败，回退到回滚路径。VmName={VmName}", vm.Name);
            return false;
        }

        if (status.RunnerStatusCode != RunnerStatusCode.Runnable)
        {
            _logger.LogInformation(
                "快速快照画像升级前 runner 未空闲，回退到回滚路径。VmName={VmName}, ProfileId={ProfileId}, RunnerStatusCode={RunnerStatusCode}",
                vm.Name,
                profileId,
                status.RunnerStatusCode);
            return false;
        }

        _logger.LogInformation(
            "当前 VM 快照匹配目标画像且 runner 空闲，将直接创建快照。VmName={VmName}, ProfileId={ProfileId}, CurrentVmSnapshotName={CurrentVmSnapshotName}, RunnerStatusCode={RunnerStatusCode}",
            vm.Name,
            profileId,
            currentVmSnapshotName,
            status.RunnerStatusCode);
        return true;
    }

    private async Task<SnapshotUpdateResult?> WaitForRunnerReadyAfterStartAsync(
        VirtualMachineOptions vm,
        CancellationToken cancellationToken)
    {
        RunnerStatusResponse? status = null;
        Exception? lastException = null;
        for (var attempt = 1; attempt <= _runnerStatusCheckMaxAttempts; attempt++)
        {
            try
            {
                _logger.LogInformation("快照画像升级检查 runner 状态。VmName={VmName}, Attempt={Attempt}, MaxAttempts={MaxAttempts}", vm.Name, attempt, _runnerStatusCheckMaxAttempts);
                status = await _guestWorkerClient.GetRunnerStatusAsync(vm, cancellationToken);
                lastException = null;
                if (status.RunnerStatusCode == RunnerStatusCode.Runnable)
                {
                    _logger.LogInformation(
                        "runner 已空闲，准备停止 VM 并创建新快照。VmName={VmName}, RunnerStatusCode={RunnerStatusCode}",
                        vm.Name,
                        status.RunnerStatusCode);
                    break;
                }

                _logger.LogInformation(
                    "快照画像升级时 runner 未空闲，如未达到上限将继续重试。VmName={VmName}, Attempt={Attempt}, MaxAttempts={MaxAttempts}, RunnerStatusCode={RunnerStatusCode}",
                    vm.Name,
                    attempt,
                    _runnerStatusCheckMaxAttempts,
                    status.RunnerStatusCode);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastException = ex;
                _logger.LogWarning(ex, "快照画像升级时检查 runner 状态失败。VmName={VmName}, Attempt={Attempt}", vm.Name, attempt);
            }

            if (attempt < _runnerStatusCheckMaxAttempts)
            {
                await Task.Delay(_runnerStatusCheckInterval, _timeProvider, cancellationToken);
            }
        }

        if (status is null)
        {
            return Fail(ErrorCodes.RunnerStatusCheckFailed, lastException?.Message ?? "Unknown error.", "check-status");
        }

        if (status.RunnerStatusCode != RunnerStatusCode.Runnable)
        {
            return Fail(ErrorCodes.RunnerNotReady,
                $"Runner status is {status.RunnerStatusCode} after VM start.", "check-status");
        }

        return null;
    }

    private async Task<SnapshotUpdateResult?> StopVmSafelyAsync(
        VirtualMachineOptions vm,
        string step,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await _vmrunService.IsVmRunningAsync(vm.VmxPath, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogInformation("快照画像升级步骤前 VM 已停止。VmName={VmName}, Step={Step}", vm.Name, step);
                return null;
            }

            _logger.LogInformation("快照画像升级步骤前停止 VM。VmName={VmName}, Step={Step}, Mode={Mode}", vm.Name, step, VmStopMode.Soft);
            await _vmrunService.StopVmAsync(vm.VmxPath, VmStopMode.Soft, cancellationToken).ConfigureAwait(false);

            var timeout = GetStopTimeout();
            if (await WaitUntilVmStoppedAsync(vm, step, timeout, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            if (!_options.Vmrun.AllowHardStopAfterSoftTimeout)
            {
                return Fail(ErrorCodes.VmStopFailed, $"VM did not power off within {timeout.TotalSeconds:0} seconds after soft stop.", step);
            }

            _logger.LogWarning("快照画像升级软关机超时，尝试硬关机。VmName={VmName}, Step={Step}", vm.Name, step);
            await _vmrunService.StopVmAsync(vm.VmxPath, VmStopMode.Hard, cancellationToken).ConfigureAwait(false);

            if (await WaitUntilVmStoppedAsync(vm, step, timeout, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            return Fail(ErrorCodes.VmStopFailed, $"VM did not power off within {timeout.TotalSeconds:0} seconds after hard stop.", step);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(ErrorCodes.VmStopFailed, ex.Message, step);
        }
    }

    private async Task<bool> WaitUntilVmStoppedAsync(
        VirtualMachineOptions vm,
        string step,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow().Add(timeout);
        while (true)
        {
            var isRunning = await _vmrunService.IsVmRunningAsync(vm.VmxPath, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("快照画像升级 VM 停止轮询结果。VmName={VmName}, Step={Step}, IsRunning={IsRunning}", vm.Name, step, isRunning);
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
        return TimeSpan.FromSeconds(_options.Vmrun.StopSoftTimeoutSeconds > 0 ? _options.Vmrun.StopSoftTimeoutSeconds : 60);
    }

    private SnapshotUpdateResult Fail(string errorCode, string errorMessage, string step)
    {
        _logger.LogWarning(
            "快照画像升级失败。Step={Step}, ErrorCode={ErrorCode}, ErrorMessage={ErrorMessage}",
            step,
            errorCode,
            errorMessage);
        return new SnapshotUpdateResult
        {
            Success = false,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            Step = step
        };
    }
}
