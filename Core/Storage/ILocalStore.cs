using Seebot.WorkerAgent.Core.Domain;

namespace Seebot.WorkerAgent.Core.Storage;

public interface ILocalStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task UpsertVmStateAsync(string hostId, VmCurrentState state, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VmCurrentState>> GetVmStatesAsync(string hostId, CancellationToken cancellationToken = default);

    Task CreateSwitchTransactionAsync(SwitchTransaction transaction, CancellationToken cancellationToken = default);

    Task UpdateSwitchTransactionAsync(
        string txId,
        SwitchTransactionStatus status,
        string? step,
        string? errorCode,
        string? errorMessage,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SwitchTransaction>> GetIncompleteSwitchTransactionsAsync(string hostId, CancellationToken cancellationToken = default);

    Task<SwitchTransaction?> GetSwitchTransactionAsync(string txId, CancellationToken cancellationToken = default);
}
