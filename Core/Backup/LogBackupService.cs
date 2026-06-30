using System.Text.Json;
using Seebot.WorkerAgent.Core.Configuration;
using Seebot.WorkerAgent.Core.Domain;
using Seebot.WorkerAgent.Core.Vmware;

namespace Seebot.WorkerAgent.Core.Backup;

public sealed class LogBackupService : ILogBackupService
{
    private static readonly string[] DirectoryNames = ["db", "logs", "cache", "file"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly IVmrunService _vmrunService;

    public LogBackupService(IVmrunService vmrunService)
    {
        _vmrunService = vmrunService;
    }

    public async Task<LogBackupResult> BackupAsync(
        VirtualMachineOptions vm,
        SwitchTransaction transaction,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        var targetPath = Path.Combine(vm.HostWorkPath, vm.Name, timestamp.ToString("yyyyMMdd"));
        Directory.CreateDirectory(targetPath);

        var timestampTag = timestamp.ToString("yyyyMMddHHmmss") + "_" + transaction.FromProfileId;
        var guestZipPath = Path.Combine(vm.GuestWorkPath, $"{timestampTag}.zip").Replace('/', '\\');
        var hostScriptPath = Path.Combine(targetPath, $"{timestampTag}_backup.ps1");

        string? errorMessage = null;
        var success = false;
        long totalBytes = 0;

        try
        {
            await CompressOnGuestAsync(vm, guestZipPath, timestampTag, hostScriptPath, cancellationToken).ConfigureAwait(false);

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
            Directories = DirectoryNames,
            FileCount = success ? 1 : 0,
            TotalBytes = totalBytes,
            Success = success,
            ErrorCode = success ? null : ErrorCodes.LogBackupFailed,
            ErrorMessage = errorMessage
        };

        //await WriteManifestAsync(
        //    vm,
        //    transaction,
        //    timestamp,
        //    targetPath,
        //    guestZipPath,
        //    timestampTag,
        //    result.FileCount,
        //    totalBytes,
        //    success,
        //    result.ErrorCode,
        //    errorMessage,
        //    cancellationToken);

        return result;
    }

    private async Task CompressOnGuestAsync(
        VirtualMachineOptions vm,
        string guestZipPath,
        string timestampTag,
        string hostScriptPath,
        CancellationToken cancellationToken)
    {
        var sourceLines = string.Join("," + Environment.NewLine, DirectoryNames.Select(d =>
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

        // 保存脚本到 host 供审计
        await File.WriteAllTextAsync(hostScriptPath, script, System.Text.Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);

        // -EncodedCommand 传入 UTF-16LE Base64，完全绕过执行策略和命令行转义
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

    private static async Task WriteManifestAsync(
        VirtualMachineOptions vm,
        SwitchTransaction transaction,
        DateTimeOffset timestamp,
        string targetPath,
        string guestZipPath,
        string timestampTag,
        int fileCount,
        long totalBytes,
        bool success,
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        var sources = new
        {
            cache = Path.Combine(vm.GuestWorkPath, "cache"),
            db = Path.Combine(vm.GuestWorkPath, "db"),
            file = Path.Combine(vm.GuestWorkPath, "file"),
            logs = Path.Combine(vm.GuestWorkPath, "logs")
        };

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
            workPath = vm.HostWorkPath,
            guestWorkPath = vm.GuestWorkPath,
            targetPath,
            guestZipPath,
            zipFileName = $"{timestampTag}.zip",
            sources,
            directories = DirectoryNames,
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
