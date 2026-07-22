param(
    [string]$ExePath = "D:\seebot\bin\Seebot.WorkerAgent.Service.exe",
    [string]$TaskName = "SeebotWorkerAgentStartup",
    [string]$TaskUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name,
    [string]$ExeArguments = "",
    [switch]$Restart,
    [switch]$Stop
)

$ErrorActionPreference = "Stop"

function Resolve-FullPath {
    param([string]$PathValue)

    return [System.IO.Path]::GetFullPath($PathValue)
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-AgentProcesses {
    param([string]$ExecutablePath)

    $targetPath = Resolve-FullPath $ExecutablePath
    $processFileName = [System.IO.Path]::GetFileName($targetPath)
    $processes = Get-CimInstance Win32_Process -Filter "Name='$processFileName'" -ErrorAction SilentlyContinue

    foreach ($process in $processes) {
        if ([string]::IsNullOrWhiteSpace($process.ExecutablePath)) {
            continue
        }

        try {
            if ([string]::Equals(
                (Resolve-FullPath $process.ExecutablePath),
                $targetPath,
                [StringComparison]::OrdinalIgnoreCase)) {
                Write-Output $process
            }
        }
        catch {
            # Ignore processes whose executable path cannot be resolved.
        }
    }

}

function Wait-AgentProcessState {
    param(
        [string]$ExecutablePath,
        [bool]$ShouldBeRunning,
        [int]$TimeoutSeconds = 15
    )

    $deadline = [DateTimeOffset]::Now.AddSeconds($TimeoutSeconds)
    do {
        $processes = @(Get-AgentProcesses $ExecutablePath)
        if (($processes.Count -gt 0) -eq $ShouldBeRunning) {
            return $processes
        }

        Start-Sleep -Milliseconds 500
    } while ([DateTimeOffset]::Now -lt $deadline)

    $expected = if ($ShouldBeRunning) { "start" } else { "stop" }
    throw "Timed out waiting for Agent to $expected. Path=$ExecutablePath, TimeoutSeconds=$TimeoutSeconds"
}

function Ensure-StartupTask {
    param(
        [string]$Name,
        [string]$ExecutablePath,
        [string]$WorkingDirectory,
        [string]$Arguments,
        [string]$UserId
    )

    $existingTask = Get-ScheduledTask -TaskName $Name -ErrorAction SilentlyContinue
    if ($null -ne $existingTask) {
        Write-Host "Startup task already exists: $Name"
        return
    }

    if (-not (Test-IsAdministrator)) {
        throw "Administrator privileges are required to create startup task '$Name'."
    }

    $actionParameters = @{
        Execute = $ExecutablePath
        WorkingDirectory = $WorkingDirectory
    }
    if (-not [string]::IsNullOrWhiteSpace($Arguments)) {
        $actionParameters.Argument = $Arguments
    }

    $action = New-ScheduledTaskAction @actionParameters
    $trigger = New-ScheduledTaskTrigger -AtStartup
    $settings = New-ScheduledTaskSettingsSet `
        -MultipleInstances IgnoreNew `
        -RestartCount 3 `
        -RestartInterval (New-TimeSpan -Minutes 1) `
        -ExecutionTimeLimit ([TimeSpan]::Zero) `
        -StartWhenAvailable

    if ($UserId -eq "SYSTEM" -or $UserId -eq "NT AUTHORITY\SYSTEM") {
        $principal = New-ScheduledTaskPrincipal `
            -UserId "SYSTEM" `
            -LogonType ServiceAccount `
            -RunLevel Highest
    }
    else {
        $principal = New-ScheduledTaskPrincipal `
            -UserId $UserId `
            -LogonType S4U `
            -RunLevel Highest
    }

    $task = New-ScheduledTask `
        -Action $action `
        -Trigger $trigger `
        -Settings $settings `
        -Principal $principal `
        -Description "Start Seebot Worker Agent when Windows starts."

    Register-ScheduledTask -TaskName $Name -InputObject $task -Force | Out-Null
    Write-Host "Startup task created: $Name (User=$UserId)" -ForegroundColor Green
}

if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
    Write-Host "ERROR: Executable was not found: $ExePath" -ForegroundColor Red
    exit 1
}

$resolvedExePath = Resolve-FullPath $ExePath
$workingDirectory = Split-Path -Path $resolvedExePath -Parent

try {
    if ($Restart -and $Stop) {
        throw "Parameters -Restart and -Stop cannot be used together."
    }

    if ($Stop) {
        $startupTask = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        if ($null -ne $startupTask) {
            Write-Host "Stopping startup task instance: $TaskName"
            Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        }

        $runningProcesses = @(Get-AgentProcesses $resolvedExePath)
        foreach ($process in $runningProcesses) {
            Write-Host "Stopping Agent process. PID=$($process.ProcessId)"
            Stop-Process -Id $process.ProcessId -Force -ErrorAction Stop
        }

        [void](Wait-AgentProcessState -ExecutablePath $resolvedExePath -ShouldBeRunning $false)
        Write-Host "Agent stopped. The startup task remains registered for the next Windows startup. Path=$resolvedExePath" -ForegroundColor Green
        exit 0
    }

    Ensure-StartupTask `
        -Name $TaskName `
        -ExecutablePath $resolvedExePath `
        -WorkingDirectory $workingDirectory `
        -Arguments $ExeArguments `
        -UserId $TaskUser

    if ($Restart) {
        Write-Host "Restarting Agent through startup task: $TaskName"
        Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue

        $runningProcesses = @(Get-AgentProcesses $resolvedExePath)
        foreach ($process in $runningProcesses) {
            Write-Host "Stopping Agent process. PID=$($process.ProcessId)"
            Stop-Process -Id $process.ProcessId -Force -ErrorAction Stop
        }

        [void](Wait-AgentProcessState -ExecutablePath $resolvedExePath -ShouldBeRunning $false)
        Start-ScheduledTask -TaskName $TaskName -ErrorAction Stop
        $restartedProcesses = @(Wait-AgentProcessState -ExecutablePath $resolvedExePath -ShouldBeRunning $true)
        $processIds = ($restartedProcesses | ForEach-Object { $_.ProcessId }) -join ","
        Write-Host "Agent restarted in background. PID=$processIds, Path=$resolvedExePath" -ForegroundColor Green
        exit 0
    }

    $existingProcesses = @(Get-AgentProcesses $resolvedExePath)
    if ($existingProcesses.Count -gt 0) {
        $processIds = ($existingProcesses | ForEach-Object { $_.ProcessId }) -join ","
        Write-Host "Agent is already running. PID=$processIds, Path=$resolvedExePath"
        exit 0
    }

    $startParameters = @{
        FilePath = $resolvedExePath
        WorkingDirectory = $workingDirectory
        WindowStyle = "Hidden"
        PassThru = $true
    }
    if (-not [string]::IsNullOrWhiteSpace($ExeArguments)) {
        $startParameters.ArgumentList = $ExeArguments
    }

    $startedProcess = Start-Process @startParameters
    Start-Sleep -Seconds 2
    $startedProcess.Refresh()
    if ($startedProcess.HasExited) {
        throw "Agent exited immediately with code $($startedProcess.ExitCode). Check the startup and application logs."
    }

    Write-Host "Agent started in background. PID=$($startedProcess.Id), Path=$resolvedExePath" -ForegroundColor Green
}
catch {
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
