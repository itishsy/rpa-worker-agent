# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**RPA Worker Agent** is a .NET 8 Windows service that orchestrates VMware Workstation VMs running RPA execution environments. It manages multi-VM pools with profile-based snapshot switching, coordinates with a cloud scheduler backend, and maintains local state via SQLite.

**Core Domain:**
- `profileId`: Task scheduling dimension assigned by cloud backend
- `workerId`: VM-local runner instance identity (unique across host)
- `snapshotName`: Revertible VM environment state
- One host → multiple VMs → multiple profiles/snapshots per VM

## Build & Test Commands

Set environment variables before running dotnet commands (required for isolated environments):

```powershell
$env:DOTNET_CLI_HOME = (Resolve-Path -LiteralPath '.').Path
$env:APPDATA = Join-Path (Resolve-Path -LiteralPath '.').Path '.dotnet-appdata'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
```

```powershell
# Build
dotnet build rpa-worker-agent/rpa-worker-agent.csproj

# Run all tests (88 smoke tests, no test framework dependency)
dotnet run --project rpa-worker-agent/tests/Seebot.WorkerAgent.Tests/Seebot.WorkerAgent.Tests.csproj
```

There is no separate "run single test" mechanism — the test runner is a custom smoke test harness in `tests/Seebot.WorkerAgent.Tests/Program.cs`. To isolate a test, comment out others or add a filter by test name inside that file.

## Architecture

### Project Structure

```
rpa-worker-agent/
├── rpa-worker-agent.csproj     # Main service project
├── Program.cs                  # IHostedService setup
├── WorkerAgent.cs              # Main BackgroundService (entry point)
├── Core/
│   ├── ServiceCollectionExtensions.cs  # All DI registration
│   ├── Configuration/          # Options models + validator
│   ├── Domain/                 # Core models, enums, evaluators
│   ├── Vmware/                 # vmrun.exe shell integration
│   ├── Guest/                  # HTTP client to in-VM runner (port 9090)
│   ├── Scheduler/              # HTTP client to cloud backend
│   ├── Storage/                # SQLite local state (agent.db)
│   ├── Backup/                 # VM directory backup before switch
│   ├── Startup/                # Config & snapshot validation on boot
│   ├── Switching/              # Single VM switch orchestration
│   ├── Scheduling/             # Pool scheduling cycle
│   └── Reporting/              # Background heartbeat/status services
└── tests/
    └── Seebot.WorkerAgent.Tests/
        └── Program.cs          # 88 inline smoke tests
```

### Execution Flow

**Startup:**
1. Validate configuration (`WorkerAgentOptionsValidator`)
2. Validate VM snapshots exist (`StartupValidator`)
3. Report host capabilities to cloud backend
4. Initialize SQLite at `{AppContext.BaseDirectory}/data/agent.db`

**Main Scheduling Cycle (`PoolSchedulerService.RunOneCycleAsync`):**
1. Query pending tasks by profileId from cloud backend
2. Load current VM states from SQLite
3. Report VM status to cloud backend
4. Find a compatible idle VM candidate for each pending profile
5. Trigger at most 1 VM switch per cycle

**VM Switch Orchestration (`VmSwitchService.SwitchAsync`):**

Transaction states (in order):
`CREATED → STOP_RUNNER_DONE → LOG_BACKUP_DONE → VM_STOP_DONE → SNAPSHOT_REVERT_DONE → VM_START_DONE → WORKER_READY_DONE → SUCCESS / FAILED / NEED_MANUAL_CHECK`

Steps:
1. Check runner status via GuestWorkerClient (HTTP :9090)
2. Kill runner if not already stopped
3. Backup VM directories (`cache/db/file/logs`) to `{HostWorkPath}/{VmName}/{timestamp}/`
4. `vmrun stop` (soft, then hard after timeout)
5. `vmrun revertToSnapshot`
6. `vmrun start nogui`
7. Poll until runner is Runnable/Running; verify workerId/profileId
8. Report result to cloud backend

### Key Domain Concepts

**RunnerStatusCode enum** (from `Core/Domain/RunnerStatusCode.cs`):
```
New=0, Runnable=1, Running=2, Closed=3, RobotError=4,
ClientError=5, Upgrading=6, UpgradeFailed=7, Offline=8
```

**Switch candidate rules** (from `WorkerStateEvaluator`):
- VM must support the target profile
- Must be Runnable (not Running/Upgrading)
- Idle for ≥ `IdleStableSeconds`
- Current profile's task queue must be empty
- Not quarantined, no active switch transaction

**SQLite tables** (from `Core/Storage/LocalStore.cs`):
- `local_vm_state`: per-VM runtime state (runner code, profile, idle time, quarantine flag)
- `local_switch_transaction`: full switch lifecycle tracking

### HTTP Clients

| Client | Target | Port |
|--------|--------|------|
| `GuestWorkerClient` | In-VM runner status/kill | 9090 (enforced by validator) |
| `SchedulerClient` | Cloud backend API | Configured via `Scheduler:BaseUrl` |

### Test Approach

Tests use a custom in-process `Assert` class (no xUnit/NUnit). Each test is a named action registered in Program.cs. Fakes are hand-rolled (no Moq/AutoFixture). Tests cover all 14 P0 development phases end-to-end.

## Configuration

Configuration comes from `appsettings.json`. Key sections:

```json
{
  "Agent": {
    "HostId": "",
    "PollIntervalSeconds": 30,
    "IdleStableSeconds": 60,
    "MaxSwitchesPerCycle": 1,
    "ForceRevertWhenBackupFailed": false
  },
  "Scheduler": { "BaseUrl": "", "AccessToken": "" },
  "Vmrun": { "VmrunPath": "" },
  "VirtualMachines": [{
    "Name": "", "VmxPath": "", "WorkerId": "",
    "RunnerStatusUrl": "http://...:9090/...",
    "RunnerKillUrl": "http://...:9090/...",
    "Profiles": [{ "ProfileId": "", "SnapshotName": "" }]
  }]
}
```

Constraints enforced at startup:
- `RunnerStatusUrl` and `RunnerKillUrl` must use port 9090
- `WorkerId` must be unique across all VMs on the host
- `ProfileId` must be unique within each VM
- `GuestBackupPaths` must define all four paths: Cache, Db, File, Logs

## Documentation

- `docs/worker-agent-design.md` — Full system design (26+ sections, authoritative reference for architecture decisions)
- `worker-agent-dev.md` — Development prompts (14 steps) with P0 scope, boundaries, and acceptance criteria
- `README.MD` — Build/test quick-start
