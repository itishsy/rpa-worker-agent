using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seebot.WorkerAgent.Core.Configuration;
using Seebot.WorkerAgent.Core.Vmware;

namespace Seebot.WorkerAgent.Core.Health;

/// <summary>
/// Performs observation-only VM health checks. Power recovery belongs to an explicitly
/// requested VM operation and must never be initiated by this service.
/// </summary>
public sealed class VmHealthCheckService : IVmHealthCheckService
{
    private readonly IVmrunService _vmrunService;
    private readonly IVmDiskCleanupService _diskCleanupService;
    private readonly WorkerAgentOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<VmHealthCheckService> _logger;

    public VmHealthCheckService(
        IVmrunService vmrunService,
        IVmDiskCleanupService diskCleanupService,
        WorkerAgentOptions options,
        TimeProvider? timeProvider = null,
        ILogger<VmHealthCheckService>? logger = null)
    {
        _vmrunService = vmrunService;
        _diskCleanupService = diskCleanupService;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<VmHealthCheckService>.Instance;
    }

    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        foreach (var vm in _options.VirtualMachines.Where(vm => vm.Enabled))
        {
            bool isRunning;
            try
            {
                isRunning = await _vmrunService.IsVmRunningAsync(vm.VmxPath, cancellationToken).ConfigureAwait(false);
                _logger.LogDebug("VM health observation completed. VmName={VmName}, IsRunning={IsRunning}", vm.Name, isRunning);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "VM health observation failed. VmName={VmName}", vm.Name);
                continue;
            }

            try
            {
                await _diskCleanupService.CleanupIfDueAsync(
                    new VmDiskCleanupContext(vm, isRunning, _timeProvider.GetUtcNow()),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A cleanup implementation is maintenance-only and must not stop the health/scheduler loop.
                _logger.LogError(exception, "VM disk cleanup hook failed. VmName={VmName}", vm.Name);
            }
        }
    }
}
