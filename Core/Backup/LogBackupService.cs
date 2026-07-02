using System.Text.Json;
using Seebot.WorkerAgent.Core.Configuration;
using Seebot.WorkerAgent.Core.Domain;
using Seebot.WorkerAgent.Core.Vmware;

namespace Seebot.WorkerAgent.Core.Backup;

public sealed class LogBackupService : ILogBackupService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly IVmrunService _vmrunService;
    private readonly WorkerAgentOptions _options;

    public LogBackupService(IVmrunService vmrunService, WorkerAgentOptions options)
    {
        _vmrunService = vmrunService;
        _options = options;
    }

    public async Task<LogBackupResult> BackupAsync(
        VirtualMachineOptions vm,
        SwitchTransaction transaction,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        var directoryNames = ParseDirectoryNames(vm.GuestBackupPaths);
        var targetPath = Path.Combine(_options.Agent.HostWorkPath, "backup", vm.Name, timestamp.ToString("yyyyMMdd"));
        Directory.CreateDirectory(targetPath);

        var timestampTag = timestamp.ToString("yyyyMMddHHmmss") + "_" + transaction.FromProfileId;
        var guestZipPath = Path.Combine(vm.GuestWorkPath, $"{timestampTag}.zip").Replace('/', '\\');
        var hostScriptPath = Path.Combine(targetPath, $"{timestampTag}_backup.ps1");

        string? errorMessage = null;
        var success = false;
        long totalBytes = 0;

        try
        {
            await CompressOnGuestAsync(
                vm,
                directoryNames,
                guestZipPath,
                timestampTag,
                hostScriptPath,
                cancellationToken).ConfigureAwait(false);

            var hostZipPath = Path.Combine(targetPath, $"{timestampTag}.zip");
            await _vmrunService.CopyFileFromGuestToHostAsync(
                vm.VmxPath,
                vm.GuestUser,
                vm.GuestPasswordSecret,
                guestZipPath,
                hostZipPath,
                cancellationToken).ConfigureAwait(false);

            if (File.Exists(hostZipPath))
            {
                totalBytes = new FileInfo(hostZipPath).Length;
            }

            if (File.Exists(hostScriptPath))
            {
                File.Delete(hostScriptPath);
            }

            success = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            errorMessage = exception.Message;
        }

        var result = new LogBackupResult
        {
            TxId = transaction.TransactionId,
            TargetPath = targetPath,
            Directories = directoryNames,
            FileCount = success ? 1 : 0,
            TotalBytes = totalBytes,
            Success = success,
            ErrorCode = success ? null : ErrorCodes.LogBackupFailed,
            ErrorMessage = errorMessage
        };

        // Manifest writing is currently disabled to preserve existing runtime behavior.
        // await WriteManifestAsync(
        //     vm,
        //     transaction,
        //     timestamp,
        //     targetPath,
        //     guestZipPath,
        //     timestampTag,
        //     directoryNames,
        //     result.FileCount,
        //     totalBytes,
        //     success,
        //     result.ErrorCode,
        //     errorMessage,
        //     cancellationToken);

        return result;
    }

    private async Task CompressOnGuestAsync(
        VirtualMachineOptions vm,
        IReadOnlyList<string> directoryNames,
        string guestZipPath,
        string timestampTag,
        string hostScriptPath,
        CancellationToken cancellationToken)
    {
        var sourceLines = string.Join("," + Environment.NewLine, directoryNames.Select(d =>
        {
            var fullPath = Path.Combine(vm.GuestWorkPath, d).Replace('/', '\\');
            return $"    [pscustomobject]@{{ Name = '{EscapePowerShellSingleQuotedString(d)}'; Path = '{EscapePowerShellSingleQuotedString(fullPath)}' }}";
        }));
        var guestTimestampPath = Path.Combine(vm.GuestWorkPath, timestampTag).Replace('/', '\\');

        var script = $@"$ErrorActionPreference = 'Stop'
$zipPath = '{EscapePowerShellSingleQuotedString(guestZipPath)}'
$timestampPath = '{EscapePowerShellSingleQuotedString(guestTimestampPath)}'
$sources = @(
{sourceLines}
)
if (Test-Path -LiteralPath $timestampPath) {{
    Remove-Item -LiteralPath $timestampPath -Recurse -Force
}}
New-Item -ItemType Directory -Path $timestampPath -Force | Out-Null

$copiedDirectoryCount = 0
foreach ($source in $sources) {{
    if (-not (Test-Path -LiteralPath $source.Path)) {{
        continue
    }}

    $destinationPath = Join-Path $timestampPath $source.Name
    New-Item -ItemType Directory -Path $destinationPath -Force | Out-Null
    $items = Get-ChildItem -LiteralPath $source.Path -Force
    if ($items.Count -gt 0) {{
        Copy-Item -LiteralPath $items.FullName -Destination $destinationPath -Recurse -Force
    }}
    $copiedDirectoryCount++
}}

if ($copiedDirectoryCount -eq 0) {{
    exit 1
}}
if (Test-Path -LiteralPath $zipPath) {{
    Remove-Item -LiteralPath $zipPath -Force
}}
Compress-Archive -Path $timestampPath -DestinationPath $zipPath -Force
";

        await File.WriteAllTextAsync(hostScriptPath, script, System.Text.Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);

        var encodedCommand = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));

        await _vmrunService.RunProgramInGuestAsync(
            vm.VmxPath,
            vm.GuestUser,
            vm.GuestPasswordSecret,
            "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe",
            ["-NonInteractive", "-EncodedCommand", encodedCommand],
            cancellationToken).ConfigureAwait(false);
    }

    private static string EscapePowerShellSingleQuotedString(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> ParseDirectoryNames(string value)
    {
        var names = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return names.Count == 0 ? ["cache", "db", "file", "logs"] : names;
    }

    private async Task WriteManifestAsync(
        VirtualMachineOptions vm,
        SwitchTransaction transaction,
        DateTimeOffset timestamp,
        string targetPath,
        string guestZipPath,
        string timestampTag,
        IReadOnlyList<string> directoryNames,
        int fileCount,
        long totalBytes,
        bool success,
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        var sources = directoryNames
            .ToDictionary(
                name => name,
                name => Path.Combine(vm.GuestWorkPath, name),
                StringComparer.OrdinalIgnoreCase);

        var manifest = new
        {
            txId = transaction.TransactionId,
            hostId = transaction.HostId,
            vmName = vm.Name,
            workerId = vm.WorkerId,
            fromProfileId = transaction.FromProfileId,
            fromSnapshotName = transaction.FromSnapshotName,
            toProfileId = transaction.TargetProfileId,
            toSnapshotName = transaction.TargetSnapshotName,
            firstTaskId = transaction.FirstTaskId,
            backupTime = timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
            workPath = _options.Agent.HostWorkPath,
            guestWorkPath = vm.GuestWorkPath,
            targetPath,
            guestZipPath,
            zipFileName = $"{timestampTag}.zip",
            sources,
            directories = directoryNames,
            fileCount,
            totalBytes,
            success,
            errorCode,
            errorMessage
        };

        await using var stream = File.Create(Path.Combine(targetPath, "backup_manifest.json"));
        await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken);
    }
}
