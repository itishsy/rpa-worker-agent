# RPA Worker Agent

Worker Agent runs on the Windows host and coordinates local VM workers.

## Build

```powershell
$env:DOTNET_CLI_HOME = (Resolve-Path -LiteralPath '.').Path
$env:APPDATA = Join-Path (Resolve-Path -LiteralPath '.').Path '.dotnet-appdata'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
dotnet build rpa-worker-agent.csproj
```

## Test

The current P0 skeleton uses a dependency-free smoke test project so it can run
without restoring external test framework packages.

```powershell
$env:DOTNET_CLI_HOME = (Resolve-Path -LiteralPath '.').Path
$env:APPDATA = Join-Path (Resolve-Path -LiteralPath '.').Path '.dotnet-appdata'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
dotnet run --project tests\Seebot.WorkerAgent.Tests\Seebot.WorkerAgent.Tests.csproj
```
