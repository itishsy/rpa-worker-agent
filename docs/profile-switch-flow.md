# Profile 切换完整流程

## 概述

Profile 切换是 RPA Worker Agent 的核心调度行为。当云端后台存在待执行任务时，Agent 会选择合适的空闲 VM，将其从当前 profile 的快照切换到目标 profile 的快照，使 VM 内的 Runner 可以承接对应 profile 的任务。

---

## 一、触发：调度轮询循环

`WorkerAgent`（`BackgroundService`）每隔 `Agent.PollIntervalSeconds`（默认 30s）调用一次 `PoolSchedulerService.RunOneCycleAsync`。

```
WorkerAgent.ExecuteAsync
  └─ 每 PollIntervalSeconds 调用
       └─ PoolSchedulerService.RunOneCycleAsync
```

---

## 二、调度周期（RunOneCycleAsync）

### 2.1 获取待切换 Profile 列表

对配置的每个 VM，取其 `WorkerId` 调用云端接口：

```
GET /robot/client/task/findTaskByWorkerId/{workerId}
→ IReadOnlyList<ProfilePendingTaskResponse>
```

返回值已由云端按优先级排序，字段含义：

| 字段 | 说明 |
|------|------|
| `ProfileId` | 目标 profile |
| `HasTask` | 是否有待执行任务 |
| `Priority` | 优先级（越大越优先） |
| `OldestQueuedAt` | 最早入队时间 |
| `FirstTaskId` | 队列中第一个任务 ID |

多个 VM 的结果合并为一个列表，即为本轮 `targetProfiles`。

### 2.2 加载 VM 状态 & 上报

从 SQLite（`local_vm_state`）读取所有 VM 的当前状态，同时向云端上报每台 VM 的最新状态（`ReportVmStatusAsync`）。

### 2.3 候选 VM 筛选（FindCandidate）

对 `targetProfiles` 中的每个目标 profile，遍历配置中的所有 VM，按以下条件过滤：

| 检查项 | 不满足时的处理 |
|--------|--------------|
| VM 配置中包含该 `profileId` 对应的快照 | 跳过此 VM |
| VM 当前 profile 不等于目标 profile | 跳过（已是目标，无需切换）|
| VM 未被隔离（`IsQuarantined = false`）| 拒绝：`WORKER_QUARANTINED` |
| 无进行中的切换事务（`HasActiveSwitchTransaction = false`）| 拒绝：`VM_NOT_IDLE` |
| Runner 状态为 `Runnable` | 拒绝：`VM_NOT_IDLE` |
| VM 当前 profile 不在本轮 targetProfiles 中（无待执行任务）| 拒绝：`VM_NOT_IDLE` |
| 空闲时长 ≥ `Agent.IdleStableSeconds` | 拒绝：`VM_NOT_IDLE` |

找到第一个通过全部条件的 VM 即为切换候选，**每个 cycle 只触发一次切换**。

---

## 三、切换执行（VmSwitchService.SwitchAsync）

切换是一个有状态的事务，每步完成后均写入 SQLite（`local_switch_transaction`），状态流转如下：

```
CREATED
  │
  ├─ 查询 Runner 状态（GET :9090/status）
  │     Running   → FAILED / WORKER_RUNNING
  │     Upgrading → FAILED / WORKER_UPGRADING
  │
  ├─ Kill Runner（POST :9090/kill，deadline=30s）
  │     失败              → FAILED / EXECUTOR_STOP_FAILED
  │     Kill 后仍有任务   → FAILED / EXECUTOR_STOP_FAILED
  │
STOP_RUNNER_DONE
  │
  ├─ 备份 VM 日志目录（cache / db / file / logs）
  │     → {HostWorkPath}/{VmName}/{timestamp}/
  │     失败且 ForceRevertWhenBackupFailed=false → FAILED / LOG_BACKUP_FAILED
  │
LOG_BACKUP_DONE
  │
  ├─ vmrun stop（soft 停机）
  │     异常 → VmState 置 ERROR + FAILED / VM_STOP_FAILED
  │
VM_STOP_DONE
  │
  ├─ vmrun revertToSnapshot {TargetSnapshotName}
  │     异常 → VmState 置 ERROR + FAILED / SNAPSHOT_REVERT_FAILED
  │
SNAPSHOT_REVERT_DONE
  │
  ├─ vmrun start nogui
  │     异常 → VmState 置 ERROR + FAILED / VM_START_FAILED
  │
VM_START_DONE
  │
  ├─ 轮询等待 Runner 就绪（每 3s 查询一次）
  │     超时（WaitVmReadyTimeoutSeconds，默认 120s）→ FAILED / VM_READY_TIMEOUT
  │     状态为 Error 类（Closed / RobotError 等）   → FAILED / 对应 ErrorCode
  │     状态为 Runnable 或 Running                  → 继续
  │
  ├─ 校验 workerId / profileId 与预期一致
  │     不匹配 → FAILED / WORKER_PROFILE_MISMATCH
  │
WORKER_READY_DONE
  │
SUCCESS
```

### 关键说明

**VM 级错误与普通失败的区别**

`VM_STOP_FAILED`、`SNAPSHOT_REVERT_FAILED`、`VM_START_FAILED` 这三种错误发生时，除写入事务状态外，还会将 `local_vm_state` 中该 VM 的 `VmStatus` 置为 `ERROR`。这会导致后续所有调度周期的候选筛选均拒绝该 VM（`Runner 状态非 Runnable`），直到人工干预恢复。

**备份失败的强制继续**

当 `Agent.ForceRevertWhenBackupFailed = true` 时，备份失败不会中止切换，会以 `LOG_BACKUP_FAILED_BUT_FORCE_REVERT` 记录警告并继续执行后续步骤。

**Runner 状态的轮询语义**

VM 启动后 Runner 可能处于初始化中（`New`）或升级中（`Upgrading`），这两种状态触发等待而非失败。其余状态的处理：

| RunnerStatusCode | EvaluateReadyAfterVmStart 结果 |
|------------------|-------------------------------|
| `Runnable` / `Running` | Ready → 成功 |
| `New` / `Upgrading` | Wait → 继续轮询 |
| `Closed` | Error / RUNNER_CLOSED |
| `RobotError` | Error / ROBOT_ERROR |
| `ClientError` | Error / CLIENT_ERROR |
| `UpgradeFailed` | Error / UPGRADE_FAILED |
| `Offline` | Error / WORKER_OFFLINE |

---

## 四、完整数据流

```
云端后台
  │  GET /robot/client/task/findTaskByWorkerId/{workerId}
  │  → [ProfilePendingTaskResponse]（已按优先级排序）
  ↓
PoolSchedulerService
  │  FindCandidate → (VirtualMachineOptions, VmCurrentState, ProfileOptions)
  │  构造 VmSwitchRequest:
  │    HostId, Vm, FromProfileId, FromSnapshotName,
  │    TargetProfileId, TargetSnapshotName, FirstTaskId
  ↓
VmSwitchService
  ├─ GuestWorkerClient  → HTTP :9090  （status / kill）
  ├─ LogBackupService   → 主机文件系统备份
  ├─ VmrunService       → vmrun.exe stop / revertToSnapshot / start
  └─ GuestWorkerClient  → HTTP :9090  （轮询 status 直到 Runnable/Running）
  ↓
LocalStore（SQLite）
  ├─ local_switch_transaction  记录每步状态流转
  └─ local_vm_state            VM 级错误时置 ERROR
```

---

## 五、配置项速查

| 配置路径 | 说明 | 默认值 |
|---------|------|--------|
| `Agent.PollIntervalSeconds` | 调度轮询间隔 | 30s |
| `Agent.IdleStableSeconds` | VM 进入可切换状态所需最小空闲时长 | 需配置 |
| `Agent.WaitVmReadyTimeoutSeconds` | VM 启动后等待 Runner 就绪的最大时长 | 120s |
| `Agent.ForceRevertWhenBackupFailed` | 备份失败时是否强制继续切换 | `false` |
| `Agent.MaxSwitchesPerCycle` | 每个调度周期最多触发的切换次数 | 1 |
| `VirtualMachines[].Profiles[].SnapshotName` | 该 profile 对应的快照名称 | 需配置 |
