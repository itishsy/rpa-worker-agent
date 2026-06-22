# Seebot RPA Worker Agent 开发文档

版本：V1.0  
项目名称：Seebot 宿主机 RPA Worker Agent  
适用范围：Windows 宿主机、VMware Workstation、本机 Windows VM、vmrun、RPA 执行器、rpa-client、rpa-runner、快照切换、日志备份、worker 状态监控  
建设目标：实现宿主机侧 Agent 对本机 VM 的启停、快照切换、workerId 状态管理、调度队列查询、执行器停止、日志备份和 worker 状态监控。

---

## 一、文档修订结论

本版文档按以下最终边界整理：

1. **Worker Agent 运行在宿主机上，不运行在 VM 内。**
2. **Worker Agent 不负责启动 runner。**
3. **VM 快照启动后，VM 内部会自动启动 rpa-client 和 rpa-runner。**
4. **Worker Agent 只监控 worker / runner 状态。**
5. **本版本仅使用 `vmrun.exe` 控制 VM。**
6. **本版本不做 lease 租约管理。**
7. **runner 状态使用原有 0–8 状态枚举。**
8. **快照名与 workerId 同名。**

示例：

```text
workerId     = SR20-2606-POC1
snapshotName = SR20-2606-POC1
```

一句话概括：

> 宿主机 Agent 不执行 RPA 业务，不启动 runner，不管理 lease。  
> 它只负责把 VM 切换到正确 workerId 快照，并确认 VM 内 rpa-client / rpa-runner 自运行状态正常。

---

## 二、项目背景

当前 Seebot RPA 任务运行在 Windows VM 中。不同城市、CA、UKey、客户端和浏览器环境长期混用后，容易出现以下问题：

1. 浏览器状态残留；
2. 下载目录残留；
3. Java / Python / WebDriver 进程残留；
4. 客户端卡死；
5. UKey / CA 驱动污染；
6. 登录态污染；
7. runner 异常退出；
8. VM 内环境不可复现；
9. 同一 VM 多次执行任务后稳定性下降。

为降低环境污染，本项目采用“**workerId 与 VM 快照绑定**”的方式。每个快照表示一个可执行环境，快照名与 workerId 保持一致。

宿主机 Agent 根据 workerId 查询调度中心。如果某个 workerId 有待执行任务，并且当前 VM 空闲，则 Agent 将本机 VM 切换到该 workerId 对应快照。快照启动后，VM 内的 `rpa-client` 和 `rpa-runner` 自动运行，继续按现有逻辑拉取和执行任务。

---

## 三、建设目标

### 3.1 总体目标

建设一个运行在宿主机上的 `Seebot.WorkerAgent.Service`，用于管理本机 VM 的启停、快照回滚、workerId 状态、执行器停止、日志备份和 worker 状态监控。

### 3.2 核心目标

1. 管理本机 VM 与 workerId / snapshotName 映射；
2. 按 workerId 查询调度中心待执行任务；
3. 判断 VM / worker 是否空闲；
4. 有待执行任务时切换到目标 workerId 快照；
5. 快照切换前停止 VM 内执行器端口；
6. 快照切换前从 VM 复制日志到宿主机；
7. 使用 `vmrun` 控制 VM 关机、快照回滚、开机；
8. 等待 VM 启动完成；
9. 监控 VM 内 `rpa-client` / `rpa-runner` 状态；
10. 监控 runner 原有 0–8 状态；
11. 上报宿主机 Agent、VM、worker 状态；
12. 记录快照切换事务；
13. 支持 Agent 重启后的事务恢复；
14. 支持异常隔离和人工处理。

### 3.3 本期不做范围

本版本不做以下内容：

1. 不启动 `rpa-runner.jar`；
2. 不生成 runner 启动参数；
3. 不接管 runner 任务拉取；
4. 不做 lease 租约管理；
5. 不做 vSphere / ESXi / PowerCLI 控制；
6. 不做跨宿主机调度；
7. 不做动态 Clone；
8. 不做完整 VM Resource Manager；
9. 不做完整健康评分体系；
10. 不做完整 UKey 锁中心；
11. 不改造现有 rpa-client 和 rpa-runner 的核心业务逻辑。

---

## 四、总体架构

```text
Seebot 调度中心
    |
    |-- 按 workerId 查询待执行任务
    |-- 接收 Agent 心跳
    |-- 接收 VM / worker 状态
    |-- 接收快照切换记录
    |-- 接收日志备份结果
    |
    v
Windows 宿主机
    |
    |-- Seebot.WorkerAgent.Service
    |       |
    |       |-- workerId / snapshotName 映射管理
    |       |-- 调度中心任务查询
    |       |-- VM 空闲判断
    |       |-- 执行器停止
    |       |-- 日志备份
    |       |-- vmrun stop
    |       |-- vmrun revertToSnapshot
    |       |-- vmrun start
    |       |-- VM ready 监控
    |       |-- runner 状态监控
    |       |-- 状态上报
    |       |-- 本地事务恢复
    |
    |-- VMware Workstation
            |
            |-- VM-RPA-001
                  |
                  |-- Snapshot: SR20-2606-POC1
                  |-- Snapshot: SR20-2606-POC2
                  |-- Snapshot: SR20-2606-POC3
                  |
                  |-- rpa-client 自动启动
                  |-- rpa-runner 自动启动
                  |-- executor-control 可选
                  |-- RPA 业务流程
```

---

## 五、核心原则

### 5.1 Agent 运行在宿主机

Agent 必须运行在宿主机上，不能运行在 VM 内。

原因：

1. VM 回滚会导致 VM 内状态丢失；
2. VM 内 Agent 自己也会被回滚；
3. 快照切换事务、日志备份记录、错误记录必须保存在 VM 外；
4. 宿主机 Agent 才能稳定控制 VM 生命周期。

### 5.2 Agent 只管 VM，不管业务执行

Agent 不写业务 RPA 逻辑。业务任务仍由 VM 内的 `rpa-client` / `rpa-runner` 执行。

### 5.3 Agent 不启动 runner

VM 快照启动后，VM 内部通过以下任一方式自动启动 `rpa-client` 和 `rpa-runner`：

1. Windows 开机启动项；
2. Windows Service；
3. 计划任务；
4. 原有 rpa-client 自启动机制。

Agent 只等待和监控状态，不执行 runner 启动命令。

### 5.4 本版本只使用 vmrun

所有 VM 控制均通过 `vmrun.exe` 实现：

1. `listSnapshots`
2. `stop`
3. `revertToSnapshot`
4. `start`
5. `copyFileFromGuestToHost`

### 5.5 不做 lease

本版本默认 workerId 与任务分配关系由调度中心保证。Agent 只查询是否有任务，不锁定任务。

### 5.6 快照切换前必须停止执行器并备份日志

快照回滚会清除 VM 内部变更。为避免日志、截图、执行现场丢失，快照切换前必须执行：

1. 停止 VM 内执行器端口；
2. 确认 runner 未处于 Running / Upgrading；
3. 复制 VM 日志到宿主机；
4. 写入日志备份 manifest；
5. 再执行 VM 关机与快照回滚。

---

## 六、技术选型


| 模块     | 技术选型                                     | 说明                     |
| ------ | ---------------------------------------- | ---------------------- |
| 主服务    | C# / .NET Worker Service                 | 适合 Windows 宿主机常驻服务     |
| 运行形态   | Windows Service                          | 开机自启                   |
| VM 控制  | vmrun.exe                                | 本版本唯一 VM 控制方式          |
| 本地状态   | SQLite                                   | 保存事务、worker 状态、恢复记录    |
| 服务端状态  | MySQL                                    | 由 Seebot 后端保存状态        |
| 日志     | Serilog / NLog                           | 结构化日志                  |
| 配置     | appsettings.json                         | 管理 VM、workerId、快照、接口地址 |
| 调度通信   | HTTP REST + JSON                         | 查询任务和上报状态              |
| VM 内辅助 | executor-control HTTP 服务 / PowerShell 脚本 | 停止执行器、查看状态、flush 日志    |
| runner | VM 内自启动                                  | Agent 不启动 runner       |


---

## 七、代码工程结构

```text
Seebot.WorkerAgent.sln
  |
  |-- Seebot.WorkerAgent.Service
  |     └── Windows Service 入口
  |
  |-- Seebot.WorkerAgent.Core
  |     ├── WorkerStateMachine
  |     ├── SchedulerClient
  |     ├── VmSwitchService
  |     ├── VmrunService
  |     ├── GuestWorkerClient
  |     ├── LogBackupService
  |     ├── LocalStore
  |     └── RecoveryService
  |
  |-- Seebot.WorkerAgent.Tests
```

### 7.1 核心类说明


| 类 / 模块             | 职责                                          |
| ------------------ | ------------------------------------------- |
| WorkerStateMachine | 管理 Agent 侧 worker 状态流转                      |
| SchedulerClient    | 查询调度中心任务、上报心跳和状态                            |
| VmSwitchService    | 编排停止执行器、日志备份、关机、回滚、开机                       |
| VmrunService       | 封装 vmrun 命令                                 |
| GuestWorkerClient  | 调用 VM 内 executor-control / worker status 接口 |
| LogBackupService   | 复制 VM 日志到宿主机并生成 manifest                    |
| LocalStore         | SQLite 本地状态持久化                              |
| RecoveryService    | Agent 重启后的事务恢复                              |


---

## 八、配置设计

### 8.1 appsettings.json 示例

```json
{
  "Agent": {
    "HostId": "HOST-SR20-001",
    "AgentName": "SR20宿主机Agent",
    "PollIntervalSeconds": 10,
    "HeartbeatIntervalSeconds": 15,
    "SwitchTimeoutSeconds": 300,
    "WaitVmReadyTimeoutSeconds": 180,
    "WaitUpgradeTimeoutSeconds": 600,
    "ForceRevertWhenBackupFailed": false,
    "AllowRevertWhenRunnerError": true
  },
  "Scheduler": {
    "BaseUrl": "http://seebot-server/api/rpa",
    "AccessToken": "replace-with-token"
  },
  "Vmrun": {
    "VmrunPath": "C:\\Program Files (x86)\\VMware\\VMware Workstation\\vmrun.exe"
  },
  "VirtualMachines": [
    {
      "VmName": "VM-RPA-001",
      "VmxPath": "D:\\VMs\\VM-RPA-001\\VM-RPA-001.vmx",
      "GuestIp": "192.168.100.101",
      "GuestUser": "Administrator",
      "GuestPasswordSecret": "encrypted-password",
      "ExecutorStopUrl": "http://192.168.100.101:18080/executor/stop",
      "ExecutorHealthUrl": "http://192.168.100.101:18080/executor/health",
      "WorkerStatusUrl": "http://192.168.100.101:18080/worker/status",
      "GuestLogPath": "C:\\seebot\\logs",
      "HostBackupRoot": "D:\\seebot-vm-log-backup",
      "Workers": [
        {
          "WorkerId": "SR20-2606-POC1",
          "SnapshotName": "SR20-2606-POC1",
          "ProfileCode": "CA-A",
          "Enabled": true
        },
        {
          "WorkerId": "SR20-2606-POC2",
          "SnapshotName": "SR20-2606-POC2",
          "ProfileCode": "CA-B",
          "Enabled": true
        }
      ]
    }
  ]
}
```

### 8.2 配置校验规则

Agent 启动时必须校验：

1. `vmrun.exe` 是否存在；
2. VMX 文件是否存在；
3. workerId 是否重复；
4. snapshotName 是否配置；
5. VM 快照是否存在；
6. 宿主机日志备份目录是否可写；
7. 调度中心是否可访问；
8. VM 内状态接口是否可访问；
9. guest 账号密码是否可用；
10. `ForceRevertWhenBackupFailed` 是否明确配置。

---

## 九、状态设计

### 9.1 Agent 状态

```text
STARTING       启动中
RUNNING        正常运行
PAUSED         人工暂停
ERROR          异常
STOPPING       停止中
```

### 9.2 VM 状态

```text
UNKNOWN        未知
POWERED_OFF    已关机
POWERED_ON     已开机
STOPPING       关机中
REVERTING      快照回滚中
STARTING       启动中
WAIT_READY     等待 VM 可用
ERROR          异常
QUARANTINED    隔离
```

### 9.3 Agent 侧 Worker 状态

Agent 侧 Worker 状态用于描述宿主机 Agent 对当前 workerId / 快照的管理状态，不替代 runner 原有状态。

```text
DISABLED       禁用
READY          快照存在，可用
IDLE           Agent 判断当前可切换或可监控
HAS_PENDING    调度中心存在待执行任务
PRE_SWITCH     切换前准备
STOPPING       停止执行器中
BACKUPPING     日志备份中
POWERING_OFF   VM 关机中
REVERTING      快照回滚中
POWERING_ON    VM 启动中
WAIT_READY     等待 VM / runner 可用
MONITORING     监控 runner 状态中
ERROR          异常
QUARANTINED    隔离
```

### 9.4 runner 原有状态枚举

Agent 必须兼容并直接使用原有 runner 状态：


| 状态码 | 状态名称          | 状态说明                |
| --- | ------------- | ------------------- |
| 0   | New           | 初始化状态、用户未登录         |
| 1   | Runnable      | 机器人已启动，准备就绪         |
| 2   | Running       | 机器人正在执行任务中          |
| 3   | Closed        | 关闭                  |
| 4   | RobotError    | 机器人程序内部异常           |
| 5   | ClientError   | 客户端 rpa-client 内部异常 |
| 6   | Upgrading     | 执行器正在升级             |
| 7   | UpgradeFailed | 执行器升级失败             |
| 8   | Offline       | 离线                  |


### 9.5 Agent 对 runner 状态的处理规则


| runner 状态码 | 状态名称          | 是否表示正在执行任务 | 切换快照前处理     | VM 启动后处理                           |
| ---------- | ------------- | ---------- | ----------- | ---------------------------------- |
| 0          | New           | 否          | 可停止、可备份、可切换 | 不算 ready，继续等待变为 Runnable 或 Running |
| 1          | Runnable      | 否          | 可停止、可备份、可切换 | 视为 ready                           |
| 2          | Running       | 是          | 禁止切换，继续监控   | 视为 runner 已接到任务并开始执行               |
| 3          | Closed        | 否          | 可备份、可切换     | 不算 ready，继续等待或报错                   |
| 4          | RobotError    | 否          | 记录异常，可备份后切换 | 启动后出现则标记 worker 异常                 |
| 5          | ClientError   | 否          | 记录异常，可备份后切换 | 启动后出现则标记 worker 异常                 |
| 6          | Upgrading     | 特殊状态       | 禁止切换，避免打断升级 | 继续等待升级完成                           |
| 7          | UpgradeFailed | 否          | 记录异常，可备份后切换 | 启动后出现则标记 worker 异常                 |
| 8          | Offline       | 否 / 不可确认   | 若无法连接则走异常处理 | 启动后仍 Offline 则 VM ready 失败         |


### 9.6 状态判断原则

1. `runnerStatusCode = 2 Running` 时，禁止快照切换；
2. `runnerStatusCode = 6 Upgrading` 时，禁止快照切换；
3. `runnerStatusCode = 1 Runnable` 时，表示 VM 内机器人已准备就绪；
4. VM 启动后如果状态变为 `2 Running`，也视为自运行成功；
5. VM 启动后如果长期停留在 `0 New`，判定为 `RUNNER_NOT_READY`；
6. VM 启动后如果长期停留在 `3 Closed`，判定为 `RUNNER_CLOSED`；
7. `4 RobotError`、`5 ClientError`、`7 UpgradeFailed`、`8 Offline` 需要上报异常。

---

## 十、主流程设计

### 10.1 Agent 启动流程

```text
1. 加载配置
2. 初始化日志
3. 初始化 SQLite
4. 校验 vmrun.exe
5. 校验 VMX 文件
6. 查询并校验 VM 快照
7. 校验 workerId / snapshotName 映射
8. 恢复上次未完成切换事务
9. 上报 Agent 心跳
10. 进入 workerId 轮询
```

### 10.2 workerId 轮询流程

```text
1. 遍历本机所有 enabled workerId
2. 调用调度中心查询该 workerId 是否有待执行任务
3. 如果无任务，继续下一个 workerId
4. 如果有任务，判断当前 VM 是否空闲
5. 如果 VM 不空闲，跳过
6. 如果 VM 空闲，判断当前快照是否已经是目标 workerId
7. 如果当前快照已经匹配，仅进入 worker 状态监控
8. 如果当前快照不匹配，执行快照切换流程
```

### 10.3 调度中心待执行任务查询

请求：

```http
GET /api/rpa/worker-task/pending?workerId=SR20-2606-POC1
```

响应：

```json
{
  "hasTask": true,
  "workerId": "SR20-2606-POC1",
  "pendingCount": 3,
  "firstTaskId": 123456,
  "executionCode": "EXE202606110001",
  "priority": 5
}
```

说明：

1. 本版本不申请 lease；
2. `firstTaskId` 仅用于 Agent 日志记录和辅助展示；
3. 实际任务拉取仍由 VM 内 `rpa-runner` 完成；
4. 调度中心需保证同一个 workerId 的任务不会被多个执行环境重复消费。

---

## 十一、VM 空闲判断

### 11.1 允许继续切换的 runner 状态

```text
0 New
1 Runnable
3 Closed
```

### 11.2 禁止切换的 runner 状态

```text
2 Running
6 Upgrading
```

### 11.3 异常处理后可按配置继续切换的 runner 状态

```text
4 RobotError
5 ClientError
7 UpgradeFailed
8 Offline
```

### 11.4 详细判断流程

```text
1. Agent 查询 VM 内 runner 状态

2. 如果 runnerStatus = 2 Running：
      禁止切换
      上报 WORKER_RUNNING
      下次轮询继续判断

3. 如果 runnerStatus = 6 Upgrading：
      禁止切换
      上报 WORKER_UPGRADING
      等待升级完成

4. 如果 runnerStatus in [0, 1, 3]：
      允许进入停止执行器、日志备份、快照切换流程

5. 如果 runnerStatus in [4, 5, 7, 8]：
      记录当前 worker 异常
      尝试备份日志
      按配置决定是否继续快照切换
```

---

## 十二、快照切换流程

```text
1. 创建 switch_transaction
2. 标记目标 worker 状态为 PRE_SWITCH
3. 调用 VM 内 executor-control 停止执行器端口
4. 确认 VM 内 runner 状态不再 Running / Upgrading
5. 从 VM 复制日志到宿主机
6. 生成 backup_manifest.json
7. 使用 vmrun stop 关闭 VM
8. 使用 vmrun revertToSnapshot 回滚到目标快照
9. 使用 vmrun start 启动 VM
10. 等待 VM 网络可用
11. 等待 rpa-client / rpa-runner 自启动
12. 监控 runner 状态
13. 上报切换完成
```

注意：

```text
Agent 不执行 runner 启动命令。
Agent 不向 runner 下发 taskId。
Agent 只等待 VM 内 runner 自启动并进入可监控状态。
```

---

## 十三、VM 启动后监控流程

```text
1. vmrun start VM
2. 等待 VM 网络可用
3. 调用 ExecutorHealthUrl
4. 读取 runnerStatusCode
5. 如果 runnerStatusCode = 1 Runnable：
      判定 VM / runner ready
      上报 worker ready
      进入 MONITORING

6. 如果 runnerStatusCode = 2 Running：
      判定 VM / runner 已经自启动并开始执行任务
      上报 worker running
      进入 MONITORING

7. 如果 runnerStatusCode = 0 New：
      继续等待
      如果超过 WaitVmReadyTimeoutSeconds，判定 RUNNER_NOT_READY

8. 如果 runnerStatusCode = 3 Closed：
      继续等待或尝试检查 rpa-client
      超时后判定 RUNNER_CLOSED

9. 如果 runnerStatusCode = 4 RobotError：
      判定 ROBOT_ERROR
      标记 worker 异常

10. 如果 runnerStatusCode = 5 ClientError：
      判定 CLIENT_ERROR
      标记 worker 异常

11. 如果 runnerStatusCode = 6 Upgrading：
      等待升级完成
      超时后判定 WORKER_UPGRADING_TIMEOUT

12. 如果 runnerStatusCode = 7 UpgradeFailed：
      判定 UPGRADE_FAILED
      标记 worker 异常

13. 如果 runnerStatusCode = 8 Offline：
      判定 WORKER_OFFLINE
      标记 VM ready 失败
```

---

## 十四、vmrun 控制设计

### 14.1 查询快照

```bat
vmrun listSnapshots "D:\VMs\VM-RPA-001\VM-RPA-001.vmx"
```

### 14.2 停止 VM

优先软关机：

```bat
vmrun stop "D:\VMs\VM-RPA-001\VM-RPA-001.vmx" soft
```

软关机失败后，按配置允许时使用强制关机：

```bat
vmrun stop "D:\VMs\VM-RPA-001\VM-RPA-001.vmx" hard
```

### 14.3 回滚快照

```bat
vmrun revertToSnapshot "D:\VMs\VM-RPA-001\VM-RPA-001.vmx" "SR20-2606-POC1"
```

### 14.4 启动 VM

```bat
vmrun start "D:\VMs\VM-RPA-001\VM-RPA-001.vmx" nogui
```

### 14.5 从 VM 复制日志到宿主机

```bat
vmrun -gu Administrator -gp "password" copyFileFromGuestToHost ^
  "D:\VMs\VM-RPA-001\VM-RPA-001.vmx" ^
  "C:\seebot\logs\runner.log" ^
  "D:\seebot-vm-log-backup\VM-RPA-001\runner.log"
```

### 14.6 vmrun 封装接口

```csharp
public interface IVmrunService
{
    Task<IReadOnlyList<string>> ListSnapshotsAsync(string vmxPath, CancellationToken ct);

    Task StopVmAsync(string vmxPath, VmStopMode mode, CancellationToken ct);

    Task RevertToSnapshotAsync(string vmxPath, string snapshotName, CancellationToken ct);

    Task StartVmAsync(string vmxPath, bool noGui, CancellationToken ct);

    Task CopyFileFromGuestToHostAsync(
        string vmxPath,
        string guestUser,
        string guestPassword,
        string guestPath,
        string hostPath,
        CancellationToken ct);
}
```

---

## 十五、VM 内 executor-control 设计

### 15.1 健康检查接口

```http
GET /executor/health
```

响应示例：

```json
{
  "success": true,
  "workerId": "SR20-2606-POC1",
  "runnerStatusCode": 1,
  "runnerStatusName": "Runnable",
  "runnerStatusDesc": "机器人已启动，准备就绪",
  "currentTaskId": null,
  "executionCode": null,
  "javaProcessCount": 1,
  "pythonProcessCount": 0,
  "chromeProcessCount": 0,
  "diskFreeGb": 45,
  "timestamp": "2026-06-11 14:30:00"
}
```

### 15.2 runner 状态接口

```http
GET /worker/status
```

响应示例：

```json
{
  "success": true,
  "workerId": "SR20-2606-POC1",
  "runnerStatusCode": 2,
  "runnerStatusName": "Running",
  "runnerStatusDesc": "机器人正在执行任务中",
  "currentTaskId": 123456,
  "executionCode": "EXE202606110001",
  "lastHeartbeatTime": "2026-06-11 14:30:00"
}
```

### 15.3 停止执行器接口

```http
POST /executor/stop
```

请求：

```json
{
  "reason": "SNAPSHOT_SWITCH",
  "txId": "SWITCH-20260611-0001",
  "deadlineSeconds": 30
}
```

响应：

```json
{
  "success": true,
  "beforeRunnerStatusCode": 1,
  "beforeRunnerStatusName": "Runnable",
  "afterRunnerStatusCode": 3,
  "afterRunnerStatusName": "Closed",
  "logFlushed": true
}
```

说明：

1. 如果 `beforeRunnerStatusCode = 2 Running`，executor-control 应拒绝停止，除非明确传入 `force=true`；
2. 默认情况下，Agent 不应在 runner 正在执行任务时切换快照；
3. executor-control 返回的状态必须使用原有 0–8 runner 状态码。

---

## 十六、调度中心接口设计

### 16.1 查询 workerId 待执行任务

```http
GET /api/rpa/worker-task/pending?workerId=SR20-2606-POC1
```

响应：

```json
{
  "hasTask": true,
  "workerId": "SR20-2606-POC1",
  "pendingCount": 3,
  "firstTaskId": 123456,
  "executionCode": "EXE202606110001",
  "priority": 5
}
```

### 16.2 上报 Agent 心跳

```http
POST /api/rpa/host-agent/heartbeat
```

请求：

```json
{
  "hostId": "HOST-SR20-001",
  "agentName": "SR20宿主机Agent",
  "status": "RUNNING",
  "vmCount": 1,
  "timestamp": "2026-06-11 14:30:00"
}
```

### 16.3 上报 worker 状态

```http
POST /api/rpa/worker/status
```

请求：

```json
{
  "hostId": "HOST-SR20-001",
  "vmName": "VM-RPA-001",
  "workerId": "SR20-2606-POC1",
  "snapshotName": "SR20-2606-POC1",
  "agentWorkerStatus": "MONITORING",
  "runnerStatusCode": 2,
  "runnerStatusName": "Running",
  "runnerStatusDesc": "机器人正在执行任务中",
  "currentTaskId": 123456,
  "executionCode": "EXE202606110001",
  "lastHeartbeatTime": "2026-06-11 14:30:00"
}
```

说明：

1. `agentWorkerStatus` 是宿主机 Agent 对 worker 的管理状态；
2. `runnerStatusCode` 是 VM 内 rpa-client / rpa-runner 原有状态码；
3. 页面展示时应优先展示 `runnerStatusName` 和 `runnerStatusDesc`；
4. 判断是否正在执行任务时，以 `runnerStatusCode = 2` 为准。

### 16.4 上报快照切换记录

```http
POST /api/rpa/worker/switch-log
```

请求：

```json
{
  "txId": "SWITCH-20260611-0001",
  "hostId": "HOST-SR20-001",
  "vmName": "VM-RPA-001",
  "fromWorkerId": "SR20-2606-POC2",
  "toWorkerId": "SR20-2606-POC1",
  "snapshotName": "SR20-2606-POC1",
  "firstTaskId": 123456,
  "status": "SUCCESS",
  "startedAt": "2026-06-11 14:20:00",
  "finishedAt": "2026-06-11 14:23:10",
  "durationSeconds": 190
}
```

### 16.5 上报日志备份结果

```http
POST /api/rpa/worker/log-backup-result
```

请求：

```json
{
  "txId": "SWITCH-20260611-0001",
  "hostId": "HOST-SR20-001",
  "vmName": "VM-RPA-001",
  "workerId": "SR20-2606-POC1",
  "firstTaskId": 123456,
  "success": true,
  "backupPath": "D:\\seebot-vm-log-backup\\...",
  "fileCount": 128,
  "totalBytes": 98234212
}
```

---

## 十七、本地事务设计

### 17.1 本地事务表

```sql
CREATE TABLE IF NOT EXISTS local_switch_transaction (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    tx_id TEXT NOT NULL,
    host_id TEXT NOT NULL,
    vm_name TEXT NOT NULL,
    from_worker_id TEXT,
    to_worker_id TEXT NOT NULL,
    snapshot_name TEXT NOT NULL,
    first_task_id INTEGER,
    status TEXT NOT NULL,
    step TEXT,
    error_code TEXT,
    error_message TEXT,
    started_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    finished_at TEXT
);
```

### 17.2 事务状态

```text
CREATED
STOP_EXECUTOR_DONE
LOG_BACKUP_DONE
VM_STOP_DONE
SNAPSHOT_REVERT_DONE
VM_START_DONE
WORKER_READY_DONE
MONITORING_DONE
SUCCESS
FAILED
NEED_MANUAL_CHECK
```

### 17.3 事务恢复规则

Agent 重启后扫描未完成事务：


| 上次状态                 | 恢复策略               |
| -------------------- | ------------------ |
| CREATED              | 标记失败，等待人工确认        |
| STOP_EXECUTOR_DONE   | 尝试继续日志备份           |
| LOG_BACKUP_DONE      | 可继续关闭 VM           |
| VM_STOP_DONE         | 可继续回滚快照            |
| SNAPSHOT_REVERT_DONE | 可继续启动 VM           |
| VM_START_DONE        | 可继续等待 worker ready |
| WORKER_READY_DONE    | 进入状态监控             |
| FAILED               | 上报失败               |
| NEED_MANUAL_CHECK    | 不自动恢复              |


---

## 十八、服务端表结构建议

### 18.1 Worker 实例表

```sql
CREATE TABLE rpa_worker_instance (
    id BIGINT PRIMARY KEY AUTO_INCREMENT,
    host_id VARCHAR(64) NOT NULL,
    vm_name VARCHAR(128) NOT NULL,
    worker_id VARCHAR(128) NOT NULL,
    snapshot_name VARCHAR(128) NOT NULL,
    profile_code VARCHAR(128),
    enabled TINYINT NOT NULL DEFAULT 1,

    agent_worker_status VARCHAR(32),

    runner_status_code TINYINT,
    runner_status_name VARCHAR(32),
    runner_status_desc VARCHAR(128),

    current_task_id BIGINT,
    execution_code VARCHAR(128),

    last_heartbeat_time DATETIME,
    last_switch_time DATETIME,

    error_code VARCHAR(64),
    error_message TEXT,

    created_at DATETIME NOT NULL,
    updated_at DATETIME NOT NULL,

    UNIQUE KEY uk_host_vm_worker (host_id, vm_name, worker_id),
    KEY idx_worker_status (worker_id, runner_status_code),
    KEY idx_runner_status (runner_status_code)
);
```

### 18.2 快照切换记录表

```sql
CREATE TABLE rpa_worker_switch_log (
    id BIGINT PRIMARY KEY AUTO_INCREMENT,
    tx_id VARCHAR(128) NOT NULL,
    host_id VARCHAR(64) NOT NULL,
    vm_name VARCHAR(128) NOT NULL,
    from_worker_id VARCHAR(128),
    to_worker_id VARCHAR(128) NOT NULL,
    snapshot_name VARCHAR(128) NOT NULL,
    first_task_id BIGINT,
    status VARCHAR(32) NOT NULL,
    error_code VARCHAR(64),
    error_message TEXT,
    started_at DATETIME NOT NULL,
    finished_at DATETIME,
    duration_seconds INT,
    created_at DATETIME NOT NULL,
    UNIQUE KEY uk_tx_id (tx_id)
);
```

### 18.3 日志备份记录表

```sql
CREATE TABLE rpa_worker_log_backup (
    id BIGINT PRIMARY KEY AUTO_INCREMENT,
    tx_id VARCHAR(128),
    host_id VARCHAR(64) NOT NULL,
    vm_name VARCHAR(128) NOT NULL,
    worker_id VARCHAR(128) NOT NULL,
    first_task_id BIGINT,
    backup_path VARCHAR(1000) NOT NULL,
    file_count INT,
    total_bytes BIGINT,
    success TINYINT NOT NULL,
    error_code VARCHAR(64),
    error_message TEXT,
    created_at DATETIME NOT NULL
);
```

---

## 十九、日志备份设计

### 19.1 备份目录

```text
D:\seebot-vm-log-backup\
  └── HOST-SR20-001\
      └── VM-RPA-001\
          └── SR20-2606-POC1\
              └── 20260611\
                  └── SWITCH-20260611-0001\
                      ├── runner\
                      ├── client\
                      ├── screenshots\
                      ├── agent\
                      └── backup_manifest.json
```

### 19.2 backup_manifest.json

```json
{
  "txId": "SWITCH-20260611-0001",
  "hostId": "HOST-SR20-001",
  "vmName": "VM-RPA-001",
  "fromWorkerId": "SR20-2606-POC2",
  "toWorkerId": "SR20-2606-POC1",
  "firstTaskId": 123456,
  "backupTime": "2026-06-11 14:30:00",
  "sourcePath": "C:\\seebot\\logs",
  "targetPath": "D:\\seebot-vm-log-backup\\HOST-SR20-001\\VM-RPA-001\\...",
  "fileCount": 128,
  "totalBytes": 98234212,
  "success": true
}
```

### 19.3 备份失败处理

默认策略：

```text
日志备份失败，不继续回滚快照。
```

如果配置允许强制回滚：

```json
{
  "ForceRevertWhenBackupFailed": true
}
```

则必须上报：

```text
LOG_BACKUP_FAILED_BUT_FORCE_REVERT
```

---

## 二十、错误码设计


| 错误码                      | 含义                             |
| ------------------------ | ------------------------------ |
| SCHEDULER_UNAVAILABLE    | 调度中心不可用                        |
| VM_NOT_IDLE              | VM 非空闲                         |
| WORKER_RUNNING           | runner 状态为 Running，禁止切换        |
| WORKER_UPGRADING         | runner 状态为 Upgrading，禁止切换      |
| EXECUTOR_STOP_FAILED     | 停止执行器失败                        |
| LOG_BACKUP_FAILED        | 日志备份失败                         |
| VM_STOP_FAILED           | VM 关机失败                        |
| SNAPSHOT_NOT_FOUND       | 快照不存在                          |
| SNAPSHOT_REVERT_FAILED   | 快照回滚失败                         |
| VM_START_FAILED          | VM 启动失败                        |
| VM_READY_TIMEOUT         | VM ready 超时                    |
| RUNNER_NOT_READY         | runner 长时间停留在 New              |
| RUNNER_CLOSED            | runner 状态为 Closed，未自动恢复        |
| ROBOT_ERROR              | runner 状态为 RobotError          |
| CLIENT_ERROR             | runner 状态为 ClientError         |
| WORKER_UPGRADING_TIMEOUT | runner 长时间处于 Upgrading         |
| UPGRADE_FAILED           | runner 状态为 UpgradeFailed       |
| WORKER_OFFLINE           | runner 状态为 Offline             |
| WORKER_ID_MISMATCH       | VM 内 workerId 与目标 workerId 不一致 |
| LOCAL_STATE_CORRUPTED    | 本地状态异常                         |
| WORKER_QUARANTINED       | Worker 已隔离                     |


---

## 二十一、异常处理规则

### 21.1 调度中心不可用

```text
1. 记录 SCHEDULER_UNAVAILABLE
2. Agent 保持运行
3. 延迟后继续重试
4. 不执行快照切换
```

### 21.2 runner 状态为 Running

```text
1. 禁止快照切换
2. 上报 WORKER_RUNNING
3. 保持当前 VM 运行
4. 下次轮询继续检查
```

### 21.3 runner 状态为 Upgrading

```text
1. 禁止快照切换
2. 上报 WORKER_UPGRADING
3. 等待升级完成
4. 如果超过升级等待阈值，上报 WORKER_UPGRADING_TIMEOUT
```

### 21.4 runner 状态为 New

```text
1. VM 启动后允许短时间处于 New
2. 继续等待状态变为 Runnable 或 Running
3. 如果超时仍为 New，上报 RUNNER_NOT_READY
```

### 21.5 runner 状态为 Runnable

```text
1. 视为机器人已启动，准备就绪
2. VM 启动后可判定 ready
3. 快照切换前可停止、备份、切换
```

### 21.6 runner 状态为 Closed

```text
1. 快照切换前可视为非执行中
2. VM 启动后如果长时间为 Closed，则上报 RUNNER_CLOSED
3. 是否需要人工处理由配置决定
```

### 21.7 runner 状态为 RobotError

```text
1. 上报 ROBOT_ERROR
2. 记录当前 worker 异常
3. 备份日志
4. 按配置决定是否隔离或切换快照
```

### 21.8 runner 状态为 ClientError

```text
1. 上报 CLIENT_ERROR
2. 记录 rpa-client 内部异常
3. 备份日志
4. 优先标记 worker 异常
5. 按配置决定是否隔离或切换快照
```

### 21.9 runner 状态为 UpgradeFailed

```text
1. 上报 UPGRADE_FAILED
2. 标记 worker 异常
3. 不再自动接收任务
4. 等待人工处理或重新升级
```

### 21.10 runner 状态为 Offline

```text
1. 上报 WORKER_OFFLINE
2. 检查 VM 网络
3. 检查 rpa-client 是否启动
4. 如果 VM 已启动但 runner 仍 Offline，标记 VM_READY_TIMEOUT
```

---

## 二十二、部署设计

### 22.1 服务目录

```text
D:\seebot-agent\
  ├── Seebot.WorkerAgent.Service.exe
  ├── appsettings.json
  ├── logs\
  ├── data\
  │   └── agent.db
  ├── scripts\
  └── backups\
```

### 22.2 Windows Service 安装

```bat
sc create Seebot.WorkerAgent.Service ^
  binPath= "D:\seebot-agent\Seebot.WorkerAgent.Service.exe" ^
  start= auto

sc start Seebot.WorkerAgent.Service
```

### 22.3 服务恢复策略

建议配置：

```text
第一次失败：自动重启
第二次失败：自动重启
后续失败：自动重启
重启间隔：60 秒
```

---

## 二十三、验收标准

### 23.1 技术验收


| 验收项           | 标准                                    |
| ------------- | ------------------------------------- |
| Agent 服务      | 可作为 Windows Service 启动                |
| vmrun 控制      | 能 stop / revertToSnapshot / start VM  |
| 快照校验          | 能校验 workerId 对应快照存在                   |
| 队列查询          | 能按 workerId 查询待执行任务                   |
| runner 状态兼容   | Agent 能识别 0–8 原有 runner 状态            |
| Running 判断    | runnerStatusCode = 2 时禁止快照切换          |
| Upgrading 判断  | runnerStatusCode = 6 时禁止快照切换          |
| Runnable 判断   | runnerStatusCode = 1 时判定 VM ready     |
| Running 启动判断  | VM 启动后 runnerStatusCode = 2 时也视为自运行成功 |
| New 超时        | 长时间为 0 New 时上报 RUNNER_NOT_READY       |
| Closed 超时     | 长时间为 3 Closed 时上报 RUNNER_CLOSED       |
| RobotError    | 状态 4 能上报 ROBOT_ERROR                  |
| ClientError   | 状态 5 能上报 CLIENT_ERROR                 |
| UpgradeFailed | 状态 7 能上报 UPGRADE_FAILED               |
| Offline       | 状态 8 能上报 WORKER_OFFLINE               |
| 执行器停止         | 切换前能停止 VM 内执行器                        |
| 日志备份          | 切换前能复制 VM 日志到宿主机                      |
| 快照回滚          | 能切换到目标 workerId 快照                    |
| 状态上报          | 能上报 Agent、VM、worker、runner 状态         |
| 事务恢复          | Agent 重启后能恢复未完成事务                     |
| 异常隔离          | 快照失败、VM ready 超时能隔离 worker            |


### 23.2 业务验收


| 验收项         | 标准                                                           |
| ----------- | ------------------------------------------------------------ |
| workerId 切换 | 至少 2 个 workerId 快照可互相切换                                      |
| 自动执行链路      | 切换快照后 VM 内 rpa-client / rpa-runner 能自启动并执行任务                 |
| 正在执行保护      | 任务执行中不会被 Agent 强制切换快照                                        |
| 升级保护        | 执行器升级中不会被 Agent 强制切换快照                                       |
| 自启动验证       | VM 启动后 rpa-client / rpa-runner 自动进入 Runnable 或 Running       |
| 日志追溯        | 每次切换都有日志备份目录和 manifest                                       |
| 环境隔离        | 上一 worker 环境污染不会影响下一 worker                                  |
| 切换耗时        | 记录每次 stop / revert / start / ready 耗时                        |
| 异常可追溯       | RobotError / ClientError / UpgradeFailed / Offline 均能留存日志并上报 |
| 状态一致性       | 调度中心展示状态与 VM 内 runner 原有状态一致                                 |


---

## 二十四、分阶段实施计划

### 阶段 1：vmrun 控制 POC

目标：

```text
1. Agent 能加载配置
2. Agent 能调用 vmrun listSnapshots
3. Agent 能调用 vmrun stop
4. Agent 能调用 vmrun revertToSnapshot
5. Agent 能调用 vmrun start
6. Agent 能记录本地切换事务
```

交付物：

1. `Seebot.WorkerAgent.Service` 初版；
2. `VmrunService`；
3. appsettings.json 模板；
4. 本地 SQLite 表；
5. 快照切换日志。

### 阶段 2：调度中心查询

目标：

```text
1. Agent 能按 workerId 查询待执行任务
2. 有任务时触发快照切换判断
3. 无任务时继续轮询
4. 能上报 Agent 心跳
```

交付物：

1. `SchedulerClient`；
2. pending 查询接口；
3. Agent 心跳接口；
4. worker 状态上报接口。

### 阶段 3：runner 状态监控

目标：

```text
1. 读取 runner 原有 0–8 状态
2. Running 禁止切换
3. Upgrading 禁止切换
4. Runnable 判定 ready
5. RobotError / ClientError / UpgradeFailed / Offline 可上报异常
```

交付物：

1. `GuestWorkerClient`；
2. `/executor/health` 对接；
3. `/worker/status` 对接；
4. 状态码映射逻辑；
5. 状态上报接口。

### 阶段 4：切换前事务

目标：

```text
1. 停止 VM 内执行器端口
2. 确认 runner 非 Running / Upgrading
3. 复制日志到宿主机
4. 生成 backup_manifest.json
5. 日志备份失败时阻断回滚
```

交付物：

1. `LogBackupService`；
2. executor stop 接口对接；
3. 备份 manifest；
4. 备份记录表；
5. 错误码上报。

### 阶段 5：快照切换和状态恢复

目标：

```text
1. 关闭 VM
2. 回滚目标快照
3. 启动 VM
4. 等待 rpa-client 自启动
5. 等待 rpa-runner 自启动
6. 监控 runner 状态
7. Agent 重启后恢复未完成事务
```

交付物：

1. `VmSwitchService`；
2. `RecoveryService`；
3. 快照切换记录表；
4. 异常隔离逻辑。

---

## 二十五、开发优先级

### P0 必须实现

1. appsettings 配置加载；
2. vmrun 命令封装；
3. workerId / snapshotName 映射；
4. 快照存在性校验；
5. 调度中心 pending 查询；
6. runner 0–8 状态读取；
7. VM 空闲判断；
8. Running / Upgrading 禁止切换；
9. 停止执行器端口；
10. 日志备份；
11. VM stop / revert / start；
12. VM 启动后 Runnable / Running 状态判断；
13. 本地事务表；
14. 心跳和状态上报。

### P1 第二批实现

1. Agent 重启事务恢复；
2. 快照切换耗时统计；
3. backup_manifest.json；
4. Worker 隔离；
5. 人工解除隔离；
6. 本机运维 API；
7. 错误码统一上报；
8. New / Closed / Offline 超时处理。

### P2 后续增强

1. 多 VM 管理；
2. UKey / 证书枚举状态接入；
3. 健康评分；
4. 自动重新入池；
5. 后台状态看板；
6. 生产运行报表；
7. vSphere / ESXi Provider。

---

## 二十六、最终执行闭环

```text
Agent 启动
    ↓
加载 workerId / 快照配置
    ↓
按 workerId 查询调度中心待执行任务
    ↓
发现目标 workerId 有任务
    ↓
读取 VM 内 runner 状态
    ↓
runner = Running / Upgrading？
    ├── 是：禁止切换，继续监控
    └── 否：进入切换前事务
            ↓
        停止 VM 内执行器端口
            ↓
        备份 VM 日志到宿主机
            ↓
        vmrun stop VM
            ↓
        vmrun revertToSnapshot 到目标 workerId 快照
            ↓
        vmrun start VM
            ↓
        等待 rpa-client / rpa-runner 自启动
            ↓
        读取 runner 状态
            ↓
        runner = Runnable / Running？
            ├── 是：上报 worker ready / running
            └── 否：按状态码上报异常
            ↓
        持续监控 worker 状态
```

---

## 二十七、总结

本版 RPA Worker Agent 的定位非常明确：

```text
宿主机 Agent 不执行 RPA 业务；
宿主机 Agent 不启动 runner；
宿主机 Agent 不管理 lease；
宿主机 Agent 只使用 vmrun 控制 VM；
宿主机 Agent 使用 runner 原有 0–8 状态判断 VM 是否可切换、是否 ready、是否异常。
```

最终目标是形成一个稳定闭环：

```text
查 workerId 待执行任务
    ↓
判断 runner 状态
    ↓
停止执行器
    ↓
备份日志
    ↓
回滚快照
    ↓
启动 VM
    ↓
等待 rpa-client / rpa-runner 自启动
    ↓
监控原有 runner 状态
    ↓
上报状态和异常
```

