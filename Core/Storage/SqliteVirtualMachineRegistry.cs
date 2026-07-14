using Microsoft.Data.Sqlite;
using Seebot.WorkerAgent.Core.Configuration;

namespace Seebot.WorkerAgent.Core.Storage;

public sealed class SqliteVirtualMachineRegistry : IVirtualMachineRegistry
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _initialized;

    public SqliteVirtualMachineRegistry(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString();
    }

    public async Task<IReadOnlyList<VirtualMachineOptions>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var vms = new List<VirtualMachineOptions>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
SELECT
    vm_name,
    vmx_path,
    base_snapshot_name,
    guest_user,
    guest_password_secret,
    worker_id,
    guest_work_path,
    guest_backup_paths,
    enabled,
    disabled_reason
FROM local_vm_config
ORDER BY vm_name;
""";

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                vms.Add(new VirtualMachineOptions
                {
                    Name = reader.GetString(0),
                    VmxPath = reader.GetString(1),
                    BaseSnapshotName = reader.GetString(2),
                    GuestUser = ReadNullableString(reader, 3) ?? "",
                    GuestPasswordSecret = ReadNullableString(reader, 4) ?? "",
                    WorkerId = reader.GetString(5),
                    GuestWorkPath = reader.GetString(6),
                    GuestBackupPaths = reader.GetString(7),
                    Enabled = reader.GetInt32(8) == 1,
                    DisabledReason = ReadNullableString(reader, 9)
                });
            }
        }

        var profilesByVm = await LoadProfilesAsync(connection, cancellationToken).ConfigureAwait(false);
        foreach (var vm in vms)
        {
            if (profilesByVm.TryGetValue(vm.Name, out var profiles))
            {
                vm.Profiles = profiles;
            }
        }

        return vms;
    }

    public async Task<VirtualMachineOptions?> GetByNameAsync(string vmName, CancellationToken cancellationToken = default)
    {
        var vms = await GetAllAsync(cancellationToken).ConfigureAwait(false);
        return vms.FirstOrDefault(vm => string.Equals(vm.Name, vmName, StringComparison.OrdinalIgnoreCase));
    }

    public async Task UpsertVmAsync(VirtualMachineOptions vm, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
INSERT INTO local_vm_config (
    vm_name,
    vmx_path,
    base_snapshot_name,
    guest_user,
    guest_password_secret,
    worker_id,
    guest_work_path,
    guest_backup_paths,
    enabled,
    disabled_reason,
    updated_at
) VALUES (
    $vm_name,
    $vmx_path,
    $base_snapshot_name,
    $guest_user,
    $guest_password_secret,
    $worker_id,
    $guest_work_path,
    $guest_backup_paths,
    $enabled,
    $disabled_reason,
    $updated_at
)
ON CONFLICT(vm_name) DO UPDATE SET
    vmx_path = excluded.vmx_path,
    base_snapshot_name = excluded.base_snapshot_name,
    guest_user = excluded.guest_user,
    guest_password_secret = excluded.guest_password_secret,
    worker_id = excluded.worker_id,
    guest_work_path = excluded.guest_work_path,
    guest_backup_paths = excluded.guest_backup_paths,
    enabled = excluded.enabled,
    disabled_reason = excluded.disabled_reason,
    updated_at = excluded.updated_at;
""";
            BindVm(command, vm);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await UpsertProfilesAsync(connection, transaction, vm.Name, vm.Profiles, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateVmStatusAsync(
        string vmName,
        bool enabled,
        string? disabledReason,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, """
UPDATE local_vm_config
SET
    enabled = $enabled,
    disabled_reason = $disabled_reason,
    updated_at = $updated_at
WHERE vm_name = $vm_name;
""", command =>
        {
            Add(command, "$enabled", enabled ? 1 : 0);
            Add(command, "$disabled_reason", enabled ? null : disabledReason);
            Add(command, "$updated_at", DateTimeOffset.Now.ToString("O"));
            Add(command, "$vm_name", vmName);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteVmAsync(string vmName, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, transaction, "DELETE FROM local_vm_profile WHERE vm_name = $vm_name;", command =>
        {
            Add(command, "$vm_name", vmName);
        }, cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, transaction, "DELETE FROM local_vm_config WHERE vm_name = $vm_name;", command =>
        {
            Add(command, "$vm_name", vmName);
        }, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertProfileAsync(string vmName, ProfileOptions profile, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await UpsertProfileAsync(connection, transaction, vmName, profile, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateProfileSnapshotAsync(
        string vmName,
        string profileId,
        string snapshotName,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, """
UPDATE local_vm_profile
SET
    snapshot_name = $snapshot_name,
    updated_at = $updated_at
WHERE vm_name = $vm_name AND profile_id = $profile_id;
""", command =>
        {
            Add(command, "$snapshot_name", snapshotName);
            Add(command, "$updated_at", DateTimeOffset.Now.ToString("O"));
            Add(command, "$vm_name", vmName);
            Add(command, "$profile_id", profileId);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteProfileAsync(string vmName, string profileId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, """
DELETE FROM local_vm_profile
WHERE vm_name = $vm_name AND profile_id = $profile_id;
""", command =>
        {
            Add(command, "$vm_name", vmName);
            Add(command, "$profile_id", profileId);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, null, """
CREATE TABLE IF NOT EXISTS local_vm_config (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    vm_name TEXT NOT NULL,
    vmx_path TEXT NOT NULL,
    base_snapshot_name TEXT NOT NULL,
    guest_user TEXT,
    guest_password_secret TEXT,
    worker_id TEXT NOT NULL,
    guest_work_path TEXT NOT NULL,
    guest_backup_paths TEXT NOT NULL,
    enabled INTEGER NOT NULL DEFAULT 1,
    disabled_reason TEXT,
    updated_at TEXT NOT NULL,
    UNIQUE(vm_name)
);
""", null, cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(connection, "local_vm_config", "disabled_reason", "TEXT", cancellationToken).ConfigureAwait(false);

            await ExecuteAsync(connection, null, """
CREATE TABLE IF NOT EXISTS local_vm_profile (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    vm_name TEXT NOT NULL,
    profile_id TEXT NOT NULL,
    profile_name TEXT NOT NULL,
    snapshot_name TEXT,
    enabled INTEGER NOT NULL DEFAULT 1,
    updated_at TEXT NOT NULL,
    UNIQUE(vm_name, profile_id)
);
""", null, cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(connection, "local_vm_profile", "snapshot_name", "TEXT", cancellationToken).ConfigureAwait(false);

            _initialized = true;
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    private async Task<Dictionary<string, List<ProfileOptions>>> LoadProfilesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var profilesByVm = new Dictionary<string, List<ProfileOptions>>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT vm_name, profile_id, profile_name, snapshot_name, updated_at
FROM local_vm_profile
WHERE enabled = 1
ORDER BY vm_name, profile_id;
""";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var vmName = reader.GetString(0);
            if (!profilesByVm.TryGetValue(vmName, out var profiles))
            {
                profiles = [];
                profilesByVm[vmName] = profiles;
            }

            profiles.Add(new ProfileOptions
            {
                ProfileId = reader.GetString(1),
                ProfileName = reader.GetString(2),
                SnapshotName = ReadNullableString(reader, 3) ?? "",
                UpdatedAt = ReadNullableString(reader, 4)
            });
        }

        return profilesByVm;
    }

    private static async Task UpsertProfilesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string vmName,
        IReadOnlyList<ProfileOptions> profiles,
        CancellationToken cancellationToken)
    {
        foreach (var profile in profiles)
        {
            await UpsertProfileAsync(connection, transaction, vmName, profile, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task UpsertProfileAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string vmName,
        ProfileOptions profile,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, """
INSERT INTO local_vm_profile (
    vm_name,
    profile_id,
    profile_name,
    snapshot_name,
    enabled,
    updated_at
) VALUES (
    $vm_name,
    $profile_id,
    $profile_name,
    $snapshot_name,
    1,
    $updated_at
)
ON CONFLICT(vm_name, profile_id) DO UPDATE SET
    profile_name = excluded.profile_name,
    snapshot_name = excluded.snapshot_name,
    enabled = 1,
    updated_at = excluded.updated_at;
""", command =>
        {
            Add(command, "$vm_name", vmName);
            Add(command, "$profile_id", profile.ProfileId);
            Add(command, "$profile_name", profile.ProfileName);
            Add(command, "$snapshot_name", profile.SnapshotName);
            Add(command, "$updated_at", DateTimeOffset.Now.ToString("O"));
        }, cancellationToken).ConfigureAwait(false);
    }

    private static void BindVm(SqliteCommand command, VirtualMachineOptions vm)
    {
        Add(command, "$vm_name", vm.Name);
        Add(command, "$vmx_path", vm.VmxPath);
        Add(command, "$base_snapshot_name", vm.BaseSnapshotName);
        Add(command, "$guest_user", vm.GuestUser);
        Add(command, "$guest_password_secret", vm.GuestPasswordSecret);
        Add(command, "$worker_id", vm.WorkerId);
        Add(command, "$guest_work_path", vm.GuestWorkPath);
        Add(command, "$guest_backup_paths", string.IsNullOrWhiteSpace(vm.GuestBackupPaths) ? "cache,db,file,logs" : vm.GuestBackupPaths);
        Add(command, "$enabled", vm.Enabled ? 1 : 0);
        Add(command, "$disabled_reason", vm.Enabled ? null : vm.DisabledReason);
        Add(command, "$updated_at", DateTimeOffset.Now.ToString("O"));
    }

    private SqliteConnection CreateConnection()
    {
        return new SqliteConnection(_connectionString);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        Action<SqliteCommand>? bind,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        bind?.Invoke(command);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Add(SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string columnDefinition,
        CancellationToken cancellationToken)
    {
        await using (var query = connection.CreateCommand())
        {
            query.CommandText = $"PRAGMA table_info({tableName});";
            await using var reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        await ExecuteAsync(connection, null, $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};", null, cancellationToken).ConfigureAwait(false);
    }

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }
}
