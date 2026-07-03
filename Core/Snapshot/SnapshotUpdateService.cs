using Seebot.WorkerAgent.Core.Configuration;
using Seebot.WorkerAgent.Core.Domain;
using Seebot.WorkerAgent.Core.Guest;
using Seebot.WorkerAgent.Core.Vmware;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Seebot.WorkerAgent.Core.Snapshot;

public sealed class SnapshotUpdateService : ISnapshotUpdateService
{
    private const int RunnerStatusCheckMaxAttempts = 5;
    private static readonly TimeSpan RunnerStatusCheckInterval = TimeSpan.FromSeconds(20);

    private readonly IVmrunService _vmrunService;
    private readonly IGuestWorkerClient _guestWorkerClient;
    private readonly IProfileSnapshotResolver _snapshotResolver;
    private readonly IVmOperationLock _vmOperationLock;
    private readonly WorkerAgentOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SnapshotUpdateService> _logger;

    public SnapshotUpdateService(
        IVmrunService vmrunService,
        IGuestWorkerClient guestWorkerClient,
        IProfileSnapshotResolver snapshotResolver,
        IVmOperationLock vmOperationLock,
        WorkerAgentOptions options,
        TimeProvider? timeProvider = null,
        ILogger<SnapshotUpdateService>? logger = null)
    {
        _vmrunService = vmrunService;
        _guestWorkerClient = guestWorkerClient;
        _snapshotResolver = snapshotResolver;
        _vmOperationLock = vmOperationLock;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<SnapshotUpdateService>.Instance;
    }

    public async Task<SnapshotUpdateResult> UpdateSnapshotAsync(
        string vmName,
        string profileId,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Snapshot update started. VmName={VmName}, ProfileId={ProfileId}", vmName, profileId);
        var vm = _options.VirtualMachines
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
        _logger.LogInformation("Snapshot update acquired VM operation lock. VmName={VmName}, VmxPath={VmxPath}", vm.Name, vm.VmxPath);

        IReadOnlyList<string> existingSnapshots;
        try
        {
            _logger.LogInformation("Listing snapshots for snapshot update. VmName={VmName}, ProfileId={ProfileId}", vm.Name, profileId);
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
            "Current profile snapshot resolved for update. VmName={VmName}, ProfileId={ProfileId}, CurrentSnapshotName={CurrentSnapshotName}",
            vm.Name,
            profileId,
            currentSnapshotName);

        try
        {
            _logger.LogInformation("Reverting VM before snapshot update. VmName={VmName}, SnapshotName={SnapshotName}", vm.Name, currentSnapshotName);
            await _vmrunService.RevertToSnapshotAsync(vm.VmxPath, currentSnapshotName, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(ErrorCodes.SnapshotRevertFailed, ex.Message, "revert");
        }

        try
        {
            _logger.LogInformation("Starting VM before snapshot update validation. VmName={VmName}", vm.Name);
            await _vmrunService.StartVmAsync(vm.VmxPath, noGui: true, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(ErrorCodes.VmStartFailed, ex.Message, "start");
        }

        await Task.Delay(TimeSpan.FromMinutes(1), _timeProvider, cancellationToken);
        _logger.LogInformation("Initial wait before runner status checks completed. VmName={VmName}, WaitSeconds={WaitSeconds}", vm.Name, 60);

        RunnerStatusResponse? status = null;
        Exception? lastException = null;
        for (var attempt = 1; attempt <= RunnerStatusCheckMaxAttempts; attempt++)
        {
            try
            {
                _logger.LogInformation("Checking runner status for snapshot update. VmName={VmName}, Attempt={Attempt}, MaxAttempts={MaxAttempts}", vm.Name, attempt, RunnerStatusCheckMaxAttempts);
                status = await _guestWorkerClient.GetRunnerStatusAsync(vm, cancellationToken);
                lastException = null;
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastException = ex;
                _logger.LogWarning(ex, "Runner status check failed during snapshot update. VmName={VmName}, Attempt={Attempt}", vm.Name, attempt);
                if (attempt < RunnerStatusCheckMaxAttempts)
                {
                    await Task.Delay(RunnerStatusCheckInterval, _timeProvider, cancellationToken);
                }
            }
        }

        if (lastException is not null || status is null)
        {
            return Fail(ErrorCodes.RunnerStatusCheckFailed, lastException?.Message ?? "Unknown error.", "check-status");
        }

        if (status.RunnerStatusCode is not (RunnerStatusCode.Runnable or RunnerStatusCode.Running))
        {
            return Fail(ErrorCodes.RunnerNotReady,
                $"Runner status is {status.RunnerStatusCode} after VM start.", "check-status");
        }

        try
        {
            _logger.LogInformation("Stopping VM before creating new snapshot. VmName={VmName}", vm.Name);
            await _vmrunService.StopVmAsync(vm.VmxPath, VmStopMode.Soft, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(ErrorCodes.VmStopFailed, ex.Message, "stop");
        }

        existingSnapshots = await _vmrunService.ListSnapshotsAsync(vm.VmxPath, cancellationToken);
        var now = _timeProvider.GetUtcNow().ToLocalTime();
        var newSnapshotName = SnapshotNameGenerator.Generate(
            profileId,
            DateOnly.FromDateTime(now.DateTime),
            existingSnapshots);
        _logger.LogInformation(
            "New snapshot name generated. VmName={VmName}, ProfileId={ProfileId}, NewSnapshotName={NewSnapshotName}",
            vm.Name,
            profileId,
            newSnapshotName);

        try
        {
            _logger.LogInformation("Creating new snapshot. VmName={VmName}, SnapshotName={SnapshotName}", vm.Name, newSnapshotName);
            await _vmrunService.CreateSnapshotAsync(vm.VmxPath, newSnapshotName, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(ErrorCodes.SnapshotCreateFailed, ex.Message, "create-snapshot");
        }

        try
        {
            _logger.LogInformation("Deleting previous profile snapshot. VmName={VmName}, SnapshotName={SnapshotName}", vm.Name, currentSnapshotName);
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

        return new SnapshotUpdateResult
        {
            Success = true,
            NewSnapshotName = newSnapshotName,
            Step = "done"
        };
    }

    private SnapshotUpdateResult Fail(string errorCode, string errorMessage, string step)
    {
        _logger.LogWarning(
            "Snapshot update failed. Step={Step}, ErrorCode={ErrorCode}, ErrorMessage={ErrorMessage}",
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
