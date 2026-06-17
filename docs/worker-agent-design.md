# Seebot RPA Worker Agent 最终设计

版本：V1.0-final

日期：2026-06-17

## 1. 文档定位

本文是 `docs/RPA Worker Agent开发文档.md` 与 `docs/superpowers/specs/2026-06-16-worker-agent-design.md` 的整合最终版。

最终版以已经确认的新模型为准：

- 调度维度使用 `profileId`，不再按 `workerId` 查询任务队列。
- `workerId` 表示 VM 内 runner 实例身份。
- `snapshotName` 表示可回滚的 VM 环境版本。
- 一台宿主机可管理多台 VM。
- 一台 VM 可配置多个 profile 快照。
- 云后台展示“静态能力 + 当前运行状态”。

旧文档中的“`workerId` 与 `snapshotName` 同名”“按 `workerId` 查询队列”仅作为历史设计背景，不作为最终实现依据。

## 2. 建设目标

建设运行在 Windows 宿主机上的 `Seebot.WorkerAgent.Service`，用于协调本机 VMware Workstation VM 与 RPA 执行环境画像。

Agent 负责：

- 管理单台宿主机上的多台 VM。
- 维护 VM 与 `workerId`、`profileId`、`snapshotName` 的关系。
- 按 `profileId` 查询云调度队列。
- 在某个 profile 存在待执行任务时，选择兼容且空闲的 VM。
- 切换前停止 VM 内 `runner.jar`。
- 切换前备份 VM 日志到宿主机。
- 使用 `vmrun.exe` 完成 VM 关机、快照回滚、启动。
- 等待 VM 内 `rpa-client` / `rpa-runner` 自启动并进入可监控状态。
- 监控 runner 原有 0-8 状态。
- 上报 Agent、VM、worker、profile、snapshot、runner、切换事务和日志备份状态。
- 支持本地事务持久化、服务重启恢复、异常隔离和人工处理。
- 提供仅监听 localhost 的本机运维 API。
- 向云后台上报 VM 静态能力与当前运行状态。

Agent 不负责：

- 执行 RPA 业务。
- 启动 `runner.jar`。
- 生成 runner 启动参数。
- 接管 runner 任务拉取。
- 向 runner 直接下发任务。
- 管理任务 lease。
- 根据任务数量计算目标容量。
- 跨宿主机调度。
- vSphere、ESXi、PowerCLI 或动态 Clone。
- 完整 UKey 锁中心、健康评分体系或生产报表。

## 3. 核心原则

### 3.1 Agent 运行在宿主机

Agent 必须运行在 Windows 宿主机上，不能运行在 VM 内。

原因：

- VM 回滚会导致 VM 内状态丢失。
- VM 内 Agent 自己也会被回滚。
- 快照切换事务、日志备份记录和错误记录必须保存在 VM 外。
- 宿主机 Agent 才能稳定控制 VM 生命周期。

### 3.2 Agent 只管 VM，不管业务执行

Agent 不写业务 RPA 逻辑。业务任务仍由 VM 内 `rpa-client` / `rpa-runner` 执行。

### 3.3 Agent 不启动 runner

VM 快照启动后，VM 内部通过以下任一方式自动启动 `rpa-client` 和 `rpa-runner`：

- Windows 开机启动项。
- Windows Service。
- Windows 计划任务。
- 原有 rpa-client 自启动机制。

Agent 只等待和监控状态，不执行 runner 启动命令。

### 3.4 本版本只使用 vmrun

所有 VM 控制均通过 `vmrun.exe` 实现：

- `listSnapshots`
- `stop`
- `revertToSnapshot`
- `start`
- `copyFileFromGuestToHost`

### 3.5 不做 lease

本版本默认任务消费幂等与并发领取由调度中心和 VM 内 runner 保证。Agent 只查询 profile 是否有待执行任务，不锁定具体任务。

### 3.6 切换前必须停止 runner 并备份日志

快照回滚会清除 VM 内部变更。为避免日志、截图、执行现场丢失，快照切换前必须：

1. 调用 VM 内停止接口停止 `runner.jar`。
2. 若 runner 已经变为 `Running`，停止接口必须拒绝，Agent 取消本次切换。
3. 确认 `runner.jar` 已停止且 `currentTaskId` 为空。
4. 复制 VM 日志到宿主机。
5. 写入 `backup_manifest.json`。
6. 再执行 VM 关机与快照回滚。

## 4. 命名模型

采用三层模型：

```text
profileId    = rpa-{city}-{business}-{system}
workerId     = {profileId}-{instance}
snapshotName = {profileId}-{version}
```

示例：

```text
profileId    = rpa-sh-tax-etax
workerId     = rpa-sh-tax-etax-001
snapshotName = rpa-sh-tax-etax-v20260615.1
```

定义：

- `profileId`：任务所需的环境能力画像，例如城市、业务、系统组合。
- `workerId`：VM 内 runner 实例身份，在宿主机内必须唯一。
- `snapshotName`：具体的 VM 环境版本。
- `version`：建议使用 `vYYYYMMDD.N`，便于排序、回滚和审计。

Agent 不要求 `workerId` 与 `snapshotName` 相同。

## 4.1 VM 镜像准备与基础快照命名

在 Agent 启动之前，必须先完成 VM 镜像准备。Agent 不负责创建 VM、安装系统、安装客户端、制作快照；Agent 只校验这些前置资源是否存在，并在校验通过后进入调度。

每一台 VM 必须有一个“纯净基础快照”，用于承载该 VM 的干净初始环境。纯净基础快照要求：

- 快照名必须与 VM 名相同。
- VM 名和纯净基础快照名建议遵循 `SR20-YYMM-XXXX` 命名规则。
- 示例：VM 名为 `SR20-2026-6HQ8` 时，纯净基础快照名也必须为 `SR20-2026-6HQ8`。
- 纯净基础快照中只包含操作系统、基础驱动、VMware Tools、通用安全配置和必要运行时。
- 纯净基础快照不应包含城市、业务系统、CA、UKey、登录态、浏览器缓存、下载目录残留或任务执行现场。
- 纯净基础快照一旦作为基线使用，不应直接修改；需要升级基础镜像时，应重新制作新 VM 或新基础快照，并经过人工确认。

每台 VM 下所有定制 profile 快照都必须基于该 VM 的纯净基础快照制作。

```text
VM: SR20-2026-6HQ8
  |
  |-- Base Snapshot: SR20-2026-6HQ8
          |
          |-- Custom Snapshot: rpa-sh-tax-etax-v20260615.1
          |-- Custom Snapshot: rpa-sh-social-portal-v20260615.1
          |-- Custom Snapshot: rpa-bj-tax-etax-v20260615.1
```

定制 profile 快照要求：

- 每个定制快照对应一个 `profileId` 的可运行环境版本。
- 定制快照命名仍使用 `snapshotName = {profileId}-{version}`。
- 定制快照必须从纯净基础快照派生，不允许从另一个业务定制快照继续派生，避免环境污染层层传递。
- 定制快照内可以包含该 profile 所需的城市配置、业务系统配置、CA/UKey 驱动、浏览器配置、客户端配置和 runner 自启动配置。
- 定制快照制作完成后，应启动验证 `rpa-client` / `rpa-runner` 能自动启动，并能正确上报 `workerId/profileId`。

Agent 启动校验只校验：

- 配置的 `BaseSnapshotName` 存在。
- `BaseSnapshotName` 与 `VmName` 一致。
- 配置的定制 `snapshotName` 存在。
- 定制快照的来源关系已由镜像准备流程保证；如果当前 VMware / `vmrun` 无法可靠读取快照父子关系，Agent 只记录配置声明和校验结果，不强行推断快照树。

## 5. 总体架构

```text
Seebot 云后台 / 调度中心
    |
    |-- 按 profileId 查询待执行任务
    |-- 接收 Agent 心跳
    |-- 接收 VM 静态能力
    |-- 接收 VM / worker / profile / runner 当前状态
    |-- 接收快照切换记录
    |-- 接收日志备份结果
    |
    v
Windows 宿主机
    |
    |-- Seebot.WorkerAgent.Service
    |       |
    |       |-- profile / snapshot 能力管理
    |       |-- 调度中心任务查询
    |       |-- VM 空闲判断
    |       |-- runner 停止
    |       |-- 日志备份
    |       |-- vmrun stop
    |       |-- vmrun revertToSnapshot
    |       |-- vmrun start
    |       |-- VM ready 监控
    |       |-- runner 状态监控
    |       |-- 云后台状态上报
    |       |-- 本地事务恢复
    |       |-- localhost 运维 API
    |
    |-- VMware Workstation
            |
            |-- SR20-2026-6HQ8
            |     |-- Snapshot: rpa-sh-tax-etax-v20260615.1
            |     |-- Snapshot: rpa-sh-social-portal-v20260615.1
            |     |-- rpa-client 自动启动
            |     |-- rpa-runner 自动启动
            |
            |-- SR20-2026-7JK9
                  |-- Snapshot: rpa-sh-tax-etax-v20260615.1
                  |-- Snapshot: rpa-bj-tax-etax-v20260615.1
                  |-- rpa-client 自动启动
                  |-- rpa-runner 自动启动
```

## 6. 技术选型

| 模块 | 技术选型 | 说明 |
|---|---|---|
| 主服务 | C# / .NET Worker Service | 适合 Windows 宿主机常驻服务 |
| 运行形态 | Windows Service | 开机自启 |
| 本机 API | ASP.NET Core Minimal API / Controller | 仅监听 `127.0.0.1` |
| VM 控制 | `vmrun.exe` | 本版本唯一 VM 控制方式 |
| 本地状态 | SQLite | 保存 VM 状态、事务、恢复记录、审计 |
| 服务端状态 | MySQL | 由 Seebot 云后台保存 |
| 日志 | Serilog 或 NLog | 结构化日志 |
| 配置 | `appsettings.json` | 管理 VM、profile、快照和接口地址 |
| 调度通信 | HTTP REST + JSON | 查询队列和上报状态 |
| VM 内辅助 | 已有 executor-control HTTP 服务 | 停止 runner、查看状态、flush 日志 |
| runner | VM 内自启动 | Agent 不启动 runner |

## 7. 工程结构

```text
Seebot.WorkerAgent.sln
  |
  |-- Seebot.WorkerAgent.Service
  |     |-- Windows Service 入口
  |     |-- ASP.NET Core localhost 运维 API
  |
  |-- Seebot.WorkerAgent.Core
  |     |-- PoolSchedulerService
  |     |-- VmCoordinator
  |     |-- VmSwitchService
  |     |-- VmrunService
  |     |-- GuestWorkerClient
  |     |-- SchedulerClient
  |     |-- CapabilityReporter
  |     |-- StateReporter
  |     |-- LogBackupService
  |     |-- LocalStore
  |     |-- RecoveryService
  |     |-- WorkerStateEvaluator
  |
  |-- Seebot.WorkerAgent.Tests
```

核心模块职责：

| 模块 | 职责 |
|---|---|
| `PoolSchedulerService` | 按 `profileId` 轮询调度队列，选择空闲 VM 并生成切换意图 |
| `VmCoordinator` | 每台 VM 一个协调器，持有 VM 独占锁 |
| `VmSwitchService` | 编排停止 runner、备份日志、关机、回滚、开机、ready 检查 |
| `VmrunService` | 封装 `vmrun.exe` 命令 |
| `GuestWorkerClient` | 调用 VM 内 executor-control / worker status 接口 |
| `SchedulerClient` | 查询调度中心任务，上报状态、事务和备份结果 |
| `CapabilityReporter` | 上报宿主机、VM、profile/snapshot 静态能力 |
| `StateReporter` | 上报 Agent、VM、worker、profile、runner 当前状态 |
| `LogBackupService` | 复制 VM 日志到宿主机并生成 manifest |
| `LocalStore` | SQLite 本地状态持久化 |
| `RecoveryService` | Agent 重启后的事务恢复 |
| `WorkerStateEvaluator` | 统一判断 runner 0-8 状态和切换许可 |

不同 VM 可以并行操作。同一 VM 的自动调度、人工操作和恢复操作必须共用同一把 VM 锁。

## 8. 配置设计

### 8.1 appsettings.json 示例

```json
{
  "Agent": {
    "HostId": "HOST-SR20-001",
    "AgentName": "SR20宿主机Agent",
    "PollIntervalSeconds": 10,
    "HeartbeatIntervalSeconds": 15,
    "CapabilityReportIntervalSeconds": 300,
    "SwitchTimeoutSeconds": 300,
    "WaitVmReadyTimeoutSeconds": 180,
    "WaitUpgradeTimeoutSeconds": 600,
    "IdleStableSeconds": 30,
    "ForceRevertWhenBackupFailed": false,
    "AllowRevertWhenRunnerError": true,
    "MaxSwitchesPerCycle": 1
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
    "VmrunPath": "C:\\Program Files (x86)\\VMware\\VMware Workstation\\vmrun.exe",
    "DefaultStartNoGui": true,
    "StopSoftTimeoutSeconds": 60,
    "AllowHardStopAfterSoftTimeout": true
  },
  "VirtualMachines": [
    {
      "VmName": "SR20-2026-6HQ8",
      "VmxPath": "D:\\VMs\\SR20-2026-6HQ8\\SR20-2026-6HQ8.vmx",
      "BaseSnapshotName": "SR20-2026-6HQ8",
      "GuestIp": "192.168.100.101",
      "GuestUser": "Administrator",
      "GuestPasswordSecret": "encrypted-password",
      "WorkerId": "rpa-sh-tax-etax-001",
      "ExecutorStopUrl": "http://192.168.100.101:18080/executor/stop",
      "ExecutorHealthUrl": "http://192.168.100.101:18080/executor/health",
      "WorkerStatusUrl": "http://192.168.100.101:18080/worker/status",
      "GuestLogPath": "C:\\seebot\\logs",
      "HostBackupRoot": "D:\\seebot-vm-log-backup",
      "Enabled": true,
      "Profiles": [
        {
          "ProfileId": "rpa-sh-tax-etax",
          "SnapshotName": "rpa-sh-tax-etax-v20260615.1",
          "City": "sh",
          "Business": "tax",
          "System": "etax",
          "Enabled": true
        },
        {
          "ProfileId": "rpa-sh-social-portal",
          "SnapshotName": "rpa-sh-social-portal-v20260615.1",
          "City": "sh",
          "Business": "social",
          "System": "portal",
          "Enabled": true
        }
      ]
    }
  ]
}
```

### 8.2 配置校验

Agent 启动时必须校验：

1. `vmrun.exe` 是否存在。
2. VMX 文件是否存在。
3. `workerId` 在宿主机内是否唯一。
4. `profileId` 是否符合命名规则。
5. 同一 VM 内每个启用 `profileId` 是否只有一个快照映射。
6. `BaseSnapshotName` 是否配置。
7. `BaseSnapshotName` 是否与 `VmName` 一致。
8. `vmrun listSnapshots` 是否能查询到纯净基础快照。
9. `snapshotName` 是否配置。
10. `vmrun listSnapshots` 是否能查询到配置的定制快照。
11. 宿主机日志备份目录是否可写。
12. 调度中心是否可访问。
13. VM 内状态接口是否可访问。
14. guest 账号密码是否可用于复制日志。
15. `ForceRevertWhenBackupFailed` 是否明确配置。
16. 本机运维 API 是否绑定到 `127.0.0.1`。

## 9. 状态模型

### 9.1 Agent 状态

```text
STARTING       启动中
RUNNING        正常运行
PAUSED         人工暂停调度
ERROR          异常
STOPPING       停止中
```

### 9.2 VM 状态

```text
UNKNOWN        未知
POWERED_OFF    已关机
POWERED_ON     已开机
STOPPING       关机中
REVERTING      快照回滚中
STARTING       启动中
WAIT_READY     等待 VM / runner 可用
MONITORING     监控中
ERROR          异常
QUARANTINED    隔离
```

### 9.3 Agent 侧 worker/profile 状态

Agent 侧状态描述宿主机 Agent 对当前 VM、worker、profile 的管理状态，不替代 runner 原有状态。

```text
DISABLED       禁用
READY          快照存在，可用
IDLE           Agent 判断当前可切换或可监控
HAS_PENDING    调度中心存在待执行任务
PRE_SWITCH     切换前准备
STOPPING       停止 runner 中
BACKUPPING     日志备份中
POWERING_OFF   VM 关机中
REVERTING      快照回滚中
POWERING_ON    VM 启动中
WAIT_READY     等待 VM / runner 可用
MONITORING     监控 runner 状态中
ERROR          异常
QUARANTINED    隔离
```

### 9.4 runner 原有状态

Agent 必须兼容并直接使用原有 runner 0-8 状态：

| 状态码 | 状态名称 | 状态说明 |
|---:|---|---|
| 0 | New | 初始化状态、用户未登录 |
| 1 | Runnable | 机器人已启动，准备就绪 |
| 2 | Running | 机器人正在执行任务中 |
| 3 | Closed | 关闭 |
| 4 | RobotError | 机器人程序内部异常 |
| 5 | ClientError | 客户端 rpa-client 内部异常 |
| 6 | Upgrading | 执行器正在升级 |
| 7 | UpgradeFailed | 执行器升级失败 |
| 8 | Offline | 离线 |

### 9.5 runner 状态处理规则

| runner 状态码 | 状态名称 | 是否执行中 | 切换快照前处理 | VM 启动后处理 |
|---:|---|---|---|---|
| 0 | New | 否 | 不作为自动切换候选 | 不算 ready，继续等待变为 Runnable 或 Running |
| 1 | Runnable | 否 | 可作为空闲候选，但必须先停止 runner | 视为 ready |
| 2 | Running | 是 | 禁止切换 | 视为 runner 已开始执行 |
| 3 | Closed | 否 | 不作为自动切换候选 | 不算 ready，继续等待或报错 |
| 4 | RobotError | 否 | 记录异常，按配置决定是否隔离 | 启动后出现则标记 worker 异常 |
| 5 | ClientError | 否 | 记录异常，按配置决定是否隔离 | 启动后出现则标记 worker 异常 |
| 6 | Upgrading | 特殊状态 | 禁止切换，避免打断升级 | 继续等待升级完成 |
| 7 | UpgradeFailed | 否 | 记录异常，按配置决定是否隔离 | 启动后出现则标记 worker 异常 |
| 8 | Offline | 不可确认 | 走异常处理 | 启动后仍 Offline 则 VM ready 失败 |

核心判断原则：

- `runnerStatusCode = 2 Running` 时，禁止快照切换。
- `runnerStatusCode = 6 Upgrading` 时，禁止快照切换。
- `runnerStatusCode = 1 Runnable` 时，表示 VM 内机器人已准备就绪。
- VM 启动后如果状态变为 `2 Running`，也视为自运行成功。
- VM 启动后如果长期停留在 `0 New`，判定 `RUNNER_NOT_READY`。
- VM 启动后如果长期停留在 `3 Closed`，判定 `RUNNER_CLOSED`。
- `4 RobotError`、`5 ClientError`、`7 UpgradeFailed`、`8 Offline` 需要上报异常。

## 10. 调度规则

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

## 11. 主流程

### 11.1 Agent 启动流程

```text
0. 人工完成 VM 镜像准备
   - 创建 VM
   - 制作与 VM 同名的纯净基础快照
   - 基于纯净基础快照制作所有 profile 定制快照
   - 验证定制快照内 rpa-client / rpa-runner 可自启动
1. 加载配置
2. 初始化日志
3. 初始化 SQLite
4. 校验 vmrun.exe
5. 校验 VMX 文件
6. 查询并校验 VM 纯净基础快照
7. 查询并校验 VM 定制 profile 快照
8. 校验 workerId / profileId / snapshotName 映射
9. 上报 VM 静态能力
10. 恢复上次未完成切换事务
11. 上报 Agent 心跳
12. 进入 profile 调度轮询
```

### 11.2 profile 调度轮询

```text
1. 遍历所有启用 profileId
2. 调用调度中心查询该 profileId 是否有待执行任务
3. 如果无任务，继续监控当前 VM 状态
4. 如果有任务，选择支持该 profile 的 VM
5. 优先复用已处于目标 profile 的 VM
6. 如果需要切换，选择空闲兼容 VM
7. 获取 VM 独占锁
8. 执行安全切换流程
9. 上报切换结果与 VM 当前状态
```

### 11.3 安全切换流程

```text
1. 获取 VM 锁
2. 创建 switch_transaction
3. 重新读取 VM runner 状态
4. 调用 VM 内 executor-control 停止 runner.jar
5. 如果 runner 已经 Running，停止接口拒绝，Agent 取消本次切换
6. 确认 runner 已停止且 currentTaskId 为空
7. 从 VM 复制日志到宿主机
8. 生成 backup_manifest.json
9. 使用 vmrun stop 关闭 VM
10. 使用 vmrun revertToSnapshot 回滚到目标快照
11. 使用 vmrun start 启动 VM
12. 等待 VM 网络可用
13. 等待 VM 内 executor-control / rpa-client / rpa-runner 自启动
14. 读取 runner 状态
15. runner = Runnable / Running 时判定 ready
16. 校验 VM 内 workerId / profileId 与预期一致
17. 上报切换完成
18. 进入持续监控
```

注意：

```text
Agent 不执行 runner 启动命令。
Agent 不向 runner 下发 taskId。
Agent 只等待 VM 内 runner 自启动并进入可监控状态。
```

### 11.4 VM 启动后监控流程

```text
1. vmrun start VM
2. 等待 VM 网络可用
3. 调用 ExecutorHealthUrl
4. 读取 runnerStatusCode
5. 如果 runnerStatusCode = 1 Runnable：
      判定 VM / runner ready
      上报 worker ready
      进入 MONITORING

6. 如果 runnerStatusCode = 2 Running：
      判定 VM / runner 已经自启动并开始执行任务
      上报 worker running
      进入 MONITORING

7. 如果 runnerStatusCode = 0 New：
      继续等待
      超过 WaitVmReadyTimeoutSeconds 后判定 RUNNER_NOT_READY

8. 如果 runnerStatusCode = 3 Closed：
      继续等待或检查 rpa-client
      超时后判定 RUNNER_CLOSED

9. 如果 runnerStatusCode = 4 RobotError：
      判定 ROBOT_ERROR
      标记 worker 异常

10. 如果 runnerStatusCode = 5 ClientError：
      判定 CLIENT_ERROR
      标记 worker 异常

11. 如果 runnerStatusCode = 6 Upgrading：
      等待升级完成
      超时后判定 WORKER_UPGRADING_TIMEOUT

12. 如果 runnerStatusCode = 7 UpgradeFailed：
      判定 UPGRADE_FAILED
      标记 worker 异常

13. 如果 runnerStatusCode = 8 Offline：
      判定 WORKER_OFFLINE
      标记 VM ready 失败
```

## 12. vmrun 控制设计

### 12.1 查询快照

```bat
vmrun listSnapshots "D:\VMs\SR20-2026-6HQ8\SR20-2026-6HQ8.vmx"
```

### 12.2 停止 VM

优先软关机：

```bat
vmrun stop "D:\VMs\SR20-2026-6HQ8\SR20-2026-6HQ8.vmx" soft
```

软关机失败后，按配置允许时使用强制关机：

```bat
vmrun stop "D:\VMs\SR20-2026-6HQ8\SR20-2026-6HQ8.vmx" hard
```

### 12.3 回滚快照

```bat
vmrun revertToSnapshot "D:\VMs\SR20-2026-6HQ8\SR20-2026-6HQ8.vmx" "rpa-sh-tax-etax-v20260615.1"
```

### 12.4 启动 VM

```bat
vmrun start "D:\VMs\SR20-2026-6HQ8\SR20-2026-6HQ8.vmx" nogui
```

### 12.5 从 VM 复制日志到宿主机

```bat
vmrun -gu Administrator -gp "password" copyFileFromGuestToHost ^
  "D:\VMs\SR20-2026-6HQ8\SR20-2026-6HQ8.vmx" ^
  "C:\seebot\logs\runner.log" ^
  "D:\seebot-vm-log-backup\HOST-SR20-001\SR20-2026-6HQ8\runner.log"
```

### 12.6 vmrun 封装接口

```csharp
public interface IVmrunService
{
    Task<IReadOnlyList<string>> ListSnapshotsAsync(string vmxPath, CancellationToken ct);

    Task StopVmAsync(string vmxPath, VmStopMode mode, CancellationToken ct);

    Task RevertToSnapshotAsync(string vmxPath, string snapshotName, CancellationToken ct);

    Task StartVmAsync(string vmxPath, bool noGui, CancellationToken ct);

    Task CopyFileFromGuestToHostAsync(
        string vmxPath,
        string guestUser,
        string guestPassword,
        string guestPath,
        string hostPath,
        CancellationToken ct);
}
```

## 13. VM 内 executor-control 接口

本项目不新建 VM 内服务，默认 VM 内已有 HTTP 控制接口。

### 13.1 健康检查接口

```http
GET /executor/health
```

响应示例：

```json
{
  "success": true,
  "workerId": "rpa-sh-tax-etax-001",
  "profileId": "rpa-sh-tax-etax",
  "runnerStatusCode": 1,
  "runnerStatusName": "Runnable",
  "runnerStatusDesc": "机器人已启动，准备就绪",
  "currentTaskId": null,
  "executionCode": null,
  "javaProcessCount": 1,
  "pythonProcessCount": 0,
  "chromeProcessCount": 0,
  "diskFreeGb": 45,
  "timestamp": "2026-06-17 10:00:00"
}
```

### 13.2 runner 状态接口

```http
GET /worker/status
```

响应示例：

```json
{
  "success": true,
  "workerId": "rpa-sh-tax-etax-001",
  "profileId": "rpa-sh-tax-etax",
  "runnerStatusCode": 2,
  "runnerStatusName": "Running",
  "runnerStatusDesc": "机器人正在执行任务中",
  "currentTaskId": 123456,
  "executionCode": "EXE202606170001",
  "lastHeartbeatTime": "2026-06-17 10:00:00"
}
```

### 13.3 停止 runner 接口

```http
POST /executor/stop
```

请求：

```json
{
  "reason": "SNAPSHOT_SWITCH",
  "txId": "SWITCH-20260617-0001",
  "deadlineSeconds": 30
}
```

成功响应：

```json
{
  "success": true,
  "beforeRunnerStatusCode": 1,
  "beforeRunnerStatusName": "Runnable",
  "afterRunnerStatusCode": 3,
  "afterRunnerStatusName": "Closed",
  "currentTaskId": null,
  "logFlushed": true
}
```

拒绝响应示例：

```json
{
  "success": false,
  "errorCode": "WORKER_RUNNING",
  "beforeRunnerStatusCode": 2,
  "beforeRunnerStatusName": "Running",
  "currentTaskId": 123456,
  "message": "runner is executing task"
}
```

停止接口必须满足：

- 如果 runner 空闲，停止任务拉取并关闭 `runner.jar`。
- 如果 runner 正在执行任务，拒绝停止请求。
- 默认不允许强制停止正在执行任务的 runner。
- 返回状态必须使用原有 0-8 runner 状态码。

## 14. 调度中心与云后台接口

### 14.1 查询 profile 待执行任务

```http
GET /api/rpa/profile-task/pending?profileId=rpa-sh-tax-etax
```

响应：

```json
{
  "hasTask": true,
  "profileId": "rpa-sh-tax-etax",
  "pendingCount": 100,
  "firstTaskId": 123456,
  "executionCode": "EXE202606170001",
  "priority": 5,
  "oldestQueuedAt": "2026-06-17 09:30:00"
}
```

说明：

- 本版本不申请 lease。
- `firstTaskId` 仅用于 Agent 日志记录和辅助展示。
- 实际任务拉取仍由 VM 内 `rpa-runner` 完成。
- 调度中心需保证同一 profile 下多 runner 并发领取任务不会重复消费。

### 14.2 上报 Agent 心跳

```http
POST /api/rpa/host-agent/heartbeat
```

请求：

```json
{
  "hostId": "HOST-SR20-001",
  "agentName": "SR20宿主机Agent",
  "status": "RUNNING",
  "vmCount": 5,
  "quarantinedVmCount": 1,
  "version": "1.0.0",
  "timestamp": "2026-06-17 10:00:00"
}
```

### 14.3 上报 VM profile 能力

云后台与调度中心属于同一个后台系统。首版展示目标是“静态能力 + 当前运行状态”，不为每个未运行快照维护复杂生命周期。

能力上报描述“这台 VM 能运行什么”，不表示这些 profile 当前正在运行。

```http
POST /api/rpa/host-agent/capabilities
```

请求：

```json
{
  "hostId": "HOST-SR20-001",
  "agentName": "SR20宿主机Agent",
  "reportedAt": "2026-06-17 10:00:00",
  "vms": [
    {
      "vmName": "SR20-2026-6HQ8",
      "workerId": "rpa-sh-tax-etax-001",
      "vmxPath": "D:\\VMs\\SR20-2026-6HQ8\\SR20-2026-6HQ8.vmx",
      "baseSnapshotName": "SR20-2026-6HQ8",
      "enabled": true,
      "isQuarantined": false,
      "profiles": [
        {
          "profileId": "rpa-sh-tax-etax",
          "snapshotName": "rpa-sh-tax-etax-v20260615.1",
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

触发时机：

- Agent 启动完成配置校验后上报一次。
- 配置变化或人工刷新时上报一次。
- 按 `CapabilityReportIntervalSeconds` 低频周期上报。

### 14.4 上报 VM 当前运行状态

运行状态描述“这台 VM 当前正在做什么”。

```http
POST /api/rpa/vm/status
```

请求：

```json
{
  "hostId": "HOST-SR20-001",
  "vmName": "SR20-2026-6HQ8",
  "workerId": "rpa-sh-tax-etax-001",
  "currentProfileId": "rpa-sh-tax-etax",
  "currentSnapshotName": "rpa-sh-tax-etax-v20260615.1",
  "agentVmStatus": "MONITORING",
  "runnerStatusCode": 1,
  "runnerStatusName": "Runnable",
  "runnerStatusDesc": "机器人已启动，准备就绪",
  "currentTaskId": null,
  "executionCode": null,
  "isQuarantined": false,
  "lastSwitchAt": "2026-06-17 09:50:00",
  "lastHeartbeatTime": "2026-06-17 10:00:00"
}
```

触发时机：

- Agent 心跳时上报。
- VM 状态变化时立即上报。
- runner 状态变化时立即上报。
- 隔离状态变化时立即上报。
- 切换事务状态变化时立即上报。

### 14.5 上报快照切换记录

```http
POST /api/rpa/worker/switch-log
```

请求：

```json
{
  "txId": "SWITCH-20260617-0001",
  "hostId": "HOST-SR20-001",
  "vmName": "SR20-2026-6HQ8",
  "workerId": "rpa-sh-tax-etax-001",
  "fromProfileId": "rpa-sh-social-portal",
  "fromSnapshotName": "rpa-sh-social-portal-v20260615.1",
  "toProfileId": "rpa-sh-tax-etax",
  "toSnapshotName": "rpa-sh-tax-etax-v20260615.1",
  "firstTaskId": 123456,
  "status": "SUCCESS",
  "startedAt": "2026-06-17 09:45:00",
  "finishedAt": "2026-06-17 09:50:00",
  "durationSeconds": 300
}
```

### 14.6 上报日志备份结果

```http
POST /api/rpa/worker/log-backup-result
```

请求：

```json
{
  "txId": "SWITCH-20260617-0001",
  "hostId": "HOST-SR20-001",
  "vmName": "SR20-2026-6HQ8",
  "workerId": "rpa-sh-tax-etax-001",
  "fromProfileId": "rpa-sh-social-portal",
  "toProfileId": "rpa-sh-tax-etax",
  "firstTaskId": 123456,
  "success": true,
  "backupPath": "D:\\seebot-vm-log-backup\\HOST-SR20-001\\SR20-2026-6HQ8\\...",
  "fileCount": 128,
  "totalBytes": 98234212
}
```

## 15. 本机运维 API

API 监听 `127.0.0.1`，并要求请求携带 `X-Agent-Api-Key`。

首版接口：

```text
GET  /api/agent/status
GET  /api/vms
GET  /api/vms/{vmName}
GET  /api/vms/{vmName}/profiles
POST /api/scheduler/pause
POST /api/scheduler/resume
POST /api/capabilities/report
POST /api/vms/{vmName}/quarantine
POST /api/vms/{vmName}/unquarantine
POST /api/vms/{vmName}/switch-profile
GET  /api/transactions
GET  /api/transactions/{txId}
```

人工 `switch-profile` 使用与自动调度相同的安全切换流程。

## 16. 本地数据设计

### 16.1 VM 状态表

```sql
CREATE TABLE IF NOT EXISTS local_vm_state (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    host_id TEXT NOT NULL,
    vm_name TEXT NOT NULL,
    worker_id TEXT NOT NULL,
    current_profile_id TEXT,
    current_snapshot_name TEXT,
    runner_status_code INTEGER,
    runner_status_name TEXT,
    agent_vm_status TEXT NOT NULL,
    last_idle_at TEXT,
    last_switch_at TEXT,
    is_quarantined INTEGER NOT NULL DEFAULT 0,
    error_code TEXT,
    error_message TEXT,
    updated_at TEXT NOT NULL,
    UNIQUE(host_id, vm_name)
);
```

### 16.2 本地切换事务表

```sql
CREATE TABLE IF NOT EXISTS local_switch_transaction (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    tx_id TEXT NOT NULL,
    host_id TEXT NOT NULL,
    vm_name TEXT NOT NULL,
    worker_id TEXT NOT NULL,
    from_profile_id TEXT,
    from_snapshot_name TEXT,
    to_profile_id TEXT NOT NULL,
    to_snapshot_name TEXT NOT NULL,
    first_task_id INTEGER,
    trigger_reason TEXT,
    status TEXT NOT NULL,
    step TEXT,
    error_code TEXT,
    error_message TEXT,
    started_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    finished_at TEXT,
    UNIQUE(tx_id)
);
```

### 16.3 事务状态

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

### 16.4 事务恢复规则

| 上次状态 | 恢复策略 |
|---|---|
| CREATED | 如果 runner 尚未停止，标记失败，允许后续重新调度 |
| STOP_RUNNER_DONE | 尽可能继续日志备份 |
| LOG_BACKUP_DONE | 继续关闭 VM |
| VM_STOP_DONE | 继续回滚快照 |
| SNAPSHOT_REVERT_DONE | 继续启动 VM |
| VM_START_DONE | 继续等待 worker ready |
| WORKER_READY_DONE | 补报状态并标记成功 |
| FAILED | 上报失败 |
| NEED_MANUAL_CHECK | 不自动恢复 |

## 17. 云后台数据模型建议

### 17.1 宿主机 Agent 表

```sql
CREATE TABLE rpa_host_agent (
    id BIGINT PRIMARY KEY AUTO_INCREMENT,
    host_id VARCHAR(64) NOT NULL,
    agent_name VARCHAR(128) NOT NULL,
    status VARCHAR(32) NOT NULL,
    version VARCHAR(64),
    vm_count INT,
    quarantined_vm_count INT,
    last_heartbeat_time DATETIME,
    created_at DATETIME NOT NULL,
    updated_at DATETIME NOT NULL,
    UNIQUE KEY uk_host_id (host_id)
);
```

### 17.2 VM 实例表

```sql
CREATE TABLE rpa_vm_instance (
    id BIGINT PRIMARY KEY AUTO_INCREMENT,
    host_id VARCHAR(64) NOT NULL,
    vm_name VARCHAR(128) NOT NULL,
    worker_id VARCHAR(128) NOT NULL,
    vmx_path VARCHAR(1000),
    base_snapshot_name VARCHAR(128),
    enabled TINYINT NOT NULL DEFAULT 1,

    current_profile_id VARCHAR(128),
    current_snapshot_name VARCHAR(128),
    agent_vm_status VARCHAR(32),

    runner_status_code TINYINT,
    runner_status_name VARCHAR(32),
    runner_status_desc VARCHAR(128),

    current_task_id BIGINT,
    execution_code VARCHAR(128),

    is_quarantined TINYINT NOT NULL DEFAULT 0,
    last_heartbeat_time DATETIME,
    last_switch_time DATETIME,

    error_code VARCHAR(64),
    error_message TEXT,

    created_at DATETIME NOT NULL,
    updated_at DATETIME NOT NULL,

    UNIQUE KEY uk_host_vm (host_id, vm_name),
    UNIQUE KEY uk_host_worker (host_id, worker_id),
    KEY idx_current_profile (current_profile_id),
    KEY idx_runner_status (runner_status_code)
);
```

### 17.3 VM profile 能力表

```sql
CREATE TABLE rpa_vm_profile_capability (
    id BIGINT PRIMARY KEY AUTO_INCREMENT,
    host_id VARCHAR(64) NOT NULL,
    vm_name VARCHAR(128) NOT NULL,
    worker_id VARCHAR(128) NOT NULL,

    profile_id VARCHAR(128) NOT NULL,
    snapshot_name VARCHAR(128) NOT NULL,
    city VARCHAR(64),
    business VARCHAR(128),
    system_code VARCHAR(128),

    enabled TINYINT NOT NULL DEFAULT 1,
    snapshot_exists TINYINT NOT NULL DEFAULT 0,
    validation_status VARCHAR(32),
    validation_message TEXT,

    last_report_time DATETIME,
    created_at DATETIME NOT NULL,
    updated_at DATETIME NOT NULL,

    UNIQUE KEY uk_vm_profile (host_id, vm_name, profile_id),
    KEY idx_profile_id (profile_id),
    KEY idx_snapshot_name (snapshot_name)
);
```

### 17.4 快照切换记录表

```sql
CREATE TABLE rpa_worker_switch_log (
    id BIGINT PRIMARY KEY AUTO_INCREMENT,
    tx_id VARCHAR(128) NOT NULL,
    host_id VARCHAR(64) NOT NULL,
    vm_name VARCHAR(128) NOT NULL,
    worker_id VARCHAR(128) NOT NULL,

    from_profile_id VARCHAR(128),
    from_snapshot_name VARCHAR(128),
    to_profile_id VARCHAR(128) NOT NULL,
    to_snapshot_name VARCHAR(128) NOT NULL,

    first_task_id BIGINT,
    status VARCHAR(32) NOT NULL,
    error_code VARCHAR(64),
    error_message TEXT,
    started_at DATETIME NOT NULL,
    finished_at DATETIME,
    duration_seconds INT,
    created_at DATETIME NOT NULL,

    UNIQUE KEY uk_tx_id (tx_id),
    KEY idx_host_vm (host_id, vm_name),
    KEY idx_to_profile (to_profile_id)
);
```

### 17.5 日志备份记录表

```sql
CREATE TABLE rpa_worker_log_backup (
    id BIGINT PRIMARY KEY AUTO_INCREMENT,
    tx_id VARCHAR(128),
    host_id VARCHAR(64) NOT NULL,
    vm_name VARCHAR(128) NOT NULL,
    worker_id VARCHAR(128) NOT NULL,
    from_profile_id VARCHAR(128),
    to_profile_id VARCHAR(128),
    first_task_id BIGINT,
    backup_path VARCHAR(1000) NOT NULL,
    file_count INT,
    total_bytes BIGINT,
    success TINYINT NOT NULL,
    error_code VARCHAR(64),
    error_message TEXT,
    created_at DATETIME NOT NULL,
    KEY idx_tx_id (tx_id),
    KEY idx_host_vm (host_id, vm_name)
);
```

## 18. 云后台页面建议

云后台展示“静态能力 + 当前运行状态”。

### 18.1 宿主机列表

展示：

- `hostId`
- Agent 名称
- Agent 在线状态
- VM 数量
- 隔离 VM 数量
- 最后心跳时间
- Agent 版本

### 18.2 宿主机详情

按 VM 展示：

- `vmName`
- `workerId`
- 当前 `profileId`
- 当前 `snapshotName`
- runner 状态
- 当前任务
- 是否隔离
- 最后切换时间
- 最后心跳时间

### 18.3 VM 详情

展示：

- VM 基础信息。
- 当前运行状态。
- 支持的所有 `profileId/snapshotName` 能力清单。
- 每个快照的启动校验结果。
- 最近切换记录。
- 最近日志备份记录。

## 19. 日志备份设计

### 19.1 备份目录

```text
D:\seebot-vm-log-backup\
  └── HOST-SR20-001\
      └── SR20-2026-6HQ8\
          └── rpa-sh-tax-etax\
              └── 20260617\
                  └── SWITCH-20260617-0001\
                      ├── runner\
                      ├── client\
                      ├── screenshots\
                      ├── agent\
                      └── backup_manifest.json
```

### 19.2 backup_manifest.json

```json
{
  "txId": "SWITCH-20260617-0001",
  "hostId": "HOST-SR20-001",
  "vmName": "SR20-2026-6HQ8",
  "workerId": "rpa-sh-tax-etax-001",
  "fromProfileId": "rpa-sh-social-portal",
  "fromSnapshotName": "rpa-sh-social-portal-v20260615.1",
  "toProfileId": "rpa-sh-tax-etax",
  "toSnapshotName": "rpa-sh-tax-etax-v20260615.1",
  "firstTaskId": 123456,
  "backupTime": "2026-06-17 10:00:00",
  "sourcePath": "C:\\seebot\\logs",
  "targetPath": "D:\\seebot-vm-log-backup\\HOST-SR20-001\\SR20-2026-6HQ8\\...",
  "fileCount": 128,
  "totalBytes": 98234212,
  "success": true
}
```

### 19.3 备份失败处理

默认策略：

```text
日志备份失败，不继续回滚快照。
```

如果配置允许强制回滚：

```json
{
  "ForceRevertWhenBackupFailed": true
}
```

则必须上报：

```text
LOG_BACKUP_FAILED_BUT_FORCE_REVERT
```

## 20. 错误码

| 错误码 | 含义 |
|---|---|
| SCHEDULER_UNAVAILABLE | 调度中心不可用 |
| VM_NOT_IDLE | VM 非空闲 |
| WORKER_RUNNING | runner 状态为 Running，禁止切换 |
| WORKER_UPGRADING | runner 状态为 Upgrading，禁止切换 |
| EXECUTOR_STOP_FAILED | 停止 runner 失败 |
| LOG_BACKUP_FAILED | 日志备份失败 |
| LOG_BACKUP_FAILED_BUT_FORCE_REVERT | 日志备份失败但配置允许强制回滚 |
| VM_STOP_FAILED | VM 关机失败 |
| SNAPSHOT_NOT_FOUND | 快照不存在 |
| SNAPSHOT_REVERT_FAILED | 快照回滚失败 |
| VM_START_FAILED | VM 启动失败 |
| VM_READY_TIMEOUT | VM ready 超时 |
| RUNNER_NOT_READY | runner 长时间停留在 New |
| RUNNER_CLOSED | runner 状态为 Closed，未自动恢复 |
| ROBOT_ERROR | runner 状态为 RobotError |
| CLIENT_ERROR | runner 状态为 ClientError |
| WORKER_UPGRADING_TIMEOUT | runner 长时间处于 Upgrading |
| UPGRADE_FAILED | runner 状态为 UpgradeFailed |
| WORKER_OFFLINE | runner 状态为 Offline |
| WORKER_PROFILE_MISMATCH | VM 内 workerId/profileId 与目标不一致 |
| LOCAL_STATE_CORRUPTED | 本地状态异常 |
| WORKER_QUARANTINED | Worker 或 VM 已隔离 |

## 21. 异常处理规则

### 21.1 调度中心不可用

```text
1. 记录 SCHEDULER_UNAVAILABLE
2. Agent 保持运行
3. 延迟后继续重试
4. 不执行快照切换
```

### 21.2 runner 状态为 Running

```text
1. 禁止快照切换
2. 上报 WORKER_RUNNING
3. 保持当前 VM 运行
4. 下次轮询继续检查
```

### 21.3 runner 状态为 Upgrading

```text
1. 禁止快照切换
2. 上报 WORKER_UPGRADING
3. 等待升级完成
4. 如果超过升级等待阈值，上报 WORKER_UPGRADING_TIMEOUT
```

### 21.4 runner 状态为 New

```text
1. VM 启动后允许短时间处于 New
2. 继续等待状态变为 Runnable 或 Running
3. 如果超时仍为 New，上报 RUNNER_NOT_READY
```

### 21.5 runner 状态为 Runnable

```text
1. 视为机器人已启动，准备就绪
2. VM 启动后可判定 ready
3. 快照切换前必须先停止 runner
```

### 21.6 runner 状态为 Closed

```text
1. VM 启动后如果长时间为 Closed，则上报 RUNNER_CLOSED
2. 是否需要人工处理由配置决定
```

### 21.7 runner 状态为 RobotError

```text
1. 上报 ROBOT_ERROR
2. 记录当前 worker 异常
3. 尝试备份日志
4. 按配置决定是否隔离
```

### 21.8 runner 状态为 ClientError

```text
1. 上报 CLIENT_ERROR
2. 记录 rpa-client 内部异常
3. 尝试备份日志
4. 优先标记 worker 异常
5. 按配置决定是否隔离
```

### 21.9 runner 状态为 UpgradeFailed

```text
1. 上报 UPGRADE_FAILED
2. 标记 worker 异常
3. 不再自动接收任务
4. 等待人工处理或重新升级
```

### 21.10 runner 状态为 Offline

```text
1. 上报 WORKER_OFFLINE
2. 检查 VM 网络
3. 检查 rpa-client 是否启动
4. 如果 VM 已启动但 runner 仍 Offline，标记 VM_READY_TIMEOUT
```

## 22. 部署设计

### 22.1 服务目录

```text
D:\seebot-agent\
  ├── Seebot.WorkerAgent.Service.exe
  ├── appsettings.json
  ├── logs\
  ├── data\
  │   └── agent.db
  ├── scripts\
  └── backups\
```

### 22.2 Windows Service 安装

```bat
sc create Seebot.WorkerAgent.Service ^
  binPath= "D:\seebot-agent\Seebot.WorkerAgent.Service.exe" ^
  start= auto

sc start Seebot.WorkerAgent.Service
```

### 22.3 服务恢复策略

建议配置：

```text
第一次失败：自动重启
第二次失败：自动重启
后续失败：自动重启
重启间隔：60 秒
```

## 23. 分阶段实施计划

### 阶段 1：基础工程与 vmrun 控制

目标：

```text
1. Agent 能加载配置
2. Agent 能作为 Windows Service 运行
3. Agent 能调用 vmrun listSnapshots
4. Agent 能调用 vmrun stop
5. Agent 能调用 vmrun revertToSnapshot
6. Agent 能调用 vmrun start
7. Agent 能记录本地切换事务
```

交付物：

- `Seebot.WorkerAgent.Service` 初版。
- `VmrunService`。
- `appsettings.json` 模板。
- 本地 SQLite 表。
- 快照切换日志。

### 阶段 2：配置、能力上报与云后台状态

目标：

```text
1. 支持多 VM 配置
2. 支持每台 VM 多 profile/snapshot 配置
3. 校验 workerId/profileId/snapshotName
4. 上报 VM profile 静态能力
5. 上报 VM 当前运行状态
```

交付物：

- `CapabilityReporter`。
- `StateReporter`。
- 云后台能力上报接口。
- 云后台 VM 状态上报接口。

### 阶段 3：调度中心查询

目标：

```text
1. Agent 能按 profileId 查询待执行任务
2. 有任务时触发 VM 空闲判断
3. 无任务时继续监控
4. 能上报 Agent 心跳
```

交付物：

- `SchedulerClient`。
- profile pending 查询接口。
- Agent 心跳接口。
- VM 状态上报接口。

### 阶段 4：runner 状态监控

目标：

```text
1. 读取 runner 原有 0-8 状态
2. Running 禁止切换
3. Upgrading 禁止切换
4. Runnable 判定 ready
5. RobotError / ClientError / UpgradeFailed / Offline 可上报异常
```

交付物：

- `GuestWorkerClient`。
- `/executor/health` 对接。
- `/worker/status` 对接。
- 状态码映射逻辑。
- 状态上报接口。

### 阶段 5：切换前事务

目标：

```text
1. 停止 VM 内 runner.jar
2. runner 正在执行任务时拒绝切换
3. 确认 currentTaskId 为空
4. 复制日志到宿主机
5. 生成 backup_manifest.json
6. 日志备份失败时阻断回滚
```

交付物：

- `LogBackupService`。
- executor stop 接口对接。
- 备份 manifest。
- 备份记录表。
- 错误码上报。

### 阶段 6：快照切换和状态恢复

目标：

```text
1. 关闭 VM
2. 回滚目标快照
3. 启动 VM
4. 等待 rpa-client 自启动
5. 等待 rpa-runner 自启动
6. 监控 runner 状态
7. Agent 重启后恢复未完成事务
```

交付物：

- `VmSwitchService`。
- `RecoveryService`。
- 快照切换记录表。
- 异常隔离逻辑。

### 阶段 7：本机运维 API

目标：

```text
1. 查看 Agent 状态
2. 查看 VM 状态
3. 查看 VM 支持 profile 列表
4. 暂停 / 恢复调度
5. 隔离 / 解除隔离 VM
6. 人工触发安全 profile 切换
7. 查询事务详情
```

交付物：

- `OperationsApi`。
- API Key 鉴权。
- 操作审计记录。

## 24. 开发优先级

### P0 必须实现

1. `appsettings.json` 配置加载。
2. 多 VM 配置模型。
3. `profileId / workerId / snapshotName` 三层模型。
4. `vmrun` 命令封装。
5. 快照存在性校验。
6. VM profile 能力上报。
7. VM 当前状态上报。
8. 调度中心 profile pending 查询。
9. runner 0-8 状态读取。
10. VM 空闲判断。
11. Running / Upgrading 禁止切换。
12. 停止 `runner.jar`。
13. 日志备份。
14. VM stop / revert / start。
15. VM 启动后 Runnable / Running 状态判断。
16. 本地事务表。
17. 心跳和状态上报。

### P1 第二批实现

1. Agent 重启事务恢复。
2. 快照切换耗时统计。
3. `backup_manifest.json`。
4. VM 隔离。
5. 人工解除隔离。
6. 本机运维 API。
7. 错误码统一上报。
8. New / Closed / Offline 超时处理。
9. 操作审计。

### P2 后续增强

1. UKey / 证书枚举状态接入。
2. 健康评分。
3. 自动重新入池。
4. 后台状态看板增强。
5. 生产运行报表。
6. vSphere / ESXi Provider。

## 25. 验收标准

### 25.1 技术验收

| 验收项 | 标准 |
|---|---|
| Agent 服务 | 可作为 Windows Service 启动 |
| 多 VM 配置 | 可加载并校验多台 VM |
| 多 profile 配置 | 每台 VM 可声明多个 `profileId/snapshotName` |
| 命名模型 | 支持 `profileId / workerId / snapshotName` 三层模型 |
| 镜像准备前置 | Agent 启动前已完成 VM、纯净基础快照和定制 profile 快照准备 |
| 纯净基础快照 | 每台 VM 存在一个与 VM 同名的 `BaseSnapshotName` |
| 快照派生规则 | 每个定制 profile 快照均基于该 VM 的纯净基础快照制作 |
| vmrun 控制 | 能 stop / revertToSnapshot / start VM |
| 快照校验 | 能校验每个配置快照存在 |
| 能力上报 | 云后台可看到每台 VM 支持的 profile/snapshot |
| 当前状态上报 | 云后台可看到 VM 当前 profile、runner 状态、任务、隔离状态 |
| 队列查询 | 能按 `profileId` 查询待执行任务 |
| runner 状态兼容 | Agent 能识别 0-8 原有 runner 状态 |
| Running 判断 | `runnerStatusCode = 2` 时禁止快照切换 |
| Upgrading 判断 | `runnerStatusCode = 6` 时禁止快照切换 |
| Runnable 判断 | `runnerStatusCode = 1` 时判定 VM ready |
| Running 启动判断 | VM 启动后 `runnerStatusCode = 2` 时也视为自运行成功 |
| New 超时 | 长时间为 0 New 时上报 `RUNNER_NOT_READY` |
| Closed 超时 | 长时间为 3 Closed 时上报 `RUNNER_CLOSED` |
| RobotError | 状态 4 能上报 `ROBOT_ERROR` |
| ClientError | 状态 5 能上报 `CLIENT_ERROR` |
| UpgradeFailed | 状态 7 能上报 `UPGRADE_FAILED` |
| Offline | 状态 8 能上报 `WORKER_OFFLINE` |
| runner 停止 | 切换前能停止 VM 内 `runner.jar` |
| 执行中保护 | runner 正在执行任务时停止接口拒绝，Agent 取消切换 |
| 日志备份 | 切换前能复制 VM 日志到宿主机 |
| 快照回滚 | 能切换到目标 profile 对应快照 |
| 事务恢复 | Agent 重启后能恢复未完成事务 |
| 异常隔离 | 快照失败、VM ready 超时能隔离 VM |
| 本机 API | localhost API 可查询状态、暂停恢复、隔离解除、人工切换 |

### 25.2 业务验收

| 验收项 | 标准 |
|---|---|
| profile 切换 | 至少 2 个 profile 快照可互相切换 |
| 多 VM 协调 | 一台宿主机可管理多台 VM |
| 同 profile 多 VM | 同一个 profile 可在多台兼容 VM 上运行 |
| 自动执行链路 | 切换快照后 VM 内 rpa-client / rpa-runner 能自启动并执行任务 |
| 正在执行保护 | 任务执行中不会被 Agent 强制切换快照 |
| 升级保护 | 执行器升级中不会被 Agent 强制切换快照 |
| 自启动验证 | VM 启动后 rpa-client / rpa-runner 自动进入 Runnable 或 Running |
| 日志追溯 | 每次切换都有日志备份目录和 manifest |
| 环境隔离 | 上一 profile 环境污染不会影响下一 profile |
| 切换耗时 | 记录每次 stop / revert / start / ready 耗时 |
| 异常可追溯 | RobotError / ClientError / UpgradeFailed / Offline 均能留存日志并上报 |
| 状态一致性 | 云后台展示状态与 VM 内 runner 原有状态一致 |

## 26. 最终执行闭环

```text
Agent 启动
    ↓
人工完成 VM 镜像准备
    ↓
制作与 VM 同名的纯净基础快照
    ↓
基于纯净基础快照制作所有 profile 定制快照
    ↓
加载 VM / profile / snapshot 配置
    ↓
校验 vmrun、VMX、纯净基础快照、定制快照、workerId、profileId
    ↓
上报 VM profile 静态能力
    ↓
按 profileId 查询调度中心待执行任务
    ↓
发现目标 profileId 有任务
    ↓
选择兼容且空闲 VM
    ↓
读取 VM 内 runner 状态
    ↓
runner = Running / Upgrading？
    ├── 是：禁止切换，继续监控
    └── 否：进入切换前事务
            ↓
        调用 VM 内停止接口关闭 runner.jar
            ↓
        runner 已领取任务？
            ├── 是：取消切换，等待下一轮
            └── 否：继续
                    ↓
                备份 VM 日志到宿主机
                    ↓
                vmrun stop VM
                    ↓
                vmrun revertToSnapshot 到目标 profile 快照
                    ↓
                vmrun start VM
                    ↓
                等待 rpa-client / rpa-runner 自启动
                    ↓
                读取 runner 状态
                    ↓
                runner = Runnable / Running？
                    ├── 是：上报 VM ready / running
                    └── 否：按状态码上报异常
                    ↓
                持续监控并上报当前状态
```

## 27. 总结

最终版 RPA Worker Agent 定位：

```text
宿主机 Agent 不执行 RPA 业务；
宿主机 Agent 不启动 runner；
宿主机 Agent 不管理 lease；
宿主机 Agent 只使用 vmrun 控制 VM；
宿主机 Agent 按 profileId 查询任务队列；
宿主机 Agent 使用 runner 原有 0-8 状态判断 VM 是否可切换、是否 ready、是否异常；
宿主机 Agent 向云后台上报 VM 静态能力和当前运行状态。
```

最终目标是形成一个稳定闭环：

```text
查 profileId 待执行任务
    ↓
判断 VM 与 runner 状态
    ↓
停止 runner
    ↓
备份日志
    ↓
回滚快照
    ↓
启动 VM
    ↓
等待 rpa-client / rpa-runner 自启动
    ↓
监控原有 runner 状态
    ↓
上报能力、状态、事务和异常
```
