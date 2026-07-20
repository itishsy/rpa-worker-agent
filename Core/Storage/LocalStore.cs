using Microsoft.Data.Sqlite;
using Seebot.WorkerAgent.Core.Domain;
using Seebot.WorkerAgent.Core.Coordination;

namespace Seebot.WorkerAgent.Core.Storage;

public sealed class LocalStore : ILocalStore, IVmOperationStore
{
    private readonly string _connectionString;

    public LocalStore(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await ExecuteNonQueryAsync(connection, """
CREATE TABLE IF NOT EXISTS local_vm_state (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    host_id TEXT NOT NULL,
    vm_name TEXT NOT NULL,
    worker_id TEXT NOT NULL,
    current_profile_id TEXT,
    current_snapshot_name TEXT,
    runner_status_code INTEGER,
    runner_status_name TEXT,
    agent_vm_status TEXT NOT NULL,
    last_idle_at TEXT,
    last_switch_at TEXT,
    is_quarantined INTEGER NOT NULL DEFAULT 0,
    error_code TEXT,
    error_message TEXT,
    updated_at TEXT NOT NULL,
    UNIQUE(host_id, vm_name)
);
""", cancellationToken);

        await ExecuteNonQueryAsync(connection, """
CREATE TABLE IF NOT EXISTS local_switch_transaction (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    tx_id TEXT NOT NULL,
    host_id TEXT NOT NULL,
    vm_name TEXT NOT NULL,
    worker_id TEXT NOT NULL,
    from_profile_id TEXT,
    from_snapshot_name TEXT,
    to_profile_id TEXT NOT NULL,
    to_snapshot_name TEXT NOT NULL,
    first_task_id INTEGER,
    trigger_reason TEXT,
    status TEXT NOT NULL,
    step TEXT,
    error_code TEXT,
    error_message TEXT,
    started_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    finished_at TEXT,
    UNIQUE(tx_id)
);
""", cancellationToken);

        await ExecuteNonQueryAsync(connection, """
CREATE TABLE IF NOT EXISTS local_vm_operation (
    host_id TEXT NOT NULL,
    vm_name TEXT NOT NULL,
    operation_id TEXT NOT NULL,
    operation_type TEXT NOT NULL,
    operation_status TEXT NOT NULL,
    owner_instance_id TEXT NOT NULL,
    fencing_token INTEGER NOT NULL,
    started_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    lease_expires_at TEXT NOT NULL,
    error_code TEXT,
    error_message TEXT,
    PRIMARY KEY(host_id, vm_name)
);
CREATE INDEX IF NOT EXISTS ix_local_vm_operation_lease ON local_vm_operation(lease_expires_at);

CREATE TABLE IF NOT EXISTS local_vm_recovery_state (
    host_id TEXT NOT NULL,
    vm_name TEXT NOT NULL,
    window_started_at TEXT NOT NULL,
    power_cycle_count INTEGER NOT NULL,
    last_power_cycle_at TEXT,
    consecutive_failures INTEGER NOT NULL DEFAULT 0,
    cooldown_until TEXT,
    updated_at TEXT NOT NULL,
    PRIMARY KEY(host_id, vm_name)
);
""", cancellationToken);
    }

    public async Task<VmOperationHandle?> TryAcquireAsync(string hostId, string vmName, string operationId,
        VmOperationType type, string ownerInstanceId, DateTimeOffset now, TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection(); await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO local_vm_operation(host_id, vm_name, operation_id, operation_type, operation_status,
 owner_instance_id, fencing_token, started_at, updated_at, lease_expires_at, error_code, error_message)
VALUES($host, $vm, $id, $type, $status, $owner, 1, $now, $now, $expires, NULL, NULL)
ON CONFLICT(host_id, vm_name) DO UPDATE SET
 operation_id=excluded.operation_id, operation_type=excluded.operation_type,
 operation_status=excluded.operation_status, owner_instance_id=excluded.owner_instance_id,
 fencing_token=local_vm_operation.fencing_token + 1, started_at=excluded.started_at,
 updated_at=excluded.updated_at, lease_expires_at=excluded.lease_expires_at,
 error_code=NULL, error_message=NULL
WHERE local_vm_operation.operation_status IN ('Success','Failed') OR local_vm_operation.lease_expires_at <= $now
RETURNING fencing_token;
""";
        Add(command, "$host", hostId); Add(command, "$vm", vmName); Add(command, "$id", operationId);
        Add(command, "$type", type.ToString()); Add(command, "$status", VmOperationStatus.Acquiring.ToString());
        Add(command, "$owner", ownerInstanceId); Add(command, "$now", Format(now)); Add(command, "$expires", Format(now + leaseDuration));
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : new VmOperationHandle(hostId, vmName, operationId, type, Convert.ToInt64(result), now + leaseDuration);
    }

    public async Task<bool> RenewAsync(VmOperationHandle handle, string ownerInstanceId, DateTimeOffset now,
        TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection(); await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE local_vm_operation SET updated_at=$now, lease_expires_at=$expires " +
            "WHERE host_id=$host AND vm_name=$vm AND operation_id=$id AND owner_instance_id=$owner " +
            "AND fencing_token=$token AND operation_status NOT IN ('Success','Failed');";
        BindHandle(command, handle); Add(command, "$owner", ownerInstanceId); Add(command, "$now", Format(now)); Add(command, "$expires", Format(now + leaseDuration));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> UpdateAsync(VmOperationHandle handle, VmOperationStatus status, string? errorCode,
        string? errorMessage, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection(); await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE local_vm_operation SET operation_status=$status, updated_at=$now, " +
            "lease_expires_at=$expires, error_code=$code, error_message=$message " +
            "WHERE host_id=$host AND vm_name=$vm AND operation_id=$id AND fencing_token=$token;";
        BindHandle(command, handle); Add(command, "$status", status.ToString()); Add(command, "$now", Format(now));
        Add(command, "$expires", Format(now + leaseDuration)); Add(command, "$code", errorCode); Add(command, "$message", errorMessage);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> TryReservePowerCycleAsync(string hostId, string vmName, DateTimeOffset now,
        TimeSpan window, int maximum, TimeSpan cooldown, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection(); await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var read = connection.CreateCommand(); read.Transaction = transaction;
        read.CommandText = "SELECT window_started_at,power_cycle_count,cooldown_until FROM local_vm_recovery_state WHERE host_id=$host AND vm_name=$vm;";
        Add(read, "$host", hostId); Add(read, "$vm", vmName);
        DateTimeOffset windowStart = now; var count = 0; DateTimeOffset? cooldownUntil = null;
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
        { if (await reader.ReadAsync(cancellationToken)) { windowStart = DateTimeOffset.Parse(reader.GetString(0)); count = reader.GetInt32(1); cooldownUntil = ReadNullableDateTimeOffset(reader, 2); } }
        if (cooldownUntil > now || (now - windowStart < window && count >= maximum)) { await transaction.RollbackAsync(cancellationToken); return false; }
        if (now - windowStart >= window) { windowStart = now; count = 0; }
        await using var write = connection.CreateCommand(); write.Transaction = transaction;
        write.CommandText = "INSERT INTO local_vm_recovery_state(host_id,vm_name,window_started_at,power_cycle_count,last_power_cycle_at,consecutive_failures,cooldown_until,updated_at) " +
            "VALUES($host,$vm,$window,1,$now,0,$cooldown,$now) " +
            "ON CONFLICT(host_id,vm_name) DO UPDATE SET window_started_at=$window,power_cycle_count=$count,last_power_cycle_at=$now,cooldown_until=$cooldown,updated_at=$now;";
        Add(write, "$host", hostId); Add(write, "$vm", vmName); Add(write, "$window", Format(windowStart)); Add(write, "$count", count + 1);
        Add(write, "$now", Format(now)); Add(write, "$cooldown", cooldown > TimeSpan.Zero ? Format(now + cooldown) : null);
        await write.ExecuteNonQueryAsync(cancellationToken); await transaction.CommitAsync(cancellationToken); return true;
    }

    public async Task RecordRecoveryResultAsync(string hostId, string vmName, bool success, DateTimeOffset now,
        int maximumConsecutiveFailures, TimeSpan failureCooldown, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection(); await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = success
            ? "UPDATE local_vm_recovery_state SET consecutive_failures=0,updated_at=$now WHERE host_id=$host AND vm_name=$vm;"
            : "UPDATE local_vm_recovery_state SET consecutive_failures=consecutive_failures+1, " +
              "cooldown_until=CASE WHEN consecutive_failures+1 >= $max THEN $cooldown ELSE cooldown_until END,updated_at=$now " +
              "WHERE host_id=$host AND vm_name=$vm;";
        Add(command, "$host", hostId); Add(command, "$vm", vmName); Add(command, "$now", Format(now));
        Add(command, "$max", maximumConsecutiveFailures); Add(command, "$cooldown", Format(now + failureCooldown));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void BindHandle(SqliteCommand command, VmOperationHandle handle)
    { Add(command, "$host", handle.HostId); Add(command, "$vm", handle.VmName); Add(command, "$id", handle.OperationId); Add(command, "$token", handle.FencingToken); }

    public async Task SeedVmStatesAsync(string hostId, IReadOnlyList<VmCurrentState> initialStates, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        foreach (var state in initialStates)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
INSERT INTO local_vm_state (
    host_id,
    vm_name,
    worker_id,
    current_profile_id,
    current_snapshot_name,
    runner_status_code,
    runner_status_name,
    agent_vm_status,
    last_idle_at,
    last_switch_at,
    is_quarantined,
    error_code,
    error_message,
    updated_at
) VALUES (
    $host_id,
    $vm_name,
    $worker_id,
    NULL,
    NULL,
    NULL,
    NULL,
    $agent_vm_status,
    NULL,
    NULL,
    0,
    NULL,
    NULL,
    $updated_at
)
ON CONFLICT(host_id, vm_name) DO UPDATE SET
    worker_id = excluded.worker_id,
    updated_at = excluded.updated_at;
""";
            Add(command, "$host_id", hostId);
            Add(command, "$vm_name", state.VmName);
            Add(command, "$worker_id", state.WorkerId);
            Add(command, "$agent_vm_status", AgentVmStatus.UNKNOWN.ToString());
            Add(command, "$updated_at", Format(state.UpdatedAt));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task UpsertVmStateAsync(string hostId, VmCurrentState state, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO local_vm_state (
    host_id,
    vm_name,
    worker_id,
    current_profile_id,
    current_snapshot_name,
    runner_status_code,
    runner_status_name,
    agent_vm_status,
    last_idle_at,
    last_switch_at,
    is_quarantined,
    error_code,
    error_message,
    updated_at
) VALUES (
    $host_id,
    $vm_name,
    $worker_id,
    $current_profile_id,
    $current_snapshot_name,
    $runner_status_code,
    $runner_status_name,
    $agent_vm_status,
    $last_idle_at,
    $last_switch_at,
    $is_quarantined,
    $error_code,
    $error_message,
    $updated_at
)
ON CONFLICT(host_id, vm_name) DO UPDATE SET
    worker_id = excluded.worker_id,
    current_profile_id = excluded.current_profile_id,
    current_snapshot_name = excluded.current_snapshot_name,
    runner_status_code = excluded.runner_status_code,
    runner_status_name = excluded.runner_status_name,
    agent_vm_status = excluded.agent_vm_status,
    last_idle_at = excluded.last_idle_at,
    last_switch_at = excluded.last_switch_at,
    is_quarantined = excluded.is_quarantined,
    error_code = excluded.error_code,
    error_message = excluded.error_message,
    updated_at = excluded.updated_at;
""";

        Add(command, "$host_id", hostId);
        Add(command, "$vm_name", state.VmName);
        Add(command, "$worker_id", state.WorkerId);
        Add(command, "$current_profile_id", state.CurrentProfileId);
        Add(command, "$current_snapshot_name", state.CurrentSnapshotName);
        Add(command, "$runner_status_code", state.RunnerStatusCode is null ? null : (int)state.RunnerStatusCode.Value);
        Add(command, "$runner_status_name", state.RunnerStatusCode?.ToString());
        Add(command, "$agent_vm_status", state.VmStatus.ToString());
        Add(command, "$last_idle_at", Format(state.IdleSince));
        Add(command, "$last_switch_at", null);
        Add(command, "$is_quarantined", state.IsQuarantined ? 1 : 0);
        Add(command, "$error_code", state.ErrorCode);
        Add(command, "$error_message", state.ErrorMessage);
        Add(command, "$updated_at", Format(state.UpdatedAt));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateVmQuarantineAsync(
        string hostId,
        string vmName,
        string workerId,
        bool isQuarantined,
        CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO local_vm_state (
    host_id,
    vm_name,
    worker_id,
    current_profile_id,
    current_snapshot_name,
    runner_status_code,
    runner_status_name,
    agent_vm_status,
    last_idle_at,
    last_switch_at,
    is_quarantined,
    error_code,
    error_message,
    updated_at
) VALUES (
    $host_id,
    $vm_name,
    $worker_id,
    NULL,
    NULL,
    NULL,
    NULL,
    $agent_vm_status,
    NULL,
    NULL,
    $is_quarantined,
    NULL,
    NULL,
    $updated_at
)
ON CONFLICT(host_id, vm_name) DO UPDATE SET
    worker_id = excluded.worker_id,
    is_quarantined = excluded.is_quarantined,
    updated_at = excluded.updated_at;
""";

        Add(command, "$host_id", hostId);
        Add(command, "$vm_name", vmName);
        Add(command, "$worker_id", workerId);
        Add(command, "$agent_vm_status", AgentVmStatus.UNKNOWN.ToString());
        Add(command, "$is_quarantined", isQuarantined ? 1 : 0);
        Add(command, "$updated_at", Format(DateTimeOffset.Now));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<VmCurrentState>> GetVmStatesAsync(string hostId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT
    vm_name,
    worker_id,
    current_profile_id,
    current_snapshot_name,
    runner_status_code,
    agent_vm_status,
    last_idle_at,
    is_quarantined,
    error_code,
    error_message,
    updated_at
FROM local_vm_state
WHERE host_id = $host_id
ORDER BY vm_name;
""";
        Add(command, "$host_id", hostId);

        var states = new List<VmCurrentState>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            states.Add(new VmCurrentState
            {
                VmName = reader.GetString(0),
                WorkerId = reader.GetString(1),
                CurrentProfileId = ReadNullableString(reader, 2),
                CurrentSnapshotName = ReadNullableString(reader, 3),
                RunnerStatusCode = ReadNullableRunnerStatus(reader, 4),
                VmStatus = Enum.Parse<AgentVmStatus>(reader.GetString(5)),
                IdleSince = ReadNullableDateTimeOffset(reader, 6),
                IsQuarantined = reader.GetInt32(7) == 1,
                ErrorCode = ReadNullableString(reader, 8),
                ErrorMessage = ReadNullableString(reader, 9),
                UpdatedAt = DateTimeOffset.Parse(reader.GetString(10))
            });
        }

        return states;
    }

    public async Task<VmCurrentState?> GetVmStateAsync(string hostId, string vmName, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT
    vm_name,
    worker_id,
    current_profile_id,
    current_snapshot_name,
    runner_status_code,
    agent_vm_status,
    last_idle_at,
    is_quarantined,
    error_code,
    error_message,
    updated_at
FROM local_vm_state
WHERE host_id = $host_id AND vm_name = $vm_name;
""";
        Add(command, "$host_id", hostId);
        Add(command, "$vm_name", vmName);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new VmCurrentState
        {
            VmName = reader.GetString(0),
            WorkerId = reader.GetString(1),
            CurrentProfileId = ReadNullableString(reader, 2),
            CurrentSnapshotName = ReadNullableString(reader, 3),
            RunnerStatusCode = ReadNullableRunnerStatus(reader, 4),
            VmStatus = Enum.Parse<AgentVmStatus>(reader.GetString(5)),
            IdleSince = ReadNullableDateTimeOffset(reader, 6),
            IsQuarantined = reader.GetInt32(7) == 1,
            ErrorCode = ReadNullableString(reader, 8),
            ErrorMessage = ReadNullableString(reader, 9),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(10))
        };
    }

    public async Task CreateSwitchTransactionAsync(SwitchTransaction transaction, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO local_switch_transaction (
    tx_id,
    host_id,
    vm_name,
    worker_id,
    from_profile_id,
    from_snapshot_name,
    to_profile_id,
    to_snapshot_name,
    first_task_id,
    trigger_reason,
    status,
    step,
    error_code,
    error_message,
    started_at,
    updated_at,
    finished_at
) VALUES (
    $tx_id,
    $host_id,
    $vm_name,
    $worker_id,
    $from_profile_id,
    $from_snapshot_name,
    $to_profile_id,
    $to_snapshot_name,
    $first_task_id,
    $trigger_reason,
    $status,
    $step,
    $error_code,
    $error_message,
    $started_at,
    $updated_at,
    $finished_at
);
""";
        BindTransaction(command, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateSwitchTransactionAsync(
        string txId,
        SwitchTransactionStatus status,
        string? step,
        string? errorCode,
        string? errorMessage,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
UPDATE local_switch_transaction
SET
    status = $status,
    step = $step,
    error_code = $error_code,
    error_message = $error_message,
    updated_at = $updated_at,
    finished_at = $finished_at
WHERE tx_id = $tx_id;
""";
        Add(command, "$status", status.ToString());
        Add(command, "$step", step);
        Add(command, "$error_code", errorCode);
        Add(command, "$error_message", errorMessage);
        Add(command, "$updated_at", Format(updatedAt));
        Add(command, "$finished_at", IsTerminal(status) ? Format(updatedAt) : null);
        Add(command, "$tx_id", txId);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SwitchTransaction>> GetIncompleteSwitchTransactionsAsync(string hostId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT
    tx_id,
    host_id,
    vm_name,
    worker_id,
    from_profile_id,
    from_snapshot_name,
    to_profile_id,
    to_snapshot_name,
    first_task_id,
    trigger_reason,
    status,
    step,
    error_code,
    error_message,
    started_at,
    updated_at,
    finished_at
FROM local_switch_transaction
WHERE host_id = $host_id
  AND status NOT IN ('SUCCESS', 'FAILED')
ORDER BY started_at;
""";
        Add(command, "$host_id", hostId);

        return await ReadTransactionsAsync(command, cancellationToken);
    }

    public async Task DeleteSwitchTransactionAsync(string txId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM local_switch_transaction WHERE tx_id = $tx_id;";
        Add(command, "$tx_id", txId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<SwitchTransaction?> GetSwitchTransactionAsync(string txId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT
    tx_id,
    host_id,
    vm_name,
    worker_id,
    from_profile_id,
    from_snapshot_name,
    to_profile_id,
    to_snapshot_name,
    first_task_id,
    trigger_reason,
    status,
    step,
    error_code,
    error_message,
    started_at,
    updated_at,
    finished_at
FROM local_switch_transaction
WHERE tx_id = $tx_id;
""";
        Add(command, "$tx_id", txId);

        var transactions = await ReadTransactionsAsync(command, cancellationToken);
        return transactions.Count == 0 ? null : transactions[0];
    }

    private SqliteConnection CreateConnection()
    {
        return new SqliteConnection(_connectionString);
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<SwitchTransaction>> ReadTransactionsAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var transactions = new List<SwitchTransaction>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            transactions.Add(new SwitchTransaction
            {
                TransactionId = reader.GetString(0),
                HostId = reader.GetString(1),
                VmName = reader.GetString(2),
                WorkerId = reader.GetString(3),
                FromProfileId = ReadNullableString(reader, 4),
                FromSnapshotName = ReadNullableString(reader, 5),
                TargetProfileId = reader.GetString(6),
                TargetSnapshotName = reader.GetString(7),
                FirstTaskId = ReadNullableInt64(reader, 8),
                TriggerReason = ReadNullableString(reader, 9),
                Status = Enum.Parse<SwitchTransactionStatus>(reader.GetString(10)),
                Step = ReadNullableString(reader, 11),
                ErrorCode = ReadNullableString(reader, 12),
                ErrorMessage = ReadNullableString(reader, 13),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(14)),
                UpdatedAt = ReadNullableDateTimeOffset(reader, 15),
                CompletedAt = ReadNullableDateTimeOffset(reader, 16)
            });
        }

        return transactions;
    }

    private static void BindTransaction(SqliteCommand command, SwitchTransaction transaction)
    {
        Add(command, "$tx_id", transaction.TransactionId);
        Add(command, "$host_id", transaction.HostId);
        Add(command, "$vm_name", transaction.VmName);
        Add(command, "$worker_id", transaction.WorkerId);
        Add(command, "$from_profile_id", transaction.FromProfileId);
        Add(command, "$from_snapshot_name", transaction.FromSnapshotName);
        Add(command, "$to_profile_id", transaction.TargetProfileId);
        Add(command, "$to_snapshot_name", transaction.TargetSnapshotName);
        Add(command, "$first_task_id", transaction.FirstTaskId);
        Add(command, "$trigger_reason", transaction.TriggerReason);
        Add(command, "$status", transaction.Status.ToString());
        Add(command, "$step", transaction.Step);
        Add(command, "$error_code", transaction.ErrorCode);
        Add(command, "$error_message", transaction.ErrorMessage);
        Add(command, "$started_at", Format(transaction.CreatedAt));
        Add(command, "$updated_at", Format(transaction.UpdatedAt ?? transaction.CreatedAt));
        Add(command, "$finished_at", Format(transaction.CompletedAt));
    }

    private static bool IsTerminal(SwitchTransactionStatus status)
    {
        return status is SwitchTransactionStatus.SUCCESS or SwitchTransactionStatus.FAILED;
    }

    private static string? Format(DateTimeOffset? value)
    {
        return value?.ToString("O");
    }

    private static void Add(SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static long? ReadNullableInt64(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    private static DateTimeOffset? ReadNullableDateTimeOffset(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal));
    }

    private static RunnerStatusCode? ReadNullableRunnerStatus(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : (RunnerStatusCode)reader.GetInt32(ordinal);
    }
}
