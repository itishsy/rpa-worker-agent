namespace Seebot.WorkerAgent.Core.Domain;

public enum SwitchTransactionStatus
{
    CREATED,
    STOP_RUNNER_DONE,
    LOG_BACKUP_DONE,
    VM_STOP_DONE,
    SNAPSHOT_REVERT_DONE,
    VM_START_DONE,
    WORKER_READY_DONE,
    SUCCESS,
    FAILED,
    NEED_MANUAL_CHECK
}
