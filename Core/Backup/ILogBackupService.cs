using Seebot.WorkerAgent.Core.Configuration;
using Seebot.WorkerAgent.Core.Domain;

namespace Seebot.WorkerAgent.Core.Backup;

public interface ILogBackupService
{
    Task<LogBackupResult> BackupAsync(
        VirtualMachineOptions vm,
        SwitchTransaction transaction,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken);
}
