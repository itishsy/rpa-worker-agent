using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Seebot.WorkerAgent.Core.Vmware;

public sealed class VmrunService : IVmrunService
{
    private readonly string _vmrunPath;
    private readonly string _hostType;
    private readonly IProcessRunner _processRunner;
    private readonly TimeSpan _commandTimeout;
    private readonly TimeSpan _fileOperationTimeout;
    private readonly ILogger<VmrunService> _logger;

    public VmrunService(
        string vmrunPath,
        string hostType,
        IProcessRunner processRunner,
        TimeSpan commandTimeout,
        TimeSpan fileOperationTimeout,
        ILogger<VmrunService>? logger = null)
    {
        _vmrunPath = vmrunPath;
        _hostType = hostType;
        _processRunner = processRunner;
        _commandTimeout = commandTimeout;
        _fileOperationTimeout = fileOperationTimeout;
        _logger = logger ?? NullLogger<VmrunService>.Instance;
    }

    public async Task<IReadOnlyList<string>> ListSnapshotsAsync(string vmxPath, CancellationToken cancellationToken)
    {
        var result = await RunVmrunAsync("listSnapshots", [vmxPath], cancellationToken);
        return ParseSnapshots(result.StandardOutput);
    }

    public async Task<bool> IsVmRunningAsync(string vmxPath, CancellationToken cancellationToken)
    {
        var result = await RunVmrunAsync("list", [], cancellationToken);
        var targetPath = NormalizeVmPath(vmxPath);
        return result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith("Total running VMs:", StringComparison.OrdinalIgnoreCase))
            .Any(line => string.Equals(NormalizeVmPath(line), targetPath, StringComparison.OrdinalIgnoreCase));
    }

    public Task<string?> GetCurrentSnapshotAsync(string vmxPath, CancellationToken cancellationToken)
    {
        return Task.FromResult(ReadCurrentSnapshotFromVmsd(vmxPath));
    }

    public async Task<string> GetGuestIPAddressAsync(string vmxPath, CancellationToken cancellationToken)
    {
        var result = await RunVmrunAsync("getGuestIPAddress", [vmxPath], cancellationToken);
        var ipAddress = result.StandardOutput.Trim();
        if (string.IsNullOrWhiteSpace(ipAddress))
        {
            throw new VmrunCommandException(result);
        }

        return ipAddress;
    }

    // vmrun 没有 getCurrentSnapshot 命令，通过同目录的 .vmsd 文件解析
    // snapshot.current = "N" 指向当前快照 UID，snapshotX.uid = "N" 对应 snapshotX.displayName
    // 注意：文件还含有 snapshot.mruX.uid 等非快照条目，必须只匹配 snapshot{数字}.uid
    private static readonly System.Text.RegularExpressions.Regex SnapshotEntryPattern =
        new(@"^(snapshot\d+)\.uid$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string? ReadCurrentSnapshotFromVmsd(string vmxPath)
    {
        var vmsdPath = Path.ChangeExtension(vmxPath, ".vmsd");
        if (!File.Exists(vmsdPath))
        {
            return null;
        }

        var lines = ReadVmsdLines(vmsdPath);

        string? currentUid = null;
        // uid → snapshotN 前缀，只收录 snapshot{数字}.uid 条目
        var uidToPrefix = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            var (key, value) = ParseVmsdLine(line);
            if (key is null || value is null)
            {
                continue;
            }

            if (string.Equals(key, "snapshot.current", StringComparison.OrdinalIgnoreCase))
            {
                currentUid = value;
                continue;
            }

            var m = SnapshotEntryPattern.Match(key);
            if (m.Success)
            {
                uidToPrefix.TryAdd(value, m.Groups[1].Value);
            }
        }

        if (currentUid is null || !uidToPrefix.TryGetValue(currentUid, out var prefix))
        {
            return null;
        }

        var displayNameKey = prefix + ".displayName";
        foreach (var line in lines)
        {
            var (key, value) = ParseVmsdLine(line);
            if (key is not null && string.Equals(key, displayNameKey, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    // 读取 vmsd 文件，首行可能声明 .encoding = "GBK" 等编码
    private static string[] ReadVmsdLines(string vmsdPath)
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        var firstLine = File.ReadLines(vmsdPath, System.Text.Encoding.Latin1).FirstOrDefault() ?? "";
        var (encodingKey, encodingValue) = ParseVmsdLine(firstLine);
        System.Text.Encoding encoding = System.Text.Encoding.UTF8;
        if (string.Equals(encodingKey, ".encoding", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(encodingValue))
        {
            try { encoding = System.Text.Encoding.GetEncoding(encodingValue); }
            catch (ArgumentException) { }
        }

        return File.ReadAllLines(vmsdPath, encoding);
    }

    private static (string? Key, string? Value) ParseVmsdLine(string line)
    {
        var trimmed = line.Trim();
        var eq = trimmed.IndexOf('=');
        if (eq < 1)
        {
            return (null, null);
        }

        var key = trimmed[..eq].Trim();
        var raw = trimmed[(eq + 1)..].Trim();

        // 值格式为 "value"，去掉首尾引号
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            raw = raw[1..^1];
        }

        return (key, raw);
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
        var arguments = new List<string> { "-T", _hostType, commandName };
        arguments.AddRange(commandArguments);

        var result = await RunProcessAsync(new ProcessCommand(_vmrunPath, arguments, _commandTimeout, commandName), arguments, cancellationToken);

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

    public async Task<VmrunCommandResult> RunProgramInGuestAsync(
        string vmxPath,
        string guestUser,
        string guestPassword,
        string programPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        // vmrun -T ws -gu user -gp pass runProgramInGuest vmx programPath [arg1 arg2 ...]
        var args = new List<string>
        {
            "-T", _hostType,
            "-gu", guestUser,
            "-gp", guestPassword,
            "runProgramInGuest",
            vmxPath, programPath
        };
        args.AddRange(arguments);

        var result = await RunProcessAsync(new ProcessCommand(_vmrunPath, args, _fileOperationTimeout, "runProgramInGuest"), args, cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new VmrunCommandException(result);
        }

        return result;
    }

    public async Task<VmrunCommandResult> CopyFileFromHostToGuestAsync(
        string vmxPath,
        string guestUser,
        string guestPassword,
        string hostPath,
        string guestPath,
        CancellationToken cancellationToken)
    {
        // vmrun -T ws -gu user -gp pass copyFileFromHostToGuest vmx hostPath guestPath
        var arguments = new List<string>
        {
            "-T", _hostType,
            "-gu", guestUser,
            "-gp", guestPassword,
            "copyFileFromHostToGuest",
            vmxPath, hostPath, guestPath
        };

        var result = await RunProcessAsync(new ProcessCommand(_vmrunPath, arguments, _fileOperationTimeout, "copyFileFromHostToGuest"), arguments, cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new VmrunCommandException(result);
        }

        return result;
    }

    public async Task<VmrunCommandResult> CopyFileFromGuestToHostAsync(
        string vmxPath,
        string guestUser,
        string guestPassword,
        string guestPath,
        string hostPath,
        CancellationToken cancellationToken)
    {
        // vmrun -T ws -gu user -gp pass copyFileFromGuestToHost vmx guestPath hostPath
        var arguments = new List<string>
        {
            "-T", _hostType,
            "-gu", guestUser,
            "-gp", guestPassword,
            "copyFileFromGuestToHost",
            vmxPath, guestPath, hostPath
        };

        var result = await RunProcessAsync(new ProcessCommand(_vmrunPath, arguments, _fileOperationTimeout, "copyFileFromGuestToHost"), arguments, cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new VmrunCommandException(result);
        }

        return result;
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

    private static string NormalizeVmPath(string path)
    {
        var normalized = path.Trim().Trim('"');
        try
        {
            normalized = Path.GetFullPath(normalized);
        }
        catch (Exception) when (
            normalized.IndexOfAny(Path.GetInvalidPathChars()) >= 0
            || string.IsNullOrWhiteSpace(normalized))
        {
        }

        return normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private async Task<VmrunCommandResult> RunProcessAsync(
        ProcessCommand command,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        _logger.LogInformation(
            "VMware command started. Command={Command}, Arguments={Arguments}, TimeoutSeconds={TimeoutSeconds}",
            command.CommandName,
            SanitizeArguments(arguments),
            command.Timeout.TotalSeconds);

        try
        {
            var result = await _processRunner.RunAsync(command, cancellationToken);
            var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (result.ExitCode == 0)
            {
                _logger.LogInformation(
                    "VMware command completed. Command={Command}, ExitCode={ExitCode}, ElapsedMs={ElapsedMs}, Stdout={Stdout}, Stderr={Stderr}",
                    command.CommandName,
                    result.ExitCode,
                    elapsedMs,
                    Truncate(result.StandardOutput),
                    Truncate(result.StandardError));
            }
            else
            {
                _logger.LogWarning(
                    "VMware command failed. Command={Command}, ExitCode={ExitCode}, ElapsedMs={ElapsedMs}, Stdout={Stdout}, Stderr={Stderr}",
                    command.CommandName,
                    result.ExitCode,
                    elapsedMs,
                    Truncate(result.StandardOutput),
                    Truncate(result.StandardError));
            }

            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(
                exception,
                "VMware command threw exception. Command={Command}, ElapsedMs={ElapsedMs}",
                command.CommandName,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
    }

    private static string SanitizeArguments(IReadOnlyList<string> arguments)
    {
        var sanitized = new List<string>(arguments.Count);
        for (var i = 0; i < arguments.Count; i++)
        {
            var value = arguments[i];
            sanitized.Add(value);
            if (string.Equals(value, "-gp", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "-EncodedCommand", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < arguments.Count)
                {
                    sanitized.Add("***");
                    i++;
                }
            }
        }

        return string.Join(" ", sanitized);
    }

    private static string Truncate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var singleLine = value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        return singleLine.Length <= 1000 ? singleLine : singleLine[..1000] + "...";
    }
}
