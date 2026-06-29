using System.Text.Json;
using Seebot.WorkerAgent.Core.Configuration;
using Seebot.WorkerAgent.Core.Domain;
using Seebot.WorkerAgent.Core.Vmware;

namespace Seebot.WorkerAgent.Core.Backup;

public sealed class LogBackupService : ILogBackupService
{
    private static readonly string[] DirectoryNames = ["cache", "db", "file", "logs"];
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
        var targetPath = Path.Combine(vm.HostWorkPath, vm.Name, timestamp.ToString("yyyyMMddHHmmss"));
        Directory.CreateDirectory(targetPath);

        string? errorMessage = null;
        var success = false;

        try
        {
            await _vmrunService.EnableSharedFoldersAsync(vm.VmxPath, cancellationToken).ConfigureAwait(false);
            try
            {
                await _vmrunService.RemoveSharedFolderAsync(vm.VmxPath, vm.Name, cancellationToken).ConfigureAwait(false);
            }
            catch (VmrunCommandException)
            {
                // 共享不存在时 removeSharedFolder 也返回 -1，忽略
            }
            await _vmrunService.AddSharedFolderAsync(vm.VmxPath, vm.Name, vm.HostSharedPath, cancellationToken).ConfigureAwait(false);

            foreach (var source in BuildSources(vm))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hostSourcePath = GuestPathToHostShared(source.GuestPath, vm.GuestSharedPath, vm.HostSharedPath);
                var hostDestPath = Path.Combine(targetPath, source.Name);
                CopyDirectory(hostSourcePath, hostDestPath);
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

        var fileCount = CountFiles(targetPath);
        var totalBytes = SumFileBytes(targetPath);
        var result = new LogBackupResult
        {
            TxId = transaction.TransactionId,
            TargetPath = targetPath,
            Directories = DirectoryNames,
            FileCount = fileCount,
            TotalBytes = totalBytes,
            Success = success,
            ErrorCode = success ? null : ErrorCodes.LogBackupFailed,
            ErrorMessage = errorMessage
        };

        await WriteManifestAsync(
            vm,
            transaction,
            timestamp,
            targetPath,
            fileCount,
            totalBytes,
            success,
            result.ErrorCode,
            errorMessage,
            cancellationToken);

        return result;
    }

    private static IReadOnlyList<BackupSource> BuildSources(VirtualMachineOptions vm)
    {
        return
        [
            new BackupSource("cache", vm.GuestBackupPaths.Cache),
            new BackupSource("db", vm.GuestBackupPaths.Db),
            new BackupSource("file", vm.GuestBackupPaths.File),
            new BackupSource("logs", vm.GuestBackupPaths.Logs)
        ];
    }

    // 将 Guest 路径转换为 Host 侧共享目录的对应路径
    // 例：GuestSharedPath=D:\seebon\rpa  HostSharedPath=D:\seebon\rpa-worker-agent\work\shared
    //     guestPath=D:\seebon\rpa\cache  → D:\seebon\rpa-worker-agent\work\shared\cache
    private static string GuestPathToHostShared(string guestPath, string guestSharedPath, string hostSharedPath)
    {
        var guestNorm = guestSharedPath.TrimEnd('\\', '/');
        var pathNorm = guestPath.TrimEnd('\\', '/');

        if (!pathNorm.StartsWith(guestNorm, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Guest path '{guestPath}' is not under GuestSharedPath '{guestSharedPath}'.");
        }

        var relative = pathNorm.Length > guestNorm.Length
            ? pathNorm[(guestNorm.Length + 1)..]
            : "";

        return string.IsNullOrEmpty(relative)
            ? hostSharedPath
            : Path.Combine(hostSharedPath, relative);
    }

    private static void CopyDirectory(string sourcePath, string destPath)
    {
        Directory.CreateDirectory(destPath);
        if (!Directory.Exists(sourcePath))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourcePath, file);
            var destFile = Path.Combine(destPath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            File.Copy(file, destFile, overwrite: true);
        }
    }

    private static async Task WriteManifestAsync(
        VirtualMachineOptions vm,
        SwitchTransaction transaction,
        DateTimeOffset timestamp,
        string targetPath,
        int fileCount,
        long totalBytes,
        bool success,
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        var sources = new
        {
            cache = vm.GuestBackupPaths.Cache,
            db = vm.GuestBackupPaths.Db,
            file = vm.GuestBackupPaths.File,
            logs = vm.GuestBackupPaths.Logs
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
            targetPath,
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

    private static int CountFiles(string targetPath)
    {
        return Directory.EnumerateFiles(targetPath, "*", SearchOption.AllDirectories).Count();
    }

    private static long SumFileBytes(string targetPath)
    {
        return Directory.EnumerateFiles(targetPath, "*", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);
    }

    private sealed record BackupSource(string Name, string GuestPath);
}
