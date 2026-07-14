using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seebot.WorkerAgent.Core.Configuration;
using Seebot.WorkerAgent.Core.Storage;
using Seebot.WorkerAgent.Core.Vmware;

namespace Seebot.WorkerAgent.Core.Snapshot;

public sealed class InitFileUpdateService : IInitFileUpdateService
{
    // guest 内 rpa.init 的固定路径
    private const string GuestInitFilePath = @"C:\Program Files\rpa\rpa.init";

    private static readonly TimeSpan DefaultProcessWaitTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultProcessPollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultVmStopTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DefaultVmStoppedPollInterval = TimeSpan.FromSeconds(2);

    private readonly IVmrunService _vmrunService;
    private readonly IVmOperationLock _vmOperationLock;
    private readonly IProfileSnapshotResolver _snapshotResolver;
    private readonly IVirtualMachineRegistry? _registry;
    private readonly WorkerAgentOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _processWaitTimeout;
    private readonly TimeSpan _processPollInterval;
    private readonly ILogger<InitFileUpdateService> _logger;

    public InitFileUpdateService(
        IVmrunService vmrunService,
        IVmOperationLock vmOperationLock,
        IProfileSnapshotResolver snapshotResolver,
        WorkerAgentOptions options,
        IVirtualMachineRegistry? registry = null,
        TimeProvider? timeProvider = null,
        TimeSpan? processWaitTimeout = null,
        TimeSpan? processPollInterval = null,
        ILogger<InitFileUpdateService>? logger = null)
    {
        _vmrunService = vmrunService;
        _vmOperationLock = vmOperationLock;
        _snapshotResolver = snapshotResolver;
        _options = options;
        _registry = registry;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _processWaitTimeout = processWaitTimeout ?? DefaultProcessWaitTimeout;
        _processPollInterval = processPollInterval ?? DefaultProcessPollInterval;
        _logger = logger ?? NullLogger<InitFileUpdateService>.Instance;
    }

    public async Task<InitFileUpdateResult> UpdateWorkerIdInSnapshotsAsync(string vmName, CancellationToken cancellationToken)
    {
        var vm = _options.VirtualMachines
            .FirstOrDefault(v => string.Equals(v.Name, vmName, StringComparison.OrdinalIgnoreCase));

        if (vm is null)
        {
            return Fail("VM_NOT_FOUND", $"VM '{vmName}' not found in configuration.");
        }

        if (string.IsNullOrWhiteSpace(vm.GuestUser))
        {
            return Fail("GUEST_CREDENTIALS_MISSING", $"VM '{vmName}' has no GuestUser configured.");
        }

        if (vm.Profiles.Count == 0)
        {
            return Fail("NO_PROFILES", $"VM '{vmName}' has no profiles.");
        }

        await using var vmLock = await _vmOperationLock.AcquireAsync(vm.VmxPath, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Init file update started. VmName={VmName}, WorkerId={WorkerId}, ProfileCount={ProfileCount}",
            vm.Name, vm.WorkerId, vm.Profiles.Count);

        var profileResults = new List<ProfileInitUpdateResult>();

        foreach (var profile in vm.Profiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _logger.LogInformation(
                "Updating profile snapshot. VmName={VmName}, ProfileId={ProfileId}",
                vm.Name, profile.ProfileId);

            var result = await UpdateProfileSnapshotAsync(vm, profile, cancellationToken).ConfigureAwait(false);
            profileResults.Add(result);

            if (!result.Success)
            {
                _logger.LogWarning(
                    "Profile snapshot update failed. VmName={VmName}, ProfileId={ProfileId}, ErrorCode={ErrorCode}, ErrorMessage={ErrorMessage}",
                    vm.Name, profile.ProfileId, result.ErrorCode, result.ErrorMessage);
            }
            else
            {
                _logger.LogInformation(
                    "Profile snapshot update succeeded. VmName={VmName}, ProfileId={ProfileId}, OldSnapshot={OldSnapshot}, NewSnapshot={NewSnapshot}",
                    vm.Name, profile.ProfileId, result.OldSnapshotName, result.NewSnapshotName);
            }
        }

        var successCount = profileResults.Count(r => r.Success);
        var failCount = profileResults.Count - successCount;
        var allSuccess = failCount == 0;

        _logger.LogInformation(
            "Init file update completed. VmName={VmName}, Success={Success}, SuccessCount={SuccessCount}, FailCount={FailCount}",
            vm.Name, allSuccess, successCount, failCount);

        return new InitFileUpdateResult
        {
            Success = allSuccess,
            ErrorCode = allSuccess ? null : "PARTIAL_FAILURE",
            ErrorMessage = allSuccess ? null : $"{failCount} profile(s) failed.",
            Profiles = profileResults
        };
    }

    private async Task<ProfileInitUpdateResult> UpdateProfileSnapshotAsync(
        VirtualMachineOptions vm,
        ProfileOptions profile,
        CancellationToken cancellationToken)
    {
        // 1. 解析当前快照名
        List<string> snapshots;
        try
        {
            snapshots = [.. await _vmrunService.ListSnapshotsAsync(vm.VmxPath, cancellationToken).ConfigureAwait(false)];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return FailProfile(profile.ProfileId, "LIST_SNAPSHOTS_FAILED", ex.Message);
        }

        var resolution = _snapshotResolver.Resolve(vm, profile, snapshots);
        if (!resolution.IsReady)
        {
            return FailProfile(profile.ProfileId, "SNAPSHOT_NOT_FOUND",
                $"No ready snapshot found for profile '{profile.ProfileId}': {resolution.Status}");
        }

        var oldSnapshotName = resolution.SnapshotName!;

        // 2. 回滚到 profile 快照
        try
        {
            await _vmrunService.RevertToSnapshotAsync(vm.VmxPath, oldSnapshotName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return FailProfile(profile.ProfileId, "SNAPSHOT_REVERT_FAILED", ex.Message);
        }

        // 3. 启动 VM
        try
        {
            await _vmrunService.StartVmAsync(vm.VmxPath, noGui: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return FailProfile(profile.ProfileId, "VM_START_FAILED", ex.Message);
        }

        // 4. 等待 rpa-client.exe 进程出现
        var processFound = await WaitForProcessAsync(vm, "rpa-client.exe", cancellationToken).ConfigureAwait(false);
        if (!processFound)
        {
            await SafeStopAsync(vm, cancellationToken).ConfigureAwait(false);
            return FailProfile(profile.ProfileId, "PROCESS_WAIT_TIMEOUT",
                $"Timed out waiting for rpa-client.exe to start (timeout={_processWaitTimeout.TotalMinutes:F0}min).");
        }

        // 5. 强杀 rpa-client.exe 和 java.exe，释放 rpa.init 文件句柄
        await ForceKillProcessesAsync(vm, cancellationToken).ConfigureAwait(false);

        // 6. 写入新 workerId 到 rpa.init
        try
        {
            await WriteInitFileAsync(vm, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await SafeStopAsync(vm, cancellationToken).ConfigureAwait(false);
            return FailProfile(profile.ProfileId, "WRITE_INIT_FAILED", ex.Message);
        }

        // 7. 停机
        await SafeStopAsync(vm, cancellationToken).ConfigureAwait(false);

        // 8. 生成新快照名并创建快照
        string newSnapshotName;
        try
        {
            var snapshotsAfterStop = await _vmrunService.ListSnapshotsAsync(vm.VmxPath, cancellationToken).ConfigureAwait(false);
            var today = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);
            newSnapshotName = SnapshotNameGenerator.Generate(profile.ProfileId, today, snapshotsAfterStop);
            await _vmrunService.CreateSnapshotAsync(vm.VmxPath, newSnapshotName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return FailProfile(profile.ProfileId, "SNAPSHOT_CREATE_FAILED", ex.Message);
        }

        // 9. 删除旧快照（失败只记录警告，不影响整体结果）
        try
        {
            await _vmrunService.DeleteSnapshotAsync(vm.VmxPath, oldSnapshotName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Failed to delete old snapshot, will proceed. VmName={VmName}, ProfileId={ProfileId}, OldSnapshot={OldSnapshot}",
                vm.Name, profile.ProfileId, oldSnapshotName);
        }

        // 10. 更新注册表中的快照名称
        if (_registry is not null)
        {
            try
            {
                await _registry.UpdateProfileSnapshotAsync(vm.Name, profile.ProfileId, newSnapshotName, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Failed to update registry snapshot name. VmName={VmName}, ProfileId={ProfileId}",
                    vm.Name, profile.ProfileId);
            }
        }

        return new ProfileInitUpdateResult
        {
            ProfileId = profile.ProfileId,
            Success = true,
            OldSnapshotName = oldSnapshotName,
            NewSnapshotName = newSnapshotName
        };
    }

    // 轮询 listProcessesInGuest 直到目标进程出现或超时
    private async Task<bool> WaitForProcessAsync(
        VirtualMachineOptions vm,
        string processName,
        CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow() + _processWaitTimeout;
        while (_timeProvider.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var processes = await _vmrunService
                    .ListProcessesInGuestAsync(vm.VmxPath, vm.GuestUser, vm.GuestPasswordSecret, cancellationToken)
                    .ConfigureAwait(false);

                if (processes.Any(p => p.CommandLine.Contains(processName, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogInformation(
                        "Guest process found. VmName={VmName}, Process={Process}",
                        vm.Name, processName);
                    return true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // VMware Tools 尚未就绪时正常失败，继续等待
                _logger.LogDebug(ex,
                    "listProcessesInGuest not yet available. VmName={VmName}", vm.Name);
            }

            await Task.Delay(_processPollInterval, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    // 强杀 Guest 内进程，best-effort：失败只记 debug 日志，不中断流程
    private async Task ForceKillProcessesAsync(VirtualMachineOptions vm, CancellationToken cancellationToken)
    {
        foreach (var processName in new[] { "rpa-client.exe", "java.exe" })
        {
            try
            {
                await _vmrunService.RunProgramInGuestAsync(
                    vm.VmxPath,
                    vm.GuestUser,
                    vm.GuestPasswordSecret,
                    @"C:\Windows\System32\taskkill.exe",
                    ["/F", "/IM", processName],
                    cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "Force killed guest process. VmName={VmName}, Process={Process}",
                    vm.Name, processName);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex,
                    "Force kill did not succeed (process may not exist). VmName={VmName}, Process={Process}",
                    vm.Name, processName);
            }
        }
    }

    // 通过 PowerShell EncodedCommand 写入 rpa.init
    private Task WriteInitFileAsync(VirtualMachineOptions vm, CancellationToken cancellationToken)
    {
        // 单引号内的 workerId 不含特殊字符，直接嵌入；如需转义可扩展
        var psCommand = $"Set-Content -Path '{GuestInitFilePath}' -Value '{vm.WorkerId}' -Encoding UTF8 -Force";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(psCommand));

        _logger.LogInformation(
            "Writing rpa.init. VmName={VmName}, WorkerId={WorkerId}",
            vm.Name, vm.WorkerId);

        return _vmrunService.RunProgramInGuestAsync(
            vm.VmxPath,
            vm.GuestUser,
            vm.GuestPasswordSecret,
            @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
            ["-NonInteractive", "-EncodedCommand", encoded],
            cancellationToken);
    }

    // soft stop → 等待停止；超时后补一次 hard stop
    private async Task SafeStopAsync(VirtualMachineOptions vm, CancellationToken cancellationToken)
    {
        try
        {
            await _vmrunService.StopVmAsync(vm.VmxPath, VmStopMode.Soft, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Soft stop failed, trying hard stop. VmName={VmName}", vm.Name);
            try
            {
                await _vmrunService.StopVmAsync(vm.VmxPath, VmStopMode.Hard, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception hardEx) when (hardEx is not OperationCanceledException)
            {
                _logger.LogWarning(hardEx, "Hard stop also failed. VmName={VmName}", vm.Name);
            }

            return;
        }

        // 等待 VM 确认停止
        var deadline = _timeProvider.GetUtcNow() + DefaultVmStopTimeout;
        while (_timeProvider.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!await _vmrunService.IsVmRunningAsync(vm.VmxPath, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "IsVmRunning check failed. VmName={VmName}", vm.Name);
            }

            await Task.Delay(DefaultVmStoppedPollInterval, cancellationToken).ConfigureAwait(false);
        }

        // 超时后 hard stop 兜底
        _logger.LogWarning("VM did not stop within timeout, sending hard stop. VmName={VmName}", vm.Name);
        try
        {
            await _vmrunService.StopVmAsync(vm.VmxPath, VmStopMode.Hard, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Hard stop fallback also failed. VmName={VmName}", vm.Name);
        }
    }

    private static InitFileUpdateResult Fail(string errorCode, string errorMessage) =>
        new() { Success = false, ErrorCode = errorCode, ErrorMessage = errorMessage, Profiles = [] };

    private static ProfileInitUpdateResult FailProfile(string profileId, string errorCode, string errorMessage) =>
        new() { ProfileId = profileId, Success = false, ErrorCode = errorCode, ErrorMessage = errorMessage };
}
