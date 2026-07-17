using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seebot.WorkerAgent.Core.Configuration;
using Seebot.WorkerAgent.Core.Vmware;

namespace Seebot.WorkerAgent.Core.Health;

public sealed class VmHealthCheckService : IVmHealthCheckService
{
    private readonly IVmrunService _vmrunService;
    private readonly IVmOperationLock _vmOperationLock;
    private readonly WorkerAgentOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<VmHealthCheckService> _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _poweredOffSince = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastStartAttempt = new(StringComparer.OrdinalIgnoreCase);

    public VmHealthCheckService(
        IVmrunService vmrunService,
        IVmOperationLock vmOperationLock,
        WorkerAgentOptions options,
        TimeProvider? timeProvider = null,
        ILogger<VmHealthCheckService>? logger = null)
    {
        _vmrunService = vmrunService;
        _vmOperationLock = vmOperationLock;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<VmHealthCheckService>.Instance;
    }

    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        var thresholdMinutes = _options.Agent.VmAutoStartAfterMinutes;
        if (thresholdMinutes <= 0)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var threshold = TimeSpan.FromMinutes(thresholdMinutes);
        var retryInterval = TimeSpan.FromSeconds(
            _options.Agent.VmAutoStartRetryIntervalSeconds > 0
                ? _options.Agent.VmAutoStartRetryIntervalSeconds
                : 60);

        foreach (var vm in _options.VirtualMachines.Where(vm => vm.Enabled))
        {
            bool isRunning;
            try
            {
                isRunning = await _vmrunService.IsVmRunningAsync(vm.VmxPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "VM health check failed. VmName={VmName}", vm.Name);
                continue;
            }

            if (isRunning)
            {
                _poweredOffSince.TryRemove(vm.Name, out _);
                _lastStartAttempt.TryRemove(vm.Name, out _);
                continue;
            }

            var poweredOffSince = _poweredOffSince.GetOrAdd(vm.Name, now);
            var poweredOffDuration = now - poweredOffSince;
            if (poweredOffDuration < threshold)
            {
                _logger.LogDebug(
                    "VM is powered off but has not reached auto-start threshold. VmName={VmName}, PoweredOffMinutes={PoweredOffMinutes:F1}, ThresholdMinutes={ThresholdMinutes}",
                    vm.Name,
                    poweredOffDuration.TotalMinutes,
                    thresholdMinutes);
                continue;
            }

            if (_lastStartAttempt.TryGetValue(vm.Name, out var lastAttempt)
                && now - lastAttempt < retryInterval)
            {
                continue;
            }

            await using var vmLock = await _vmOperationLock.AcquireAsync(vm.VmxPath, cancellationToken).ConfigureAwait(false);

            try
            {
                if (await _vmrunService.IsVmRunningAsync(vm.VmxPath, cancellationToken).ConfigureAwait(false))
                {
                    _poweredOffSince.TryRemove(vm.Name, out _);
                    _lastStartAttempt.TryRemove(vm.Name, out _);
                    continue;
                }

                _lastStartAttempt[vm.Name] = _timeProvider.GetUtcNow();
                _logger.LogWarning(
                    "VM has been powered off beyond threshold; starting it automatically. VmName={VmName}, PoweredOffSince={PoweredOffSince}, ThresholdMinutes={ThresholdMinutes}",
                    vm.Name,
                    poweredOffSince,
                    thresholdMinutes);

                await _vmrunService.StartVmAsync(
                    vm.VmxPath,
                    _options.Vmrun.DefaultStartNoGui,
                    cancellationToken).ConfigureAwait(false);

                _poweredOffSince.TryRemove(vm.Name, out _);
                _logger.LogInformation("VM auto-start command completed. VmName={VmName}", vm.Name);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(exception, "VM auto-start failed. VmName={VmName}", vm.Name);
            }
        }
    }
}
