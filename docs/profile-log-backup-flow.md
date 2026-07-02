# 环境画像日志备份处理流程

## 概述

环境画像日志备份是在 profile 切换过程中执行的一步保护动作。它会在切换 VM 快照前，把 Guest 系统内当前环境画像相关的运行数据目录压缩成 zip，并复制回宿主机保存。

该流程由 `LogBackupService` 实现，当前不是独立 API，也不是定时任务，而是由 `VmSwitchService` 在 profile 切换事务中自动调用。

备份目录固定为：

```text
db
logs
cache
file
```

这些目录由 VM 配置的 `GuestBackupPaths` 字符串指定，默认值为 `cache,db,file,logs`。实际备份源目录位于 VM 的 `GuestWorkPath` 下，宿主机备份输出根目录位于 Agent 的 `HostWorkPath` 下。

---

## 一、什么时候触发

日志备份只在 profile 切换流程中触发。

触发链路如下：

```text
WorkerAgent 调度轮询
  -> PoolSchedulerService.RunOneCycleAsync
  -> 找到需要切换 profile 的 VM
  -> VmSwitchService.SwitchAsync
  -> 停止 Guest Runner
  -> LogBackupService.BackupAsync
```

也就是说，只有当调度器决定某台 VM 需要从当前 profile 切换到目标 profile 时，才会执行日志备份。

日志备份发生在：

```text
Kill Runner 成功之后
VM stop / revertToSnapshot / start 之前
```

这样可以尽量保证备份时 Runner 已经停止写入业务数据，同时 VM 还没有被还原到目标快照。

---

## 二、如何触发

当前没有单独的日志备份接口。外部不能直接通过 Operations API 触发 `LogBackupService`。

实际触发点在：

```text
Core/Switching/VmSwitchService.cs
```

核心调用：

```csharp
var backup = await _logBackupService.BackupAsync(
    request.Vm,
    tx,
    request.Timestamp,
    cancellationToken);
```

服务注册位于：

```text
Core/ServiceCollectionExtensions.cs
```

```csharp
services.AddSingleton<ILogBackupService, LogBackupService>();
```

`LogBackupService` 依赖 `IVmrunService` 执行两个关键动作：

- 在 Guest 中运行 PowerShell 压缩脚本。
- 把 Guest 中生成的 zip 文件复制回宿主机。

---

## 三、处理过程

核心实现位于：

```text
Core/Backup/LogBackupService.cs
```

完整成功流程如下：

```text
1. 计算宿主机备份目录
2. 创建宿主机备份目录
3. 生成本次备份的 timestampTag
4. 生成 Guest 侧 zip 路径
5. 生成宿主机侧临时 PowerShell 脚本路径
6. 在 Guest 中执行压缩脚本
7. 从 Guest 复制 zip 到宿主机
8. 统计 zip 文件大小
9. 删除宿主机侧临时 PowerShell 脚本
10. 返回 LogBackupResult
```

在 profile 切换整体流程中的位置如下：

```text
CREATED
  -> 查询 Runner 状态
  -> Kill Runner
  -> STOP_RUNNER_DONE
  -> 日志备份
  -> LOG_BACKUP_DONE
  -> VM stop
  -> revertToSnapshot
  -> VM start
  -> 等待 Runner Ready
  -> SUCCESS
```

---

## 四、备份路径规则

### 4.1 宿主机备份目录

当前代码中的宿主机目标目录为：

```text
{HostWorkPath}/{VmName}/{yyyyMMdd}
```

示例：

```text
D:\seebot\SR20-2026-6HQ8\20260702
```

- `HostWorkPath` 来自 `Agent.HostWorkPath`
- `VmName` 来自 `VirtualMachines[].Name`
- `yyyyMMdd` 来自当前切换事务时间

### 4.2 备份文件名

备份文件名由时间戳和来源 profile 组成：

```text
{yyyyMMddHHmmss}_{fromProfileId}.zip
```

示例：

```text
20260702153045_rpa-sh-tax-etax.zip
```

其中 `fromProfileId` 来自切换事务的 `FromProfileId`，表示切换前的环境画像。

### 4.3 Guest 侧 zip 路径

Guest 中的 zip 文件生成在：

```text
{GuestWorkPath}\{yyyyMMddHHmmss}_{fromProfileId}.zip
```

示例：

```text
C:\seebot\20260702153045_rpa-sh-tax-etax.zip
```

### 4.4 Guest 侧临时目录

压缩前会在 Guest 中创建临时目录：

```text
{GuestWorkPath}\{yyyyMMddHHmmss}_{fromProfileId}
```

脚本会先把 `db`、`logs`、`cache`、`file` 四类目录复制到该临时目录下，再压缩整个临时目录。

---

## 五、Guest 压缩逻辑

`LogBackupService` 会动态生成一段 PowerShell 脚本，并通过 vmrun 在 Guest 内执行：

```text
C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe
  -NonInteractive
  -EncodedCommand {base64}
```

脚本执行逻辑：

```text
1. 设置 ErrorActionPreference = Stop
2. 定义 zipPath
3. 定义 timestampPath
4. 根据 `GuestBackupPaths` 定义需要备份的源目录，默认：
   - {GuestWorkPath}\cache
   - {GuestWorkPath}\db
   - {GuestWorkPath}\file
   - {GuestWorkPath}\logs
5. 如果 timestampPath 已存在，先删除
6. 创建 timestampPath
7. 遍历四个源目录
8. 源目录不存在时跳过
9. 源目录存在时复制其内容到 timestampPath 下同名子目录
10. 如果四个目录都不存在或都未复制，exit 1
11. 如果 zipPath 已存在，先删除
12. Compress-Archive 生成 zip
```

目录复制后的结构大致如下：

```text
{timestampPath}
  db
  logs
  cache
  file
```

然后压缩为：

```text
{GuestWorkPath}\{timestampTag}.zip
```

---

## 六、复制回宿主机

Guest 压缩成功后，执行：

```text
vmrun CopyFileFromGuestToHost
```

复制方向：

```text
Guest:
{GuestWorkPath}\{timestampTag}.zip

Host:
{HostWorkPath}\{VmName}\{yyyyMMdd}\{timestampTag}.zip
```

复制完成后，如果宿主机 zip 文件存在，会读取文件大小并写入 `LogBackupResult.TotalBytes`。

当前实现里 `FileCount` 的语义是 zip 文件数量：

```text
成功 = 1
失败 = 0
```

---

## 七、结果对象

备份返回 `LogBackupResult`：

```csharp
public sealed class LogBackupResult
{
    public string TxId { get; set; }
    public string TargetPath { get; set; }
    public IReadOnlyList<string> Directories { get; set; }
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
    public bool Success { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}
```

成功时：

```text
Success = true
ErrorCode = null
ErrorMessage = null
FileCount = 1
TotalBytes = zip 文件大小
Directories = GuestBackupPaths 解析后的目录名列表
```

失败时：

```text
Success = false
ErrorCode = LOG_BACKUP_FAILED
ErrorMessage = 异常消息
FileCount = 0
TotalBytes = 0
Directories = GuestBackupPaths 解析后的目录名列表
```

`OperationCanceledException` 不会被转换成失败结果，会继续向上抛出，用于响应请求取消或服务停止。

---

## 八、失败处理

### 8.1 备份内部失败

以下任一情况都会导致 `LogBackupResult.Success = false`：

- Guest PowerShell 脚本执行失败。
- 四个源目录都不存在，脚本 `exit 1`。
- Guest 压缩失败。
- Guest zip 复制到宿主机失败。
- 宿主机临时脚本写入失败。
- 其他非取消类异常。

失败结果：

```text
ErrorCode = LOG_BACKUP_FAILED
ErrorMessage = exception.Message
```

### 8.2 对 profile 切换的影响

`VmSwitchService` 收到备份结果后，会根据配置决定是否继续切换。

如果：

```text
backup.Success = false
Agent.ForceRevertWhenBackupFailed = false
```

则 profile 切换立即失败：

```text
SwitchTransaction.Status = FAILED
ErrorCode = LOG_BACKUP_FAILED
```

后续不会执行：

```text
vmrun stop
vmrun revertToSnapshot
vmrun start
```

如果：

```text
backup.Success = false
Agent.ForceRevertWhenBackupFailed = true
```

则仍然会继续后续 VM 停机、快照还原、启动流程。事务会进入：

```text
Status = LOG_BACKUP_DONE
Step = log-backup-done
ErrorCode = LOG_BACKUP_FAILED
ErrorMessage = 备份失败原因
```

也就是说，`LOG_BACKUP_DONE` 不一定表示备份成功，它也可能表示“备份失败但允许继续切换”。

---

## 九、与切换事务的关系

日志备份使用 `SwitchTransaction` 中的信息生成备份上下文：

| 字段 | 用途 |
| --- | --- |
| `TransactionId` | 写入 `LogBackupResult.TxId` |
| `FromProfileId` | 拼接备份文件名 |
| `FromSnapshotName` | 表示备份来源快照 |
| `TargetProfileId` | 表示切换目标 profile |
| `TargetSnapshotName` | 表示切换目标快照 |
| `FirstTaskId` | 表示触发切换的首个任务 |

备份完成后，切换事务会更新到：

```text
SwitchTransactionStatus.LOG_BACKUP_DONE
Step = log-backup-done
```

如果备份失败且不允许强制继续，则事务会更新到：

```text
SwitchTransactionStatus.FAILED
Step = failed
ErrorCode = LOG_BACKUP_FAILED
```

---

## 十、当前实现注意事项

1. 当前备份流程没有独立 API，只能通过 profile 切换间接触发。
2. 当前通过 `VirtualMachines[].GuestBackupPaths` 配置备份目录名，默认 `cache,db,file,logs`。
3. Guest 中不存在的目录会被跳过；只有四个目录全部没有成功复制时才失败。
4. 当前实现会生成宿主机侧 PowerShell 脚本文件用于执行 Guest 压缩，但成功后会删除该脚本。
5. 代码中存在 `WriteManifestAsync`，但当前调用被注释掉，所以当前不会生成 `backup_manifest.json`。
6. 代码中存在 `DirectoryBackupResultRequest` 和 `ISchedulerClient.ReportDirectoryBackupResultAsync`，但当前切换流程没有调用，因此备份结果不会上报到 Scheduler。
7. 备份 zip 复制回宿主机后，Guest 侧 zip 和 Guest 侧临时目录当前没有清理逻辑。
8. 备份失败是否阻断切换由 `Agent.ForceRevertWhenBackupFailed` 决定。
9. 备份成功只表示 zip 已复制回宿主机，不代表压缩包内容经过完整性校验。

---

## 十一、配置项速查

| 配置路径 | 说明 |
| --- | --- |
| `Agent.HostWorkPath` | 宿主机工作根路径，日志备份和本地数据库默认都放在该目录下 |
| `VirtualMachines[].GuestWorkPath` | Guest 内工作目录，也是备份源目录根路径 |
| `VirtualMachines[].GuestBackupPaths` | 逗号分隔的 Guest 备份目录名，默认 `cache,db,file,logs` |
| `VirtualMachines[].GuestUser` | vmrun 操作 Guest 使用的用户名 |
| `VirtualMachines[].GuestPasswordSecret` | vmrun 操作 Guest 使用的密码 |
| `VirtualMachines[].VmxPath` | vmrun 操作目标 VM 的 vmx 路径 |
| `Agent.ForceRevertWhenBackupFailed` | 日志备份失败时是否继续执行快照切换 |

---

## 十二、完整数据流

```text
PoolSchedulerService
  -> 构造 VmSwitchRequest
  -> VmSwitchService.SwitchAsync
     -> 创建 SwitchTransaction
     -> GuestWorkerClient.GetRunnerStatusAsync
     -> GuestWorkerClient.KillRunnerAsync
     -> LocalStore.UpdateSwitchTransactionAsync(STOP_RUNNER_DONE)
     -> LogBackupService.BackupAsync
        -> 计算 Host 备份目录
        -> 生成 Guest PowerShell 压缩脚本
        -> VmrunService.RunProgramInGuestAsync
        -> VmrunService.CopyFileFromGuestToHostAsync
        -> 返回 LogBackupResult
     -> 根据备份结果决定继续或失败
     -> LocalStore.UpdateSwitchTransactionAsync(LOG_BACKUP_DONE 或 FAILED)
     -> 后续 VM stop / revert / start
```
