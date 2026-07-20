# Profile 快照切换流程

## 1. 目标

将指定 VM 切换到目标 Profile 快照，同时保证：

- 只有 Runner 正常且空闲时才执行快照切换；
- VM 或 Runner 异常时，本次只执行恢复，不继续切换；
- VM 必须确认关机后才能恢复快照；
- VM 开机后先写入调度 Token，再确认 Runner 状态；
- 手动切换、自动切换和快照升级不能并发操作同一 VM。

核心实现：

```text
Core/Switching/VmSwitchService.cs
Core/Scheduling/PoolSchedulerService.cs
Core/Operations/OperationsApiExtensions.cs
Core/Operations/VmPowerOnService.cs
Core/Snapshot/SnapshotUpdateService.cs
Core/Coordination/VmOperationCoordinator.cs
Core/Storage/LocalStore.cs
```

`VmPowerOnService.cs` 内分为两层：

- `IVmPowerRecoveryService`：无锁的统一开机/蓝屏恢复核心，由页面 API 和快照切换共同调用；
- `IVmPowerOnService`：页面 API 编排层，负责查找 VM、获取 `IVmOperationLock` 和 `ManualPowerOn` 租约。

快照切换已经持有 `ProfileSwitch` 租约，因此直接调用恢复核心，不会嵌套申请 `ManualPowerOn` 租约。

## 2. 完整流程

```text
取得 VM 进程锁
  -> 取得持久化 ProfileSwitch 租约
  -> 创建切换事务
  -> 检查 VM 电源和 Runner
       ├─ VM 已关机：启动 VM + Runner 恢复响应 -> 跳过本次切换
       ├─ Runner 短探测失败：硬关机 + 启动 VM + Runner 恢复响应 -> 跳过本次切换
       ├─ Runner 非正常闲时：直接跳过本次切换
       └─ Runner Runnable 且 currentTaskId 为空：继续
  -> 停止 Runner
  -> 备份 Guest 数据
  -> VM 软关机并确认停止
       └─ 配置允许时，软关机超时后执行硬关机并再次确认
  -> 等待 VMware 文件锁释放
  -> 恢复目标快照（瞬时失败最多重试 3 次）
  -> 启动 VM
  -> 等待 VMware Tools 可执行 Guest 操作并写入 Token
  -> 等待 Runner 正常
  -> 校验 workerId 和 profileId
  -> 更新本地状态及快照注册信息
  -> 完成切换事务
```

## 3. 前置检查与跳过语义

### 3.1 VM 处于关机状态

统一恢复服务调用 `vmrun start`，先通过 `vmrun list` 确认 VM 已进入运行状态，再等待 Runner 在 `ManualPowerOnRunnerReadyTimeoutSeconds` 内恢复响应。两项都成功后结束本次切换，不停止 Runner、不备份、不恢复快照。

如果 VMware 电源状态已运行但 Runner 未恢复，返回 `VM_RUNNER_READY_TIMEOUT`，本次切换失败，不会错误地返回“开机成功”。

返回结果：

```json
{
  "success": true,
  "skipped": true,
  "errorCode": "SWITCH_SKIPPED_VM_STARTED"
}
```

### 3.2 Runner 无法请求

切换前置步骤与页面“开机”按钮统一调用 `IVmPowerRecoveryService.EnsureOperationalAsync`。Runner/Guest-IP 探测使用 `ManualPowerOnRunnerProbeTimeoutSeconds` 短超时；请求失败或超时后执行硬关机加开机。重启受持久化重启预算和冷却时间保护。

硬关机最多执行两次，每次都通过 `vmrun list` 确认停止；启动最多执行 `ManualPowerOnStartMaxAttempts` 次。启动后还必须等待 Runner 恢复响应，才返回 `Restarted`。随后结束本次切换，不继续恢复快照。

返回 `SWITCH_SKIPPED_VM_RESTARTED`。

### 3.3 Runner 非正常闲时

只有同时满足以下条件才可继续：

```text
status 请求成功
RunnerStatusCode == Runnable
currentTaskId == null
```

Running、Upgrading、Closed、Offline、错误状态或仍有任务时均直接跳过，返回 `SWITCH_SKIPPED_RUNNER_NOT_IDLE`，且不会调用停止 Runner、备份或任何 VM 电源操作。

`Skipped` 是已正确处理的非切换结果，因此 `Success=true`；自动调度会将其视为 `SwitchStarted=false`，不会误报切换完成。

## 4. 正常切换

### 4.1 停止 Runner 和备份

调用 Runner kill 接口。kill 失败或返回 `currentTaskId` 时终止流程。随后备份配置的 Guest 目录；备份失败时，除非明确启用 `ForceRevertWhenBackupFailed`，否则不关机、不恢复快照。

### 4.2 确认 VM 关机

调用软关机后持续通过 `vmrun list` 判断 VM 是否仍运行。只有确认停止后才允许恢复快照。若启用 `Vmrun.AllowHardStopAfterSoftTimeout`，软关机超时后可执行一次硬关机，并再次等待确认；仍未停止则返回 `VM_STOP_FAILED`。

### 4.3 恢复快照并启动

VM 停止后等待短暂稳定时间和 `.lck` 释放，然后调用 `revertToSnapshot`。恢复完成后等待 `VmPostRevertStabilizationSeconds`，再启动 VM。

### 4.4 Token 写入

VM 启动命令成功后立即调用 `IGuestTokenProvisioningService`。该服务会等待 VMware Tools Guest Operations 可用，然后把 `rpa.token` 写入 Guest 的 `application.properties` 并回读验证。

Token 写入失败时切换失败，不进入 Runner 状态确认。

### 4.5 Runner 确认

Token 成功后轮询 Runner。Runner 必须进入正常可用状态，并且返回的 `workerId`、`profileId` 必须与目标配置一致。未就绪或身份不一致时切换失败，不再自动执行第二次冷重启。

## 5. 并发与事务保护

同一 VM 使用两层保护：

1. `IVmOperationLock`：按规范化后的 `VmxPath` 提供进程内互斥；
2. `local_vm_operation`：按 `hostId + vmName` 提供持久化租约、心跳和 fencing token。

入口规则：

- 自动切换在读取快照列表前取得 VM 锁；
- 手动切换同样在读取快照列表前取得 VM 锁；
- `VmSwitchService` 取得 `ProfileSwitch` 持久化租约；
- `SnapshotUpdateService` 取得 `SnapshotUpdate` 持久化租约并使用同一 VM 锁；
- 租约冲突返回 `VM_OPERATION_BUSY`。

因此：

- 手动切换不能与自动切换同时操作同一 VM；
- 切换期间不能升级同一 VM 的快照；
- 不同 VM 仍可独立执行；
- 服务异常退出后，只有租约过期才能接管；旧操作的 fencing token 无法更新新事务。

## 6. 关键配置

| 配置 | 作用 |
| --- | --- |
| `ManualPowerOnRunnerProbeTimeoutSeconds` | 页面开机与切换前置步骤共用的 Runner/Guest-IP 探测超时 |
| `ManualPowerOnStartMaxAttempts` | 页面开机与切换前置步骤共用的 VM 启动最大尝试次数 |
| `ManualPowerOnRunnerReadyTimeoutSeconds` | VM 启动后等待 Runner 恢复响应的总超时 |
| `VmPowerCycleStopTimeoutSeconds` | 前置恢复关机及重新运行确认超时 |
| `VmPostStopStabilizationSeconds` | VM 停止后的稳定等待 |
| `VmPostRevertStabilizationSeconds` | 恢复快照后的稳定等待 |
| `WaitVmReadyTimeoutSeconds` | Token 写入后等待 Runner 的超时 |
| `VmOperationLeaseSeconds` | VM 操作租约有效期 |
| `VmOperationHeartbeatSeconds` | 租约心跳间隔 |
| `VmRecoveryWindowMinutes` | 重启预算统计窗口 |
| `VmMaxPowerCyclesPerWindow` | 窗口内最大恢复重启次数 |
| `VmRecoveryCooldownMinutes` | 两次恢复重启之间的冷却 |

## 7. 事务结果

- 真正完成快照切换：`Success=true, Skipped=false`；
- 前置恢复或非闲时跳过：`Success=true, Skipped=true`；
- 外部操作失败或校验失败：`Success=false, Skipped=false`。

跳过事务以 `SUCCESS` 终止，但保留明确的 reason code 和 step，避免被当作未完成事务重复恢复。

## 8. VM 编辑页“开机”操作

`/vms` 的 VM 编辑框提供“开机”按钮，请求 `POST /operations/vms/{vmName}/power-on`：

- VM 已关机：启动、确认进入运行状态并确认 Runner 恢复，返回 `action=started`；
- VM 已运行且 Runner 请求正常：不改变电源状态，返回 `action=skipped`；
- VM 已运行但 Runner 请求异常：硬关机并确认停止，再启动并确认运行及 Runner 恢复，返回 `action=restarted`。

该操作使用同一 `IVmOperationLock` 和 `ManualPowerOn` 持久化租约，因此不能与切换、快照升级或其他 VM 维护操作并发。

针对蓝屏或 VMware Tools 卡死场景，“开机”操作使用独立的 Runner 探测超时，不会长期阻塞在 `getGuestIPAddress`：

- `ManualPowerOnRunnerProbeTimeoutSeconds`：Runner/Guest-IP 探测超时，默认5秒；
- 硬关机命令最多尝试2次，每次通过 `vmrun list` 确认 VM 已停止；
- 停止成功后等待 `VmPostStopStabilizationSeconds`，降低残留 VMX/VMDK 锁导致的启动失败；
- `ManualPowerOnStartMaxAttempts`：启动最大尝试次数，默认3次；
- 每次启动后都通过 `vmrun list` 确认 VM 真正进入运行状态。
- `ManualPowerOnRunnerReadyTimeoutSeconds`：电源启动后等待 Runner 恢复响应的总超时，默认180秒。

`vmrun list` 出现 VMX 只表示 VMware 虚拟机进程已运行，不代表 Windows Guest 和 Runner 已恢复。因此 `started` 和 `restarted` 必须同时满足：VM 位于运行列表，并且 Runner 在总超时内重新响应。若只有电源状态成功但 Runner 未恢复，返回 `VM_RUNNER_READY_TIMEOUT`，不会显示启动或重启成功。

若最终失败，接口会在错误消息中保留最后一次 `vmrun` 异常，便于排查服务账号权限、VM 文件锁或 `vmware-vmx.exe` 卡死。
