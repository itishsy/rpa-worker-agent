[CmdletBinding()]
param(
    [ValidateSet("Install", "Run", "Uninstall")]
    [string]$Action = "Install",
    [string]$TaskName = "Seebot Robot After Token",
    [ValidateSet("Logon", "Startup")]
    [string]$TriggerMode = "Logon",
    [string]$RunAsUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name,
    [string]$ApplicationPropertiesPath = "D:\seebon\rpa\application.properties",
    [string]$RobotPath = "D:\seebon\rpa\robot.exe",
    [int]$TimeoutSeconds = 600,
    [int]$PollIntervalSeconds = 1,
    [string[]]$RobotArguments = @()
)

$ErrorActionPreference = "Stop"

function Write-StartupLog {
    param([string]$Message)

    Write-Output ("{0:yyyy-MM-dd HH:mm:ss.fff} {1}" -f (Get-Date), $Message)
}

function Assert-Administrator {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this action from an elevated PowerShell session."
    }
}

function Quote-ProcessArgument {
    param([string]$Value)

    return '"' + $Value.Replace('"', '\"') + '"'
}

function Assert-WaitSettings {
    if ($TimeoutSeconds -le 0) {
        throw "TimeoutSeconds must be greater than zero."
    }

    if ($PollIntervalSeconds -le 0) {
        throw "PollIntervalSeconds must be greater than zero."
    }
}

function Install-RobotStartupTask {
    Assert-Administrator
    Assert-WaitSettings

    if ([string]::IsNullOrWhiteSpace($PSCommandPath) -or
        -not (Test-Path -LiteralPath $PSCommandPath -PathType Leaf)) {
        throw "The current script path could not be resolved."
    }

    $scriptPath = (Resolve-Path -LiteralPath $PSCommandPath).Path
    $powershellPath = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
    $actionArguments = @(
        "-NoProfile"
        "-NonInteractive"
        "-ExecutionPolicy Bypass"
        "-File $(Quote-ProcessArgument $scriptPath)"
        "-Action Run"
        "-ApplicationPropertiesPath $(Quote-ProcessArgument $ApplicationPropertiesPath)"
        "-RobotPath $(Quote-ProcessArgument $RobotPath)"
        "-TimeoutSeconds $TimeoutSeconds"
        "-PollIntervalSeconds $PollIntervalSeconds"
    )
    if ($RobotArguments.Count -gt 0) {
        $actionArguments += "-RobotArguments"
        foreach ($robotArgument in $RobotArguments) {
            $actionArguments += (Quote-ProcessArgument $robotArgument)
        }
    }

    $taskAction = New-ScheduledTaskAction `
        -Execute $powershellPath `
        -Argument ($actionArguments -join " ") `
        -WorkingDirectory (Split-Path -Parent $scriptPath)

    if ($TriggerMode -eq "Logon") {
        if ([string]::IsNullOrWhiteSpace($RunAsUser)) {
            throw "RunAsUser is required when TriggerMode is Logon."
        }

        $trigger = New-ScheduledTaskTrigger -AtLogOn -User $RunAsUser
        $principal = New-ScheduledTaskPrincipal `
            -UserId $RunAsUser `
            -LogonType Interactive `
            -RunLevel Highest
    }
    else {
        $trigger = New-ScheduledTaskTrigger -AtStartup
        $principal = New-ScheduledTaskPrincipal `
            -UserId "SYSTEM" `
            -LogonType ServiceAccount `
            -RunLevel Highest
    }

    $executionTimeLimit = New-TimeSpan -Seconds ($TimeoutSeconds + 300)
    $settings = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries `
        -StartWhenAvailable `
        -MultipleInstances IgnoreNew `
        -RestartCount 3 `
        -RestartInterval (New-TimeSpan -Minutes 1) `
        -ExecutionTimeLimit $executionTimeLimit

    $task = New-ScheduledTask `
        -Action $taskAction `
        -Trigger $trigger `
        -Principal $principal `
        -Settings $settings `
        -Description "Starts robot.exe only after application.properties contains a valid rpa.token."

    Register-ScheduledTask -TaskName $TaskName -InputObject $task -Force | Out-Null

    $registeredTask = Get-ScheduledTask -TaskName $TaskName
    Write-Output "Scheduled task installed successfully."
    Write-Output "TaskName=$($registeredTask.TaskName)"
    Write-Output "TriggerMode=$TriggerMode"
    Write-Output "RunAsUser=$($registeredTask.Principal.UserId)"
    Write-Output "ScriptPath=$scriptPath"
}

function Uninstall-RobotStartupTask {
    Assert-Administrator

    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($null -eq $task) {
        Write-Output "Scheduled task does not exist. TaskName=$TaskName"
        return
    }

    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    Write-Output "Scheduled task uninstalled successfully. TaskName=$TaskName"
}

function Start-RobotAfterToken {
    Assert-WaitSettings

    if (-not (Test-Path -LiteralPath $RobotPath -PathType Leaf)) {
        throw "robot.exe does not exist: $RobotPath"
    }

    $robotProcessName = [System.IO.Path]::GetFileNameWithoutExtension($RobotPath)
    $runningRobot = Get-Process -Name $robotProcessName -ErrorAction SilentlyContinue
    if ($null -ne $runningRobot) {
        Write-StartupLog "robot.exe is already running; duplicate startup was skipped."
        return
    }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    Write-StartupLog "Waiting for a valid rpa.token. Path=$ApplicationPropertiesPath"

    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $ApplicationPropertiesPath -PathType Leaf) {
            try {
                $tokenLine = Get-Content -LiteralPath $ApplicationPropertiesPath -ErrorAction Stop |
                    Where-Object {
                        $_ -match '^\s*rpa\.token\s*=\s*(.+?)\s*$' -and
                        -not [string]::IsNullOrWhiteSpace($Matches[1])
                    } |
                    Select-Object -Last 1

                if ($null -ne $tokenLine) {
                    $runningRobot = Get-Process -Name $robotProcessName -ErrorAction SilentlyContinue
                    if ($null -ne $runningRobot) {
                        Write-StartupLog "A valid token was found, but robot.exe is already running; duplicate startup was skipped."
                        return
                    }

                    $startParameters = @{
                        FilePath         = $RobotPath
                        WorkingDirectory = (Split-Path -Parent $RobotPath)
                        PassThru         = $true
                    }
                    if ($RobotArguments.Count -gt 0) {
                        $startParameters.ArgumentList = $RobotArguments
                    }

                    $process = Start-Process @startParameters
                    Write-StartupLog "A valid token was found and robot.exe was started. ProcessId=$($process.Id)"
                    return
                }
            }
            catch {
                # The host may be writing or atomically replacing the file. Retry.
                Write-StartupLog "application.properties could not be read yet; waiting. Error=$($_.Exception.Message)"
            }
        }

        Start-Sleep -Seconds $PollIntervalSeconds
    }

    throw "Timed out waiting for a valid rpa.token; robot.exe was not started. Path=$ApplicationPropertiesPath, TimeoutSeconds=$TimeoutSeconds"
}

switch ($Action) {
    "Install" {
        Install-RobotStartupTask
    }
    "Run" {
        Start-RobotAfterToken
    }
    "Uninstall" {
        Uninstall-RobotStartupTask
    }
}
