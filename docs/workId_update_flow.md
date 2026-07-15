# WorkerId 更新快照流程

本文说明“Rename WorkerId”功能的执行逻辑、数据来源、写入保障机制和常见失败原因。

核心实现：
- API/UI：`Core/Operations/OperationsApiExtensions.cs`
- 编排服务：`Core/Snapshot/InitFileUpdateService.cs`
- 快照匹配：`Core/Snapshot/ProfileSnapshotResolver.cs`
- VMware 操作：`Core/Vmware/VmrunService.cs`
- VM/Profile 本地登记：`Core/Storage/SqliteVirtualMachineRegistry.cs`

## 1. 功能目标

当一台 VM 的 `WorkerId` 发生变化后，需要把该 VM 下所有 profile 快照里的 guest 文件同步更新：

```text
C:\Program Files\rpa\rpa.init
```

文件内容应等于当前 VM 配置中的：

```text
VirtualMachineOptions.WorkerId
```

更新成功后，每个 profile 都会生成一个新的版本化快照，并尽量删除旧快照。

## 2. 入口

用户页面：

```http
GET /vms
```

页面 VM 编辑区提供按钮：

```text
Rename WorkerId
```

点击后调用：

```http
POST /operations/vms/{vmName}/update-init-workerid
```

示例：

```http
POST /operations/vms/SR20-2606-POC1/update-init-workerid
X-Api-Key: local-secret
```

如果配置了 `OperationsApi.ApiKey`，请求必须携带 `X-Api-Key` 或 `?apiKey={apiKey}`。

## 3. 数据来源

该流程遍历的是运行时 `WorkerAgentOptions.VirtualMachines` 中的 VM 和 profile。

当前代码启动时会从本地 SQLite registry 读取 VM/Profile 配置，然后放入 `WorkerAgentOptions.VirtualMachines`。也就是说：
- VM/Profile 的持久化来源是本地 SQLite。
- `appsettings.json` 不是运行时 VM/Profile 的直接来源。
- 页面保存 VM/Profile 后会写入 SQLite，但当前进程内的 `WorkerAgentOptions` 不会自动刷新。
- 保存后需要重启服务，`Rename WorkerId` 才会使用最新 VM/Profile/WorkerId 配置。

对应表：

```text
local_vm_config.worker_id
local_vm_profile.profile_id
local_vm_profile.snapshot_name
```

## 4. 快照匹配规则

每个 profile 的当前快照通过 `ProfileId` 匹配 VMware 快照名，格式必须是：

```text
{ProfileId}.v{yyMMdd}.{sequence}
```

示例：

```text
General.v260714.1
SuZhou-CA.v260714.1
```

匹配要求：
- 同一个 VM + profile 必须能匹配到且只匹配到一个快照。
- 匹配不到会返回 `SNAPSHOT_NOT_FOUND`。
- 匹配到多个会被视为歧义，当前 WorkerId 更新流程会按未 ready 处理。

## 5. 总体流程

对目标 VM 下的每个 profile 依次执行：

```text
1. vmrun listSnapshots
2. 根据 ProfileId 解析当前 profile 快照
3. vmrun stop soft 停机，确保 VMware 释放 VMX 锁
4. vmrun revertToSnapshot 回滚到该 profile 快照
5. vmrun start nogui 启动 VM
6. 等待 VMware Tools guest 操作可用
7. 直接写入并校验 C:\Program Files\rpa\rpa.init
8. vmrun stop soft 停机，必要时 hard stop 兜底
9. vmrun listSnapshots
10. 生成新快照名
11. vmrun snapshot 创建新快照
12. vmrun deleteSnapshot 删除旧快照
13. 回写 local_vm_profile.snapshot_name
```

所有 profile 都成功时，接口返回 `success=true`。只要有任一 profile 失败，接口整体返回 `PARTIAL_FAILURE`，并在 `profiles` 中列出每个 profile 的结果。

## 6. rpa.init 写入保障机制

为了避免 `robot.exe` 占用 `rpa.init` 导致 WorkerId 写入失败，当前实现采用“先尝试、后处理占用、再校验”的策略。

### 6.1 第一次直接写入

服务先不停止 `robot.exe`，直接在 guest 内执行 PowerShell：

```text
[System.IO.File]::WriteAllText(target, expectedWorkerId, UTF8)
```

写入后立即读回：

```text
[System.IO.File]::ReadAllText(target, UTF8).TrimEnd("\r", "\n")
```

只有读回内容完全等于目标 `WorkerId`，才认为写入成功。

### 6.2 失败后停止 robot.exe

如果直接写入失败，或读回校验不一致，服务才会停止：

```text
robot.exe
```

当前只处理 `robot.exe`，不会停止 `java.exe`。

停止命令：

```text
taskkill /F /IM robot.exe
```

随后通过 `vmrun listProcessesInGuest` 轮询，直到 guest 进程列表中不再出现 `robot.exe`。

### 6.3 临时文件替换后再次校验

确认 `robot.exe` 已退出后，服务使用临时文件写入并替换目标文件：

```text
target = C:\Program Files\rpa\rpa.init
tmp = C:\Program Files\rpa\rpa.init.tmp
```

执行逻辑：

```text
WriteAllText(tmp, expectedWorkerId, UTF8)
Move-Item tmp target -Force
ReadAllText(target, UTF8)
```

再次读回校验成功后，才继续停机并创建新快照。

### 6.4 写入失败时的处理

如果两次写入或校验仍失败：

```text
ErrorCode = WRITE_INIT_FAILED
```

服务会尝试停止 VM，并且不会创建新快照，避免把错误 WorkerId 固化进新快照。

## 7. 快照生成与回写

写入 `rpa.init` 成功后，服务停机并重新读取快照列表：

```text
vmrun listSnapshots {vmxPath}
```

新快照名由 `SnapshotNameGenerator` 生成：

```text
{ProfileId}.v{todayYYMMDD}.{nextSequence}
```

创建成功后尝试删除旧快照：

```text
vmrun deleteSnapshot {vmxPath} {oldSnapshotName}
```

删除旧快照失败只记录 warning，不会让该 profile 更新失败，因为新快照已经包含正确的 `WorkerId`。

最后回写：

```text
local_vm_profile.snapshot_name = newSnapshotName
```

## 8. 成功响应

示例：

```json
{
  "success": true,
  "errorCode": null,
  "errorMessage": null,
  "profiles": [
    {
      "profileId": "General",
      "success": true,
      "errorCode": null,
      "errorMessage": null,
      "oldSnapshotName": "General.v260714.1",
      "newSnapshotName": "General.v260714.2"
    }
  ]
}
```

## 9. 失败响应

示例：

```json
{
  "success": false,
  "errorCode": "PARTIAL_FAILURE",
  "errorMessage": "1 profile(s) failed.",
  "profiles": [
    {
      "profileId": "SuZhou-CA",
      "success": false,
      "errorCode": "WRITE_INIT_FAILED",
      "errorMessage": "Direct write failed: ...; retry after stopping robot.exe failed: ..."
    }
  ]
}
```

常见 profile 失败码：

| ErrorCode | 含义 |
| --- | --- |
| `LIST_SNAPSHOTS_FAILED` | `vmrun listSnapshots` 失败 |
| `SNAPSHOT_NOT_FOUND` | 未找到唯一可用的 profile 快照 |
| `SNAPSHOT_REVERT_FAILED` | 回滚快照失败 |
| `VM_START_FAILED` | 启动 VM 失败 |
| `GUEST_OPERATIONS_TIMEOUT` | 等待 VMware Tools guest 操作可用超时 |
| `WRITE_INIT_FAILED` | 写入或读回校验 `rpa.init` 失败 |
| `SNAPSHOT_CREATE_FAILED` | 创建新快照失败 |

## 10. 排障要点

### 10.1 页面点击无反应

检查浏览器控制台是否存在 JavaScript 语法错误，并确认请求是否发到：

```text
POST /operations/vms/{vmName}/update-init-workerid
```

### 10.2 返回 VM_NOT_FOUND

说明运行时 `WorkerAgentOptions.VirtualMachines` 中没有该 VM。常见原因：
- VM 只写在 `appsettings.json`，未写入 SQLite registry。
- 页面刚保存 VM，但服务尚未重启。
- VM 名大小写或空格不一致。

### 10.3 返回 NO_PROFILES

说明本地 SQLite 中该 VM 没有 profile，或服务启动时没有加载到 profile。保存 profile 后需要重启服务。

### 10.4 返回 SNAPSHOT_NOT_FOUND

检查 VMware 快照名是否符合：

```text
{ProfileId}.v{yyMMdd}.{sequence}
```

并确认同一 profile 不存在多个匹配快照。

### 10.5 VMware 提示无法独占锁定配置文件

如果 VMware Workstation 弹窗提示：

```text
以独占方式锁定此配置文件失败。另一个正在运行的 VMware 进程可能正在使用配置文件。
```

说明 `.vmx` 配置文件仍被 VMware 进程占用。常见原因：
- 该 VM 正在 VMware Workstation UI 中打开或运行。
- 上一次 `vmrun start/stop/revert` 尚未完全结束。
- 存在残留的 `vmware-vmx.exe` 进程。
- VM 目录下存在异常残留的 `*.lck` 锁目录。
- 同一 VM 被人工操作、Agent 调度、快照更新同时操作。

处理建议：
- 关闭 VMware Workstation 中该 VM 的窗口或标签页。
- 确认该 VM 已完全关机，而不是挂起或正在关机。
- 在任务管理器中检查是否仍有对应 VM 的 `vmware-vmx.exe`。
- 确认没有其它 Agent/脚本正在操作同一个 `.vmx`。
- 只有在确认没有 VMware 进程使用该 VM 后，才清理 VM 目录下的残留 `*.lck`。

当前 WorkerId 更新流程已在回滚快照前增加停机步骤，用于降低运行中 VM 导致的 VMX 锁冲突。

### 10.6 返回 GUEST_OPERATIONS_TIMEOUT

说明启动 VM 后，`vmrun listProcessesInGuest` 在超时时间内一直无法成功返回。需要检查：
- VMware Tools 是否已启动并可用。
- guest 账号密码是否正确。
- VM 是否完成系统启动。
- 当前 guest 用户是否允许执行 VMware guest operations。

### 10.7 返回 WRITE_INIT_FAILED

优先检查：
- guest 用户是否有权限写入 `C:\Program Files\rpa\rpa.init`。
- `rpa.init` 是否被安全软件、权限策略或其他进程锁定。
- `taskkill /F /IM robot.exe` 是否可由当前 guest 用户执行。
- `robot.exe` 是否会被守护进程立即拉起，导致文件再次被占用。

如果是权限问题，单纯停止进程无法解决，需要提升 guest 执行权限，或在 guest 内安装专用 helper/service 来负责更新 `rpa.init`。

## 11. 运维注意事项

- 该流程会逐个 profile 回滚、启动、停机并重建快照，耗时较长。
- 执行期间会持有 VM 操作锁，避免和 profile 切换、快照更新等操作并发冲突。
- 更新成功后 VM 会停留在停机状态，等待后续调度或人工启动。
- 删除旧快照失败不会阻断成功结果，但需要后续清理旧快照，避免同一 profile 出现多个匹配快照。
- 修改 VM/Profile/WorkerId 后，应重启 Worker Agent 服务再执行本流程。
