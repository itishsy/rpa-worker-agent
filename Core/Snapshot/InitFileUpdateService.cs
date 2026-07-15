using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seebot.WorkerAgent.Core.Configuration;
using Seebot.WorkerAgent.Core.Guest;
using Seebot.WorkerAgent.Core.Storage;
using Seebot.WorkerAgent.Core.Vmware;

namespace Seebot.WorkerAgent.Core.Snapshot;

public sealed class InitFileUpdateService : IInitFileUpdateService
{
    // Guest path for the worker id consumed by the legacy client.
    private const string GuestInitFilePath = @"C:\Program Files\rpa\rpa.init";

    private static readonly TimeSpan DefaultProcessWaitTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultProcessPollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultVmStopTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DefaultVmStoppedPollInterval = TimeSpan.FromSeconds(2);

    private readonly IVmrunService _vmrunService;
    private readonly IVmOperationLock _vmOperationLock;
    private readonly IProfileSnapshotResolver _snapshotResolver;
    private readonly IGuestTokenProvisioningService? _guestTokenProvisioningService;
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
        ILogger<InitFileUpdateService>? logger = null,
        IGuestTokenProvisioningService? guestTokenProvisioningService = null)
    {
        _vmrunService = vmrunService;
        _vmOperationLock = vmOperationLock;
        _snapshotResolver = snapshotResolver;
        _guestTokenProvisioningService = guestTokenProvisioningService;
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
        // 1. Resolve the profile snapshot to update.
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

        // Ensure VMware releases the VMX before reverting to a profile snapshot.
        await SafeStopAsync(vm, cancellationToken).ConfigureAwait(false);

        // 2. Revert to the profile snapshot.
        try
        {
            await _vmrunService.RevertToSnapshotAsync(vm.VmxPath, oldSnapshotName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return FailProfile(profile.ProfileId, "SNAPSHOT_REVERT_FAILED", ex.Message);
        }

        // 3. Start the VM.
        try
        {
            await _vmrunService.StartVmAsync(vm.VmxPath, noGui: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return FailProfile(profile.ProfileId, "VM_START_FAILED", ex.Message);
        }

        var tokenFailure = await ProvisionGuestTokenAsync(vm, profile.ProfileId, cancellationToken).ConfigureAwait(false);
        if (tokenFailure is not null)
        {
            await SafeStopAsync(vm, cancellationToken).ConfigureAwait(false);
            return tokenFailure;
        }

        // 4. Wait until VMware Tools guest operations are available.
        var guestOperationsReady = await WaitForGuestOperationsReadyAsync(vm, cancellationToken).ConfigureAwait(false);
        if (!guestOperationsReady)
        {
            await SafeStopAsync(vm, cancellationToken).ConfigureAwait(false);
            return FailProfile(profile.ProfileId, "GUEST_OPERATIONS_TIMEOUT",
                $"Timed out waiting for VMware Tools guest operations to become available (timeout={_processWaitTimeout.TotalMinutes:F0}min).");
        }

        // 5. Write and verify rpa.init; stop robot.exe only when direct update fails.
        try
        {
            var initUpdate = await UpdateInitFileWithFallbackAsync(vm, cancellationToken).ConfigureAwait(false);
            if (!initUpdate.Success)
            {
                await SafeStopAsync(vm, cancellationToken).ConfigureAwait(false);
                return FailProfile(profile.ProfileId, "WRITE_INIT_FAILED", initUpdate.ErrorMessage ?? "Failed to update rpa.init.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await SafeStopAsync(vm, cancellationToken).ConfigureAwait(false);
            return FailProfile(profile.ProfileId, "WRITE_INIT_FAILED", ex.Message);
        }

        // 7. Stop VM before creating the updated snapshot.
        await SafeStopAsync(vm, cancellationToken).ConfigureAwait(false);

        // 8. Generate a new snapshot name and create the updated snapshot.
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

    // Stop guest processes best-effort; failures are logged and ignored.
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

        // 10. Persist the new snapshot mapping when a registry is configured.
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

    // Poll listProcessesInGuest until VMware Tools guest operations are available.
    private async Task<bool> WaitForGuestOperationsReadyAsync(
        VirtualMachineOptions vm,
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

                _logger.LogInformation(
                    "VMware Tools guest operations are available. VmName={VmName}, ProcessCount={ProcessCount}",
                    vm.Name,
                    processes.Count);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // VMware Tools may still be initializing; keep polling.
                _logger.LogDebug(ex,
                    "listProcessesInGuest not yet available. VmName={VmName}", vm.Name);
            }

            await Task.Delay(_processPollInterval, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    // Stop guest processes best-effort; failures are logged and ignored.
    private async Task ForceKillProcessesAsync(VirtualMachineOptions vm, CancellationToken cancellationToken)
    {
        foreach (var processName in new[] { "robot.exe" })
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

    private async Task<InitFileWriteAttempt> UpdateInitFileWithFallbackAsync(
        VirtualMachineOptions vm,
        CancellationToken cancellationToken)
    {
        var directAttempt = await TryWriteAndVerifyInitFileAsync(vm, useTemporaryFile: false, cancellationToken)
            .ConfigureAwait(false);
        if (directAttempt.Success)
        {
            return directAttempt;
        }

        _logger.LogWarning(
            "Direct rpa.init update failed; stopping robot.exe before retry. VmName={VmName}, Error={Error}",
            vm.Name,
            directAttempt.ErrorMessage);

        await ForceKillProcessesAsync(vm, cancellationToken).ConfigureAwait(false);
        if (!await WaitForProcessExitAsync(vm, "robot.exe", cancellationToken).ConfigureAwait(false))
        {
            return new InitFileWriteAttempt(false, "Timed out waiting for robot.exe to exit before writing rpa.init.");
        }

        var retryAttempt = await TryWriteAndVerifyInitFileAsync(vm, useTemporaryFile: true, cancellationToken)
            .ConfigureAwait(false);
        if (retryAttempt.Success)
        {
            return retryAttempt;
        }

        return new InitFileWriteAttempt(
            false,
            $"Direct write failed: {directAttempt.ErrorMessage}; retry after stopping robot.exe failed: {retryAttempt.ErrorMessage}");
    }

    private async Task<InitFileWriteAttempt> TryWriteAndVerifyInitFileAsync(
        VirtualMachineOptions vm,
        bool useTemporaryFile,
        CancellationToken cancellationToken)
    {
        var psCommand = BuildWriteAndVerifyInitFileCommand(vm.WorkerId, useTemporaryFile);
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(psCommand));

        _logger.LogInformation(
            "Writing and verifying rpa.init. VmName={VmName}, WorkerId={WorkerId}, UseTemporaryFile={UseTemporaryFile}",
            vm.Name,
            vm.WorkerId,
            useTemporaryFile);

        try
        {
            await _vmrunService.RunProgramInGuestAsync(
                vm.VmxPath,
                vm.GuestUser,
                vm.GuestPasswordSecret,
                @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
                ["-NoProfile", "-NonInteractive", "-EncodedCommand", encoded],
                cancellationToken).ConfigureAwait(false);
            return new InitFileWriteAttempt(true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new InitFileWriteAttempt(false, ex.Message);
        }
    }

    private static string BuildWriteAndVerifyInitFileCommand(string workerId, bool useTemporaryFile)
    {
        var escapedPath = EscapePowerShellSingleQuotedString(GuestInitFilePath);
        var escapedWorkerId = EscapePowerShellSingleQuotedString(workerId);
        var writeCommand = useTemporaryFile
            ? """
$tmp = "$target.tmp"
[System.IO.File]::WriteAllText($tmp, $expected, [System.Text.Encoding]::UTF8)
Move-Item -LiteralPath $tmp -Destination $target -Force
"""
            : """
[System.IO.File]::WriteAllText($target, $expected, [System.Text.Encoding]::UTF8)
""";

        return $$"""
$ErrorActionPreference = 'Stop'
$target = '{{escapedPath}}'
$expected = '{{escapedWorkerId}}'
{{writeCommand}}
$actual = [System.IO.File]::ReadAllText($target, [System.Text.Encoding]::UTF8).TrimEnd("`r", "`n")
if ($actual -ne $expected) {
    Write-Error "rpa.init verification failed. Expected '$expected' but read '$actual'."
    exit 10
}
""";
    }

    private static string EscapePowerShellSingleQuotedString(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private async Task<bool> WaitForProcessExitAsync(
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

                if (!processes.Any(p => p.CommandLine.Contains(processName, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogInformation(
                        "Guest process exited. VmName={VmName}, Process={Process}",
                        vm.Name,
                        processName);
                    return true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "listProcessesInGuest failed while waiting for process exit. VmName={VmName}", vm.Name);
            }

            await Task.Delay(_processPollInterval, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private sealed record InitFileWriteAttempt(bool Success, string? ErrorMessage);

    private async Task<ProfileInitUpdateResult?> ProvisionGuestTokenAsync(
        VirtualMachineOptions vm,
        string profileId,
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
            : new ProfileInitUpdateResult
            {
                ProfileId = profileId,
                Success = false,
                ErrorCode = result.ErrorCode ?? "WRITE_TOKEN_FAILED",
                ErrorMessage = result.ErrorMessage ?? "Failed to provision scheduler token in guest."
            };
    }

    // soft stop first; fall back to hard stop when the VM does not stop.
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

        // Wait until VMware reports the VM as stopped.
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

        // Use hard stop as the final fallback after the timeout.
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
