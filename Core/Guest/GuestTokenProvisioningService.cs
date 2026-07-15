using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seebot.WorkerAgent.Core.Configuration;
using Seebot.WorkerAgent.Core.Domain;
using Seebot.WorkerAgent.Core.Scheduler;
using Seebot.WorkerAgent.Core.Vmware;

namespace Seebot.WorkerAgent.Core.Guest;

public sealed class GuestTokenProvisioningService : IGuestTokenProvisioningService
{
    private const string TokenKey = "rpa.token";
    private const string ApplicationPropertiesFileName = "application.properties";
    private static readonly TimeSpan DefaultGuestOperationsTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultGuestOperationsPollInterval = TimeSpan.FromSeconds(5);

    private readonly IVmrunService _vmrunService;
    private readonly ISchedulerTokenProvider _tokenProvider;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _guestOperationsTimeout;
    private readonly TimeSpan _guestOperationsPollInterval;
    private readonly ILogger<GuestTokenProvisioningService> _logger;

    public GuestTokenProvisioningService(
        IVmrunService vmrunService,
        ISchedulerTokenProvider tokenProvider,
        TimeProvider? timeProvider = null,
        TimeSpan? guestOperationsTimeout = null,
        TimeSpan? guestOperationsPollInterval = null,
        ILogger<GuestTokenProvisioningService>? logger = null)
    {
        _vmrunService = vmrunService;
        _tokenProvider = tokenProvider;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _guestOperationsTimeout = guestOperationsTimeout ?? DefaultGuestOperationsTimeout;
        _guestOperationsPollInterval = guestOperationsPollInterval ?? DefaultGuestOperationsPollInterval;
        _logger = logger ?? NullLogger<GuestTokenProvisioningService>.Instance;
    }

    public async Task<GuestTokenProvisioningResult> ProvisionAsync(
        VirtualMachineOptions vm,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(vm.GuestUser))
        {
            return Fail(ErrorCodes.ConfigUpdateFailed, $"VM '{vm.Name}' has no GuestUser configured.");
        }

        if (!await WaitForGuestOperationsReadyAsync(vm, cancellationToken).ConfigureAwait(false))
        {
            return Fail(
                ErrorCodes.ConfigUpdateFailed,
                $"Timed out waiting for VMware Tools guest operations before writing {TokenKey}.");
        }

        string token;
        try
        {
            token = await _tokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(ErrorCodes.SchedulerUnavailable, $"Failed to get scheduler access token: {ex.Message}");
        }

        var propertiesPath = GetApplicationPropertiesPath(vm);
        var command = BuildUpdateTokenCommand(propertiesPath, token);
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));

        try
        {
            _logger.LogInformation(
                "Writing scheduler token to guest application properties. VmName={VmName}, Path={Path}, Key={Key}",
                vm.Name,
                propertiesPath,
                TokenKey);
            await _vmrunService.RunProgramInGuestAsync(
                vm.VmxPath,
                vm.GuestUser,
                vm.GuestPasswordSecret,
                @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
                ["-NoProfile", "-NonInteractive", "-EncodedCommand", encoded],
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(ErrorCodes.ConfigUpdateFailed, $"Failed to write {TokenKey}: {ex.Message}");
        }

        _logger.LogInformation(
            "Guest scheduler token provisioned successfully. VmName={VmName}, Path={Path}, Key={Key}",
            vm.Name,
            propertiesPath,
            TokenKey);
        return new GuestTokenProvisioningResult { Success = true };
    }

    private async Task<bool> WaitForGuestOperationsReadyAsync(
        VirtualMachineOptions vm,
        CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow() + _guestOperationsTimeout;
        while (_timeProvider.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _vmrunService
                    .ListProcessesInGuestAsync(vm.VmxPath, vm.GuestUser, vm.GuestPasswordSecret, cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "VMware Tools guest operations not ready before token provisioning. VmName={VmName}", vm.Name);
            }

            await Task.Delay(_guestOperationsPollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static string GetApplicationPropertiesPath(VirtualMachineOptions vm)
    {
        var guestWorkPath = string.IsNullOrWhiteSpace(vm.GuestWorkPath)
            ? @"D:\seebon\rpa"
            : vm.GuestWorkPath.TrimEnd('\\', '/');
        return guestWorkPath + @"\" + ApplicationPropertiesFileName;
    }

    private static string BuildUpdateTokenCommand(string propertiesPath, string token)
    {
        var escapedPath = EscapePowerShellSingleQuotedString(propertiesPath);
        var escapedToken = EscapePowerShellSingleQuotedString(token.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal));
        return $$"""
$ErrorActionPreference = 'Stop'
$path = '{{escapedPath}}'
$key = '{{TokenKey}}'
$value = '{{escapedToken}}'
$expected = "$key=$value"
$directory = Split-Path -Parent $path
if (-not [string]::IsNullOrWhiteSpace($directory) -and -not (Test-Path -LiteralPath $directory)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}
$lines = if (Test-Path -LiteralPath $path) { @(Get-Content -LiteralPath $path) } else { @() }
$found = $false
$updated = foreach ($line in $lines) {
    if ($line -match '^\s*rpa\.token\s*=') {
        $found = $true
        $expected
    } else {
        $line
    }
}
if (-not $found) {
    $updated += $expected
}
Set-Content -LiteralPath $path -Value $updated -Encoding UTF8 -Force
$actual = @(Get-Content -LiteralPath $path) |
    Where-Object { $_ -match '^\s*rpa\.token\s*=' } |
    Select-Object -Last 1
if ($actual -ne $expected) {
    Write-Error "Token verification failed for key $key."
    exit 10
}
""";
    }

    private static string EscapePowerShellSingleQuotedString(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private static GuestTokenProvisioningResult Fail(string errorCode, string errorMessage)
    {
        return new GuestTokenProvisioningResult
        {
            Success = false,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage
        };
    }
}
