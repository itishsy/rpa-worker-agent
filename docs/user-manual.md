# RPA Worker Agent 使用手册

更新时间：2026-06-24

本文面向部署和运维人员，说明 RPA Worker Agent 的安装部署、配置、启动、日常运维、快照更新和故障处理方法。本文以当前代码模型和 `appsettings.json` 字段为准。

## 1. 系统定位

RPA Worker Agent 部署在 Windows 宿主机上，用于协调本机 VMware Workstation 虚拟机池。它不执行 RPA 业务，也不启动 `runner.jar`；业务执行仍由 VM 内的 `rpa-client` / `rpa-runner` 完成。

Agent 主要职责：

- 管理宿主机上的 VM 配置和 profile 快照能力。
- 通过 `vmrun.exe` 控制 VM 生命周期。
- 通过 VM 内 `rpa-runner` 9090 接口查询状态和停止 runner。
- 在切换 profile 快照前备份 VM 内 `cache`、`db`、`file`、`logs` 目录。
- 通过本地 SQLite 保存 VM 状态和切换事务。
- 向云后台上报能力、VM 状态、切换记录和备份结果；worker 心跳由 VM 内 runner 上报。
- 提供本机运维 API，目前包含 profile 快照更新接口。

## 2. 部署前准备

### 2.1 宿主机要求

- Windows 宿主机。
- 已安装 VMware Workstation。
- 已安装 .NET 8 Runtime；如需本机编译，需要 .NET 8 SDK。
- Agent 运行账号需要具备：
  - 读取和执行 `vmrun.exe` 的权限。
  - 访问 VMX 文件所在目录的权限。
  - 写入 `HostWorkPath`、程序目录 `data`、程序日志目录的权限。
  - 访问 VM 内 runner 9090 端口的网络权限。

### 2.2 VM 要求

每台 VM 需要提前准备：

- VMX 文件路径固定且可访问。
- 基础快照 `BaseSnapshotName` 已存在。
- 每个 profile 对应一个版本化快照，命名格式为 `ProfileId.vYYMMDD.No`，例如 `DongGuan-CA.v260624.1`。
- VM 内 `rpa-runner` 可自启动并监听 9090。
- 9090 状态接口可访问：`GET /api/robot/start/status`。
- 9090 kill 接口可访问，例如：`POST /api/robot/kill`。
- VM 内需要备份的目录存在或可访问：
  - `cache`
  - `db`
  - `file`
  - `logs`

### 2.3 云后台要求

云后台需要提供：

- profile pending 查询接口。
- VM profile 能力接收接口。
- VM 当前状态接收接口。
- 切换日志接收接口。
- 目录备份结果接收接口。

`Scheduler:BaseUrl` 应配置为云后台接口根地址，`Scheduler:AccessToken` 用于 Bearer 鉴权。

## 3. 构建与发布

### 3.1 本地构建

在仓库根目录执行：

```powershell
$env:DOTNET_CLI_HOME = (Resolve-Path -LiteralPath '.').Path
$env:APPDATA = Join-Path (Resolve-Path -LiteralPath '.').Path '.dotnet-appdata'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
dotnet build rpa-worker-agent.csproj
```

### 3.2 发布程序

示例发布到 `D:\seebot\rpa-worker-agent`：

```powershell
dotnet publish rpa-worker-agent.csproj -c Release -o D:\seebot\rpa-worker-agent
```

发布后确认目录中至少包含：

- `Seebot.WorkerAgent.Service.exe`
- `appsettings.json`
- 运行依赖 DLL

## 4. 配置说明

配置文件为程序目录下的 `appsettings.json`。字段名必须与代码模型一致。

### 4.1 完整示例

```json
{
  "Agent": {
    "HostId": "RPA Workstation 001",
    "AgentName": "RPA Worker Agent 001",
    "PollIntervalSeconds": 10,
    "CapabilityReportIntervalSeconds": 300,
    "SwitchTimeoutSeconds": 300,
    "WaitVmReadyTimeoutSeconds": 180,
    "WaitUpgradeTimeoutSeconds": 300,
    "IdleStableSeconds": 60,
    "ForceRevertWhenBackupFailed": false,
    "AllowRevertWhenRunnerError": false,
    "MaxSwitchesPerCycle": 1
  },
  "OperationsApi": {
    "ListenUrl": "http://127.0.0.1:18090",
    "ApiKey": ""
  },
  "Scheduler": {
    "BaseUrl": "http://localhost:9528",
    "AccessToken": "replace-with-token"
  },
  "Vmrun": {
    "VmrunPath": "D:\\VMware\\VMware Workstation\\vmrun.exe",
    "DefaultStartNoGui": true,
    "StopSoftTimeoutSeconds": 60,
    "AllowHardStopAfterSoftTimeout": false
  },
  "VirtualMachines": [
    {
      "Name": "SR20-2606-POC1",
      "VmxPath": "E:\\vms\\SR20-2026-POC1\\Windows 10 x64.vmx",
      "BaseSnapshotName": "SR20-2606-POC1",
      "GuestUser": "Administrator",
      "GuestPasswordSecret": "",
      "WorkerId": "SR20-2606-POC1",
      "RunnerControlBaseUrl": "http://192.168.100.101:9090",
      "RunnerStatusUrl": "http://192.168.100.101:9090/api/robot/start/status",
      "RunnerKillUrl": "http://192.168.100.101:9090/api/robot/kill",
      "HostWorkPath": "D:\\seebon\\rpa-worker-agent\\work",
      "GuestBackupPaths": {
        "Cache": "D:\\seebon\\rpa\\cache",
        "Db": "D:\\seebon\\rpa\\db",
        "File": "D:\\seebon\\rpa\\file",
        "Logs": "D:\\seebon\\logs"
      },
      "Profiles": [
        {
          "ProfileId": "General",
          "SnapshotName": "General.v260624.1"
        }
      ]
    }
  ]
}
```

### 4.2 关键字段

`Agent`：

- `HostId`：宿主机唯一标识。
- `AgentName`：展示名称。
- WorkerAgent 不上报 heartbeat 或周期 VM 状态心跳，worker 心跳由 VM 内 runner 上报。
- `CapabilityReportIntervalSeconds`：能力上报间隔。
- `IdleStableSeconds`：VM 空闲稳定时间阈值。
- `ForceRevertWhenBackupFailed`：目录备份失败后是否仍允许回滚快照。生产建议保持 `false`。
- `MaxSwitchesPerCycle`：单轮调度最大切换数量。当前调度实现每轮最多启动一次切换。

`OperationsApi`：

- `ListenUrl`：本机运维 API 监听地址，建议只绑定 `127.0.0.1`。
- `ApiKey`：非空时启用鉴权。调用方需要传 `X-Api-Key` 请求头或 `apiKey` query。

`Vmrun`：

- `VmrunPath`：`vmrun.exe` 完整路径。
- `DefaultStartNoGui`：启动 VM 时是否使用 `nogui`。
- `StopSoftTimeoutSeconds`：vmrun 命令超时时间。

`VirtualMachines`：

- `Name`：VM 名称，需与 `BaseSnapshotName` 一致。
- `VmxPath`：VMX 文件路径。
- `BaseSnapshotName`：基础快照名。
- `WorkerId`：VM 内 runner 实例身份，宿主机内唯一。
- `RunnerStatusUrl`：runner 状态接口，端口必须为 9090。
- `RunnerKillUrl`：runner kill 接口，端口必须为 9090。
- `HostWorkPath`：宿主机备份工作目录。
- `GuestBackupPaths`：VM 内待备份目录。
- `Profiles`：该 VM 支持的 profile 快照清单。

`Profiles`：

- `ProfileId`：调度维度。
- `SnapshotName`：必填，必须为 `ProfileId.vYYMMDD.No` 格式，例如 `General.v260624.1`。

## 5. 启动方式

### 5.1 控制台启动

进入发布目录：

```powershell
cd D:\seebot\rpa-worker-agent
.\Seebot.WorkerAgent.Service.exe
```

启动后检查：

- 进程是否仍在运行。
- 运维 API 是否监听在 `OperationsApi:ListenUrl`。
- 云后台是否收到 runner heartbeat。
- 程序目录下是否创建了 `data\agent.db`。

### 5.2 注册为 Windows Service

示例：

```powershell
sc.exe create Seebot.WorkerAgent.Service `
  binPath= "D:\seebot\rpa-worker-agent\Seebot.WorkerAgent.Service.exe" `
  start= auto `
  DisplayName= "Seebot RPA Worker Agent"

sc.exe start Seebot.WorkerAgent.Service
```

停止服务：

```powershell
sc.exe stop Seebot.WorkerAgent.Service
```

删除服务：

```powershell
sc.exe delete Seebot.WorkerAgent.Service
```

## 6. 部署后验收

### 6.1 配置验收

检查配置字段：

- 不应出现旧字段：`VmName`、`Workers`、`ExecutorStopUrl`、`ExecutorHealthUrl`、`WorkerStatusUrl`、`GuestLogPath`、`HostBackupRoot`。
- 必须使用当前字段：`Name`、`Profiles`、`RunnerStatusUrl`、`RunnerKillUrl`、`HostWorkPath`、`GuestBackupPaths`。
- 每个 `SnapshotName` 必须是版本化格式。

### 6.2 运行验收

执行以下检查：

```powershell
Test-Path "D:\VMware\VMware Workstation\vmrun.exe"
Test-Path "E:\vms\SR20-2026-POC1\Windows 10 x64.vmx"
Invoke-RestMethod "http://192.168.100.101:9090/api/robot/start/status"
```

检查 VM 快照：

```powershell
& "D:\VMware\VMware Workstation\vmrun.exe" listSnapshots "E:\vms\SR20-2026-POC1\Windows 10 x64.vmx"
```

结果中应包含：

- `BaseSnapshotName`
- 每个 profile 对应的 `SnapshotName`

### 6.3 构建和测试验收

在源码目录执行：

```powershell
$env:DOTNET_CLI_HOME = (Resolve-Path -LiteralPath '.').Path
$env:APPDATA = Join-Path (Resolve-Path -LiteralPath '.').Path '.dotnet-appdata'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
dotnet build rpa-worker-agent.csproj
dotnet run --project tests\Seebot.WorkerAgent.Tests\Seebot.WorkerAgent.Tests.csproj
```

## 7. 日常运维指引

### 7.1 查看 Agent 状态

优先从云后台查看：

- runner 心跳时间。
- VM 数量。
- 隔离 VM 数量。
- VM 当前 profile。
- VM 当前 snapshot。
- runner 状态码。
- 最近错误码和错误信息。

本地检查：

```powershell
Get-Process | Where-Object { $_.ProcessName -like "*WorkerAgent*" }
Test-Path ".\data\agent.db"
```

### 7.2 查看 VM 内 runner 状态

```powershell
Invoke-RestMethod "http://192.168.100.101:9090/api/robot/start/status"
```

重点字段：

- `workerId`
- `profileId`
- `runnerStatusCode`
- `currentTaskId`
- `lastHeartbeatTime`

runner 状态码：

| 状态码 | 含义 |
|---:|---|
| 0 | New |
| 1 | Runnable |
| 2 | Running |
| 3 | Closed |
| 4 | RobotError |
| 5 | ClientError |
| 6 | Upgrading |
| 7 | UpgradeFailed |
| 8 | Offline |

### 7.3 查看本地备份目录

目录格式：

```text
{HostWorkPath}\{VmName}\{yyyyMMddHHmmss}\
  cache\
  db\
  file\
  logs\
  backup_manifest.json
```

检查 manifest：

```powershell
Get-Content "D:\seebon\rpa-worker-agent\work\SR20-2606-POC1\20260624103000\backup_manifest.json"
```

### 7.4 更新 profile 快照

当前本机运维 API 支持更新某个 VM 的某个 profile 快照：

```http
POST /operations/snapshots/{vmName}/{profileId}/update
```

示例：

```powershell
Invoke-RestMethod `
  -Method Post `
  -Uri "http://127.0.0.1:18090/operations/snapshots/SR20-2606-POC1/DongGuan-CA/update"
```

如果配置了 `OperationsApi:ApiKey`：

```powershell
Invoke-RestMethod `
  -Method Post `
  -Uri "http://127.0.0.1:18090/operations/snapshots/SR20-2606-POC1/DongGuan-CA/update" `
  -Headers @{ "X-Api-Key" = "your-api-key" }
```

快照更新流程：

1. 回滚到当前 `SnapshotName`。
2. 启动 VM。
3. 等待 runner ready。
4. 停止 VM。
5. 生成新快照名，例如 `DongGuan-CA.v260624.2`。
6. 创建新快照。
7. 删除旧快照。
8. 更新 `appsettings.json` 中该 profile 的 `SnapshotName`。

返回成功示例：

```json
{
  "success": true,
  "newSnapshotName": "DongGuan-CA.v260624.2",
  "step": "done"
}
```

返回失败示例：

```json
{
  "success": false,
  "errorCode": "RUNNER_NOT_READY",
  "errorMessage": "Runner status is Closed after VM start.",
  "step": "check-status"
}
```

### 7.5 修改配置后的处理

修改 `appsettings.json` 后建议：

1. 停止 Agent。
2. 校验 JSON 格式。
3. 确认 VMX、vmrun、runner URL、备份目录路径有效。
4. 启动 Agent。
5. 在云后台确认 runner heartbeat、capability 和 VM status 已刷新。

JSON 格式检查：

```powershell
Get-Content .\appsettings.json -Raw | ConvertFrom-Json | Out-Null
```

## 8. 常见故障处理

### 8.1 Agent 无法启动

检查项：

- `appsettings.json` 是否存在且 JSON 格式正确。
- `Vmrun:VmrunPath` 是否存在。
- `OperationsApi:ListenUrl` 端口是否被占用。
- 运行账号是否有目录写权限。

### 8.2 RunnerStatusUrl 校验失败

原因：

- URL 为空。
- URL 不是绝对地址。
- 端口不是 9090。

处理：

```json
"RunnerStatusUrl": "http://192.168.100.101:9090/api/robot/start/status"
```

### 8.3 SnapshotName 校验失败

规则：

- 不能为空。
- 必须以 `ProfileId.v` 开头。
- 日期部分必须是 6 位 `YYMMDD`。
- 序号必须是数字。

正确示例：

```json
{
  "ProfileId": "DongGuan-CA",
  "SnapshotName": "DongGuan-CA.v260624.1"
}
```

错误示例：

```json
{
  "ProfileId": "DongGuan-CA",
  "SnapshotName": "DongGuan-CA"
}
```

### 8.4 快照不存在

现象：

- capability 校验失败。
- 云后台显示 profile snapshot missing。

处理：

```powershell
& "D:\VMware\VMware Workstation\vmrun.exe" listSnapshots "E:\vms\SR20-2026-POC1\Windows 10 x64.vmx"
```

确认输出中存在 `SnapshotName`。如果不存在，需要先在 VMware 中创建或通过快照更新流程生成。

### 8.5 备份失败

可能原因：

- VM guest 用户密码错误。
- VM 内目录不存在。
- `HostWorkPath` 无写权限。
- VMware Tools 未正常运行。

处理：

- 确认 `GuestUser` 和 `GuestPasswordSecret`。
- 确认 `GuestBackupPaths` 中四个目录存在。
- 确认宿主机 `HostWorkPath` 可写。
- 查看 `backup_manifest.json` 中的 `errorCode` 和 `errorMessage`。

### 8.6 切换被拒绝

常见错误码：

| 错误码 | 含义 |
|---|---|
| `WORKER_RUNNING` | runner 正在执行任务，禁止切换 |
| `WORKER_UPGRADING` | runner 正在升级，禁止切换 |
| `LOG_BACKUP_FAILED` | 切换前目录备份失败 |
| `SNAPSHOT_REVERT_FAILED` | 快照回滚失败 |
| `VM_START_FAILED` | VM 启动失败 |
| `RUNNER_NOT_READY` | VM 启动后 runner 未 ready |
| `WORKER_PROFILE_MISMATCH` | VM 内 workerId/profileId 与目标不一致 |

## 9. 安全建议

- `OperationsApi:ListenUrl` 建议只监听 `127.0.0.1`。
- 生产环境建议配置 `OperationsApi:ApiKey`。
- `Scheduler:AccessToken` 不要提交到公开仓库。
- `GuestPasswordSecret` 应使用安全存储或加密配置替代明文。
- `ForceRevertWhenBackupFailed` 生产建议保持 `false`。
- 运维 API 执行快照更新前，应确认 VM 未在执行业务任务。

## 10. 当前实现边界

当前代码已经实现配置模型、vmrun 封装、runner 9090 客户端、云后台客户端、本地 SQLite、目录备份、单 VM 切换编排、单轮调度、后台上报和快照更新 API。

仍需注意：

- `WorkerAgent.cs` 当前仍是服务骨架，尚未接入周期性调度主循环。
- 本机运维 API 当前主要实现快照更新，尚未提供完整的暂停调度、隔离 VM、解除隔离、事务查询等接口。
- Windows Service 安装脚本尚未内置，需要按本文命令手工注册或后续补充安装脚本。
