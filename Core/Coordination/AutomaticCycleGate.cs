using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Seebot.WorkerAgent.Core.Coordination;

public interface IAutomaticCycleGate
{
    Task<IAsyncDisposable> EnterAutomaticCycleAsync(CancellationToken cancellationToken);

    Task<IAsyncDisposable> PauseForMaintenanceAsync(
        string operationName,
        string vmName,
        CancellationToken cancellationToken);
}

/// <summary>
/// Serializes the complete automatic health/scheduler cycle with manual maintenance operations.
/// A maintenance caller waits for the active automatic cycle to finish and prevents new cycles
/// from starting until its lease is disposed.
/// </summary>
public sealed class AutomaticCycleGate : IAutomaticCycleGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<AutomaticCycleGate> _logger;

    public AutomaticCycleGate(ILogger<AutomaticCycleGate>? logger = null)
    {
        _logger = logger ?? NullLogger<AutomaticCycleGate>.Instance;
    }

    public Task<IAsyncDisposable> EnterAutomaticCycleAsync(CancellationToken cancellationToken) =>
        AcquireAsync("AutomaticCycle", "", cancellationToken);

    public Task<IAsyncDisposable> PauseForMaintenanceAsync(
        string operationName,
        string vmName,
        CancellationToken cancellationToken) =>
        AcquireAsync(operationName, vmName, cancellationToken);

    private async Task<IAsyncDisposable> AcquireAsync(
        string operationName,
        string vmName,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        _logger.LogInformation(
            "等待获取自动周期执行权。Operation={Operation}, VmName={VmName}",
            operationName,
            vmName);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "已获取自动周期执行权。Operation={Operation}, VmName={VmName}, WaitMs={WaitMs}",
            operationName,
            vmName,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return new Lease(_gate, _logger, operationName, vmName);
    }

    private sealed class Lease : IAsyncDisposable
    {
        private readonly SemaphoreSlim _gate;
        private readonly ILogger _logger;
        private readonly string _operationName;
        private readonly string _vmName;
        private int _released;

        public Lease(SemaphoreSlim gate, ILogger logger, string operationName, string vmName)
        {
            _gate = gate;
            _logger = logger;
            _operationName = operationName;
            _vmName = vmName;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _gate.Release();
                _logger.LogInformation(
                    "已释放自动周期执行权。Operation={Operation}, VmName={VmName}",
                    _operationName,
                    _vmName);
            }

            return ValueTask.CompletedTask;
        }
    }
}
