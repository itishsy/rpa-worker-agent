namespace Seebot.WorkerAgent.Core.Vmware;

public sealed class VmrunService : IVmrunService
{
    private readonly string _vmrunPath;
    private readonly IProcessRunner _processRunner;
    private readonly TimeSpan _commandTimeout;

    public VmrunService(string vmrunPath, IProcessRunner processRunner, TimeSpan commandTimeout)
    {
        _vmrunPath = vmrunPath;
        _processRunner = processRunner;
        _commandTimeout = commandTimeout;
    }

    public async Task<IReadOnlyList<string>> ListSnapshotsAsync(string vmxPath, CancellationToken cancellationToken)
    {
        var result = await RunVmrunAsync("listSnapshots", [vmxPath], cancellationToken);
        return ParseSnapshots(result.StandardOutput);
    }

    public async Task<string?> GetCurrentSnapshotAsync(string vmxPath, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "getCurrentSnapshot", vmxPath };
        var result = await _processRunner.RunAsync(
            new ProcessCommand(_vmrunPath, arguments, _commandTimeout, "getCurrentSnapshot"),
            cancellationToken);

        if (result.ExitCode != 0)
        {
            return null;
        }

        var snapshot = result.StandardOutput.Trim();
        return string.IsNullOrEmpty(snapshot) ? null : snapshot;
    }

    public Task<VmrunCommandResult> StopVmAsync(string vmxPath, VmStopMode mode, CancellationToken cancellationToken)
    {
        var modeArgument = mode == VmStopMode.Hard ? "hard" : "soft";
        return RunVmrunAsync("stop", [vmxPath, modeArgument], cancellationToken);
    }

    public Task<VmrunCommandResult> RevertToSnapshotAsync(string vmxPath, string snapshotName, CancellationToken cancellationToken)
    {
        return RunVmrunAsync("revertToSnapshot", [vmxPath, snapshotName], cancellationToken);
    }

    public Task<VmrunCommandResult> StartVmAsync(string vmxPath, bool noGui, CancellationToken cancellationToken)
    {
        return RunVmrunAsync("start", noGui ? [vmxPath, "nogui"] : [vmxPath], cancellationToken);
    }

    private async Task<VmrunCommandResult> RunVmrunAsync(
        string commandName,
        IReadOnlyList<string> commandArguments,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string> { commandName };
        arguments.AddRange(commandArguments);

        var result = await _processRunner.RunAsync(
            new ProcessCommand(_vmrunPath, arguments, _commandTimeout, commandName),
            cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new VmrunCommandException(result);
        }

        return result;
    }

    public Task<VmrunCommandResult> EnableSharedFoldersAsync(string vmxPath, CancellationToken cancellationToken)
    {
        return RunVmrunAsync("enableSharedFolders", [vmxPath], cancellationToken);
    }

    public Task<VmrunCommandResult> AddSharedFolderAsync(string vmxPath, string shareName, string hostPath, CancellationToken cancellationToken)
    {
        return RunVmrunAsync("addSharedFolder", [vmxPath, shareName, hostPath], cancellationToken);
    }

    public Task<VmrunCommandResult> RemoveSharedFolderAsync(string vmxPath, string shareName, CancellationToken cancellationToken)
    {
        return RunVmrunAsync("removeSharedFolder", [vmxPath, shareName], cancellationToken);
    }

    public Task<VmrunCommandResult> CreateSnapshotAsync(string vmxPath, string snapshotName, CancellationToken cancellationToken)
    {
        return RunVmrunAsync("snapshot", [vmxPath, snapshotName], cancellationToken);
    }

    public Task<VmrunCommandResult> DeleteSnapshotAsync(string vmxPath, string snapshotName, CancellationToken cancellationToken)
    {
        return RunVmrunAsync("deleteSnapshot", [vmxPath, snapshotName], cancellationToken);
    }

    private static IReadOnlyList<string> ParseSnapshots(string standardOutput)
    {
        return standardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith("Total snapshots:", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }
}
