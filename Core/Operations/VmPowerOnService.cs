using Microsoft.Extensions.Logging;
using Seebot.WorkerAgent.Core.Configuration;
using Seebot.WorkerAgent.Core.Coordination;
using Seebot.WorkerAgent.Core.Guest;
using Seebot.WorkerAgent.Core.Storage;
using Seebot.WorkerAgent.Core.Vmware;

namespace Seebot.WorkerAgent.Core.Operations;

public enum VmPowerRecoveryAction { AlreadyResponsive, Started, Restarted, Failed }

public sealed class VmPowerRecoveryResult
{
    public bool Success => Action is not VmPowerRecoveryAction.Failed;
    public VmPowerRecoveryAction Action { get; init; }
    public RunnerStatusResponse? RunnerStatus { get; init; }
    public string? ErrorCode { get; init; }
    public string Message { get; init; } = "";
}

/// <summary>
/// Shared, lock-free VM power recovery primitive. The outer caller owns the VM lock and operation lease.
/// </summary>
public interface IVmPowerRecoveryService
{
    Task<VmPowerRecoveryResult> EnsureOperationalAsync(VirtualMachineOptions vm, IVmOperationLease? operation,
        CancellationToken cancellationToken);
}

public sealed class VmPowerRecoveryService : IVmPowerRecoveryService
{
    private const int RunningVmRunnerProbeAttempts = 3;

    private readonly IVmrunService _vmrun; private readonly IGuestWorkerClient _guest;
    private readonly WorkerAgentOptions _options; private readonly TimeProvider _time;
    private readonly TimeSpan _poll; private readonly ILogger<VmPowerRecoveryService> _logger;

    public VmPowerRecoveryService(IVmrunService vmrun, IGuestWorkerClient guest, WorkerAgentOptions options,
        ILogger<VmPowerRecoveryService> logger, TimeProvider? timeProvider = null, TimeSpan? pollInterval = null)
    { _vmrun = vmrun; _guest = guest; _options = options; _logger = logger; _time = timeProvider ?? TimeProvider.System; _poll = pollInterval ?? TimeSpan.FromSeconds(2); }

    public async Task<VmPowerRecoveryResult> EnsureOperationalAsync(VirtualMachineOptions vm,
        IVmOperationLease? operation, CancellationToken cancellationToken)
    {
        if (!await _vmrun.IsVmRunningAsync(vm.VmxPath, cancellationToken).ConfigureAwait(false))
        {
            if (operation is not null) await operation.SetStatusAsync(VmOperationStatus.StartingVm, cancellationToken).ConfigureAwait(false);
            var error = await StartAndConfirmAsync(vm, cancellationToken).ConfigureAwait(false);
            if (error is not null) return Fail("VM_START_FAILED", error);
            if (operation is not null) await operation.SetStatusAsync(VmOperationStatus.WaitingRunner, cancellationToken).ConfigureAwait(false);
            var runner = await WaitForRunnerResponseAsync(vm, cancellationToken).ConfigureAwait(false);
            return runner is null
                ? Fail("VM_RUNNER_READY_TIMEOUT", $"VM '{vm.Name}' entered the VMware running list, but Runner did not respond after startup.")
                : Ok(VmPowerRecoveryAction.Started, $"VM '{vm.Name}' started and Runner responded successfully.", runner);
        }

        var currentRunner = await ProbeRunningVmRunnerAsync(vm, cancellationToken).ConfigureAwait(false);
        if (currentRunner is not null)
            return new VmPowerRecoveryResult { Action = VmPowerRecoveryAction.AlreadyResponsive, RunnerStatus = currentRunner, Message = $"VM '{vm.Name}' is running and Runner responded." };

        if (operation is not null && !await operation.TryReservePowerCycleAsync(cancellationToken).ConfigureAwait(false))
            return Fail("RECOVERY_BUDGET_EXHAUSTED", "VM restart was suppressed by the persistent recovery budget or cooldown.");

        if (operation is not null) await operation.SetStatusAsync(VmOperationStatus.StoppingVm, cancellationToken).ConfigureAwait(false);
        var stopError = await HardStopAndConfirmAsync(vm, cancellationToken).ConfigureAwait(false);
        if (stopError is not null)
        {
            if (operation is not null) await operation.RecordRecoveryResultAsync(false, cancellationToken).ConfigureAwait(false);
            return Fail("VM_STOP_FAILED", stopError);
        }

        var stabilization = Math.Max(0, _options.Agent.VmPostStopStabilizationSeconds);
        if (stabilization > 0) await Task.Delay(TimeSpan.FromSeconds(stabilization), _time, cancellationToken).ConfigureAwait(false);
        if (operation is not null) await operation.SetStatusAsync(VmOperationStatus.StartingVm, cancellationToken).ConfigureAwait(false);
        var startError = await StartAndConfirmAsync(vm, cancellationToken).ConfigureAwait(false);
        if (startError is not null)
        {
            if (operation is not null) await operation.RecordRecoveryResultAsync(false, cancellationToken).ConfigureAwait(false);
            return Fail("VM_START_FAILED", startError);
        }

        if (operation is not null) await operation.SetStatusAsync(VmOperationStatus.WaitingRunner, cancellationToken).ConfigureAwait(false);
        var recoveredRunner = await WaitForRunnerResponseAsync(vm, cancellationToken).ConfigureAwait(false);
        if (recoveredRunner is null)
        {
            if (operation is not null) await operation.RecordRecoveryResultAsync(false, cancellationToken).ConfigureAwait(false);
            return Fail("VM_RUNNER_READY_TIMEOUT", $"VM '{vm.Name}' restarted at the VMware power level, but Runner never recovered.");
        }

        if (operation is not null) await operation.RecordRecoveryResultAsync(true, cancellationToken).ConfigureAwait(false);
        return Ok(VmPowerRecoveryAction.Restarted, $"VM '{vm.Name}' restarted and Runner recovered successfully.", recoveredRunner);
    }

    private async Task<RunnerStatusResponse?> ProbeRunningVmRunnerAsync(
        VirtualMachineOptions vm,
        CancellationToken cancellationToken)
    {
        var timeoutSeconds = Math.Max(1, _options.Agent.ManualPowerOnRunnerProbeTimeoutSeconds);
        for (var attempt = 1; attempt <= RunningVmRunnerProbeAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                probe.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                var status = await _guest.GetRunnerStatusAsync(vm, probe.Token).ConfigureAwait(false);
                _logger.LogInformation(
                    "运行中的 VM Runner 响应正常。VmName={VmName}, Attempt={Attempt}, MaxAttempts={MaxAttempts}",
                    vm.Name, attempt, RunningVmRunnerProbeAttempts);
                return status;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "运行中的 VM Runner 请求超时。VmName={VmName}, Attempt={Attempt}, MaxAttempts={MaxAttempts}, TimeoutSeconds={TimeoutSeconds}",
                    vm.Name, attempt, RunningVmRunnerProbeAttempts, timeoutSeconds);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(
                    exception,
                    "运行中的 VM Runner 请求失败。VmName={VmName}, Attempt={Attempt}, MaxAttempts={MaxAttempts}",
                    vm.Name, attempt, RunningVmRunnerProbeAttempts);
            }

            if (attempt < RunningVmRunnerProbeAttempts && _poll > TimeSpan.Zero)
                await Task.Delay(_poll, _time, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogWarning(
            "运行中的 VM Runner 连续三次无响应，准备执行 hard 关机并重新启动。VmName={VmName}",
            vm.Name);
        return null;
    }

    private async Task<RunnerStatusResponse?> WaitForRunnerResponseAsync(VirtualMachineOptions vm, CancellationToken cancellationToken)
    {
        var deadline = _time.GetUtcNow().AddSeconds(Math.Max(1, _options.Agent.ManualPowerOnRunnerReadyTimeoutSeconds));
        while (_time.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var remaining = deadline - _time.GetUtcNow();
            var attemptTimeout = TimeSpan.FromSeconds(Math.Max(1, _options.Agent.ManualPowerOnRunnerProbeTimeoutSeconds));
            attempt.CancelAfter(remaining < attemptTimeout ? remaining : attemptTimeout);
            try
            {
                return await _guest.GetRunnerStatusAsync(vm, attempt.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogDebug(exception, "Runner is not ready after VM power-on. VmName={VmName}", vm.Name);
            }

            remaining = deadline - _time.GetUtcNow();
            if (remaining <= TimeSpan.Zero) break;
            var delay = remaining < TimeSpan.FromSeconds(3) ? remaining : TimeSpan.FromSeconds(3);
            await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    private async Task<string?> HardStopAndConfirmAsync(VirtualMachineOptions vm, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try { await _vmrun.StopVmAsync(vm.VmxPath, VmStopMode.Hard, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { last = ex; _logger.LogWarning(ex, "Hard stop failed. VmName={VmName}, Attempt={Attempt}", vm.Name, attempt); }
            if (await WaitForPowerStateAsync(vm.VmxPath, false, ct).ConfigureAwait(false)) return null;
        }
        return last is null ? "VM remained running after two hard power-off attempts." : $"VM remained running. Last vmrun error: {last.Message}";
    }

    private async Task<string?> StartAndConfirmAsync(VirtualMachineOptions vm, CancellationToken ct)
    {
        var max = Math.Max(1, _options.Agent.ManualPowerOnStartMaxAttempts); Exception? last = null;
        for (var attempt = 1; attempt <= max; attempt++)
        {
            try { await _vmrun.StartVmAsync(vm.VmxPath, _options.Vmrun.DefaultStartNoGui, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { last = ex; _logger.LogWarning(ex, "VM start failed. VmName={VmName}, Attempt={Attempt}, MaxAttempts={MaxAttempts}", vm.Name, attempt, max); }
            if (await WaitForPowerStateAsync(vm.VmxPath, true, ct).ConfigureAwait(false)) return null;
            if (attempt < max) await Task.Delay(_poll, _time, ct).ConfigureAwait(false);
        }
        return last is null ? $"VM did not reach running state after {max} attempt(s)." : $"VM failed to start after {max} attempt(s). Last vmrun error: {last.Message}";
    }

    private async Task<bool> WaitForPowerStateAsync(string vmxPath, bool running, CancellationToken ct)
    {
        var deadline = _time.GetUtcNow().AddSeconds(Math.Max(1, _options.Agent.VmPowerCycleStopTimeoutSeconds));
        while (true)
        {
            if (await _vmrun.IsVmRunningAsync(vmxPath, ct).ConfigureAwait(false) == running) return true;
            if (_time.GetUtcNow() >= deadline) return false;
            await Task.Delay(_poll, _time, ct).ConfigureAwait(false);
        }
    }

    private static VmPowerRecoveryResult Ok(VmPowerRecoveryAction action, string message, RunnerStatusResponse? runner = null) => new() { Action = action, Message = message, RunnerStatus = runner };
    private static VmPowerRecoveryResult Fail(string code, string message) => new() { Action = VmPowerRecoveryAction.Failed, ErrorCode = code, Message = message };
}

public sealed class VmPowerOnResult { public bool Success { get; set; } public string Action { get; set; } = ""; public string Message { get; set; } = ""; public string? ErrorCode { get; set; } }
public interface IVmPowerOnService { Task<VmPowerOnResult> PowerOnAsync(string vmName, CancellationToken cancellationToken); }

/// <summary>Manual API orchestration layer. Concurrency is controlled by the operator.</summary>
public sealed class VmPowerOnService : IVmPowerOnService
{
    private readonly IVirtualMachineRegistry _registry;
    private readonly IVmPowerRecoveryService _recovery;

    public VmPowerOnService(IVirtualMachineRegistry registry, IVmPowerRecoveryService recovery)
    {
        _registry = registry;
        _recovery = recovery;
    }

    public async Task<VmPowerOnResult> PowerOnAsync(string vmName, CancellationToken ct)
    {
        var vm = await _registry.GetByNameAsync(vmName, ct).ConfigureAwait(false);
        if (vm is null) return Map(new VmPowerRecoveryResult { Action = VmPowerRecoveryAction.Failed, ErrorCode = "VM_NOT_FOUND", Message = $"VM '{vmName}' was not found." });
        try
        {
            return Map(await _recovery.EnsureOperationalAsync(vm, null, ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Map(new VmPowerRecoveryResult { Action = VmPowerRecoveryAction.Failed, ErrorCode = "VM_POWER_ON_FAILED", Message = ex.Message });
        }
    }

    private static VmPowerOnResult Map(VmPowerRecoveryResult result) => new()
    {
        Success = result.Success,
        Action = result.Action switch { VmPowerRecoveryAction.AlreadyResponsive => "skipped", VmPowerRecoveryAction.Started => "started", VmPowerRecoveryAction.Restarted => "restarted", _ => "failed" },
        ErrorCode = result.ErrorCode,
        Message = result.Message
    };
}
