# Profile 画像上报流程

本文档描述 Worker Agent 向云后台上报本机 VM/Profile 能力画像的当前实现逻辑。

核心实现：

- 启动上报服务：`Core/Reporting/CapabilityReportService.cs`
- 上报客户端：`Core/Scheduler/SchedulerClient.cs`
- 请求模型：`Core/Scheduler/HostProfileCapabilityRequest.cs`
- 快照匹配：`Core/Snapshot/ProfileSnapshotResolver.cs`
- VMware 操作：`Core/Vmware/VmrunService.cs`
- 服务注册：`Core/ServiceCollectionExtensions.cs`

## 1. 上报频率

画像上报由 `WorkerAgent` 在服务启动时调用 `CapabilityReportService.ReportOnceAsync` 执行一次。

启动后执行节奏：

```text
服务启动
-> 执行 ReportOnceAsync 一次
-> 后续不再自动上报
```

## 2. 触发方式

当前没有单独的手动 API 触发画像上报。服务启动后由 `WorkerAgent.ExecuteAsync` 调用一次，后续调度周期不会再次上报。

注册位置：

```csharp
services.AddSingleton<CapabilityReportService>();
```

## 3. 数据来源

上报数据来自运行时的：

```text
WorkerAgentOptions.VirtualMachines
```

该列表在启动时从本地 SQLite VM/Profile registry 加载，不再以 `appsettings.json` 的 `VirtualMachines` 作为主维护来源。

只上报启用的 VM：

```csharp
_options.VirtualMachines.Where(vm => vm.Enabled)
```

每台 VM 下会遍历它登记的全部 Profiles。

## 4. 总体流程

```text
1. WorkerAgent.ExecuteAsync 启动
2. 调用 CapabilityReportService.ReportOnceAsync
3. 读取启用 VM 列表
4. 对每台 VM 执行 vmrun listSnapshots
5. 对该 VM 下每个 Profile 解析匹配快照
6. 组装 HostProfileCapabilityRequest 列表
7. 调用 SchedulerClient.ReportCapabilitiesAsync
8. POST 到云后台 robot/vmProfile/reportSave
9. 上报结束，服务运行期间不再重复上报
```

## 5. VM 快照读取

对每台启用 VM 执行：

```text
vmrun listSnapshots {vm.VmxPath}
```

如果读取成功，得到该 VM 当前全部快照名称。

如果读取失败：

- 记录 Warning 日志。
- 不阻断整轮上报。
- 当前 VM 的快照列表按空列表处理。
- 该 VM 下 Profile 仍会上报，但 `snapshotName` 可能为空。

## 6. Profile 快照匹配

每个 Profile 通过 `ProfileSnapshotResolver.Resolve` 在 VM 快照列表中匹配快照。

匹配规则：

```text
{ProfileId}.v{yyMMdd}.{sequence}
```

示例：

```text
rpa-sh-tax-etax.v260707.1
rpa-sh-tax-etax.v260707.2
```

匹配结果：

| 结果 | 上报行为 |
| --- | --- |
| 匹配到 1 个快照 | 上报该快照名 |
| 没有匹配快照 | `snapshotName` 上报空字符串 |
| 匹配到多个快照 | `snapshotName` 上报空字符串 |
| 读取快照失败 | `snapshotName` 上报空字符串 |

注意：画像上报只负责报告 VM/Profile 能力，不会在这里自动修复快照、删除重复快照或禁用 Profile。

## 7. 上报字段

每个 VM/Profile 会生成一条 `HostProfileCapabilityRequest`：

```json
{
  "hostName": "RPA Worker Agent 001",
  "machineCode": "SR20-2606-POC1",
  "profileId": "rpa-sh-tax-etax",
  "profileName": "上海税务电子税局",
  "snapshotName": "rpa-sh-tax-etax.v260707.1"
}
```

字段来源：

| 字段 | 来源 |
| --- | --- |
| `hostName` | 优先 `Agent.AgentName`，为空时使用 `Agent.HostId` |
| `machineCode` | 优先 `vm.WorkerId`，为空时使用 `vm.Name` |
| `profileId` | `profile.ProfileId` |
| `profileName` | 优先 `profile.ProfileName`，为空时使用 `profile.ProfileId` |
| `snapshotName` | `ProfileSnapshotResolver.Resolve` 解析出的快照名；没有唯一匹配时为空 |

## 8. 云后台接口

上报调用：

```http
POST {Scheduler.BaseUrl}/robot/vmProfile/reportSave
Authorization: Bearer {accessToken}
Content-Type: application/json
```

请求体是数组：

```json
[
  {
    "hostName": "RPA Worker Agent 001",
    "machineCode": "SR20-2606-POC1",
    "profileId": "rpa-sh-tax-etax",
    "profileName": "上海税务电子税局",
    "snapshotName": "rpa-sh-tax-etax.v260707.1"
  }
]
```

接口路径在代码中定义为：

```csharp
robot/vmProfile/reportSave
```

## 9. Token 与重试

`SchedulerClient` 发起请求前会先获取 access token，并设置：

```http
Authorization: Bearer {token}
```

请求流程：

```text
1. 使用缓存 token 请求一次
2. 如果响应不是 401，按普通 HTTP 成功/失败处理
3. 如果响应是 401，刷新 token
4. 使用新 token 重试一次
5. 重试后仍失败则抛出异常
```

画像上报外层会捕获异常并记录 Warning，不会让 HostedService 退出。

## 10. 日志点

画像上报会记录以下关键日志：

- 上报开始：`Capability report started`
- 读取 VM 快照开始：`Listing snapshots for capability report`
- 读取 VM 快照完成：`Snapshots listed for capability report`
- 单个 Profile 解析结果：`Profile capability resolved`
- payload 构建完成：`Capability report payload built`
- Scheduler POST 开始：`Scheduler post started`
- Scheduler HTTP 请求开始/完成
- Scheduler POST 完成
- 上报完成：`Capability report completed`
- 上报失败：`Failed to report VM profile capabilities`

## 11. 异常处理

| 场景 | 处理 |
| --- | --- |
| 单台 VM `listSnapshots` 失败 | 记录 Warning，该 VM 快照列表为空，继续处理其它 VM/Profile |
| Profile 没有唯一匹配快照 | `snapshotName` 为空，仍上报 |
| Scheduler HTTP 失败 | 抛出异常，被 `ReportOnceAsync` 捕获并记录 Warning |
| Scheduler 返回 401 | 刷新 token 后重试一次 |
| API 返回业务 `code != 200` | 视为失败，记录 Warning |
| 服务停止或请求取消 | `OperationCanceledException` 继续向上抛出，用于正常停止 |

## 12. 与其它流程的关系

### 与 VM/Profile 维护

`/vms` 页面维护的 VM/Profile 信息最终进入本地 SQLite registry。服务启动后会加载这些 VM/Profile，画像上报只读取启用 VM 与其 Profiles。

### 与 Profile 快照更新

Profile 快照更新成功后会回写：

```text
local_vm_profile.snapshot_name = newSnapshotName
```

但画像上报本身仍会通过 `vmrun listSnapshots` 和 `ProfileSnapshotResolver` 解析实时快照列表，避免只依赖 DB 中的旧值。

### 与 Profile 切换

Profile 切换成功后也会更新 `local_vm_profile.snapshot_name`。下一次画像上报仍会重新读取 VM 快照列表，并按命名规则解析当前可用快照。

### 与 VM 状态上报

画像上报只上报静态能力：

```text
Host + VM + Profile + SnapshotName
```

它不同于 VM 状态上报。VM 当前运行状态、当前 Profile、当前 Snapshot、runner 状态由状态刷新和状态上报链路负责。

## 13. 当前约束

1. 画像上报是周期执行，不是按 VM/Profile 变更实时触发。
2. 上报前会对每台启用 VM 调用 `vmrun listSnapshots`，生产环境需要保证服务账号有 VMware 访问权限。
3. 单个 VM 快照读取失败不会阻断其它 VM 上报。
4. Profile 快照必须遵循 `{ProfileId}.v{yyMMdd}.{sequence}` 命名规则，否则 `snapshotName` 会为空。
5. 同一 VM/Profile 如果存在多个匹配快照，会被视为不唯一，`snapshotName` 为空，需要人工清理或通过更新快照流程收敛。
6. 画像仅在 Worker Agent 启动时上报一次，不执行周期上报。
