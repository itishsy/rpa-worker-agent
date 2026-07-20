using Microsoft.Extensions.Logging;
using Seebot.WorkerAgent.Core.Configuration;

namespace Seebot.WorkerAgent.Core.Coordination;

public sealed class VmOperationCoordinator : IVmOperationCoordinator
{
    private readonly IVmOperationStore _store;
    private readonly AgentOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<VmOperationCoordinator> _logger;
    private readonly string _owner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public VmOperationCoordinator(IVmOperationStore store, WorkerAgentOptions options,
        ILogger<VmOperationCoordinator> logger, TimeProvider? timeProvider = null)
    {
        _store = store;
        _options = options.Agent;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IVmOperationLease?> TryAcquireAsync(string hostId, string vmName,
        VmOperationType type, CancellationToken cancellationToken)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(60, _options.VmOperationLeaseSeconds));
        var id = Guid.NewGuid().ToString("N");
        var handle = await _store.TryAcquireAsync(hostId, vmName, id, type, _owner,
            _timeProvider.GetUtcNow(), duration, cancellationToken).ConfigureAwait(false);
        if (handle is null) return null;
        _logger.LogInformation("VM operation lease acquired. VmName={VmName}, OperationId={OperationId}, Type={Type}, FencingToken={FencingToken}",
            vmName, id, type, handle.FencingToken);
        return new Lease(_store, handle, _owner, _options, _timeProvider, _logger);
    }

    private sealed class Lease : IVmOperationLease
    {
        private readonly IVmOperationStore _store; private readonly string _owner;
        private readonly AgentOptions _options; private readonly TimeProvider _time;
        private readonly ILogger _logger; private readonly CancellationTokenSource _stop = new();
        private readonly Task _heartbeat; private int _terminal;
        public VmOperationHandle Handle { get; }

        public Lease(IVmOperationStore store, VmOperationHandle handle, string owner, AgentOptions options,
            TimeProvider time, ILogger logger)
        {
            _store = store; Handle = handle; _owner = owner; _options = options; _time = time; _logger = logger;
            _heartbeat = HeartbeatAsync();
        }

        private TimeSpan Duration => TimeSpan.FromSeconds(Math.Max(60, _options.VmOperationLeaseSeconds));
        private async Task HeartbeatAsync()
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(10, Math.Min(_options.VmOperationHeartbeatSeconds, Duration.TotalSeconds / 3))));
                while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
                {
                    if (!await _store.RenewAsync(Handle, _owner, _time.GetUtcNow(), Duration, _stop.Token).ConfigureAwait(false))
                    {
                        _logger.LogError("VM operation lease was lost. VmName={VmName}, OperationId={OperationId}, FencingToken={FencingToken}", Handle.VmName, Handle.OperationId, Handle.FencingToken);
                        _stop.Cancel();
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.LogError(ex, "VM operation heartbeat failed. OperationId={OperationId}", Handle.OperationId); _stop.Cancel(); }
        }

        public Task SetStatusAsync(VmOperationStatus status, CancellationToken ct = default) => UpdateRequiredAsync(status, null, null, ct);
        public Task CompleteAsync(CancellationToken ct = default) { Interlocked.Exchange(ref _terminal, 1); return UpdateRequiredAsync(VmOperationStatus.Success, null, null, ct); }
        public Task FailAsync(string code, string message, CancellationToken ct = default) { Interlocked.Exchange(ref _terminal, 1); return UpdateRequiredAsync(VmOperationStatus.Failed, code, message, ct); }
        private async Task UpdateRequiredAsync(VmOperationStatus status, string? code, string? message, CancellationToken ct)
        {
            if (_stop.IsCancellationRequested && status is not VmOperationStatus.Failed and not VmOperationStatus.Success)
                throw new InvalidOperationException($"VM operation lease {Handle.OperationId} is no longer valid.");
            if (!await _store.UpdateAsync(Handle, status, code, message, _time.GetUtcNow(), Duration, ct).ConfigureAwait(false))
                throw new InvalidOperationException($"VM operation fencing token {Handle.FencingToken} is stale.");
        }
        public Task<bool> TryReservePowerCycleAsync(CancellationToken ct = default) => _store.TryReservePowerCycleAsync(
            Handle.HostId, Handle.VmName, _time.GetUtcNow(), TimeSpan.FromMinutes(Math.Max(1, _options.VmRecoveryWindowMinutes)),
            Math.Max(1, _options.VmMaxPowerCyclesPerWindow), TimeSpan.FromMinutes(Math.Max(0, _options.VmRecoveryCooldownMinutes)), ct);
        public Task RecordRecoveryResultAsync(bool success, CancellationToken ct = default) => _store.RecordRecoveryResultAsync(
            Handle.HostId, Handle.VmName, success, _time.GetUtcNow(), Math.Max(1, _options.VmMaxConsecutiveRecoveryFailures),
            TimeSpan.FromMinutes(Math.Max(1, _options.VmRecoveryFailureCooldownMinutes)), ct);
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.CompareExchange(ref _terminal, 1, 0) == 0)
            {
                try { await UpdateRequiredAsync(VmOperationStatus.Failed, "OPERATION_ABANDONED", "Operation ended without a terminal result.", CancellationToken.None).ConfigureAwait(false); } catch { }
            }
            _stop.Cancel(); try { await _heartbeat.ConfigureAwait(false); } catch { } _stop.Dispose();
        }
    }
}
