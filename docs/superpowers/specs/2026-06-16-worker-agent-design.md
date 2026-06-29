# Worker Agent 设计

日期：2026-06-16

## 目标

建设一个运行在 Windows 宿主机侧的 Worker Agent，用于协调本机 VMware Workstation VM 与 RPA 执行环境画像。Agent 不执行 RPA 任务，不启动 `runner.jar`，也不管理任务 lease。Agent 只负责选择空闲且兼容的 VM，将其切换到目标 profile 快照，等待 VM 内 runner 可被监控，并上报状态。

VM 内的 `runner.jar` 继续按原有机制自行从调度中心拉取任务。

## 确认范围

本期范围：

- 以 C#/.NET Windows Service 形式运行。
- 管理单台宿主机上的多台本地 VM。
- 仅使用 `vmrun.exe` 作为 VM 控制方式。
- 按 `profileId` 查询调度队列。
- 当某个 profile 存在待执行任务时，将一台空闲 VM 切换到目标 profile 快照。
- 允许同一个 `profileId` 同时运行在多台兼容 VM 上。
- VM 空闲后保持开机。
- 快照切换和日志备份前先停止 `runner.jar`。
- 快照回滚前备份 VM 日志。
- 使用 SQLite 持久化本地切换事务。
- 服务重启或 VM 操作失败后支持恢复或隔离。
- 暴露仅监听 localhost、使用 API Key 保护的本机运维 API。
- 上报 Agent、VM、worker、profile、snapshot 和 runner 状态。

不在本期范围：

- RPA 任务执行。
- 由 Agent 启动 `runner.jar`。
- lease 管理。
- 向 runner 直接分配任务。
- 根据任务数量计算目标容量。
- vSphere、ESXi、PowerCLI、动态 Clone 或跨宿主机调度。

## 命名模型

采用三层模型：

```text
profileId    = rpa-{city}-{business}-{system}
workerId     = {profileId}-{instance}
snapshotName = {profileId}.v{YYMMDD}.{No}
```

示例：

```text
profileId    = rpa-sh-tax-etax
workerId     = rpa-sh-tax-etax-001
snapshotName = rpa-sh-tax-etax.v260624.1
```

定义：

- `profileId` 表示任务所需的环境能力画像。
- `workerId` 表示一个 runner 实例，在宿主机内必须唯一。
- `snapshotName` 表示具体的 VM 环境版本。
- `version` 建议使用可排序格式，例如 `vYYYYMMDD.N`。

Agent 要求 `snapshotName` 必填并以 `profileId` 为前缀；切换快照时使用配置的版本化 `SnapshotName`。

## 配置模型

每台 VM 显式声明它支持的 profiles，以及每个 profile 对应的快照。

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
          "SnapshotName": "rpa-sh-tax-etax.v260624.1",
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

启动校验：

- `vmrun.exe` 存在。
- VMX 文件存在。
- `workerId` 在宿主机内唯一。
- `profileId` 符合命名规则。
- 每台 VM 内每个启用 profile 只能配置一个快照映射。
- 每个配置的 `profileId` 都能通过 `vmrun listSnapshots` 查询到同名快照。
- 日志备份根目录可写。
- 本机运维 API 默认绑定到 `127.0.0.1`，除非后续版本明确调整。

## 架构

`Seebot.WorkerAgent.Service` 是一个 .NET Windows Service，内部托管后台任务和 localhost ASP.NET Core 运维 API。

组件：

- `PoolSchedulerService`：按 `profileId` 轮询调度队列，并生成 VM 切换意图。
- `VmCoordinator`：每台 VM 一个协调器，每台 VM 持有一把异步锁。
- `VmSwitchService`：编排停止 runner、备份日志、关闭 VM、回滚快照、启动 VM 和 ready 检查。
- `VmrunService`：封装 `vmrun.exe`，记录输出、退出码、超时和耗时。
- `GuestWorkerClient`：调用 VM 内已有 HTTP API，读取 runner 健康、状态并停止 runner。
- `SchedulerClient`：查询 profile 待执行任务，并上报 Agent、VM、worker、profile 能力和切换状态。
- `LogBackupService`：复制或收集 VM 日志，并生成备份 manifest。
- `LocalStore`：使用 SQLite 持久化 VM 状态、切换事务、恢复标记和审计记录。
- `RecoveryService`：服务重启后继续安全事务步骤，或隔离异常 VM。
- `OperationsApi`：仅监听 localhost，并通过 `X-Agent-Api-Key` 鉴权。

不同 VM 可以并行操作。同一 VM 的自动调度、人工操作和恢复操作必须共用同一把 VM 锁。

## 调度规则

调度器使用优先级、等待时间、profile 兼容性和快照粘性进行决策。它不根据待执行任务数计算目标 VM 数量。

每轮调度：

1. 查询所有启用 `profileId` 的待执行状态。
2. 如果没有任何 profile 存在待执行任务，只监控并上报当前 VM 状态。
3. 根据调度优先级和队列等待时间选择下一个目标 profile。
4. 优先复用已经运行目标 profile 的 VM。
5. 如果目标 profile 仍有待执行任务，则选择一台兼容的空闲 VM。
6. 每轮最多启动一次 VM 快照切换。
7. 只要队列仍有待执行任务，同一个 profile 可在多轮调度后占用全部兼容 VM。
8. 不切换 `Running`、`Upgrading`、已隔离或存在事务中的 VM。

空闲切换候选 VM 必须同时满足：

- VM 支持目标 profile。
- VM 未被隔离。
- VM 没有活跃切换事务。
- runner 状态为 `Runnable`。
- VM 当前 profile 的队列为空。
- VM 已持续空闲至少 `IdleStableSeconds`。

如果候选 VM 在切换尝试期间变为忙碌，Agent 取消该 VM 的事务，并在后续轮次重试。

## 安全切换流程

本版本不实现 drain 协议。已有的 VM 内停止接口是最终并发闸门。

切换流程：

1. 获取 VM 锁。
2. 创建本地切换事务。
3. 重新读取 VM runner 状态。
4. 调用 VM 内停止接口关闭 `runner.jar`。
5. 如果停止接口报告 runner 已经开始任务或状态为 `Running`，取消本次事务，不切换该 VM。
6. 确认 runner 已停止且 `currentTaskId` 为空。
7. 将 VM 日志备份到宿主机。
8. 写入 `backup_manifest.json`。
9. 使用 `vmrun stop` 关闭 VM。
10. 使用 `vmrun revertToSnapshot` 回滚到目标快照。
11. 使用 `vmrun start` 启动 VM。
12. 等待网络和 VM 内 HTTP 健康接口可用。
13. 等待 runner 状态变为 `Runnable` 或 `Running`。
14. 校验上报的 `workerId` 和 `profileId` 与预期 VM/profile 匹配。
15. 上报成功并进入监控。

停止接口必须满足：

- 如果 runner 空闲，停止任务拉取并关闭 `runner.jar`。
- 如果 runner 正在执行任务，拒绝停止请求。
- Agent 不强制停止正在执行任务的 runner。

## Runner 状态处理

Agent 继续使用已有 runner 状态码：

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

切换决策：

- `Runnable`：如果当前 profile 队列为空且空闲状态稳定，可作为切换候选。
- `Running`：永不切换。
- `Upgrading`：永不切换。
- `New` 或 `Closed`：不作为调度器中的切换候选，可在 ready 等待阶段处理或上报未 ready。
- `RobotError`、`ClientError`、`UpgradeFailed`、`Offline`：上报异常，并按策略考虑隔离。

VM 启动后的 ready 判断：

- `Runnable`：ready。
- `Running`：ready，且说明已经开始消费任务。
- `New`：等待直到超时，随后上报 `RUNNER_NOT_READY`。
- `Closed`：等待直到超时，随后上报 `RUNNER_CLOSED`。
- `Upgrading`：等待直到升级超时，随后上报 `WORKER_UPGRADING_TIMEOUT`。
- 错误和离线状态按对应错误码立即上报。

## 本地状态

VM 状态字段：

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

切换事务状态：

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

切换事务字段：

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

## 恢复规则

服务重启后，`RecoveryService` 扫描未完成事务。

- `CREATED`：如果 runner 尚未停止，标记失败，并允许后续重新调度。
- `STOP_RUNNER_DONE`：尽可能继续日志备份。
- `LOG_BACKUP_DONE`：继续关闭 VM。
- `VM_STOP_DONE`：继续快照回滚。
- `SNAPSHOT_REVERT_DONE`：继续启动 VM。
- `VM_START_DONE`：继续等待 ready。
- `WORKER_READY_DONE`：补报状态并标记成功。
- `NEED_MANUAL_CHECK`：不自动恢复。

失败处理：

- 停止 runner 失败或 runner 变为 `Running`：取消本次切换，后续再寻找其他 VM。
- 日志备份失败：阻断快照回滚，除非 `ForceRevertWhenBackupFailed=true`。
- VM stop、revert 或 start 失败：隔离该 VM。
- VM 启动后出现非预期 `workerId` 或 `profileId`：上报 `WORKER_PROFILE_MISMATCH` 并隔离该 VM。
- 允许通过运维 API 人工解除隔离，但必须写入审计记录。

## 云后台可观测模型

云后台与调度中心属于同一个后台系统。首版展示目标是“静态能力 + 当前运行状态”，不为每个未运行快照维护复杂生命周期。

云后台应能看到：

- 一台宿主机下有多少台 VM。
- 每台 VM 的 `workerId`、VMX 路径、是否启用、是否隔离。
- 每台 VM 支持哪些 `profileId/snapshotName`，`snapshotName` 使用版本化格式。
- 每个配置快照是否通过 Agent 启动校验。
- 每台 VM 当前运行的 `currentProfileId/currentSnapshotName`，两者在 profile 快照场景中同值。
- 当前 runner 状态、当前任务、最后心跳时间和最后切换时间。

能力上报：

- Agent 启动完成配置校验后上报一次。
- 配置变化或人工刷新时上报一次。
- 可配置低频周期上报，用于后台修复丢失数据。
- 能力上报只描述“这台 VM 能运行什么”，不表示这些 profile 当前正在运行。

能力上报示例：

```json
{
  "hostId": "HOST-SR20-001",
  "agentName": "SR20 Host Worker Agent",
  "reportedAt": "2026-06-16 10:00:00",
  "vms": [
    {
      "vmName": "VM-RPA-001",
      "workerId": "rpa-sh-tax-etax-001",
      "vmxPath": "D:\\VMs\\VM-RPA-001\\VM-RPA-001.vmx",
      "enabled": true,
      "profiles": [
        {
          "profileId": "rpa-sh-tax-etax",
          "snapshotName": "rpa-sh-tax-etax.v260624.1",
          "city": "sh",
          "business": "tax",
          "system": "etax",
          "enabled": true,
          "snapshotExists": true,
          "validationStatus": "READY"
        }
      ]
    }
  ]
}
```

运行状态上报：

- Agent 心跳时上报。
- VM 状态、runner 状态、隔离状态或切换事务状态变化时立即上报。
- 运行状态只描述“这台 VM 当前正在做什么”。

运行状态上报示例：

```json
{
  "hostId": "HOST-SR20-001",
  "vmName": "VM-RPA-001",
  "workerId": "rpa-sh-tax-etax-001",
  "currentProfileId": "rpa-sh-tax-etax",
  "currentSnapshotName": "rpa-sh-tax-etax.v260624.1",
  "agentVmStatus": "MONITORING",
  "runnerStatusCode": 1,
  "runnerStatusName": "Runnable",
  "currentTaskId": null,
  "isQuarantined": false,
  "lastSwitchAt": "2026-06-16 09:50:00",
  "lastHeartbeatTime": "2026-06-16 10:00:00"
}
```

建议服务端模型：

- `rpa_host_agent`：宿主机 Agent 心跳和版本信息。
- `rpa_vm_instance`：每台 VM 的静态信息、当前 profile、当前快照和运行状态。
- `rpa_vm_profile_capability`：每台 VM 支持的 profile/snapshot 能力清单。
- `rpa_worker_switch_log`：快照切换事务记录。

后台页面建议：

- 宿主机列表：展示 Agent 在线状态、VM 数量、异常 VM 数量。
- 宿主机详情：按 VM 展示当前 profile、runner 状态、任务、隔离状态和最后心跳。
- VM 详情：展示该 VM 支持的所有 profile/snapshot，以及当前正在运行的 profile。

## 调度中心 API

待执行任务按 `profileId` 查询，而不是按 `workerId` 查询。

```http
GET /api/rpa/profile-task/pending?profileId=rpa-sh-tax-etax
```

响应示例：

```json
{
  "hasTask": true,
  "profileId": "rpa-sh-tax-etax",
  "pendingCount": 100,
  "firstTaskId": 123456,
  "priority": 5,
  "oldestQueuedAt": "2026-06-16 09:30:00"
}
```

运行状态上报同时包含实例标识和画像标识：

```json
{
  "hostId": "HOST-SR20-001",
  "vmName": "VM-RPA-001",
  "workerId": "rpa-sh-tax-etax-001",
  "profileId": "rpa-sh-tax-etax",
  "snapshotName": "rpa-sh-tax-etax.v260624.1",
  "agentVmStatus": "MONITORING",
  "runnerStatusCode": 1,
  "runnerStatusName": "Runnable",
  "currentTaskId": null,
  "lastHeartbeatTime": "2026-06-16 10:00:00"
}
```

## 本机运维 API

API 监听 `127.0.0.1`，并要求请求携带 `X-Agent-Api-Key`。

首版接口：

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

人工 `switch-profile` 使用与自动调度相同的安全切换流程。

## 验收标准

- Agent 可作为 Windows Service 运行。
- Agent 可加载并校验多 VM 配置。
- 每台 VM 可声明不同的 profile 支持集合。
- Agent 可使用 `vmrun listSnapshots` 校验配置快照。
- Agent 按 `profileId` 查询队列。
- 当某个 profile 有待执行任务时，Agent 可将一台兼容空闲 VM 切换到该 profile 快照。
- 多轮调度后，同一个 profile 可在任务未清空期间占用多台兼容 VM。
- Agent 不根据任务数计算目标容量。
- `Running` 和 `Upgrading` runner 永不被切换。
- Agent 在日志备份和快照回滚前成功停止 `runner.jar`。
- 如果 runner 在停止尝试期间开始任务，Agent 会取消本次切换。
- 快照回滚前完成日志备份并写入 manifest。
- 切换事务本地持久化，并可在服务重启后恢复。
- VM 控制失败会隔离 VM。
- 状态上报包含 `hostId`、`vmName`、`workerId`、`profileId`、`snapshotName` 和 runner 状态。
- 云后台可看到每台宿主机下的 VM 清单、每台 VM 支持的 profile/snapshot 能力，以及 VM 当前运行状态。
- localhost 运维 API 可查看状态、暂停/恢复调度、隔离/解除隔离 VM，并可人工触发安全 profile 切换。
