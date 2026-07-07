using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seebot.WorkerAgent.Core.Configuration;
using Seebot.WorkerAgent.Core.Domain;

namespace Seebot.WorkerAgent.Core.Storage;

public sealed class LocalStoreInitializerService : IHostedService
{
    private readonly ILocalStore _localStore;
    private readonly WorkerAgentOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LocalStoreInitializerService> _logger;

    public LocalStoreInitializerService(
        ILocalStore localStore,
        WorkerAgentOptions options,
        ILogger<LocalStoreInitializerService> logger,
        TimeProvider? timeProvider = null)
    {
        _localStore = localStore;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _localStore.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var now = _timeProvider.GetUtcNow();
        var initialStates = _options.VirtualMachines
            .Where(vm => vm.Enabled)
            .Select(vm => new VmCurrentState
            {
                VmName = vm.Name,
                WorkerId = vm.WorkerId,
                VmStatus = AgentVmStatus.UNKNOWN,
                UpdatedAt = now
            })
            .ToList();
        await _localStore.SeedVmStatesAsync(_options.Agent.HostId, initialStates, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Local SQLite store initialized.");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
