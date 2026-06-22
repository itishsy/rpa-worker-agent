# RPA Worker Agent P0 开发提示词

本文基于 `docs/worker-agent-design.md` 生成，用于手工逐步提交给 AI 编码工具。本文不是代码实现，不要求一次性完成全部功能。

使用方式：

1. 每次只复制一个“开发提示词”给编码 AI。
2. 等该步骤完成、测试通过、人工确认后，再提交下一步。
3. 每一步都必须遵守 `docs/worker-agent-design.md`，不得自行改需求边界。
4. P0 只实现基础闭环，不实现 P1/P2 增强。

## 全局开发约束

给每一步编码 AI 的通用约束：

```text
你正在开发 Seebot RPA Worker Agent。

请先阅读并遵守 docs/worker-agent-design.md。

本次只实现当前提示词要求的步骤，不要提前实现后续步骤，不要引入 P1/P2 功能。

P0 核心边界：
- Agent 运行在 Windows 宿主机。
- Agent 不执行 RPA 业务。
- Agent 不启动 runner.jar。
- Agent 不管理 lease。
- Agent 只通过 vmrun.exe 控制 VM。
- 调度维度是 profileId，不是 workerId。
- workerId 是 VM 内 runner 实例身份。
- snapshotName 是 profile 环境版本快照。
- VM 内控制接口由 rpa-runner 提供，监听 9090 端口。
- runner 状态查询接口是 GET /api/robot/start/status。
- 关闭 runner 通过 RunnerKillUrl 配置的 9090 kill 接口完成。
- 切换前必须停止 runner，并复制 VM 内 cache/db/file/logs 到 {work-path}/{vm-name}/{yyyyMMddHHmmss}/。

编码要求：
- 优先使用现有项目结构；如果项目结构不完整，按设计文档建立最小必要结构。
- 每个模块职责单一，避免大类堆叠。
- 每一步都要补充或更新对应测试。
- 每一步完成后运行相关测试。
- 不要提交 .vs、obj、bin 等 IDE/构建产物。
- 不要改动与当前步骤无关的文件。
```

## P0 范围

P0 必须实现：

- `appsettings.json` 配置加载。
- 多 VM 配置模型。
- `profileId / workerId / snapshotName` 三层模型。
- VM 纯净基础快照 `BaseSnapshotName` 校验。
- VM 定制 profile 快照校验。
- `vmrun` 命令封装。
- VM profile 能力上报。
- VM 当前状态上报。
- 调度中心 profile pending 查询。
- runner 0-8 状态读取。
- VM 空闲判断。
- Running / Upgrading 禁止切换。
- 通过 `rpa-runner` 9090 kill 接口停止 runner。
- 目录备份：`cache`、`db`、`file`、`logs`。
- VM stop / revert / start。
- VM 启动后 Runnable / Running 状态判断。
- 本地 SQLite 事务表。
- Agent 心跳和状态上报。

P0 不实现：

- 本机运维 API。
- 人工隔离/解除隔离 API。
- 操作审计 UI。
- 健康评分。
- UKey/证书状态接入。
- vSphere/ESXi。
- 自动按任务数计算扩容。
- Agent 启动 runner。
- 任务 lease。

---

## 开发提示词 01：项目基线与 P0 骨架

```text
请实现 P0 第 1 步：建立 Worker Agent 的最小工程骨架。

先阅读 docs/worker-agent-design.md，重点理解：
- 第 2 节 建设目标
- 第 6 节 技术选型
- 第 7 节 工程结构
- 第 24 节 P0 必须实现

本步骤目标：
1. 梳理当前仓库已有 C# 项目结构。
2. 如果已有 Windows Service / Worker Service 项目，则沿用现有项目。
3. 如果项目不完整，则建立最小可编译的 Worker Agent 工程骨架。
4. 准备 Core 模块边界，但不要实现业务逻辑。

需要实现的内容：
- Service 入口：可启动、可加载依赖注入容器、可注册后台服务。
- Core 命名空间或项目：预留后续模块位置。
- Tests 项目：建立测试项目并能运行一个最小空测试。
- README 或项目说明可简单标注如何运行测试。

本步骤不要实现：
- vmrun 调用。
- SQLite。
- 调度中心 HTTP 调用。
- runner 9090 调用。
- 快照切换逻辑。

验收标准：
- 项目能编译。
- 测试项目能运行。
- 没有引入 .vs、bin、obj 到 Git。
- 工程结构能支撑后续步骤新增 Core 服务。

完成后请输出：
- 修改/新增的文件列表。
- 编译命令和结果。
- 测试命令和结果。
- 下一步建议执行“开发提示词 02”。
```

## 开发提示词 02：配置模型与配置加载

```text
请实现 P0 第 2 步：appsettings.json 配置模型与加载校验基础。

先阅读 docs/worker-agent-design.md：
- 第 4 节 命名模型
- 第 4.1 节 VM 镜像准备与基础快照命名
- 第 8 节 配置设计

本步骤目标：
1. 定义强类型配置模型。
2. 支持多 VM。
3. 支持每台 VM 多 Profiles。
4. 支持 BaseSnapshotName、GuestBackupPaths、HostWorkPath。
5. 支持 rpa-runner 9090 控制配置：
   - RunnerControlBaseUrl
   - RunnerStatusUrl
   - RunnerKillUrl

需要实现的内容：
- AgentOptions
- OperationsApiOptions
- SchedulerOptions
- VmrunOptions
- VirtualMachineOptions
- ProfileOptions
- GuestBackupPathsOptions
- 配置加载服务或 Options 注册。
- 配置校验器，至少校验：
  - HostId 非空。
  - VmrunPath 非空。
  - VM 名称非空。
  - VmxPath 非空。
  - BaseSnapshotName 非空。
  - WorkerId 非空。
  - RunnerStatusUrl 非空且端口应为 9090。
  - RunnerKillUrl 非空且端口应为 9090。
  - GuestBackupPaths 包含 Cache、Db、File、Logs。
  - HostWorkPath 非空。
  - 每个 Profile 的 ProfileId、SnapshotName 非空。
  - 同一 VM 内 ProfileId 不重复。
  - 宿主机内 WorkerId 不重复。

本步骤不要实现：
- 检查文件是否真实存在。
- 调用 vmrun listSnapshots。
- 调用 HTTP。

测试要求：
- 配置完整时校验通过。
- 缺少 RunnerStatusUrl 时校验失败。
- RunnerStatusUrl 不是 9090 端口时校验失败。
- GuestBackupPaths 缺少 db 时校验失败。
- 同一 VM 内 ProfileId 重复时校验失败。
- 多 VM WorkerId 重复时校验失败。

验收标准：
- 配置模型字段名称与 docs/worker-agent-design.md 保持一致。
- 测试覆盖成功和失败路径。
- 不实现后续业务逻辑。

完成后请输出：
- 配置模型文件列表。
- 校验规则列表。
- 测试命令和结果。
```

## 开发提示词 03：领域模型、状态枚举与错误码

```text
请实现 P0 第 3 步：领域模型、runner 状态枚举、Agent/VM 状态、错误码。

先阅读 docs/worker-agent-design.md：
- 第 9 节 状态模型
- 第 20 节 错误码
- 第 25 节 验收标准

本步骤目标：
1. 定义 runner 原有 0-8 状态枚举。
2. 定义 Agent 状态、VM 状态、事务状态。
3. 定义错误码常量。
4. 定义核心 DTO/实体模型，供后续模块复用。

需要实现的内容：
- RunnerStatusCode：New=0、Runnable=1、Running=2、Closed=3、RobotError=4、ClientError=5、Upgrading=6、UpgradeFailed=7、Offline=8。
- AgentStatus：STARTING、RUNNING、PAUSED、ERROR、STOPPING。
- AgentVmStatus：UNKNOWN、POWERED_OFF、POWERED_ON、STOPPING、REVERTING、STARTING、WAIT_READY、MONITORING、ERROR、QUARANTINED。
- SwitchTransactionStatus：CREATED、STOP_RUNNER_DONE、LOG_BACKUP_DONE、VM_STOP_DONE、SNAPSHOT_REVERT_DONE、VM_START_DONE、WORKER_READY_DONE、SUCCESS、FAILED、NEED_MANUAL_CHECK。
- ErrorCodes：按设计文档第 20 节定义。
- VM 当前状态模型。
- Profile 能力模型。
- 切换事务模型。
- 目录备份 manifest 模型。

本步骤不要实现：
- SQLite。
- HTTP。
- vmrun。
- 调度逻辑。

测试要求：
- RunnerStatusCode 数值必须与 0-8 完全一致。
- ErrorCodes 中必须包含 WORKER_RUNNING、WORKER_UPGRADING、LOG_BACKUP_FAILED、WORKER_PROFILE_MISMATCH。
- SwitchTransactionStatus 包含 P0 所需全部状态。

验收标准：
- 后续模块可以引用这些模型，不重复定义状态字符串。
- 枚举/常量命名清晰。
- 测试通过。
```

## 开发提示词 04：WorkerStateEvaluator 空闲与切换许可判断

```text
请实现 P0 第 4 步：runner 状态判断与 VM 空闲判断。

先阅读 docs/worker-agent-design.md：
- 第 9.5 节 runner 状态处理规则
- 第 10 节 调度规则
- 第 21 节 异常处理规则

本步骤目标：
实现 WorkerStateEvaluator，用统一规则判断：
1. runner 是否 ready。
2. runner 是否正在执行任务。
3. runner 是否禁止切换。
4. VM 是否可作为切换候选。

需要实现的内容：
- IsRunnerReady(status)：Runnable 或 Running 为 true。
- IsRunnerBusy(status)：Running 为 true。
- IsRunnerUpgradeLocked(status)：Upgrading 为 true。
- CanSwitchBeforeStop(status)：只有 Runnable 可作为自动切换候选。
- EvaluateReadyAfterVmStart(status)：返回 ready / wait / error 结果。
- EvaluateSwitchCandidate(vmState, currentProfilePending, idleStableSeconds, now)：判断 VM 是否满足：
  - 未隔离。
  - 无活跃事务。
  - runner 为 Runnable。
  - 当前 profile 队列为空。
  - 持续空闲时间达到 IdleStableSeconds。

本步骤不要实现：
- 真实调度器。
- HTTP 调用。
- vmrun。

测试要求：
- Running 禁止切换。
- Upgrading 禁止切换。
- Runnable 可作为候选。
- New/Closed 不作为自动切换候选。
- 当前 profile 还有任务时不可抢占。
- idle 时间未达到阈值时不可切换。
- VM 已隔离时不可切换。

验收标准：
- 判断逻辑全部集中在 WorkerStateEvaluator。
- 后续 PoolSchedulerService 不直接硬编码 runner 状态判断。
```

## 开发提示词 05：SQLite 本地状态与切换事务

```text
请实现 P0 第 5 步：SQLite 本地状态存储。

先阅读 docs/worker-agent-design.md：
- 第 16 节 本地数据设计
- 第 11.3 节 安全切换流程
- 第 16.4 节 事务恢复规则

本步骤目标：
实现 LocalStore，用 SQLite 持久化：
1. VM 当前状态。
2. 本地切换事务。

需要实现的内容：
- 初始化 SQLite 数据库。
- 创建 local_vm_state 表。
- 创建 local_switch_transaction 表。
- Upsert VM 状态。
- 查询 VM 状态列表。
- 创建切换事务。
- 更新切换事务状态、step、error。
- 查询未完成事务。
- 按 txId 查询事务。

事务状态必须使用开发提示词 03 中定义的 SwitchTransactionStatus。

本步骤不要实现：
- RecoveryService 自动恢复。
- vmrun。
- HTTP。

测试要求：
- 初始化数据库后表存在。
- Upsert VM 状态后可查询。
- 创建事务后可查询。
- 更新事务状态后可查询到最新状态。
- 未完成事务查询不返回 SUCCESS/FAILED。

验收标准：
- 测试使用临时 SQLite 文件或内存数据库。
- 不依赖真实 VM。
- 不依赖真实网络。
```

## 开发提示词 06：VmrunService 命令封装

```text
请实现 P0 第 6 步：vmrun.exe 命令封装。

先阅读 docs/worker-agent-design.md：
- 第 12 节 vmrun 控制设计
- 第 8 节 Vmrun 配置

本步骤目标：
实现 VmrunService，封装以下命令：
1. listSnapshots
2. stop
3. revertToSnapshot
4. start
5. copyFileFromGuestToHost

需要实现的内容：
- IVmrunService 接口。
- VmrunService 实现。
- VmrunCommandResult 模型，包含：
  - ExitCode
  - StandardOutput
  - StandardError
  - Duration
  - CommandName
- 命令超时支持。
- 参数必须独立传递给 ProcessStartInfo，不要拼接成单个 shell 字符串。
- 路径带空格时必须能正确运行。
- listSnapshots 输出解析为 IReadOnlyList<string>。

本步骤不要实现：
- VM 切换编排。
- 日志备份服务。

测试要求：
- 使用 fake process runner 或进程执行抽象，不调用真实 vmrun。
- 验证 listSnapshots 参数顺序正确。
- 验证 stop soft/hard 参数正确。
- 验证 revertToSnapshot 参数正确。
- 验证 start nogui 参数正确。
- 验证 copyFileFromGuestToHost 包含 -gu、-gp、guestPath、hostPath。
- 验证非 0 ExitCode 抛出或返回可识别失败结果。

验收标准：
- 业务层不直接使用 Process。
- 所有 vmrun 调用都经过 IVmrunService。
```

## 开发提示词 07：SchedulerClient 云后台接口

```text
请实现 P0 第 7 步：调度中心与云后台 HTTP 客户端。

先阅读 docs/worker-agent-design.md：
- 第 14 节 调度中心与云后台接口
- 第 17 节 云后台数据模型建议

本步骤目标：
实现 SchedulerClient，支持 P0 所需云后台交互：
1. 查询 profile 待执行任务。
2. 上报 Agent 心跳。
3. 上报 VM profile 能力。
4. 上报 VM 当前运行状态。
5. 上报 switch-log。
6. 上报目录备份结果。

接口要求：
- profile 待执行任务查询按 profileId，不按 workerId。
- 状态上报同时包含 hostId、vmName、workerId、profileId、snapshotName/CurrentSnapshotName。
- HTTP 请求需要带 AccessToken，具体放在 Authorization Bearer 或约定 header 中，按项目现有约定实现；如果无现有约定，使用 Authorization: Bearer {token}。

需要实现的 DTO：
- ProfilePendingTaskResponse。
- HostAgentHeartbeatRequest。
- HostAgentCapabilitiesRequest。
- VmStatusReportRequest。
- WorkerSwitchLogRequest。
- DirectoryBackupResultRequest。

本步骤不要实现：
- 调度器。
- VM 状态采集。
- vmrun。

测试要求：
- 使用 fake HttpMessageHandler。
- pending 查询 URL 包含 profileId。
- 心跳请求 JSON 字段正确。
- 能力上报包含 vms/profiles/baseSnapshotName。
- VM 状态上报包含 currentProfileId/currentSnapshotName/runnerStatusCode。
- 目录备份结果包含 backedUpDirectories。
- 非成功 HTTP 状态返回可诊断错误。

验收标准：
- 所有外部 HTTP 调用集中在 SchedulerClient。
- DTO 字段与设计文档一致。
```

## 开发提示词 08：GuestWorkerClient 对接 rpa-runner 9090

```text
请实现 P0 第 8 步：VM 内 rpa-runner 9090 控制客户端。

先阅读 docs/worker-agent-design.md：
- 第 13 节 VM 内 rpa-runner 9090 控制接口
- 第 3.6 节 切换前必须停止 runner 并备份日志

本步骤目标：
实现 GuestWorkerClient，用于：
1. GET /api/robot/start/status 查询 runner 运行状态。
2. POST RunnerKillUrl kill runner。

配置来源：
- RunnerControlBaseUrl
- RunnerStatusUrl
- RunnerKillUrl

需要实现的内容：
- IGuestWorkerClient。
- GetRunnerStatusAsync(vm, ct)。
- KillRunnerAsync(vm, txId, reason, deadlineSeconds, ct)。
- RunnerStatusResponse DTO。
- KillRunnerResponse DTO。
- 将 runnerStatusCode 映射为 RunnerStatusCode 枚举。

kill 规则：
- 如果 runner 正在 Running，kill 接口应拒绝；客户端要把 WORKER_RUNNING 暴露给调用方。
- 如果 kill 成功，必须能读取 afterRunnerStatusCode、currentTaskId、logFlushed。
- 客户端不做强制 kill 正在执行任务的逻辑。

本步骤不要实现：
- VmSwitchService。
- 调度器。

测试要求：
- GetRunnerStatusAsync 调用 RunnerStatusUrl。
- Running 响应映射为 RunnerStatusCode.Running。
- KillRunnerAsync 成功响应可解析。
- KillRunnerAsync 遇到 WORKER_RUNNING 返回明确失败结果。
- HTTP 超时或连接失败返回可诊断错误。

验收标准：
- 文档中的 executor-control/18080 不应出现在代码中。
- 所有 VM 内控制都走 rpa-runner 9090 配置。
```

## 开发提示词 09：LogBackupService 目录备份与 manifest

```text
请实现 P0 第 9 步：切换前目录备份。

先阅读 docs/worker-agent-design.md：
- 第 3.6 节 切换前必须停止 runner 并备份日志
- 第 12.5 节 从 VM 复制目录到宿主机
- 第 19 节 切换前目录备份设计

本步骤目标：
实现 LogBackupService，将 VM 内相关目录复制到宿主机：
{work-path}/{vm-name}/{yyyyMMddHHmmss}/

必须备份的目录：
- cache
- db
- file
- logs

配置来源：
- GuestBackupPaths.Cache
- GuestBackupPaths.Db
- GuestBackupPaths.File
- GuestBackupPaths.Logs
- HostWorkPath

需要实现的内容：
- ILogBackupService。
- BackupAsync(vm, tx, timestamp, ct)。
- 为每次备份创建目录：HostWorkPath / VmName / yyyyMMddHHmmss。
- 调用 IVmrunService.CopyFileFromGuestToHostAsync 复制四个目录。
- 写 backup_manifest.json。
- manifest 包含：
  - txId
  - hostId
  - vmName
  - workerId
  - fromProfileId
  - fromSnapshotName
  - toProfileId
  - toSnapshotName
  - backupTime
  - workPath
  - targetPath
  - sources.cache/db/file/logs
  - directories
  - fileCount
  - totalBytes
  - success

本步骤不要实现：
- 停止 runner。
- VM stop/revert/start。

测试要求：
- 生成目录名格式为 yyyyMMddHHmmss。
- 会调用 CopyFileFromGuestToHostAsync 四次。
- 目标目录包含 cache/db/file/logs。
- 写出的 manifest JSON 包含 directories: cache/db/file/logs。
- 任意目录复制失败时返回失败结果，不继续标记 success=true。

验收标准：
- 备份路径符合设计文档。
- 不只备份 logs。
- 不使用硬编码 VM 名。
```

## 开发提示词 10：启动校验与 VM profile 能力生成

```text
请实现 P0 第 10 步：启动校验与 VM profile capability 生成。

先阅读 docs/worker-agent-design.md：
- 第 4.1 节 VM 镜像准备与基础快照命名
- 第 8.2 节 配置校验
- 第 14.3 节 上报 VM profile 能力

本步骤目标：
在 Agent 启动时校验 VM 和快照配置，并生成可上报的 VM profile 能力模型。

需要实现的内容：
- StartupValidator 或 CapabilityBuilder。
- 校验 vmrun.exe 路径存在。
- 校验 VMX 文件路径存在。
- 调用 IVmrunService.ListSnapshotsAsync 获取快照列表。
- 校验 BaseSnapshotName 存在。
- 校验 BaseSnapshotName 与 VmName 一致。
- 校验每个 Profile.SnapshotName 存在。
- 生成 capability：
  - hostId
  - agentName
  - vmName
  - workerId
  - vmxPath
  - baseSnapshotName
  - profiles[]
  - snapshotExists
  - validationStatus

注意：
- 如果 vmrun 无法读取快照父子关系，不要推断快照树。
- 只记录配置声明和校验结果。

测试要求：
- BaseSnapshotName 不存在时校验失败。
- BaseSnapshotName 与 VmName 不一致时校验失败。
- Profile snapshot 不存在时 validationStatus 不是 READY。
- 所有快照存在时 capability profiles 都是 READY。

验收标准：
- 能力上报前必须经过此校验。
- 不连接真实 VMware，测试使用 fake IVmrunService。
```

## 开发提示词 11：VmSwitchService 安全切换编排

```text
请实现 P0 第 11 步：VmSwitchService 安全切换编排。

先阅读 docs/worker-agent-design.md：
- 第 11.3 节 安全切换流程
- 第 13 节 VM 内 rpa-runner 9090 控制接口
- 第 16 节 本地数据设计
- 第 19 节 切换前目录备份设计

本步骤目标：
实现单台 VM 从当前 profile 切换到目标 profile snapshot 的完整编排。

流程必须严格按顺序：
1. 获取 VM 锁，由 VmCoordinator 或调用方保证。
2. 创建 switch_transaction。
3. 调用 GuestWorkerClient.GetRunnerStatusAsync。
4. 如果 runner Running 或 Upgrading，取消切换并记录失败原因。
5. 调用 GuestWorkerClient.KillRunnerAsync。
6. 如果 kill 返回 WORKER_RUNNING，取消切换，不执行备份和回滚。
7. 确认 runner 已停止且 currentTaskId 为空。
8. 调用 LogBackupService.BackupAsync。
9. 备份失败时，如果 ForceRevertWhenBackupFailed=false，则停止流程。
10. vmrun stop。
11. vmrun revertToSnapshot。
12. vmrun start。
13. 等待 RunnerStatusUrl 可访问。
14. 等待 runnerStatusCode 变为 Runnable 或 Running。
15. 校验 VM 内 workerId/profileId 与预期一致。
16. 更新事务 SUCCESS。

每个关键步骤更新本地事务状态：
- CREATED
- STOP_RUNNER_DONE
- LOG_BACKUP_DONE
- VM_STOP_DONE
- SNAPSHOT_REVERT_DONE
- VM_START_DONE
- WORKER_READY_DONE
- SUCCESS / FAILED / NEED_MANUAL_CHECK

本步骤不要实现：
- 多 VM 调度。
- 周期轮询。
- 本机运维 API。

测试要求：
- Running 时不会调用 kill、backup、vmrun。
- kill 返回 WORKER_RUNNING 时不会调用 backup、vmrun。
- backup 失败且 ForceRevertWhenBackupFailed=false 时不会 stop VM。
- 成功路径按 stop runner -> backup -> vmrun stop -> revert -> start -> wait ready 顺序调用。
- ready 后 workerId/profileId 不匹配时标记 WORKER_PROFILE_MISMATCH。

验收标准：
- VmSwitchService 是单 VM 切换编排中心。
- 所有外部动作通过接口注入，测试不依赖真实 VM。
```

## 开发提示词 12：PoolSchedulerService 单轮调度

```text
请实现 P0 第 12 步：PoolSchedulerService 单轮调度。

先阅读 docs/worker-agent-design.md：
- 第 10 节 调度规则
- 第 11.2 节 profile 调度轮询
- 第 24 节 P0 必须实现

本步骤目标：
实现调度器的一次轮询决策：
1. 查询所有启用 profileId 的 pending 状态。
2. 按 priority 和 oldestQueuedAt 选择目标 profile。
3. 优先复用已在目标 profile 的 VM。
4. 如需要切换，选择兼容且空闲的 VM。
5. 每轮最多启动一次 VM 切换。

需要实现的内容：
- PoolSchedulerService 或 PoolScheduler。
- RunOneCycleAsync(ct) 便于测试。
- 调用 SchedulerClient.QueryPendingAsync(profileId)。
- 使用 WorkerStateEvaluator 选择候选 VM。
- 调用 VmCoordinator/SwitchService 执行切换。
- 无任务时只刷新/上报状态，不切换。

候选 VM 必须满足：
- 支持目标 profile。
- 未隔离。
- 无活跃事务。
- runner 状态 Runnable。
- 当前 profile 队列为空。
- idle 时间达到 IdleStableSeconds。

本步骤不要实现：
- 本机运维 API。
- P1 自动恢复增强。

测试要求：
- 无 pending 任务时不切换。
- 有 pending 任务时选择兼容 VM。
- 同一轮最多切换一个 VM。
- Running VM 不会被选择。
- 当前 profile 仍有 pending 时不会抢占。
- 优先级高的 profile 先被处理。

验收标准：
- 调度器不直接操作 vmrun。
- 调度器通过 VmSwitchService 发起切换。
- 调度器不按任务数计算目标容量。
```

## 开发提示词 13：Agent 心跳、能力上报与当前状态上报后台服务

```text
请实现 P0 第 13 步：后台上报服务。

先阅读 docs/worker-agent-design.md：
- 第 14.2 上报 Agent 心跳
- 第 14.3 上报 VM profile 能力
- 第 14.4 上报 VM 当前运行状态

本步骤目标：
实现 Agent 启动后对云后台的基础上报：
1. Agent heartbeat。
2. VM profile capability。
3. VM current status。

需要实现的内容：
- HeartbeatBackgroundService。
- CapabilityReportService 或在启动校验后触发能力上报。
- VmStatusReportService。
- 使用 SchedulerClient 上报。
- 使用 LocalStore 或内存状态提供 VM 当前状态。
- 上报失败要记录日志，但不能导致 Agent 进程崩溃。

上报时机：
- 启动完成配置校验后上报 capability。
- 按 HeartbeatIntervalSeconds 上报 heartbeat。
- VM 状态变化后上报 current status。
- P0 可使用周期上报 current status，状态变化即时上报可留到后续优化，但接口和服务边界要预留。

测试要求：
- heartbeat 定时调用 SchedulerClient。
- capability 上报包含所有 VM 和 profiles。
- VM status 上报包含 currentProfileId/currentSnapshotName/runnerStatusCode。
- SchedulerClient 抛异常时服务记录失败并继续运行。

验收标准：
- 云后台可看到静态能力和当前运行状态所需数据。
- 上报逻辑不直接读取 appsettings，应通过配置模型和状态模型。
```

## 开发提示词 14：P0 集成验收与清理

```text
请实现 P0 第 14 步：集成验收、测试补齐和清理。

先阅读 docs/worker-agent-design.md：
- 第 24 节 P0 必须实现
- 第 25.1 节 技术验收
- 第 26 节 最终执行闭环

本步骤目标：
确保 P0 从配置加载到单轮调度、停止 runner、目录备份、vmrun 切换、状态上报的基础闭环可测试。

需要完成：
1. 增加集成级测试，使用 fake 依赖，不连接真实 VM。
2. 覆盖成功闭环：
   - 配置加载。
   - 快照校验。
   - pending profile 有任务。
   - VM Runnable 且空闲。
   - kill runner 成功。
   - cache/db/file/logs 备份成功。
   - vmrun stop/revert/start 成功。
   - runner ready。
   - 状态上报成功。
3. 覆盖失败闭环：
   - runner Running 时不切换。
   - kill 返回 WORKER_RUNNING 时不切换。
   - 目录备份失败时不回滚。
   - vmrun revert 失败时事务失败并标记 VM 异常。
4. 清理命名：
   - 代码中不得出现 executor-control、18080、/executor/health、/worker/status。
   - 应使用 rpa-runner 9090、/api/robot/start/status、RunnerKillUrl。
5. 清理 Git：
   - 不提交 .vs、bin、obj。
   - 不提交本机临时数据库。

验收标准：
- P0 相关测试全部通过。
- 设计文档中的 P0 必须实现项都有对应模块或测试覆盖。
- 没有提前实现 P1/P2。
- 输出最终测试命令和结果。
```

## 手工提交建议

建议每个开发提示词完成后单独提交一次：

```text
Prompt 01 -> commit: Build worker agent project skeleton
Prompt 02 -> commit: Add configuration model and validation
Prompt 03 -> commit: Add domain states and error codes
Prompt 04 -> commit: Add worker state evaluator
Prompt 05 -> commit: Add SQLite local store
Prompt 06 -> commit: Add vmrun command service
Prompt 07 -> commit: Add scheduler client contracts
Prompt 08 -> commit: Add runner control client
Prompt 09 -> commit: Add VM directory backup service
Prompt 10 -> commit: Add startup validation and capability model
Prompt 11 -> commit: Add VM switch orchestration
Prompt 12 -> commit: Add pool scheduler cycle
Prompt 13 -> commit: Add reporting background services
Prompt 14 -> commit: Complete P0 integration validation
```

## P0 完成定义

P0 完成时应满足：

- 能加载多 VM、多 profile 配置。
- 能校验基础快照和定制快照存在。
- 能按 profileId 查询待执行任务。
- 能读取 VM 内 rpa-runner 9090 状态。
- Running / Upgrading 时不切换。
- 能通过 RunnerKillUrl 停止空闲 runner。
- 能复制 cache/db/file/logs 到 `{work-path}/{vm-name}/{yyyyMMddHHmmss}/`。
- 能生成 `backup_manifest.json`。
- 能执行 vmrun stop/revert/start。
- 能等待 runner Runnable 或 Running。
- 能持久化本地事务。
- 能上报心跳、能力、VM 当前状态、切换记录、目录备份结果。
- 不启动 runner。
- 不执行 RPA 任务。
- 不管理 lease。
