using Seebot.WorkerAgent.Core.Configuration;

namespace Seebot.WorkerAgent.Core.Health;

public sealed record VmDiskCleanupContext(
    VirtualMachineOptions Vm,
    bool IsVmRunning,
    DateTimeOffset CheckedAt);

/// <summary>
/// Extension point for scheduled local-backup and guest-disk cleanup.
/// Implementations own due-time, idle-window, transaction-lock and idempotency checks.
/// </summary>
public interface IVmDiskCleanupService
{
    Task CleanupIfDueAsync(VmDiskCleanupContext context, CancellationToken cancellationToken);
}

/// <summary>Default registration: disk cleanup is disabled and has no side effects.</summary>
public sealed class NoOpVmDiskCleanupService : IVmDiskCleanupService
{
    public Task CleanupIfDueAsync(VmDiskCleanupContext context, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
