namespace Seebot.WorkerAgent.Core.Coordination;

public enum VmOperationType
{
    ProfileSwitch,
    SnapshotUpdate,
    ManualPowerOn,
    Maintenance
}

public enum VmOperationStatus
{
    Acquiring,
    CheckingRunner,
    StoppingRunner,
    BackingUp,
    StoppingVm,
    RevertingSnapshot,
    StartingVm,
    WaitingRunner,
    VerifyingIdentity,
    Success,
    Failed
}

public sealed record VmOperationHandle(
    string HostId,
    string VmName,
    string OperationId,
    VmOperationType Type,
    long FencingToken,
    DateTimeOffset LeaseExpiresAt);

public interface IVmOperationStore
{
    Task<VmOperationHandle?> TryAcquireAsync(string hostId, string vmName, string operationId,
        VmOperationType type, string ownerInstanceId, DateTimeOffset now, TimeSpan leaseDuration,
        CancellationToken cancellationToken);
    Task<bool> RenewAsync(VmOperationHandle handle, string ownerInstanceId, DateTimeOffset now,
        TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task<bool> UpdateAsync(VmOperationHandle handle, VmOperationStatus status, string? errorCode,
        string? errorMessage, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task<bool> TryReservePowerCycleAsync(string hostId, string vmName, DateTimeOffset now,
        TimeSpan window, int maximum, TimeSpan cooldown, CancellationToken cancellationToken);
    Task RecordRecoveryResultAsync(string hostId, string vmName, bool success, DateTimeOffset now,
        int maximumConsecutiveFailures, TimeSpan failureCooldown, CancellationToken cancellationToken);
}

public interface IVmOperationLease : IAsyncDisposable
{
    VmOperationHandle Handle { get; }
    Task SetStatusAsync(VmOperationStatus status, CancellationToken cancellationToken = default);
    Task<bool> TryReservePowerCycleAsync(CancellationToken cancellationToken = default);
    Task RecordRecoveryResultAsync(bool success, CancellationToken cancellationToken = default);
    Task CompleteAsync(CancellationToken cancellationToken = default);
    Task FailAsync(string errorCode, string errorMessage, CancellationToken cancellationToken = default);
}

public interface IVmOperationCoordinator
{
    Task<IVmOperationLease?> TryAcquireAsync(string hostId, string vmName, VmOperationType type,
        CancellationToken cancellationToken);
}
