# VM 健康与磁盘维护设计

## 1. 背景与目标

RPA Worker Agent 长期运行时，磁盘增长主要来自以下来源：

1. 宿主机 `Agent.HostWorkPath` 下累积的 VM 日志备份 ZIP；
2. Guest 内备份过程中产生的临时目录和 ZIP；
3. Guest 日志、缓存和临时文件；
4. VMware 稀疏虚拟磁盘随写入增长；
5. VMware 快照增量磁盘长期增长。

本设计在现有 VM 健康模块基础上增加定期维护能力，目标是：

- 自动删除宿主机上超过 50 天的备份文件；
- 每晚在 VM 确认空闲后执行 Guest 清理、安全关机、离线检查和恢复启动；
- 避免维护任务与调度、快照切换、画像升级互相干扰；
- 维护失败时尽可能恢复 VM 可用状态，并留下可诊断记录；
- 不直接删除未知 VMDK、快照链或 VMware 状态文件。

本文档只描述设计，不包含代码修改。

## 2. 当前实现与约束

### 2.1 当前备份位置

`LogBackupService` 当前将 Guest 中的 `cache,db,file,logs` 压缩并复制到：

```text
{HostWorkPath}\backup\{VmName}\{yyyyMMdd}\{yyyyMMddHHmmss}_{ProfileId}.zip
```

备份过程还会在 Guest 中生成：

```text
{GuestWorkPath}\{timestampTag}\
{GuestWorkPath}\{timestampTag}.zip
```

当前成功复制到宿主机后，没有删除上述 Guest 临时目录和 ZIP。这些文件应纳入每日 Guest 清理，否则 Guest 磁盘会持续增长。

### 2.2 当前并发控制

项目已有 `IVmOperationLock`，快照切换和画像升级都按 `VmxPath` 获取同一把锁。健康检测会对每台启用 VM 调用统一的 `IVmPowerOnService`，确保 VMware 电源和 Runner 均可用。磁盘维护实现必须复用维护锁，并从空闲确认开始一直持有到维护结束。

### 2.3 `vmrun` 能力边界

本机 VMware Workstation 提供的 `vmrun 1.17.0 build-24832109` 支持：

- VM 启停；
- 快照列举、创建、删除和恢复；
- 在运行中的 Guest 内执行程序和删除 Guest 文件。

该版本 `vmrun` **没有虚拟磁盘 shrink/compact 命令**。虚拟磁盘离线收缩由 VMware Workstation 同目录下的工具执行：

```text
vmware-vdiskmanager.exe -k <disk.vmdk>
```

因此，需求中的“关机后调用 vmrun 清理”需要拆分为：

1. 关机前通过 `vmrun runProgramInGuest` 执行 Guest 文件清理；
2. 关机通过 `vmrun stop ... soft` 完成；
3. 关机后的磁盘检查或收缩通过 `vmware-vdiskmanager.exe` 完成；
4. 重新启动通过 `vmrun start` 完成。

### 2.4 快照限制

本项目使用 Base Snapshot 和每个 Profile 的画像快照。VMware 官方说明，有快照时不能直接执行普通虚拟磁盘收缩；快照合并也可能需要较大的额外宿主机空间。

所以：

- 每晚维护不得自动删除 Base Snapshot 或有效 Profile Snapshot；
- 不得直接删除 `*-00000x.vmdk`、`.vmsn`、`.vmsd`、`.vmem` 或 `.lck`；
- 检测到 VM 存在快照时，默认跳过 `vmware-vdiskmanager -k`；
- 快照整理必须作为独立、人工批准的维护流程，不属于本设计的自动每日任务。

参考：

- [Broadcom：Defragmenting and shrinking VMware Workstation virtual machine disks](https://knowledge.broadcom.com/external/article/315631/defragmenting-and-shrinking-vmware-works.html)
- [Broadcom：Consolidating snapshots in VMware Workstation](https://knowledge.broadcom.com/external/article/340664/consolidating-snapshots-in-vmware-workst.html)

## 3. 总体方案

新增一个独立的 VM 维护编排服务，由健康模块定时触发，但不把清理逻辑直接写入 `VmHealthCheckService`。

```text
VmHealthCoordinator
├── VmHealthCheckService
│   └── 停机超过阈值自动启动
└── VmMaintenanceService
    ├── HostBackupRetentionService
    ├── GuestCleanupService
    ├── VmOfflineMaintenanceService
    └── VmMaintenanceStore
```

职责划分：

- `HostBackupRetentionService`：清理宿主机超过 50 天的备份；
- `GuestCleanupService`：关机前通过 VMware Tools 清理 Guest 临时文件；
- `VmOfflineMaintenanceService`：安全关机、离线检查、条件式磁盘收缩、重新启动；
- `VmMaintenanceStore`：持久化计划、阶段、结果和下次重试时间；
- `VmMaintenanceService`：检查维护窗口和空闲条件，串联完整流程。

## 4. 宿主机备份清理

### 4.1 默认策略

- 每天执行一次，建议在 VM 维护窗口之前执行；
- 删除超过 50 天的备份 ZIP；
- 每台 VM 无论日期如何，至少保留最近 3 个成功 ZIP；
- 单次任务设置最大删除文件数和最大删除容量；
- 先输出候选清单，再逐文件删除；
- 删除空日期目录，但不删除 VM 根目录；
- 清理失败不影响 VM 调度和启动。

### 4.2 清理范围

唯一允许的根目录：

```text
{HostWorkPath}\backup
```

仅允许删除满足全部条件的文件：

1. 文件解析后的完整路径仍位于上述根目录内；
2. 扩展名为 `.zip`；
3. 路径结构符合 `backup/{VmName}/{yyyyMMdd}/{file}.zip`；
4. 文件日期早于当前时间 50 天；
5. 不属于每台 VM 最近 3 个备份；
6. 文件当前没有被其他进程占用。

`agent.db`、日志目录、VMX/VMDK 目录、安装目录和任意未识别文件都不在清理范围内。

### 4.3 日期判定

优先使用路径中的 `yyyyMMdd` 和文件名中的 `yyyyMMddHHmmss`。解析失败时不删除，仅记录警告。不能单独依赖 `LastWriteTime`，因为文件复制、恢复或迁移可能改变该时间。

“超过 50 天”定义为：

```text
backupTimestamp < now - 50 days
```

所有比较使用本机配置时区，持久化记录使用 UTC。

## 5. 每晚 VM 维护流程

### 5.1 调度方式

默认维护窗口建议为本地时间 `02:00-05:00`。多台 VM 使用稳定散列错峰，例如根据 `HostId + VmName` 分配窗口内的分钟，避免同时关机、压缩和启动造成宿主机 IO 峰值。

每日每台 VM 最多成功执行一次。任务状态必须持久化，Agent 重启后不能重复执行已完成的当日维护。

### 5.2 准入条件

VM 必须同时满足：

- `VirtualMachines[].Enabled == true`；
- 当前时间位于维护窗口；
- 当日尚未成功维护；
- VM 未被隔离；
- 没有未完成的 Switch Transaction；
- 能获取该 VM 的 `IVmOperationLock`；
- VM 当前正在运行且 VMware Tools 可用；
- Runner 状态为 `Runnable`；
- Runner 无当前任务；
- 连续空闲达到配置阈值，建议至少 30 分钟；
- Scheduler 查询确认当前 Profile 无待处理任务；
- 如 Scheduler 不可访问，默认跳过维护，不以“查询失败”等同于“没有任务”；
- 宿主机 VM 所在磁盘具有安全余量。

取得操作锁之后必须重新检查以上动态条件，防止检查与执行之间出现新任务。

### 5.3 状态机

```text
SCHEDULED
  -> LOCK_ACQUIRED
  -> IDLE_REVALIDATED
  -> GUEST_CLEANUP_DONE
  -> VM_STOPPED
  -> DISK_CHECK_DONE
  -> DISK_COMPACT_DONE | DISK_COMPACT_SKIPPED
  -> VM_STARTED
  -> WORKER_READY
  -> SUCCESS

任一步骤失败
  -> RECOVERY_START_ATTEMPTED
  -> FAILED_RECOVERED | FAILED_VM_OFFLINE | NEED_MANUAL_CHECK
```

状态必须逐步持久化，便于 Agent 异常退出后判断 VM 是正常停机、维护中停机，还是意外停机。

### 5.4 详细流程

#### 阶段 A：关机前 Guest 清理

通过现有 `vmrun runProgramInGuest` 执行固定、版本化的 PowerShell 脚本，而不是动态接受任意删除路径。

第一版只清理：

- `GuestWorkPath` 下备份成功且超过 1 天的 `{timestampTag}.zip`；
- `GuestWorkPath` 下超过 1 天的备份暂存目录；
- 明确白名单中的日志和缓存文件；
- Windows 用户临时目录中超过保留期的普通临时文件，可配置关闭。

不自动清理：

- `GuestWorkPath\db`；
- 当前正在写入的日志；
- Windows、Program Files、用户配置根目录；
- 任意盘符根目录；
- 未在白名单中的路径。

脚本输出 JSON 结果：清理前后可用空间、删除文件数、释放字节数、跳过文件数和错误列表。Guest 清理失败时默认中止离线维护并保持 VM 运行。

#### 阶段 B：安全关机

1. 再次确认 Runner 仍为 `Runnable` 且无任务；
2. 必要时调用现有 Runner stop/kill 协议，使业务进程停止写入；
3. 执行 `vmrun stop <vmx> soft`；
4. 按现有软关机超时轮询 `IsVmRunningAsync`；
5. 默认不为普通磁盘维护使用 hard stop；
6. 软关机超时则取消本次磁盘维护，并尝试恢复 Runner/VM 状态。

#### 阶段 C：离线检查与条件式收缩

关机后先执行只读检查：

- VMX 和其引用的 VMDK 文件存在；
- VM 确认未运行；
- VM 目录不存在活动锁；
- `vmrun listSnapshots` 结果与配置的 Base/Profile 快照一致；
- 宿主机剩余空间达到安全阈值；
- 可选执行 `vmware-vdiskmanager -e <disk.vmdk>` 检查磁盘链一致性。

`vmware-vdiskmanager -k` 的默认策略：

- 默认关闭，不作为每日强制动作；
- VM 存在任何快照时跳过；
- 仅支持已验证可收缩的 growable/sparse 磁盘；
- 建议改为每月或达到宿主机空间阈值时执行，而不是每晚执行；
- 每次只处理一台 VM、一个磁盘，限制最长运行时间；
- 命令失败时不修改或删除任何 VMDK 文件，转入恢复启动。

原因是磁盘收缩属于重 IO 操作，频繁运行会增加维护时间和宿主机存储压力，而本项目的快照模型通常会使收缩条件不成立。

#### 阶段 D：恢复启动与健康验证

无论离线检查或收缩成功与否，只要 VM 文件仍可用，都应进入恢复启动：

1. 执行 `vmrun start`，沿用 `Vmrun.DefaultStartNoGui`；
2. 等待 `IsVmRunningAsync == true`；
3. 等待 VMware Tools/Guest IP 可用；
4. 等待 Runner 状态进入 `Runnable` 或 `Running`；
5. 校验 WorkerId 和当前 Profile 未发生变化；
6. 更新维护结果并释放 VM 操作锁。

启动失败按退避策略重试，例如 30 秒、60 秒、120 秒，共 3 次。仍失败则：

- 标记 `FAILED_VM_OFFLINE`；
- VM 状态置为 `ERROR` 或维护隔离；
- 记录明确错误码和最后一步；
- 触发告警；
- 交由现有停机自动启动健康检查继续兜底，但健康检查应识别维护状态，避免维护任务尚未结束时提前启动 VM。

## 6. 与现有健康检查的协调

当前 `VmHealthCheckService` 会在 VM 停机超过 30 分钟后自动启动。新增维护后需要增加“维护租约”概念：

```text
VmName
MaintenanceId
Status
LeaseExpiresAt
UpdatedAt
```

规则：

- 维护任务持有 VM 操作锁和有效租约时，自动启动检查只记录状态，不抢占启动；
- 租约必须有最大期限，例如 2 小时，防止 Agent 崩溃后永久抑制自动恢复；
- 租约过期且 VM 仍关机时，健康检查恢复自动启动；
- 服务启动时优先恢复未完成的维护事务，再运行普通调度；
- 维护成功启动 VM 后清除租约和停机计时。

## 7. 配置设计

建议新增：

```json
{
  "Maintenance": {
    "Enabled": true,
    "TimeZoneId": "China Standard Time",
    "WindowStart": "02:00",
    "WindowEnd": "05:00",
    "MinimumIdleMinutes": 30,
    "MaintenanceLeaseMinutes": 120,
    "HostBackup": {
      "Enabled": true,
      "RetentionDays": 50,
      "MinimumCopiesPerVm": 3,
      "MaxDeleteFilesPerRun": 1000,
      "MaxDeleteGbPerRun": 100,
      "DryRun": false
    },
    "GuestCleanup": {
      "Enabled": true,
      "BackupArtifactRetentionDays": 1,
      "LogRetentionDays": 14,
      "CacheRetentionDays": 7,
      "WindowsTempCleanupEnabled": false,
      "MaxDeleteGbPerRun": 20
    },
    "OfflineDisk": {
      "ConsistencyCheckEnabled": true,
      "CompactEnabled": false,
      "CompactSchedule": "Monthly",
      "SkipWhenSnapshotsExist": true,
      "MinimumHostFreeSpaceGb": 100,
      "CommandTimeoutMinutes": 120
    }
  }
}
```

每台 VM 可增加：

```json
{
  "Name": "SR20-2606-POC1",
  "MaintenanceEnabled": true,
  "MaintenanceWindowOffsetMinutes": 0,
  "GuestCleanupPaths": [
    {
      "RelativePath": "logs",
      "RetentionDays": 14,
      "Pattern": "*.log"
    },
    {
      "RelativePath": "cache",
      "RetentionDays": 7,
      "Pattern": "*"
    }
  ]
}
```

Guest 路径优先使用相对于 `GuestWorkPath` 的路径，禁止普通配置直接提供任意绝对删除路径。

## 8. 持久化设计

建议增加 `local_vm_maintenance` 表：

```text
maintenance_id
host_id
vm_name
maintenance_date
status
current_step
lease_expires_at
guest_free_bytes_before
guest_free_bytes_after
host_free_bytes_before
host_free_bytes_after
deleted_file_count
deleted_bytes
compact_skipped_reason
error_code
error_message
started_at
updated_at
finished_at
```

宿主机备份清理可以共用该表，或者增加 `local_cleanup_history` 记录扫描、候选、删除和跳过数量。

## 9. 错误码建议

```text
MAINTENANCE_WINDOW_EXPIRED
MAINTENANCE_VM_NOT_IDLE
MAINTENANCE_PENDING_TASKS
MAINTENANCE_SCHEDULER_UNAVAILABLE
GUEST_CLEANUP_FAILED
VM_MAINTENANCE_STOP_FAILED
VM_DISK_CHAIN_INVALID
VM_DISK_COMPACT_SKIPPED_SNAPSHOTS
VM_DISK_COMPACT_FAILED
VM_MAINTENANCE_START_FAILED
VM_MAINTENANCE_READY_TIMEOUT
HOST_BACKUP_CLEANUP_FAILED
```

“不满足条件”和“执行失败”必须区分。空闲不足、存在待处理任务、存在快照而跳过 compact 都是正常跳过，不应把 VM 标记为错误。

## 10. 可观测性与告警

每次维护至少记录：

- VM 名称、维护 ID、维护窗口；
- 准入检查结果和跳过原因；
- Guest/宿主机清理前后可用空间；
- 删除文件数和释放容量；
- VM 停止、离线检查、启动和 Ready 各阶段耗时；
- 磁盘检查/收缩命令、退出码和脱敏输出；
- 最终状态和错误码。

需要告警的情况：

- VM 在维护后无法启动；
- 磁盘链检查失败；
- 宿主机剩余空间低于安全阈值；
- 连续多日因非业务原因无法维护；
- 本地备份清理连续失败；
- 维护租约过期但事务未结束。

## 11. 安全要求

1. 所有宿主机删除操作先做 `Path.GetFullPath`，并验证仍位于 `{HostWorkPath}\backup` 下；
2. 禁止符号链接、junction 或 reparse point 逃逸清理根目录；
3. Guest 清理脚本只接受服务端生成的白名单配置；
4. 禁止删除盘符根目录和关键系统目录；
5. 禁止直接删除任何 VMware 虚拟磁盘、快照和状态文件；
6. 第一版上线时先运行至少 7 天 `DryRun`，核对候选清单后再允许实际删除；
7. 维护前必须保证宿主机有足够空间，不能在磁盘接近满载时贸然合并快照或转换磁盘；
8. 密码和 Guest 凭据不得写入维护日志或命令诊断输出。

## 12. 代码改动范围

预计新增：

```text
Core/Maintenance/IVmMaintenanceService.cs
Core/Maintenance/VmMaintenanceService.cs
Core/Maintenance/IHostBackupRetentionService.cs
Core/Maintenance/HostBackupRetentionService.cs
Core/Maintenance/IGuestCleanupService.cs
Core/Maintenance/GuestCleanupService.cs
Core/Maintenance/IVmOfflineMaintenanceService.cs
Core/Maintenance/VmOfflineMaintenanceService.cs
Core/Maintenance/VmMaintenanceResult.cs
Core/Configuration/MaintenanceOptions.cs
Core/Domain/VmMaintenanceTransaction.cs
```

预计修改：

```text
WorkerAgent.cs
Core/Health/VmHealthCheckService.cs
Core/ServiceCollectionExtensions.cs
Core/Vmware/IVmrunService.cs
Core/Vmware/VmrunService.cs
Core/Configuration/WorkerAgentOptions.cs
Core/Configuration/VirtualMachineOptions.cs
Core/Configuration/WorkerAgentOptionsValidator.cs
Core/Storage/ILocalStore.cs
Core/Storage/LocalStore.cs
Core/Storage/SqliteVirtualMachineRegistry.cs
appsettings.json
```

如果增加 Operations API 的手动触发、DryRun 预览、结果查询和页面展示，还会涉及 `Core/Operations/OperationsApiExtensions.cs`。

## 13. 测试范围

### 13.1 宿主机清理

- 49、50、51 天边界；
- 每台 VM 最近 3 份保护；
- 解析失败文件不删除；
- 路径穿越、junction/reparse point 拒绝；
- 文件占用、部分删除失败可继续；
- 单次文件数和容量上限；
- DryRun 不产生删除。

### 13.2 维护准入

- VM Disabled、隔离、Runner Running、存在任务时跳过；
- Scheduler 不可用时跳过；
- 锁内二次检查发现新任务时退出；
- 多 VM 错峰且同一 VM 每日仅执行一次。

### 13.3 VM 生命周期

- Guest 清理成功后 soft stop、检查、start、Ready；
- soft stop 超时不执行离线磁盘动作；
- 快照存在时跳过 compact；
- 磁盘检查失败仍尝试启动；
- 启动失败按退避重试并进入正确错误状态；
- Agent 在 VM 停机后崩溃，重启可恢复事务并启动 VM；
- 健康检测通过 `IVmPowerOnService` 执行可用性检查和必要的电源恢复；磁盘清理实现仍需自行管理维护租约。

### 13.4 真机验证

自动化测试之外，必须使用非生产 VM 验证：

- 当前 VMware Workstation 版本的 `vmrun` 和 `vmware-vdiskmanager` 参数；
- 有/无快照、稀疏/预分配、单文件/分片 VMDK；
- 收缩前后 VM 可启动、快照可恢复、WorkerId/Profile 正确；
- 宿主机突然重启或服务终止后的恢复能力。

## 14. 推荐实施阶段

### 第一阶段：低风险稳定性改进

- 宿主机超过 50 天备份清理；
- Guest 备份临时目录和 ZIP 清理；
- 维护窗口、空闲准入、操作锁、持久化状态；
- soft stop 后重新启动并验证 Runner Ready；
- DryRun、日志和告警；
- 不执行 VMDK compact。

### 第二阶段：离线健康检查

- VMDK 引用解析；
- `vmware-vdiskmanager -e` 一致性检查；
- 宿主机空间阈值与维护结果展示；
- Agent 重启后的未完成维护恢复。

### 第三阶段：受控磁盘收缩

- 仅对无快照且磁盘类型满足条件的 VM 开放；
- 默认按月或空间阈值触发；
- 先在非生产 VM 验证；
- 独立开关，默认关闭；
- 不与每日健康维护绑定为必做步骤。

## 15. 验收标准

1. 超过 50 天且不属于保护集合的备份能够自动删除；
2. 清理逻辑无法越过配置的备份根目录；
3. VM 有任务、正在升级或切换时不会进入维护；
4. 正常维护结束后 VM 自动启动并通过 Runner Ready 校验；
5. 离线步骤失败时仍优先恢复 VM 在线；
6. Agent 中途重启后能够识别并恢复停机中的维护事务；
7. 快照存在时不会执行 VMDK 收缩，也不会直接删除快照文件；
8. 每次维护可查询清理容量、步骤耗时、最终状态和失败原因；
9. 多台 VM 不同时执行高 IO 的离线维护；
10. 维护功能关闭时，不改变现有调度、切换和 VM 可用性健康恢复行为。
