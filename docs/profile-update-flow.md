# Profile 快照更新流程

## 概述

Profile 快照更新用于把某个 VM 上某个 profile 当前配置的快照滚动成一个新版本快照。流程会先还原到当前快照，启动 VM 并确认 Runner 可用，然后停机创建新快照，删除旧快照，最后把 `appsettings.json` 中该 profile 的 `SnapshotName` 更新为新快照名。

该流程由 `SnapshotUpdateService` 实现，当前是手动触发能力，不会被调度循环自动触发。

---

## 一、什么时候触发

当前只有 Operations API 会触发快照更新：

```http
POST /operations/snapshots/{vmName}/{profileId}/update
```

触发条件：

- 服务已启动，并注册了 Operations API。
- 调用方发起上述 `POST` 请求。
- 如果配置了 `OperationsApi.ApiKey`，请求必须携带正确的 API Key。

API Key 支持两种传递方式：

```http
X-Api-Key: {apiKey}
```

或：

```http
POST /operations/snapshots/{vmName}/{profileId}/update?apiKey={apiKey}
```

如果 API Key 不匹配，接口直接返回 `401 Unauthorized`，不会进入快照更新逻辑。

---

## 二、如何触发

启动时 `Program` 会调用 `app.MapOperationsApi()` 注册接口。接口定义位于：

```text
Core/Operations/OperationsApiExtensions.cs
```

调用链如下：

```text
HTTP POST /operations/snapshots/{vmName}/{profileId}/update
  -> OperationsApiExtensions
  -> ISnapshotUpdateService.UpdateSnapshotAsync(vmName, profileId)
  -> SnapshotUpdateService.UpdateSnapshotAsync
```

服务注册位于：

```text
Core/ServiceCollectionExtensions.cs
```

```csharp
services.AddSingleton<ISnapshotUpdateService, SnapshotUpdateService>();
```

请求示例：

```http
POST /operations/snapshots/SR20-2026-6HQ8/rpa-sh-tax-etax/update
X-Api-Key: local-secret
```

成功时返回 `200 OK`：

```json
{
  "success": true,
  "newSnapshotName": "rpa-sh-tax-etax.v260702.1",
  "errorCode": null,
  "errorMessage": null,
  "step": "done"
}
```

失败时返回 `422 Unprocessable Entity`：

```json
{
  "success": false,
  "newSnapshotName": null,
  "errorCode": "RUNNER_NOT_READY",
  "errorMessage": "Runner status is Closed after VM start.",
  "step": "check-status"
}
```

---

## 三、处理过程

核心实现位于：

```text
Core/Snapshot/SnapshotUpdateService.cs
```

完整成功流程如下：

```text
1. 查找 VM 配置
2. 查找 profile 配置
3. 读取当前 profile.SnapshotName 作为旧快照名
4. vmrun revertToSnapshot，还原到旧快照
5. vmrun start nogui，启动 VM
6. 等待 1 分钟
7. 调用 Guest Worker 状态接口，检查 Runner 状态
8. Runner 为 Runnable 或 Running 时继续
9. vmrun stop soft，软停机
10. vmrun listSnapshots，读取现有快照列表
11. 生成新快照名
12. vmrun snapshot，创建新快照
13. vmrun deleteSnapshot，删除旧快照
14. 更新 appsettings.json 中对应 profile 的 SnapshotName
15. 返回成功结果
```

测试中断言的成功步骤顺序为：

```text
vmrun-revert
vmrun-start
get-status
vmrun-stop
list-snapshots
vmrun-create
vmrun-delete
config-update
```

---

## 四、关键步骤说明

### 4.1 查找 VM 和 Profile

服务首先从 `WorkerAgentOptions.VirtualMachines` 中按 `vmName` 查找 VM。

如果找不到 VM，返回：

```text
ErrorCode = VM_NOT_FOUND
Step = lookup
```

找到 VM 后，再从该 VM 的 `Profiles` 中按 `profileId` 查找 profile。

如果找不到 profile，返回：

```text
ErrorCode = PROFILE_NOT_FOUND
Step = lookup
```

### 4.2 还原当前快照

取当前配置的 `profile.SnapshotName` 作为旧快照名：

```text
currentSnapshotName = profile.SnapshotName
```

然后执行：

```text
vmrun revertToSnapshot {vm.VmxPath} {currentSnapshotName}
```

失败时返回：

```text
ErrorCode = SNAPSHOT_REVERT_FAILED
Step = revert
```

### 4.3 启动 VM 并检查 Runner

还原成功后，执行：

```text
vmrun start {vm.VmxPath} nogui
```

失败时返回：

```text
ErrorCode = VM_START_FAILED
Step = start
```

启动后固定等待 1 分钟，然后调用 Guest Worker 状态接口：

```text
IGuestWorkerClient.GetRunnerStatusAsync(vm)
```

只有以下状态会继续：

| Runner 状态 | 处理 |
| --- | --- |
| `Runnable` | 继续更新 |
| `Running` | 继续更新 |

其他状态会失败：

```text
ErrorCode = RUNNER_NOT_READY
Step = check-status
```

如果状态接口调用异常，返回：

```text
ErrorCode = RUNNER_STATUS_CHECK_FAILED
Step = check-status
```

### 4.4 停止 VM

Runner 可用后，执行软停机：

```text
vmrun stop {vm.VmxPath} soft
```

失败时返回：

```text
ErrorCode = VM_STOP_FAILED
Step = stop
```

### 4.5 生成新快照名

停机成功后，先读取已有快照：

```text
vmrun listSnapshots {vm.VmxPath}
```

然后按以下格式生成新快照名：

```text
{profileId}.v{yyMMdd}.{sequence}
```

示例：

```text
rpa-sh-tax-etax.v260702.1
rpa-sh-tax-etax.v260702.2
```

序号规则：

- 只统计同一个 `profileId`、同一天日期前缀的快照。
- 如果当天没有同 profile 快照，序号为 `1`。
- 如果已有同 profile 当天快照，取最大序号加 `1`。

例如现有快照：

```text
rpa-sh-tax-etax.v260702.1
rpa-sh-tax-etax.v260702.3
rpa-sh-tax-etax.v260701.9
other-profile.v260702.8
```

新快照名会是：

```text
rpa-sh-tax-etax.v260702.4
```

### 4.6 创建新快照

执行：

```text
vmrun snapshot {vm.VmxPath} {newSnapshotName}
```

失败时返回：

```text
ErrorCode = SNAPSHOT_CREATE_FAILED
Step = create-snapshot
```

### 4.7 删除旧快照

新快照创建成功后，删除旧快照：

```text
vmrun deleteSnapshot {vm.VmxPath} {currentSnapshotName}
```

失败时返回：

```text
ErrorCode = SNAPSHOT_DELETE_FAILED
Step = delete-snapshot
```

注意：如果删除旧快照失败，新快照已经创建成功，但配置文件还不会更新到新快照名。

### 4.8 更新配置文件

最后更新 `appsettings.json`：

```text
VirtualMachines[]
  -> 匹配 Name 或 VmName = vmName
  -> Profiles[] 或 Workers[]
  -> 匹配 ProfileId = profileId
  -> SnapshotName = newSnapshotName
```

配置更新由 `ConfigFileUpdater` 完成，支持两种配置结构：

- `VirtualMachines[].Profiles[]`
- `VirtualMachines[].Workers[]`

失败时返回：

```text
ErrorCode = CONFIG_UPDATE_FAILED
Step = update-config
```

成功时返回：

```text
Success = true
NewSnapshotName = {newSnapshotName}
Step = done
```

---

## 五、失败分支汇总

| 步骤 | 失败场景 | ErrorCode | Step |
| --- | --- | --- | --- |
| 查找 VM | 配置中找不到 `vmName` | `VM_NOT_FOUND` | `lookup` |
| 查找 profile | VM 中找不到 `profileId` | `PROFILE_NOT_FOUND` | `lookup` |
| 还原快照 | `revertToSnapshot` 异常 | `SNAPSHOT_REVERT_FAILED` | `revert` |
| 启动 VM | `start` 异常 | `VM_START_FAILED` | `start` |
| 查询 Runner | 状态接口异常 | `RUNNER_STATUS_CHECK_FAILED` | `check-status` |
| 检查 Runner | 状态不是 `Runnable` 或 `Running` | `RUNNER_NOT_READY` | `check-status` |
| 停止 VM | `stop soft` 异常 | `VM_STOP_FAILED` | `stop` |
| 创建快照 | `snapshot` 异常 | `SNAPSHOT_CREATE_FAILED` | `create-snapshot` |
| 删除旧快照 | `deleteSnapshot` 异常 | `SNAPSHOT_DELETE_FAILED` | `delete-snapshot` |
| 更新配置 | 写入 `appsettings.json` 异常 | `CONFIG_UPDATE_FAILED` | `update-config` |

---

## 六、与 Profile 切换流程的关系

Profile 切换流程使用的是配置里的：

```text
VirtualMachines[].Profiles[].SnapshotName
```

快照更新成功后，会把该字段写成新快照名。这样下一次服务启动读取配置后，后续 profile 切换会使用新的快照。

需要注意：当前 `WorkerAgentOptions` 是启动时绑定为单例的内存对象。`SnapshotUpdateService` 更新的是 `appsettings.json` 文件，不会自动刷新当前进程内的 `WorkerAgentOptions`。

因此，快照更新成功后：

- 配置文件中的 `SnapshotName` 已经更新。
- 当前运行进程内的配置对象可能仍是旧值。
- 如果希望调度、能力上报、启动校验立即使用新快照，建议重启 Worker Agent 服务。

---

## 七、当前实现注意事项

1. 当前流程是手动 API 触发，不会自动按时间或任务状态触发。
2. 当前没有显式并发锁，同一个 VM/profile 重复调用可能产生快照创建、删除、配置写入竞争。
3. Runner 状态检查只检查一次，且是在启动后固定等待 1 分钟后检查；不会像 profile 切换流程那样循环等待。
4. 删除旧快照发生在创建新快照之后，配置写入之前。
5. 配置写入失败时，新快照可能已经创建，旧快照可能已经删除，需要人工确认快照与配置是否一致。
6. `OperationCanceledException` 不会被转换为业务失败结果，会继续向上抛出，用于响应请求取消或服务停止。
