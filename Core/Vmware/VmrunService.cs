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

    public Task<VmrunCommandResult> CopyFileFromGuestToHostAsync(
        string vmxPath,
        string guestUser,
        string guestPassword,
        string guestPath,
        string hostPath,
        CancellationToken cancellationToken)
    {
        return RunVmrunWithArgumentsAsync(
            "copyFileFromGuestToHost",
            ["-gu", guestUser, "-gp", guestPassword, "copyFileFromGuestToHost", vmxPath, guestPath, hostPath],
            cancellationToken);
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

    private async Task<VmrunCommandResult> RunVmrunWithArgumentsAsync(
        string commandName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync(
            new ProcessCommand(_vmrunPath, arguments, _commandTimeout, commandName),
            cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new VmrunCommandException(result);
        }

        return result;
    }

    private static IReadOnlyList<string> ParseSnapshots(string standardOutput)
    {
        return standardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith("Total snapshots:", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }
}
