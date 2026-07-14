$ServiceName = "SeebotWorkerAgent"

Write-Host "开始卸载 Windows 服务：$ServiceName"

# 检查管理员权限
$IsAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()
).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $IsAdmin) {
    Write-Host "错误：请使用管理员权限运行 PowerShell。" -ForegroundColor Red
    exit 1
}

# 检查服务是否存在
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

if (-not $existing) {
    Write-Host "服务不存在：$ServiceName"
    exit 0
}

# 如果服务正在运行，先停止
if ($existing.Status -ne "Stopped") {
    Write-Host "正在停止服务：$ServiceName"

    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue

    $timeout = 30
    while ($timeout -gt 0) {
        $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($svc.Status -eq "Stopped") {
            break
        }

        Start-Sleep -Seconds 1
        $timeout--
    }
}

# 删除服务
Write-Host "正在删除服务：$ServiceName"

sc.exe delete $ServiceName

if ($LASTEXITCODE -ne 0) {
    Write-Host "错误：服务删除失败。" -ForegroundColor Red
    exit 1
}

Start-Sleep -Seconds 2

# 再次确认
$check = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

if ($check) {
    Write-Host "服务已提交删除，但系统仍能查询到。可能处于 pending delete 状态，关闭 services.msc 或重启机器后会消失。" -ForegroundColor Yellow
} else {
    Write-Host "服务已删除：$ServiceName"
}