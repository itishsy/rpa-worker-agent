using Seebot.WorkerAgent.Core.Configuration;
using Seebot.WorkerAgent.Core.Domain;
using Seebot.WorkerAgent.Core.Guest;
using Seebot.WorkerAgent.Core.Vmware;

namespace Seebot.WorkerAgent.Core.Snapshot;

public sealed class SnapshotUpdateService : ISnapshotUpdateService
{
    private readonly IVmrunService _vmrunService;
    private readonly IGuestWorkerClient _guestWorkerClient;
    private readonly IConfigFileUpdater _configFileUpdater;
    private readonly WorkerAgentOptions _options;
    private readonly TimeProvider _timeProvider;

    public SnapshotUpdateService(
        IVmrunService vmrunService,
        IGuestWorkerClient guestWorkerClient,
        IConfigFileUpdater configFileUpdater,
        WorkerAgentOptions options,
        TimeProvider? timeProvider = null)
    {
        _vmrunService = vmrunService;
        _guestWorkerClient = guestWorkerClient;
        _configFileUpdater = configFileUpdater;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<SnapshotUpdateResult> UpdateSnapshotAsync(
        string vmName,
        string profileId,
        CancellationToken cancellationToken)
    {
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

        var currentSnapshotName = profile.SnapshotName;

        try
        {
            await _vmrunService.RevertToSnapshotAsync(vm.VmxPath, currentSnapshotName, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(ErrorCodes.SnapshotRevertFailed, ex.Message, "revert");
        }

        try
        {
            await _vmrunService.StartVmAsync(vm.VmxPath, noGui: true, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(ErrorCodes.VmStartFailed, ex.Message, "start");
        }

        await Task.Delay(TimeSpan.FromMinutes(1), _timeProvider, cancellationToken);

        RunnerStatusResponse status;
        try
        {
            status = await _guestWorkerClient.GetRunnerStatusAsync(vm, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(ErrorCodes.RunnerStatusCheckFailed, ex.Message, "check-status");
        }

        if (status.RunnerStatusCode is not (RunnerStatusCode.Runnable or RunnerStatusCode.Running))
        {
            return Fail(ErrorCodes.RunnerNotReady,
                $"Runner status is {status.RunnerStatusCode} after VM start.", "check-status");
        }

        try
        {
            await _vmrunService.StopVmAsync(vm.VmxPath, VmStopMode.Soft, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(ErrorCodes.VmStopFailed, ex.Message, "stop");
        }

        var existingSnapshots = await _vmrunService.ListSnapshotsAsync(vm.VmxPath, cancellationToken);
        var now = _timeProvider.GetUtcNow().ToLocalTime();
        var newSnapshotName = SnapshotNameGenerator.Generate(
            profileId,
            DateOnly.FromDateTime(now.DateTime),
            existingSnapshots);

        try
        {
            await _vmrunService.CreateSnapshotAsync(vm.VmxPath, newSnapshotName, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(ErrorCodes.SnapshotCreateFailed, ex.Message, "create-snapshot");
        }

        try
        {
            await _vmrunService.DeleteSnapshotAsync(vm.VmxPath, currentSnapshotName, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(ErrorCodes.SnapshotDeleteFailed, ex.Message, "delete-snapshot");
        }

        try
        {
            await _configFileUpdater.UpdateSnapshotNameAsync(vmName, profileId, newSnapshotName, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(ErrorCodes.ConfigUpdateFailed, ex.Message, "update-config");
        }

        return new SnapshotUpdateResult
        {
            Success = true,
            NewSnapshotName = newSnapshotName,
            Step = "done"
        };
    }

    private static SnapshotUpdateResult Fail(string errorCode, string errorMessage, string step)
    {
        return new SnapshotUpdateResult
        {
            Success = false,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            Step = step
        };
    }
}
