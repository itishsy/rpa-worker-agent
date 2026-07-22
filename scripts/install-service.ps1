param(
    [string]$ServiceName = "SeebotWorkerAgent",
    [string]$DisplayName = "Seebot Worker Agent Service",
    [string]$Description = "Seebot Worker Agent service.",
    [string]$ExePath = "C:\Program Files\Seebot Worker Agent\service\Seebot.WorkerAgent.Service.exe",
    [string[]]$AdditionalGrantPaths = @(),
    [string]$ServiceUser,
    [Security.SecureString]$ServicePassword,
    [bool]$StartAfterInstall = $true
)

Write-Host "Installing Windows service: $ServiceName"

$IsAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()
).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $IsAdmin) {
    Write-Host "ERROR: Please run PowerShell as Administrator." -ForegroundColor Red
    exit 1
}

if (-not (Test-Path -LiteralPath $ExePath)) {
    Write-Host "ERROR: Service executable was not found: $ExePath" -ForegroundColor Red
    exit 1
}

function Resolve-FullPath {
    param([string]$PathValue)

    if ([string]::IsNullOrWhiteSpace($PathValue)) {
        return $null
    }

    try {
        return [System.IO.Path]::GetFullPath($PathValue)
    }
    catch {
        return $PathValue
    }
}

function Add-GrantPath {
    param(
        [System.Collections.Generic.HashSet[string]]$PathSet,
        [string]$PathValue
    )

    $resolved = Resolve-FullPath $PathValue
    if ([string]::IsNullOrWhiteSpace($resolved)) {
        return
    }

    [void]$PathSet.Add($resolved)
}

function Add-ParentGrantPath {
    param(
        [System.Collections.Generic.HashSet[string]]$PathSet,
        [string]$PathValue
    )

    $resolved = Resolve-FullPath $PathValue
    if ([string]::IsNullOrWhiteSpace($resolved)) {
        return
    }

    $parent = Split-Path -Path $resolved -Parent
    Add-GrantPath $PathSet $parent
}

function Resolve-ServiceIdentity {
    param([string]$AccountName)

    if ($AccountName -eq "LocalSystem") {
        return "*S-1-5-18"
    }

    try {
        $account = [Security.Principal.NTAccount]::new($AccountName)
        $sid = $account.Translate([Security.Principal.SecurityIdentifier])
        return "*$($sid.Value)"
    }
    catch {
        Write-Host "ERROR: Windows account could not be resolved: $AccountName" -ForegroundColor Red
        exit 1
    }
}

function Grant-ServiceAccountAccess {
    param(
        [string]$PathValue,
        [string]$Identity
    )

    if ([string]::IsNullOrWhiteSpace($PathValue)) {
        return
    }

    if (-not (Test-Path -LiteralPath $PathValue)) {
        Write-Host "Skipping service account ACL, path does not exist: $PathValue" -ForegroundColor Yellow
        return
    }

    Write-Host "Granting service account full control: $PathValue"
    & icacls $PathValue /grant "${Identity}:(OI)(CI)F" /T /C | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Host "WARNING: Failed to grant service account ACL: $PathValue" -ForegroundColor Yellow
    }
}

function Get-AppSettingsPath {
    param([string]$ExecutablePath)

    $exeDirectory = Split-Path -Path (Resolve-FullPath $ExecutablePath) -Parent
    $parentDirectory = Split-Path -Path $exeDirectory -Parent
    $parentSettings = Join-Path $parentDirectory "appsettings.json"
    if (Test-Path -LiteralPath $parentSettings) {
        return $parentSettings
    }

    $exeSettings = Join-Path $exeDirectory "appsettings.json"
    if (Test-Path -LiteralPath $exeSettings) {
        return $exeSettings
    }

    return $null
}

$grantPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$exeDirectory = Split-Path -Path (Resolve-FullPath $ExePath) -Parent
Add-GrantPath $grantPaths $exeDirectory

$appSettingsPath = Get-AppSettingsPath $ExePath
if ($appSettingsPath) {
    Add-ParentGrantPath $grantPaths $appSettingsPath

    try {
        $settings = Get-Content -LiteralPath $appSettingsPath -Raw | ConvertFrom-Json
        Add-GrantPath $grantPaths $settings.Agent.HostWorkPath
        Add-ParentGrantPath $grantPaths $settings.Vmrun.VmrunPath

        if ($settings.VirtualMachines) {
            foreach ($vm in $settings.VirtualMachines) {
                Add-ParentGrantPath $grantPaths $vm.VmxPath
            }
        }
    }
    catch {
        Write-Host "WARNING: Failed to parse appsettings.json for ACL paths: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}
else {
    Write-Host "WARNING: appsettings.json was not found. Only executable and additional paths will be granted." -ForegroundColor Yellow
}

foreach ($path in $AdditionalGrantPaths) {
    foreach ($pathItem in ($path -split '[,;]')) {
        Add-GrantPath $grantPaths $pathItem
    }
}

if ([string]::IsNullOrWhiteSpace($ServiceUser)) {
    $ServiceUser = Read-Host "Windows service account (for example .\seebot-service or DOMAIN\user; press Enter for LocalSystem)"
}

if ([string]::IsNullOrWhiteSpace($ServiceUser)) {
    $ServiceUser = "LocalSystem"
}

if ($ServiceUser.StartsWith(".\", [StringComparison]::Ordinal)) {
    $localUserName = $ServiceUser.Substring(2)
    if ([string]::IsNullOrWhiteSpace($localUserName)) {
        Write-Host "ERROR: Local Windows account name cannot be empty." -ForegroundColor Red
        exit 1
    }

    $ServiceUser = "$env:COMPUTERNAME\$localUserName"
}

$useLocalSystem = $ServiceUser -eq "LocalSystem" -or $ServiceUser -eq "NT AUTHORITY\SYSTEM"
if ($useLocalSystem) {
    $ServiceUser = "LocalSystem"
    $ServicePassword = $null
}
elseif ($null -eq $ServicePassword) {
    $ServicePassword = Read-Host "Password for $ServiceUser" -AsSecureString
}

$serviceIdentity = Resolve-ServiceIdentity $ServiceUser

Write-Host "Granting permissions to service account: $ServiceUser"
foreach ($path in $grantPaths) {
    Grant-ServiceAccountAccess $path $serviceIdentity
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Service already exists. Stopping and deleting: $ServiceName"

    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2

    sc.exe delete $ServiceName
    Start-Sleep -Seconds 3
}

Write-Host "Creating service..."

$QuotedExePath = "`"$ExePath`""

$plainTextPassword = $null
$passwordPointer = [IntPtr]::Zero
try {
    $createArguments = @(
        "create", $ServiceName,
        "binPath=", $QuotedExePath,
        "start=", "auto",
        "obj=", $ServiceUser,
        "DisplayName=", $DisplayName
    )

    if (-not $useLocalSystem) {
        $passwordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($ServicePassword)
        $plainTextPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPointer)
        $createArguments += @("password=", $plainTextPassword)
    }

    & sc.exe $createArguments
}
finally {
    $plainTextPassword = $null
    if ($passwordPointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPointer)
    }
}

if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Service creation failed." -ForegroundColor Red
    exit 1
}

sc.exe description $ServiceName $Description
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/none/60000

if ($StartAfterInstall) {
    Write-Host "Starting service: $ServiceName"
    try {
        Start-Service -Name $ServiceName -ErrorAction Stop
        $installedService = Get-Service -Name $ServiceName -ErrorAction Stop
        $installedService.WaitForStatus(
            [System.ServiceProcess.ServiceControllerStatus]::Running,
            [TimeSpan]::FromSeconds(30)
        )
        $installedService.Refresh()
        Write-Host "Service is running: $ServiceName" -ForegroundColor Green
    }
    catch {
        Write-Host "ERROR: Service was installed but failed to start: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "Check the service logs and Windows Event Viewer for startup errors." -ForegroundColor Yellow
        exit 1
    }
}

Write-Host "Service configuration:"
sc.exe qc $ServiceName
sc.exe query $ServiceName

Write-Host ""
Write-Host "Install completed."
Write-Host "Service account: $ServiceUser"
Write-Host "Startup mode: Automatic"
if (-not $StartAfterInstall) {
    Write-Host "Start service manually: Start-Service $ServiceName"
}
Write-Host "If VM configs are stored in SQLite, pass VM roots explicitly, for example:"
Write-Host '  .\install-service.ps1 -ExePath "C:\Program Files\Seebot Worker Agent\service\Seebot.WorkerAgent.Service.exe" -AdditionalGrantPaths @("E:\VMS", "D:\VMS")'
