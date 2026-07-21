# RPA Worker Agent 已实现功能与关键代码分析

更新时间：2026-06-24

本文基于当前工作区代码分析，包含尚未提交的本地改动。当前项目是一个 .NET 8 RPA Worker Agent，目标是在 Windows 宿主机上协调 VMware Workstation VM、VM 内 runner、云调度后台和本地 SQLite 状态。

## 1. 项目入口与运行形态

已实现内容：

- 服务入口从普通 `IHost` 切换为 ASP.NET Core `WebApplication`。
- 启动时注册核心依赖、后台服务和本机运维 API。
- 支持通过 `OperationsApi:ListenUrl` 配置监听地址。

关键代码：

```csharp
// Program.cs
public static async Task Main(string[] args)
{
    var builder = CreateWebApplicationBuilder(args);
    var app = builder.Build();
    app.MapOperationsApi();
    await app.RunAsync();
}
```

```csharp
// Core/ServiceCollectionExtensions.cs
services.AddSingleton<IVmrunService>(...);
services.AddHttpClient<ISchedulerClient, SchedulerClient>(...);
services.AddHttpClient<IGuestWorkerClient, GuestWorkerClient>();
services.AddSingleton<ILocalStore>(...);
services.AddSingleton<IVmSwitchService, VmSwitchService>();
services.AddSingleton<IPoolSchedulerService, PoolSchedulerService>();
services.AddSingleton<ISnapshotUpdateService, SnapshotUpdateService>();
services.AddSingleton<CapabilityReportService>();
```

说明：

- `WorkerAgent.cs` 目前仍是服务骨架，只记录启动日志，没有主动调用调度循环。
- 实际定时能力主要由已注册的 capability、VM status 后台服务承担；worker heartbeat 由 VM 内 runner 上报。

## 2. 配置模型与配置校验

已实现内容：

- 定义了强类型配置模型：
  - `WorkerAgentOptions`
  - `AgentOptions`
  - `OperationsApiOptions`
  - `SchedulerOptions`
  - `VmrunOptions`
  - `VirtualMachineOptions`
  - `ProfileOptions`
  - `GuestBackupPathsOptions`
- 校验核心必填项、runner 9090 端口、VM workerId 唯一性、VM 内 profileId 唯一性。
- `SnapshotName` 必须配置，且必须匹配 `ProfileId.vYYMMDD.No` 版本化格式。

关键代码：

```csharp
// Core/Configuration/WorkerAgentOptionsValidator.cs
Require(vm.Name, $"{vmPath}.Name", errors);
Require(vm.VmxPath, $"{vmPath}.VmxPath", errors);
Require(vm.BaseSnapshotName, $"{vmPath}.BaseSnapshotName", errors);
Require(vm.WorkerId, $"{vmPath}.WorkerId", errors);
Require(vm.RunnerStatusUrl, $"{vmPath}.RunnerStatusUrl", errors);
Require(vm.RunnerKillUrl, $"{vmPath}.RunnerKillUrl", errors);
ValidateGuestBackupPaths(vm.GuestBackupPaths, vmPath, errors);
ValidateProfiles(vm.Profiles, vmPath, errors);
```

```csharp
// Core/Configuration/WorkerAgentOptionsValidator.cs
Require(profile.SnapshotName, $"{profilePath}.SnapshotName", errors);
var expectedPrefix = profile.ProfileId + ".v";
if (!string.IsNullOrWhiteSpace(profile.SnapshotName)
    && (!VersionedSnapshotSuffix.IsMatch(profile.SnapshotName)
        || !profile.SnapshotName.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)))
{
    errors.Add($"{profilePath}.SnapshotName must match format ProfileId.vYYMMDD.No ...");
}
```

注意：

- 当前 `appsettings.json` 使用了 `VmName`、`Workers`、`ExecutorStopUrl` 等旧字段；当前代码模型使用的是 `Name`、`Profiles`、`RunnerStatusUrl`、`RunnerKillUrl`。这说明配置样例和强类型模型仍有对齐风险。

## 3. 领域模型与状态判断

已实现内容：

- runner 原有 0-8 状态枚举。
- Agent / VM 状态枚举。
- 快照切换事务状态。
- 错误码常量。
- VM 是否可切换的集中判断逻辑。

关键代码：

```csharp
// Core/Domain/RunnerStatusCode.cs
public enum RunnerStatusCode
{
    New = 0,
    Runnable = 1,
    Running = 2,
    Closed = 3,
    RobotError = 4,
    ClientError = 5,
    Upgrading = 6,
    UpgradeFailed = 7,
    Offline = 8
}
```

```csharp
// Core/Domain/WorkerStateEvaluator.cs
public static SwitchCandidateEvaluation EvaluateSwitchCandidate(
    VmCurrentState vmState,
    bool currentProfilePending,
    int idleStableSeconds,
    DateTimeOffset now)
{
    if (vmState.IsQuarantined) return Rejected(...);
    if (vmState.HasActiveSwitchTransaction) return Rejected(...);
    if (vmState.RunnerStatusCode is not RunnerStatusCode.Runnable) return Rejected(...);
    if (currentProfilePending) return Rejected(...);
    if (!HasReachedIdleThreshold(...)) return Rejected(...);
    return SwitchCandidateEvaluation.Allowed();
}
```

## 4. VMware vmrun 封装

已实现内容：

- 统一封装 `vmrun.exe` 调用。
- 支持：
  - `listSnapshots`
  - `stop`
  - `revertToSnapshot`
  - `start`
  - `copyFileFromGuestToHost`
  - `snapshot`
  - `deleteSnapshot`
- 命令执行通过 `IProcessRunner` 抽象，便于测试。

关键代码：

```csharp
// Core/Vmware/VmrunService.cs
public Task<VmrunCommandResult> RevertToSnapshotAsync(
    string vmxPath,
    string snapshotName,
    CancellationToken cancellationToken)
{
    return RunVmrunAsync("revertToSnapshot", [vmxPath, snapshotName], cancellationToken);
}
```

```csharp
// Core/Vmware/VmrunService.cs
public Task<VmrunCommandResult> CreateSnapshotAsync(...)
{
    return RunVmrunAsync("snapshot", [vmxPath, snapshotName], cancellationToken);
}

public Task<VmrunCommandResult> DeleteSnapshotAsync(...)
{
    return RunVmrunAsync("deleteSnapshot", [vmxPath, snapshotName], cancellationToken);
}
```

## 5. VM 内 runner 9090 控制客户端

已实现内容：

- 查询 VM 内 runner 状态。
- 调用 VM 内 kill runner 接口。
- 对 HTTP 非成功状态、空响应、网络异常包装为 `GuestWorkerClientException`。

关键代码：

```csharp
// Core/Guest/GuestWorkerClient.cs
public async Task<RunnerStatusResponse> GetRunnerStatusAsync(
    VirtualMachineOptions vm,
    CancellationToken cancellationToken)
{
    using var response = await _httpClient.GetAsync(vm.RunnerStatusUrl, cancellationToken);
    await EnsureSuccessAsync(response, "GetRunnerStatus", vm.RunnerStatusUrl, cancellationToken);
    return await ReadJsonAsync<RunnerStatusResponse>(response, ...);
}
```

```csharp
// Core/Guest/GuestWorkerClient.cs
public async Task<KillRunnerResponse> KillRunnerAsync(...)
{
    using var request = JsonContent.Create(new { reason, txId, deadlineSeconds }, options: JsonOptions);
    using var response = await _httpClient.PostAsync(vm.RunnerKillUrl, request, cancellationToken);
    ...
}
```

## 6. 云后台调度与状态上报客户端

已实现内容：

- 按 `profileId` 查询待执行任务。
- 上报 VM profile 能力。
- 上报 VM 当前状态。
- 上报切换记录。
- 上报目录备份结果。
- 支持 `Authorization: Bearer {AccessToken}`。

关键代码：

```csharp
// Core/Scheduler/SchedulerClient.cs
public async Task<ProfilePendingTaskResponse> QueryPendingTasksAsync(
    string profileId,
    CancellationToken cancellationToken)
{
    var url = BuildUri($"profile-task/pending?profileId={Uri.EscapeDataString(profileId)}");
    using var request = CreateRequest(HttpMethod.Get, url);
    ...
}
```

```csharp
// Core/Scheduler/SchedulerClient.cs
public Task ReportVmStatusAsync(VmStatusReportRequest request, CancellationToken cancellationToken)
{
    return PostAsync("vm/status", request, "ReportVmStatus", cancellationToken);
}
```

## 7. SQLite 本地状态存储

已实现内容：

- 初始化本地 SQLite 数据库。
- 创建并维护两张表：
  - `local_vm_state`
  - `local_switch_transaction`
- 支持 upsert VM 状态、查询 VM 状态、创建/更新/查询切换事务。

关键代码：

```sql
CREATE TABLE IF NOT EXISTS local_vm_state (
    host_id TEXT NOT NULL,
    vm_name TEXT NOT NULL,
    worker_id TEXT NOT NULL,
    current_profile_id TEXT,
    current_snapshot_name TEXT,
    runner_status_code INTEGER,
    agent_vm_status TEXT NOT NULL,
    is_quarantined INTEGER NOT NULL DEFAULT 0,
    UNIQUE(host_id, vm_name)
);
```

```sql
CREATE TABLE IF NOT EXISTS local_switch_transaction (
    tx_id TEXT NOT NULL,
    host_id TEXT NOT NULL,
    vm_name TEXT NOT NULL,
    worker_id TEXT NOT NULL,
    from_profile_id TEXT,
    from_snapshot_name TEXT,
    to_profile_id TEXT NOT NULL,
    to_snapshot_name TEXT NOT NULL,
    status TEXT NOT NULL,
    UNIQUE(tx_id)
);
```

## 8. 切换前目录备份

已实现内容：

- 在切换前把 VM 内四类目录复制到宿主机：
  - `cache`
  - `db`
  - `file`
  - `logs`
- 目标目录格式为 `{HostWorkPath}/{VmName}/{yyyyMMddHHmmss}`。
- 写入 `backup_manifest.json`。
- 任意复制失败时返回失败结果，并仍写 manifest。

关键代码：

```csharp
// Core/Backup/LogBackupService.cs
var targetPath = Path.Combine(vm.HostWorkPath, vm.Name, timestamp.ToString("yyyyMMddHHmmss"));
Directory.CreateDirectory(targetPath);

foreach (var source in BuildSources(vm))
{
    var hostPath = Path.Combine(targetPath, source.Name);
    Directory.CreateDirectory(hostPath);
    await _vmrunService.CopyFileFromGuestToHostAsync(...);
}
```

## 9. 启动校验与能力生成

已实现内容：

- 检查 `vmrun.exe` 路径。
- 检查 VMX 文件路径。
- 通过 `vmrun listSnapshots` 获取快照列表。
- 校验基础快照存在且 `BaseSnapshotName == VmName`。
- 校验 profile 快照存在。
- 启动校验生成内部 `HostAgentCapabilitiesRequest`，能力上报时展平为 `List<HostProfileCapabilityRequest>`。

关键代码：

```csharp
// Core/Startup/StartupValidator.cs
var snapshots = await LoadSnapshotsAsync(vm, errors, cancellationToken);
if (!ContainsSnapshot(snapshots, vm.BaseSnapshotName))
{
    errors.Add($"BaseSnapshotName does not exist for VM {vm.Name}: {vm.BaseSnapshotName}");
}

foreach (var profile in vm.Profiles)
{
    var snapshotName = profile.SnapshotName;
    var snapshotExists = ContainsSnapshot(snapshots, snapshotName);
    ...
}
```

## 10. 单 VM 快照切换编排

已实现内容：

- 创建切换事务。
- 查询 runner 状态。
- Running / Upgrading 时禁止切换。
- 调用 kill runner。
- 校验 `currentTaskId` 已清空。
- 执行目录备份。
- `vmrun stop`。
- `vmrun revertToSnapshot`。
- `vmrun start`。
- 启动后再次检查 runner ready。
- 校验 VM 内 `workerId/profileId` 与目标一致。
- 成功或失败均更新本地事务。

关键代码：

```csharp
// Core/Switching/VmSwitchService.cs
var beforeStatus = await _guestWorkerClient.GetRunnerStatusAsync(request.Vm, cancellationToken);
if (beforeStatus.RunnerStatusCode == RunnerStatusCode.Running)
{
    return await FailAsync(tx, ErrorCodes.WorkerRunning, "Runner is Running.", ...);
}
```

```csharp
// Core/Switching/VmSwitchService.cs
await _vmrunService.StopVmAsync(request.Vm.VmxPath, VmStopMode.Soft, cancellationToken);
await _vmrunService.RevertToSnapshotAsync(request.Vm.VmxPath, request.TargetSnapshotName, cancellationToken);
await _vmrunService.StartVmAsync(request.Vm.VmxPath, _options.Vmrun.DefaultStartNoGui, cancellationToken);
```

## 11. Profile 调度轮询

已实现内容：

- 读取配置中所有 profileId。
- 逐个查询 pending task。
- 按优先级、最早排队时间、profileId 排序。
- 查询本地 VM 状态。
- 上报 VM 状态。
- 选择兼容且空闲的 VM。
- 每轮最多发起一次切换。

关键代码：

```csharp
// Core/Scheduling/PoolSchedulerService.cs
var targetProfiles = pendingByProfile.Values
    .Where(response => response.HasTask)
    .OrderByDescending(response => response.Priority)
    .ThenBy(response => ParseOldestQueuedAt(response.OldestQueuedAt))
    .ThenBy(response => response.ProfileId, StringComparer.OrdinalIgnoreCase)
    .ToList();
```

```csharp
// Core/Scheduling/PoolSchedulerService.cs
TargetSnapshotName = profile.SnapshotName,
```

注意：

- `PoolSchedulerService` 已实现单轮调度方法 `RunOneCycleAsync`。
- 当前 `WorkerAgent` 后台主服务还没有周期性调用 `RunOneCycleAsync`。

## 12. 后台上报服务

已实现内容：

- `WorkerAgent` 启动时调用 `CapabilityReportService` 上报一次 VM profile 能力，运行期间不再周期上报。
- WorkerAgent 不处理心跳上报；worker 心跳由 VM 内 runner 上报。
- 能力上报异常会记录 warning，不直接终止进程。

关键代码：

## 13. 本机运维 API 与快照更新

已实现内容：

- 暴露本机运维 API 分组 `/operations`。
- 支持 API Key 校验，来源为请求头 `X-Api-Key` 或 query `apiKey`。
- 已实现 profile 快照更新接口：
  - `POST /operations/snapshots/{vmName}/{profileId}/update`
- 快照更新流程：
  1. 查找 VM 和 profile 配置。
  2. 回滚到当前快照。
  3. 启动 VM。
  4. 等待 1 分钟。
  5. 查询 runner 状态，必须 Runnable 或 Running。
  6. 停止 VM。
  7. 查询已有快照。
  8. 生成新快照名。
  9. 创建新快照。
  10. 删除旧快照。
  11. 更新 `appsettings.json` 中对应 profile 的 `SnapshotName`。

关键代码：

```csharp
// Core/Operations/OperationsApiExtensions.cs
group.MapPost("/snapshots/{vmName}/{profileId}/update", async (
    string vmName,
    string profileId,
    ISnapshotUpdateService snapshotService,
    CancellationToken cancellationToken) =>
{
    var result = await snapshotService.UpdateSnapshotAsync(vmName, profileId, cancellationToken);
    return result.Success ? Results.Ok(result) : Results.UnprocessableEntity(result);
});
```

```csharp
// Core/Snapshot/SnapshotNameGenerator.cs
var prefix = $"{profileId}.v{dateStr}.";
...
return $"{prefix}{maxNo + 1}";
```

```csharp
// Core/Snapshot/ConfigFileUpdater.cs
worker["SnapshotName"] = newSnapshotName;
```

## 14. 测试覆盖

已实现内容：

- 使用自定义 smoke test runner，不依赖 xUnit/NUnit。
- 测试集中在 `tests/Seebot.WorkerAgent.Tests/Program.cs`。
- 覆盖范围包括：
  - DI 注册与服务入口。
  - 配置校验。
  - runner 状态枚举和错误码。
  - VM 切换候选判断。
  - SQLite 本地状态。
  - vmrun 命令参数。
  - SchedulerClient 请求。
  - GuestWorkerClient 请求。
  - 目录备份与 manifest。
  - 启动校验。
  - VM 切换编排。
  - 单轮调度。
  - 上报服务。
  - P0 集成路径。
  - SnapshotUpdateService 成功/失败路径。

关键代码：

```csharp
// tests/Seebot.WorkerAgent.Tests/Program.cs
var tests = new (string Name, Action Body)[]
{
    ("Host registers core services and hosted services", ...),
    ("VmSwitchService success path executes ordered external actions", ...),
    ("PoolSchedulerService switches a compatible idle VM when pending", ...),
    ("SnapshotUpdateService success path executes steps in order", ...)
};
```

## 15. 当前已实现能力总览

| 能力 | 状态 | 关键代码 |
|---|---|---|
| WebApplication 服务入口 | 已实现 | `Program.cs` |
| 依赖注入注册 | 已实现 | `Core/ServiceCollectionExtensions.cs` |
| 强类型配置与基础校验 | 已实现 | `Core/Configuration/*` |
| runner 0-8 状态模型 | 已实现 | `Core/Domain/RunnerStatusCode.cs` |
| VM 切换候选判断 | 已实现 | `Core/Domain/WorkerStateEvaluator.cs` |
| vmrun 封装 | 已实现 | `Core/Vmware/VmrunService.cs` |
| VM 内 runner HTTP 客户端 | 已实现 | `Core/Guest/GuestWorkerClient.cs` |
| 云后台 HTTP 客户端 | 已实现 | `Core/Scheduler/SchedulerClient.cs` |
| SQLite 本地状态 | 已实现 | `Core/Storage/LocalStore.cs` |
| 切换前目录备份 | 已实现 | `Core/Backup/LogBackupService.cs` |
| 启动快照校验和能力生成 | 已实现 | `Core/Startup/StartupValidator.cs` |
| 单 VM 切换编排 | 已实现 | `Core/Switching/VmSwitchService.cs` |
| 单轮 profile 调度 | 已实现 | `Core/Scheduling/PoolSchedulerService.cs` |
| Agent/能力/VM 状态上报 | 已实现 | `Core/Reporting/*` |
| 本机运维 API | 部分实现 | `Core/Operations/OperationsApiExtensions.cs` |
| profile 快照更新 | 已实现 | `Core/Snapshot/*` |
| 调度主循环 | 尚未接入主服务 | `WorkerAgent.cs` 仍为骨架 |

## 16. 当前注意事项

1. 当前工作区有未提交改动，本文分析的是工作区现状，不一定等同于 `HEAD`。
2. `WorkerAgent.cs` 仍未调用 `PoolSchedulerService.RunOneCycleAsync`，因此自动调度主循环尚未真正接入服务入口。
3. 当前 `appsettings.json` 看起来仍是旧结构，包含 `Workers`、`ExecutorStopUrl`、`WorkerStatusUrl` 等字段；当前强类型配置期望 `Profiles`、`RunnerStatusUrl`、`RunnerKillUrl` 等字段。
4. `SnapshotName` 必填，语义是版本化格式 `ProfileId.vYYMMDD.No`，并由 Snapshot 更新流程自动生成下一版本。
5. 本机运维 API 当前只实现了快照更新接口，暂停调度、隔离 VM、解除隔离、查询事务等接口尚未看到实现。
6. `VmSwitchService` 使用 soft stop，当前没有看到 soft 超时后 hard stop 的完整降级逻辑。
7. `CapabilityReportService` 仅在服务启动时执行真实文件和 vmrun 快照校验及能力上报。
