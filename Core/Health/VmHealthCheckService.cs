using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seebot.WorkerAgent.Core.Configuration;
using Seebot.WorkerAgent.Core.Domain;
using Seebot.WorkerAgent.Core.Operations;
using Seebot.WorkerAgent.Core.Storage;
using Seebot.WorkerAgent.Core.Vmware;

namespace Seebot.WorkerAgent.Core.Health;

/// <summary>
/// Ensures every enabled VM is operational and invokes the disk-cleanup extension point.
/// </summary>
public sealed class VmHealthCheckService : IVmHealthCheckService
{
    private readonly IVmPowerOnService _powerOnService;
    private readonly IVmrunService _vmrunService;
    private readonly IVmDiskCleanupService _diskCleanupService;
    private readonly ILocalStore _localStore;
    private readonly WorkerAgentOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<VmHealthCheckService> _logger;

    public VmHealthCheckService(
        IVmPowerOnService powerOnService,
        IVmrunService vmrunService,
        IVmDiskCleanupService diskCleanupService,
        ILocalStore localStore,
        WorkerAgentOptions options,
        TimeProvider? timeProvider = null,
        ILogger<VmHealthCheckService>? logger = null)
    {
        _powerOnService = powerOnService;
        _vmrunService = vmrunService;
        _diskCleanupService = diskCleanupService;
        _localStore = localStore;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<VmHealthCheckService>.Instance;
    }

    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, VmCurrentState> stateByVmName;
        try
        {
            var states = await _localStore
                .GetVmStatesAsync(_options.Agent.HostId, cancellationToken)
                .ConfigureAwait(false);
            stateByVmName = states.ToDictionary(state => state.VmName, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Quarantine is a safety boundary. If its state cannot be read, do not risk
            // powering on, restarting or cleaning a quarantined VM.
            _logger.LogError(exception, "无法读取 VM 隔离状态，本次健康检查已取消。");
            return;
        }

        foreach (var vm in _options.VirtualMachines.Where(vm => vm.Enabled))
        {
            if (stateByVmName.TryGetValue(vm.Name, out var state) && state.IsQuarantined)
            {
                _logger.LogInformation(
                    "VM 处于隔离状态，跳过全部健康检查操作。VmName={VmName}",
                    vm.Name);
                continue;
            }

            try
            {
                var result = await _powerOnService.PowerOnAsync(vm.Name, cancellationToken).ConfigureAwait(false);
                if (result.Success)
                {
                    _logger.LogInformation(
                        "VM 健康恢复检查完成。VmName={VmName}, Action={Action}, Message={Message}",
                        vm.Name, result.Action, result.Message);
                }
                else
                {
                    _logger.LogWarning(
                        "VM 健康恢复失败。VmName={VmName}, ErrorCode={ErrorCode}, Message={Message}",
                        vm.Name, result.ErrorCode, result.Message);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(exception, "VM 健康恢复发生异常。VmName={VmName}", vm.Name);
            }

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
