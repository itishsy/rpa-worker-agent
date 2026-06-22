using System.Diagnostics;

namespace Seebot.WorkerAgent.Core.Vmware;

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<VmrunCommandResult> RunAsync(ProcessCommand command, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(command.Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var stopwatch = Stopwatch.StartNew();

        using var process = new Process
        {
            StartInfo = CreateStartInfo(command)
        };

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(linkedCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            stopwatch.Stop();

            return new VmrunCommandResult(
                process.ExitCode,
                stdout,
                stderr,
                stopwatch.Elapsed,
                command.CommandName);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            stopwatch.Stop();
            return new VmrunCommandResult(
                ExitCode: -1,
                StandardOutput: await ReadSafelyAsync(stdoutTask),
                StandardError: $"Command timed out after {command.Timeout}.",
                Duration: stopwatch.Elapsed,
                CommandName: command.CommandName);
        }
    }

    private static ProcessStartInfo CreateStartInfo(ProcessCommand command)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static async Task<string> ReadSafelyAsync(Task<string> task)
    {
        try
        {
            return await task;
        }
        catch (OperationCanceledException)
        {
            return "";
        }
    }
}
