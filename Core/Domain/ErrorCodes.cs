namespace Seebot.WorkerAgent.Core.Domain;

public static class ErrorCodes
{
    public const string SchedulerUnavailable = "SCHEDULER_UNAVAILABLE";
    public const string VmNotIdle = "VM_NOT_IDLE";
    public const string WorkerRunning = "WORKER_RUNNING";
    public const string WorkerUpgrading = "WORKER_UPGRADING";
    public const string ExecutorStopFailed = "EXECUTOR_STOP_FAILED";
    public const string LogBackupFailed = "LOG_BACKUP_FAILED";
    public const string LogBackupFailedButForceRevert = "LOG_BACKUP_FAILED_BUT_FORCE_REVERT";
    public const string VmStopFailed = "VM_STOP_FAILED";
    public const string SnapshotNotFound = "SNAPSHOT_NOT_FOUND";
    public const string SnapshotRevertFailed = "SNAPSHOT_REVERT_FAILED";
    public const string VmStartFailed = "VM_START_FAILED";
    public const string VmReadyTimeout = "VM_READY_TIMEOUT";
    public const string RunnerNotReady = "RUNNER_NOT_READY";
    public const string RunnerClosed = "RUNNER_CLOSED";
    public const string RobotError = "ROBOT_ERROR";
    public const string ClientError = "CLIENT_ERROR";
    public const string WorkerUpgradingTimeout = "WORKER_UPGRADING_TIMEOUT";
    public const string UpgradeFailed = "UPGRADE_FAILED";
    public const string WorkerOffline = "WORKER_OFFLINE";
    public const string WorkerProfileMismatch = "WORKER_PROFILE_MISMATCH";
    public const string LocalStateCorrupted = "LOCAL_STATE_CORRUPTED";
    public const string WorkerQuarantined = "WORKER_QUARANTINED";
    public const string VmNotFound = "VM_NOT_FOUND";
    public const string ProfileNotFound = "PROFILE_NOT_FOUND";
    public const string SnapshotCreateFailed = "SNAPSHOT_CREATE_FAILED";
    public const string SnapshotDeleteFailed = "SNAPSHOT_DELETE_FAILED";
    public const string RunnerStatusCheckFailed = "RUNNER_STATUS_CHECK_FAILED";
    public const string ConfigUpdateFailed = "CONFIG_UPDATE_FAILED";
}
