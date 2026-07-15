# Profile 快照更新流程

本文档描述当前代码中“更新某个 VM 的某个 Profile 快照”的完整逻辑。

核心实现：

- API/UI：`Core/Operations/OperationsApiExtensions.cs`
- 编排服务：`Core/Snapshot/SnapshotUpdateService.cs`
- 快照匹配：`Core/Snapshot/ProfileSnapshotResolver.cs`
- VMware 操作：`Core/Vmware/VmrunService.cs`
- VM/Profile 本地登记：`Core/Storage/SqliteVirtualMachineRegistry.cs`

## 1. 入口

用户页面：

```http
GET /vms
```

页面上的 Profile 编辑区提供 `Upgrade Snapshot` 按钮。点击后调用实际更新接口：

```http
POST /operations/snapshots/{vmName}/{profileId}/update
```

示例：

```http
POST /operations/snapshots/SR20-2606-POC1/rpa-sh-tax-etax/update
X-Api-Key: local-secret
```

如果配置了 `OperationsApi.ApiKey`，请求必须携带：

```http
X-Api-Key: {apiKey}
```

或：

```http
POST /operations/snapshots/{vmName}/{profileId}/update?apiKey={apiKey}
```

API Key 不匹配时直接返回 `401 Unauthorized`，不会进入快照更新逻辑。

## 2. 数据来源

当前 VM/Profile 维护已改为使用本地 SQLite 登记，运行时配置会从本地 registry 加载到 `WorkerAgentOptions.VirtualMachines`。

Profile 快照名不再以 `appsettings.json` 作为主维护点。更新成功后会回写：

```text
local_vm_profile.snapshot_name = newSnapshotName
```

同时也会更新当前进程内的：

```text
profile.SnapshotName = newSnapshotName
```

这样 `/vms` 页面可以看到 Profile 对应的最新 Snapshot 名称。

## 3. 快照命名规则

每个 Profile 在同一台 VM 内只允许有一个有效快照，通过约定命名匹配：

```text
{ProfileId}.v{yyMMdd}.{sequence}
```

示例：

```text
rpa-sh-tax-etax.v260707.1
rpa-sh-tax-etax.v260707.2
```

匹配规则：

- 快照名必须以 `ProfileId.v` 开头。
- 日期为 6 位 `yyMMdd`。
- 序号为正整数。
- 同一个 VM + Profile 正常情况下只能匹配到一个当前有效快照。

生成新快照名时，会读取当前 VM 的全部快照，找到同 Profile、同日期的最大序号，再加 `1`。

## 4. 总体流程

```text
1. 查找启用的 VM
2. 查找 VM 下的 Profile
3. 获取 VM 操作锁
4. vmrun listSnapshots
5. 通过 ProfileId 解析当前 Profile 对应的旧快照
6. 判断是否可以走快速路径
7. 如果不能快速更新，则走安全校验路径
8. vmrun stop soft
9. vmrun listSnapshots
10. 生成新快照名
11. vmrun snapshot 创建新快照
12. vmrun deleteSnapshot 删除旧快照
13. vmrun start nogui 自动启动 VM
14. 回写 local_vm_profile.snapshot_name
15. 返回成功
```

成功响应：

```json
{
  "success": true,
  "newSnapshotName": "rpa-sh-tax-etax.v260707.2",
  "errorCode": null,
  "errorMessage": null,
  "step": "done"
}
```

失败响应示例：

```json
{
  "success": false,
  "newSnapshotName": null,
  "errorCode": "RUNNER_NOT_READY",
  "errorMessage": "Runner status is Closed after VM start.",
  "step": "check-status"
}
```

## 5. 快速路径

最新逻辑增加了快速路径，用于避免当前 VM 已经处于目标 Profile 且 runner 空闲时，还重复执行 `revertToSnapshot`。

触发条件必须同时满足：

1. `GetCurrentSnapshotAsync(vm.VmxPath)` 读取到的当前 VM 快照名匹配目标 `profileId`。
2. `GetRunnerStatusAsync(vm)` 返回空闲。
3. runner 接口语义里的 `runnerStatus=0` 在代码中会映射为 `RunnerStatusCode.Runnable`。

满足条件时，直接执行：

```text
Stop -> Create Snapshot -> Delete Old Snapshot -> Start
```

对应动作顺序：

```text
list-snapshots
get-status
vmrun-stop
list-snapshots
vmrun-create
vmrun-delete
vmrun-start
```

快速路径不会执行：

```text
vmrun revertToSnapshot
vmrun start before validation
启动后等待 runner ready
```

如果读取当前快照失败、当前快照不匹配目标 Profile、runner 状态读取失败、runner 不是 `Runnable`，都会自动回退到安全校验路径。

## 6. 安全校验路径

当快速路径条件不满足时，使用原有安全路径：

```text
Revert -> Start -> Wait -> Check Runner -> Stop -> Create -> Delete -> Start
```

详细步骤：

1. `vmrun revertToSnapshot {vmxPath} {currentSnapshotName}`
2. `vmrun start {vmxPath} nogui`
3. 等待 `2 分钟`
4. 检查 runner 状态，最多 `30 次`
5. 每次检查间隔 `10 秒`
6. runner 状态为 `Runnable` 时继续
7. 多次检查后仍未 ready，则失败

注意：`Closed`、`Offline` 等非 ready 状态不会立即失败，会继续重试，直到达到最大次数。

## 7. Runner 状态判定

`GuestWorkerClient` 当前读取 VM 内 runner 状态接口后，会将接口返回值映射为内部状态：

```text
接口 data = 0 -> RunnerStatusCode.Runnable
接口 data != 0 -> RunnerStatusCode.Running
```

快照更新中的使用方式：

| 场景 | 允许状态 |
| --- | --- |
| 快速路径 | 只允许 `Runnable` |
| 安全路径启动后校验 | 只允许 `Runnable` |

快速路径只允许 `Runnable`，是为了确认 VM 当前已经在目标 Profile 且没有任务执行，可以直接停机制作新快照。

安全路径也只允许 `Runnable`，是为了避免 runner 仍在执行任务时停机制作新快照。`Running` 会继续重试；达到最大次数后仍为 `Running`，返回 `RUNNER_NOT_READY`。

## 8. 创建与删除快照

VM 停止成功后重新读取快照列表：

```text
vmrun listSnapshots {vmxPath}
```

然后生成新快照名：

```text
SnapshotNameGenerator.Generate(profileId, today, existingSnapshots)
```

创建新快照：

```text
vmrun snapshot {vmxPath} {newSnapshotName}
```

删除旧快照：

```text
vmrun deleteSnapshot {vmxPath} {currentSnapshotName}
```

如果删除旧快照失败，服务会尝试删除刚创建的新快照作为回滚：

```text
vmrun deleteSnapshot {vmxPath} {newSnapshotName}
```

如果回滚删除也失败，错误信息会同时包含旧快照删除失败和新快照回滚删除失败的信息。

## 9. 更新后自动启动 VM

新快照创建成功并删除旧快照后，会自动启动 VM：

```text
vmrun start {vmxPath} nogui
```

如果这里失败：

```text
ErrorCode = VM_START_FAILED
Step = start-after-update
```

只有更新后启动成功，才会继续回写 Profile 的 `snapshot_name`。

## 10. 回写 Profile 快照名

快照更新成功后执行两层回写：

```text
profile.SnapshotName = newSnapshotName
local_vm_profile.snapshot_name = newSnapshotName
```

本地 SQLite 更新通过：

```text
IVirtualMachineRegistry.UpdateProfileSnapshotAsync(vmName, profileId, newSnapshotName)
```

这样 `/vms` 页面中 Profile 列表可以显示最新 Snapshot 名称。

## 11. 与 Profile 切换流程的关系

Profile 切换成功后也会更新：

```text
local_vm_profile.snapshot_name
```

原因是切换时会根据 `ProfileId` 解析 VM 上实时匹配到的 Snapshot 名称。切换成功说明该 VM 已经切到目标 Profile 对应快照，因此需要把该 Snapshot 名同步到本地 Profile 记录。

两类成功都会保持 DB 里的 Profile 快照名最新：

| 场景 | 更新内容 |
| --- | --- |
| Profile 切换成功 | 当前使用的目标 Profile Snapshot |
| Snapshot 更新成功 | 新创建的 Profile Snapshot |

## 12. 失败分支

| 步骤 | 失败场景 | ErrorCode | Step |
| --- | --- | --- | --- |
| 查找 VM | 找不到启用 VM | `VM_NOT_FOUND` | `lookup` |
| 查找 Profile | VM 下找不到 Profile | `PROFILE_NOT_FOUND` | `lookup` |
| 读取快照列表 | `listSnapshots` 异常 | `SNAPSHOT_NOT_FOUND` | `list-snapshots` |
| 解析 Profile 快照 | 没有匹配快照 | `SNAPSHOT_NOT_FOUND` | `resolve-snapshot` |
| 解析 Profile 快照 | 匹配到多个快照 | `SNAPSHOT_AMBIGUOUS` | `resolve-snapshot` |
| 回滚快照 | `revertToSnapshot` 异常 | `SNAPSHOT_REVERT_FAILED` | `revert` |
| 启动 VM | 前置启动失败 | `VM_START_FAILED` | `start` |
| 查询 Runner | 多次查询都异常 | `RUNNER_STATUS_CHECK_FAILED` | `check-status` |
| 校验 Runner | 重试后仍未 ready | `RUNNER_NOT_READY` | `check-status` |
| 停止 VM | `stop soft` 异常 | `VM_STOP_FAILED` | `stop` |
| 创建快照 | `snapshot` 异常 | `SNAPSHOT_CREATE_FAILED` | `create-snapshot` |
| 删除旧快照 | `deleteSnapshot` 异常 | `SNAPSHOT_DELETE_FAILED` | `delete-snapshot` |
| 更新后启动 VM | `start nogui` 异常 | `VM_START_FAILED` | `start-after-update` |

`OperationCanceledException` 不会被转换为业务失败结果，会继续向上抛出，用于响应请求取消或服务停止。

## 13. 日志点

当前流程会记录关键交互与结果，包括：

- 快照更新开始。
- 获取 VM 操作锁。
- `listSnapshots`。
- Profile 快照解析结果。
- 当前 VM 快照读取。
- 快速路径判断结果。
- runner 状态检查与重试。
- `revertToSnapshot`、`start`、`stop`、`snapshot`、`deleteSnapshot`。
- 新快照名生成。
- 更新后启动 VM。
- 回写 Profile Snapshot。
- 失败步骤、错误码、错误信息。

底层 `VmrunService` 会记录每个 VMware 命令：

- Command
- Arguments
- ExitCode
- ElapsedMs
- Stdout
- Stderr

## 14. 当前约束

1. 每个 VM/Profile 通过约定命名匹配 Snapshot，不再要求 `BaseSnapshotName == VM Name`。
2. 同一 VM 内一个 Profile 正常只应保留一个有效 Snapshot。
3. 快速路径只在当前 VM 快照已经匹配目标 Profile 且 runner 空闲时生效。
4. 更新快照期间会持有 VM 操作锁，避免同一 VM 并发切换或更新。
5. 更新成功后会自动启动 VM。
6. 更新成功后会同步更新 `local_vm_profile.snapshot_name`。
7. 如果创建新快照成功但删除旧快照失败，会尽量删除新快照回滚；回滚失败时需要人工检查 VMware 快照树。
