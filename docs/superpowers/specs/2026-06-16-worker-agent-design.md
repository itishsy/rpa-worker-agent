# Worker Agent Design

Date: 2026-06-16

## Purpose

Build a Windows host-side Worker Agent that coordinates local VMware Workstation VMs for RPA execution profiles. The Agent does not execute RPA tasks, does not start `runner.jar`, and does not manage task leases. It selects idle compatible VMs, switches them to the required profile snapshot, waits for the VM-side runner to become observable, and reports state.

The VM-side `runner.jar` continues to pull tasks from the scheduler center by itself.

## Confirmed Scope

In scope:

- Run as a C#/.NET Windows Service.
- Manage multiple local VMs on one host.
- Use `vmrun.exe` as the only VM control provider.
- Query scheduler queues by `profileId`.
- Switch an idle VM to a target profile snapshot when that profile has pending tasks.
- Allow the same `profileId` to run on multiple compatible VMs.
- Keep VMs powered on after they become idle.
- Stop `runner.jar` before log backup and snapshot switching.
- Back up VM logs before reverting snapshots.
- Persist local switch transactions in SQLite.
- Recover or quarantine after service restart and failed VM operations.
- Expose a localhost-only operations API protected by API key.
- Report Agent, VM, worker, profile, snapshot, and runner state.

Out of scope:

- RPA task execution.
- Starting `runner.jar` from the Agent.
- Lease management.
- Direct task assignment to runner.
- Capacity calculation based on task count.
- vSphere, ESXi, PowerCLI, dynamic clone, or cross-host scheduling.

## Naming Model

Use a three-layer model:

```text
profileId    = rpa-{city}-{business}-{system}
workerId     = {profileId}-{instance}
snapshotName = {profileId}-{version}
```

Example:

```text
profileId    = rpa-sh-tax-etax
workerId     = rpa-sh-tax-etax-001
snapshotName = rpa-sh-tax-etax-v20260615.1
```

Definitions:

- `profileId` identifies the environment capability needed by tasks.
- `workerId` identifies one runner instance and must be unique on the host.
- `snapshotName` identifies the concrete VM environment version.
- `version` should use a sortable format such as `vYYYYMMDD.N`.

The Agent must not require `workerId` and `snapshotName` to be the same.

## Configuration Model

Each VM explicitly declares the profiles it supports and the snapshot used for each profile.

```json
{
  "Agent": {
    "HostId": "HOST-SR20-001",
    "AgentName": "SR20 Host Worker Agent",
    "PollIntervalSeconds": 10,
    "HeartbeatIntervalSeconds": 15,
    "SwitchTimeoutSeconds": 300,
    "WaitVmReadyTimeoutSeconds": 180,
    "IdleStableSeconds": 30,
    "ForceRevertWhenBackupFailed": false
  },
  "OperationsApi": {
    "ListenUrl": "http://127.0.0.1:18090",
    "ApiKey": "replace-with-local-secret"
  },
  "Scheduler": {
    "BaseUrl": "http://seebot-server/api/rpa",
    "AccessToken": "replace-with-token"
  },
  "Vmrun": {
    "VmrunPath": "C:\\Program Files (x86)\\VMware\\VMware Workstation\\vmrun.exe"
  },
  "VirtualMachines": [
    {
      "VmName": "VM-RPA-001",
      "VmxPath": "D:\\VMs\\VM-RPA-001\\VM-RPA-001.vmx",
      "GuestIp": "192.168.100.101",
      "WorkerId": "rpa-sh-tax-etax-001",
      "ExecutorStopUrl": "http://192.168.100.101:18080/executor/stop",
      "ExecutorHealthUrl": "http://192.168.100.101:18080/executor/health",
      "WorkerStatusUrl": "http://192.168.100.101:18080/worker/status",
      "GuestLogPath": "C:\\seebot\\logs",
      "HostBackupRoot": "D:\\seebot-vm-log-backup",
      "Profiles": [
        {
          "ProfileId": "rpa-sh-tax-etax",
          "SnapshotName": "rpa-sh-tax-etax-v20260615.1",
          "City": "sh",
          "Business": "tax",
          "System": "etax",
          "Enabled": true
        }
      ]
    }
  ]
}
```

Startup validation:

- `vmrun.exe` exists.
- VMX files exist.
- `workerId` values are unique on the host.
- `profileId` values match the naming rule.
- Each enabled profile has exactly one snapshot mapping per VM.
- Each configured `snapshotName` exists according to `vmrun listSnapshots`.
- Backup root is writable.
- Local operations API is bound to `127.0.0.1` unless explicitly changed in a later version.

## Architecture

`Seebot.WorkerAgent.Service` is one .NET Windows Service that hosts background workers and a localhost ASP.NET Core operations API.

Components:

- `PoolSchedulerService`: polls scheduler queues by `profileId` and creates VM switch intents.
- `VmCoordinator`: one coordinator per VM, with one async lock per VM.
- `VmSwitchService`: orchestrates stop runner, backup logs, stop VM, revert snapshot, start VM, and readiness checks.
- `VmrunService`: wraps `vmrun.exe`, captures output, exit code, timeout, and duration.
- `GuestWorkerClient`: calls existing VM-side HTTP APIs for runner health, status, and stop.
- `SchedulerClient`: queries pending profile tasks and reports Agent, VM, worker, and switch state.
- `LogBackupService`: copies or collects VM logs and writes backup manifests.
- `LocalStore`: persists VM state, switch transactions, recovery markers, and audit records in SQLite.
- `RecoveryService`: resumes safe transaction steps or quarantines VMs after service restart.
- `OperationsApi`: localhost-only API protected by `X-Agent-Api-Key`.

Different VMs can be operated in parallel. Automatic scheduling, manual operations, and recovery for the same VM must use the same VM lock.

## Scheduling Rules

The scheduler uses priority, waiting time, profile compatibility, and snapshot stickiness. It does not calculate a target VM count from pending task count.

Per poll cycle:

1. Query pending state for enabled `profileId` values.
2. If no profile has pending tasks, only monitor and report current VM state.
3. Pick the next target profile by scheduler priority and queue wait time.
4. Prefer VMs already running the target profile.
5. If more capacity is still useful because the target profile still has pending tasks, choose one compatible idle VM.
6. At most one VM switch is started per poll cycle.
7. The same profile may run on all compatible VMs over multiple cycles while its queue still has pending tasks.
8. Do not switch VMs in `Running`, `Upgrading`, quarantined, or transaction-in-progress states.

An idle switch candidate must satisfy all of these:

- VM supports the target profile.
- VM is not quarantined.
- VM has no active switch transaction.
- Runner status is `Runnable`.
- The VM current profile queue is empty.
- The VM has stayed idle for at least `IdleStableSeconds`.

If a candidate becomes busy during the switch attempt, the Agent cancels that VM's transaction and tries again in a future cycle.

## Safe Switch Flow

The Agent does not implement a drain protocol in this version. The existing VM-side stop API is the final concurrency gate.

Switch flow:

1. Acquire the VM lock.
2. Create a local switch transaction.
3. Re-read VM runner status.
4. Call the VM-side stop API to stop `runner.jar`.
5. If the stop API reports that runner has already started a task or is `Running`, cancel this transaction and do not switch the VM.
6. Confirm runner is stopped and `currentTaskId` is empty.
7. Back up VM logs to the host.
8. Write `backup_manifest.json`.
9. Stop the VM with `vmrun stop`.
10. Revert to the target snapshot with `vmrun revertToSnapshot`.
11. Start the VM with `vmrun start`.
12. Wait for network and VM-side HTTP health.
13. Wait until runner status is `Runnable` or `Running`.
14. Verify reported `workerId` and `profileId` match the expected VM/profile.
15. Report success and enter monitoring.

Required stop API semantics:

- If runner is idle, stop task polling and close `runner.jar`.
- If runner is executing a task, reject the stop request.
- The Agent must not force-stop an executing runner.

## Runner Status Handling

The Agent keeps using the existing runner status codes:

```text
0 New
1 Runnable
2 Running
3 Closed
4 RobotError
5 ClientError
6 Upgrading
7 UpgradeFailed
8 Offline
```

Switch decisions:

- `Runnable`: candidate for switch if the current profile queue is empty and idle is stable.
- `Running`: never switch.
- `Upgrading`: never switch.
- `New` or `Closed`: not a switch candidate in the scheduler; can be handled during ready wait or reported as not ready.
- `RobotError`, `ClientError`, `UpgradeFailed`, `Offline`: report error and consider quarantine according to policy.

Ready decisions after VM start:

- `Runnable`: ready.
- `Running`: ready and already consuming work.
- `New`: wait until timeout, then report `RUNNER_NOT_READY`.
- `Closed`: wait until timeout, then report `RUNNER_CLOSED`.
- `Upgrading`: wait until upgrade timeout, then report `WORKER_UPGRADING_TIMEOUT`.
- Error and offline states are reported immediately according to their error code.

## Local State

VM state fields:

```text
vm_name
worker_id
current_profile_id
current_snapshot_name
runner_status_code
agent_vm_status
last_idle_at
last_switch_at
is_quarantined
updated_at
```

Switch transaction statuses:

```text
CREATED
STOP_RUNNER_DONE
LOG_BACKUP_DONE
VM_STOP_DONE
SNAPSHOT_REVERT_DONE
VM_START_DONE
WORKER_READY_DONE
SUCCESS
FAILED
NEED_MANUAL_CHECK
```

Switch transaction fields:

```text
tx_id
host_id
vm_name
worker_id
from_profile_id
from_snapshot_name
to_profile_id
to_snapshot_name
trigger_reason
status
step
error_code
error_message
started_at
updated_at
finished_at
```

## Recovery Rules

After service restart, `RecoveryService` scans incomplete transactions.

- `CREATED`: if runner was not stopped, mark failed and allow future scheduling.
- `STOP_RUNNER_DONE`: continue log backup if possible.
- `LOG_BACKUP_DONE`: continue VM stop.
- `VM_STOP_DONE`: continue snapshot revert.
- `SNAPSHOT_REVERT_DONE`: continue VM start.
- `VM_START_DONE`: continue ready wait.
- `WORKER_READY_DONE`: report state and mark success.
- `NEED_MANUAL_CHECK`: do not recover automatically.

Failure handling:

- Stop runner failed or runner became `Running`: cancel this switch and find another VM later.
- Log backup failed: block snapshot revert unless `ForceRevertWhenBackupFailed=true`.
- VM stop, revert, or start failed: quarantine the VM.
- VM starts with unexpected `workerId` or `profileId`: report `WORKER_PROFILE_MISMATCH` and quarantine the VM.
- Manual unquarantine is allowed through the operations API and must write an audit record.

## Scheduler API

Pending tasks are queried by `profileId`, not `workerId`.

```http
GET /api/rpa/profile-task/pending?profileId=rpa-sh-tax-etax
```

Example response:

```json
{
  "hasTask": true,
  "profileId": "rpa-sh-tax-etax",
  "pendingCount": 100,
  "firstTaskId": 123456,
  "executionCode": "EXE202606160001",
  "priority": 5,
  "oldestQueuedAt": "2026-06-16 09:30:00"
}
```

Status reporting includes both instance and profile identifiers:

```json
{
  "hostId": "HOST-SR20-001",
  "vmName": "VM-RPA-001",
  "workerId": "rpa-sh-tax-etax-001",
  "profileId": "rpa-sh-tax-etax",
  "snapshotName": "rpa-sh-tax-etax-v20260615.1",
  "agentVmStatus": "MONITORING",
  "runnerStatusCode": 1,
  "runnerStatusName": "Runnable",
  "currentTaskId": null,
  "lastHeartbeatTime": "2026-06-16 10:00:00"
}
```

## Operations API

The API listens on `127.0.0.1` and requires `X-Agent-Api-Key`.

Initial endpoints:

```text
GET  /api/agent/status
GET  /api/vms
GET  /api/vms/{vmName}
POST /api/scheduler/pause
POST /api/scheduler/resume
POST /api/vms/{vmName}/quarantine
POST /api/vms/{vmName}/unquarantine
POST /api/vms/{vmName}/switch-profile
GET  /api/transactions
GET  /api/transactions/{txId}
```

Manual `switch-profile` uses the same safe switch flow as automatic scheduling.

## Acceptance Criteria

- The Agent runs as a Windows Service.
- The Agent loads and validates multi-VM configuration.
- Each VM can declare a different supported profile set.
- The Agent validates configured snapshots using `vmrun listSnapshots`.
- The Agent queries queues by `profileId`.
- If a profile has pending tasks, the Agent can switch one compatible idle VM to that profile snapshot.
- Over multiple cycles, the same profile can occupy multiple compatible VMs while tasks remain pending.
- The Agent does not compute target capacity from task count.
- `Running` and `Upgrading` runners are never switched.
- The Agent stops `runner.jar` successfully before log backup and snapshot revert.
- If runner starts a task during the stop attempt, the Agent cancels the switch.
- Logs are backed up and a manifest is written before snapshot revert.
- Switch transactions are persisted locally and can be recovered after service restart.
- VM control failures quarantine the VM.
- State reporting includes `hostId`, `vmName`, `workerId`, `profileId`, `snapshotName`, and runner status.
- The localhost operations API can show state, pause/resume scheduling, quarantine/unquarantine a VM, and manually trigger a safe profile switch.
