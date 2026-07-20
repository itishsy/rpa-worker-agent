using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Seebot.WorkerAgent.Core.Configuration;
using Seebot.WorkerAgent.Core.Domain;
using Seebot.WorkerAgent.Core.Snapshot;
using Seebot.WorkerAgent.Core.Storage;
using Seebot.WorkerAgent.Core.Switching;
using Seebot.WorkerAgent.Core.Vmware;

namespace Seebot.WorkerAgent.Core.Operations;

public static class OperationsApiExtensions
{
    public static WebApplication MapOperationsApi(this WebApplication app)
    {
        app.MapGet("/vms", (HttpContext context) =>
        {
            return Results.Content(BuildVmManagementPage(), "text/html; charset=utf-8");
        }).AddEndpointFilter(ApiKeyFilter);

        var group = app.MapGroup("/operations").AddEndpointFilter(ApiKeyFilter);

        group.MapGet("/vms", async (
            IVirtualMachineRegistry registry,
            CancellationToken cancellationToken) =>
        {
            var vms = await registry.GetAllAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(vms);
        });

        group.MapGet("/vms/overview", async (
            IVirtualMachineRegistry registry,
            ILocalStore localStore,
            WorkerAgentOptions options,
            CancellationToken cancellationToken) =>
        {
            var vms = await registry.GetAllAsync(cancellationToken).ConfigureAwait(false);
            var states = await localStore.GetVmStatesAsync(options.Agent.HostId, cancellationToken).ConfigureAwait(false);
            var stateByVmName = states.ToDictionary(state => state.VmName, StringComparer.OrdinalIgnoreCase);

            return Results.Ok(vms.Select(vm =>
            {
                stateByVmName.TryGetValue(vm.Name, out var state);
                return new
                {
                    vm.Name,
                    vm.VmxPath,
                    vm.BaseSnapshotName,
                    vm.GuestUser,
                    vm.GuestPasswordSecret,
                    vm.WorkerId,
                    vm.GuestWorkPath,
                    vm.GuestBackupPaths,
                    vm.Enabled,
                    vm.DisabledReason,
                    vm.Profiles,
                    CurrentProfileId = state?.CurrentProfileId,
                    CurrentSnapshotName = state?.CurrentSnapshotName,
                    RunnerStatus = state?.RunnerStatusCode?.ToString(),
                    VmStatus = state?.VmStatus.ToString(),
                    IsQuarantined = state?.IsQuarantined ?? false,
                    StateUpdatedAt = state?.UpdatedAt
                };
            }));
        });

        group.MapGet("/vms/{vmName}", async (
            string vmName,
            IVirtualMachineRegistry registry,
            CancellationToken cancellationToken) =>
        {
            var vm = await registry.GetByNameAsync(vmName, cancellationToken).ConfigureAwait(false);
            return vm is null ? Results.NotFound() : Results.Ok(vm);
        });

        group.MapPost("/vms", async (
            VirtualMachineOptions vm,
            IVirtualMachineRegistry registry,
            CancellationToken cancellationToken) =>
        {
            var errors = ValidateVm(vm);
            if (errors.Count > 0)
            {
                return Results.BadRequest(new { errors });
            }

            await registry.UpsertVmAsync(vm, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new
            {
                message = "VM configuration saved. Restart the service to apply runtime changes.",
                restartRequired = true
            });
        });

        group.MapDelete("/vms/{vmName}", async (
            string vmName,
            IVirtualMachineRegistry registry,
            CancellationToken cancellationToken) =>
        {
            await registry.DeleteVmAsync(vmName, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new
            {
                message = "VM configuration deleted. Restart the service to apply runtime changes.",
                restartRequired = true
            });
        });

        group.MapPost("/vms/{vmName}/quarantine", async (
            string vmName,
            IVirtualMachineRegistry registry,
            ILocalStore localStore,
            WorkerAgentOptions options,
            CancellationToken cancellationToken) =>
        {
            var vm = await registry.GetByNameAsync(vmName, cancellationToken).ConfigureAwait(false);
            if (vm is null)
            {
                return Results.NotFound(new { message = $"VM '{vmName}' was not found." });
            }

            await localStore.UpdateVmQuarantineAsync(
                options.Agent.HostId,
                vm.Name,
                vm.WorkerId,
                isQuarantined: true,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(new
            {
                message = $"VM '{vm.Name}' quarantined.",
                restartRequired = false
            });
        });

        group.MapPost("/vms/{vmName}/unquarantine", async (
            string vmName,
            IVirtualMachineRegistry registry,
            ILocalStore localStore,
            WorkerAgentOptions options,
            CancellationToken cancellationToken) =>
        {
            var vm = await registry.GetByNameAsync(vmName, cancellationToken).ConfigureAwait(false);
            if (vm is null)
            {
                return Results.NotFound(new { message = $"VM '{vmName}' was not found." });
            }

            await localStore.UpdateVmQuarantineAsync(
                options.Agent.HostId,
                vm.Name,
                vm.WorkerId,
                isQuarantined: false,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(new
            {
                message = $"VM '{vm.Name}' unquarantined.",
                restartRequired = false
            });
        });

        group.MapPost("/vms/{vmName}/profiles", async (
            string vmName,
            ProfileOptions profile,
            IVirtualMachineRegistry registry,
            CancellationToken cancellationToken) =>
        {
            var vm = await registry.GetByNameAsync(vmName, cancellationToken).ConfigureAwait(false);
            if (vm is null)
            {
                return Results.NotFound(new { message = $"VM '{vmName}' was not found." });
            }

            var errors = ValidateProfile(profile);
            if (errors.Count > 0)
            {
                return Results.BadRequest(new { errors });
            }

            await registry.UpsertProfileAsync(vmName, profile, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new
            {
                message = "Profile configuration saved. Restart the service to apply runtime changes.",
                restartRequired = true
            });
        });

        group.MapDelete("/vms/{vmName}/profiles/{profileId}", async (
            string vmName,
            string profileId,
            IVirtualMachineRegistry registry,
            CancellationToken cancellationToken) =>
        {
            await registry.DeleteProfileAsync(vmName, profileId, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new
            {
                message = "Profile configuration deleted. Restart the service to apply runtime changes.",
                restartRequired = true
            });
        });

        group.MapPost("/vms/{vmName}/update-init-workerid", async (
            string vmName,
            IInitFileUpdateService initFileUpdateService,
            CancellationToken cancellationToken) =>
        {
            var result = await initFileUpdateService.UpdateWorkerIdInSnapshotsAsync(vmName, cancellationToken)
                .ConfigureAwait(false);
            return result.Success
                ? Results.Ok(result)
                : Results.UnprocessableEntity(result);
        });

        group.MapPost("/snapshots/{vmName}/{profileId}/update", async (
            string vmName,
            string profileId,
            ISnapshotUpdateService snapshotService,
            CancellationToken cancellationToken) =>
        {
            var result = await snapshotService.UpdateSnapshotAsync(vmName, profileId, cancellationToken);
            return result.Success
                ? Results.Ok(result)
                : Results.UnprocessableEntity(result);
        });

        group.MapPost("/vms/{vmName}/power-on", async (
            string vmName,
            IVmPowerOnService powerOnService,
            CancellationToken cancellationToken) =>
        {
            var result = await powerOnService.PowerOnAsync(vmName, cancellationToken).ConfigureAwait(false);
            return result.Success ? Results.Ok(result) : Results.UnprocessableEntity(result);
        });

        group.MapPost("/snapshots/{vmName}/{profileId}/switch", async (
            string vmName,
            string profileId,
            WorkerAgentOptions options,
            ILocalStore localStore,
            IVmrunService vmrunService,
            IVmOperationLock vmOperationLock,
            IProfileSnapshotResolver snapshotResolver,
            IVmSwitchService switchService,
            CancellationToken cancellationToken) =>
        {
            var vm = options.VirtualMachines
                .Where(v => v.Enabled)
                .FirstOrDefault(v => string.Equals(v.Name, vmName, StringComparison.OrdinalIgnoreCase));
            if (vm is null)
            {
                return Results.NotFound(new { success = false, errorCode = ErrorCodes.VmNotFound, errorMessage = $"VM '{vmName}' not found in runtime configuration." });
            }

            var profile = vm.Profiles
                .FirstOrDefault(p => string.Equals(p.ProfileId, profileId, StringComparison.OrdinalIgnoreCase));
            if (profile is null)
            {
                return Results.NotFound(new { success = false, errorCode = ErrorCodes.ProfileNotFound, errorMessage = $"Profile '{profileId}' not found in VM '{vmName}'." });
            }

            await using var vmLock = await vmOperationLock.AcquireAsync(vm.VmxPath, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<string> snapshots;
            try
            {
                snapshots = await vmrunService.ListSnapshotsAsync(vm.VmxPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.UnprocessableEntity(new { success = false, errorCode = ErrorCodes.SnapshotNotFound, errorMessage = ex.Message });
            }

            var resolution = snapshotResolver.Resolve(vm, profile, snapshots);
            if (!resolution.IsReady)
            {
                var errorCode = resolution.Status == ProfileSnapshotResolutionStatus.Duplicate
                    ? ErrorCodes.SnapshotAmbiguous
                    : ErrorCodes.SnapshotNotFound;
                return Results.UnprocessableEntity(new { success = false, errorCode, errorMessage = resolution.Message ?? "Profile snapshot is not ready." });
            }

            var states = await localStore.GetVmStatesAsync(options.Agent.HostId, cancellationToken).ConfigureAwait(false);
            var state = states.FirstOrDefault(s => string.Equals(s.VmName, vm.Name, StringComparison.OrdinalIgnoreCase));
            var result = await switchService.SwitchAsync(new VmSwitchRequest
            {
                HostId = options.Agent.HostId,
                Vm = vm,
                FromProfileId = state?.CurrentProfileId,
                FromSnapshotName = state?.CurrentSnapshotName,
                TargetProfileId = profile.ProfileId,
                TargetSnapshotName = resolution.SnapshotName!,
                Timestamp = DateTimeOffset.Now
            }, cancellationToken).ConfigureAwait(false);

            return result.Success
                ? Results.Ok(result)
                : Results.UnprocessableEntity(result);
        });

        return app;
    }

    private static List<string> ValidateVm(VirtualMachineOptions vm)
    {
        var result = WorkerAgentOptionsValidator.Validate(new WorkerAgentOptions
        {
            Agent = new AgentOptions
            {
                HostId = "local",
                HostWorkPath = "."
            },
            Vmrun = new VmrunOptions
            {
                VmrunPath = "vmrun"
            },
            VirtualMachines = [vm]
        });

        return result.Errors.ToList();
    }

    private static List<string> ValidateProfile(ProfileOptions profile)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(profile.ProfileId))
        {
            errors.Add("ProfileId is required.");
        }

        if (string.IsNullOrWhiteSpace(profile.ProfileName))
        {
            errors.Add("ProfileName is required.");
        }

        return errors;
    }

    private static string BuildVmManagementPage()
    {
        return """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Worker Agent VM Profiles</title>
  <style>
    :root { color-scheme: light dark; font-family: Segoe UI, Arial, sans-serif; }
    body { margin: 0; background: #f7f8fa; color: #1f2937; }
    header { padding: 18px 24px; background: #1f2937; color: white; }
    main {
      padding: 20px 24px;
      display: grid;
      gap: 18px;
      grid-template-columns: minmax(560px, 1.35fr) minmax(380px, 520px);
      grid-template-areas:
        "vm-list vm-editor"
        "profile-list profile-editor";
      align-items: start;
    }
    .column { display: contents; }
    section {
      background: white;
      border: 1px solid #d8dee8;
      border-radius: 6px;
      padding: 16px;
      box-shadow: 0 1px 2px rgba(15, 23, 42, .04);
    }
    .vm-list { grid-area: vm-list; height: 750px; }
    .vm-editor { grid-area: vm-editor; min-height: 560px; }
    .profile-list { grid-area: profile-list; height: 390px; }
    .profile-editor { grid-area: profile-editor; min-height: 360px; }
    .fixed-panel { box-sizing: border-box; display: flex; flex-direction: column; overflow: hidden; }
    .editor-panel { overflow: visible; }
    .table-wrap { flex: 1; min-height: 0; overflow: auto; border: 1px solid #e5e7eb; border-radius: 4px; }
    h1 { font-size: 20px; margin: 0; }
    h2 { font-size: 16px; margin: 0 0 12px; }
    form { display: grid; gap: 7px; }
    label { display: block; font-size: 12px; font-weight: 600; margin: 2px 0 0; }
    input { box-sizing: border-box; width: 100%; padding: 7px 8px; border: 1px solid #c8d0dc; border-radius: 4px; font: inherit; }
    button { padding: 8px 11px; border: 1px solid #9aa7b8; border-radius: 4px; background: #fff; cursor: pointer; }
    button:hover { background: #f8fafc; }
    button.primary { background: #2563eb; color: white; border-color: #2563eb; }
    button.primary:hover { background: #1d4ed8; }
    button.danger { color: #b91c1c; border-color: #f0b4b4; }
    button.danger:hover { background: #fff5f5; }
    table { width: 100%; border-collapse: collapse; font-size: 13px; }
    th, td { border-bottom: 1px solid #e5e7eb; padding: 8px; text-align: left; vertical-align: top; }
    th { position: sticky; top: 0; z-index: 1; background: #f8fafc; font-size: 12px; }
    tr.selected-row td { background: #eff6ff; }
    tr.selected-row td:first-child { box-shadow: inset 3px 0 0 #2563eb; }
    .row { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; margin-top: 10px; }
    .status { min-height: 20px; font-size: 13px; color: #475569; white-space: pre-wrap; }
    .muted { color: #64748b; font-size: 12px; }
    @media (max-width: 1100px) {
      main {
        grid-template-columns: 1fr;
        grid-template-areas:
          "vm-list"
          "vm-editor"
          "profile-list"
          "profile-editor";
      }
      .vm-list, .profile-list { height: auto; min-height: 320px; }
      .vm-editor, .profile-editor { min-height: 0; }
    }
  </style>
</head>
<body>
  <header><h1>Worker Agent VM Profiles</h1></header>
  <main>
    <div class="column">
      <section class="fixed-panel vm-list">
        <h2>Registered VMs</h2>
        <div class="table-wrap">
          <table>
            <thead><tr><th>VM</th><th>Worker</th><th>Status</th><th>Current Profile</th><th>Actions</th></tr></thead>
            <tbody id="vmRows"></tbody>
          </table>
        </div>
      </section>
      <section class="fixed-panel profile-list">
        <h2>Profiles</h2>
        <div class="table-wrap">
          <table>
            <thead><tr><th>VM</th><th>Profile ID</th><th>Profile Name</th><th>Snapshot</th><th>Updated</th><th>Actions</th></tr></thead>
            <tbody id="profileRows"></tbody>
          </table>
        </div>
      </section>
    </div>
    <div class="column">
      <section class="fixed-panel editor-panel vm-editor">
        <h2>VM</h2>
        <form id="vmForm">
          <label>Name</label><input name="name" required>
          <label>VMX Path</label><input name="vmxPath" required>
          <label>Base Snapshot Name</label><input name="baseSnapshotName" required>
          <label>Worker ID</label><input name="workerId" required>
          <label>Guest User</label><input name="guestUser">
          <label>Guest Password</label><input name="guestPasswordSecret" type="password">
          <label>Guest Work Path</label><input name="guestWorkPath" required>
          <label>Guest Backup Paths</label><input name="guestBackupPaths" value="cache,db,file,logs" required>
          <label><input name="enabled" type="checkbox" checked style="width:auto"> Enabled</label>
          <div class="row">
            <button class="primary" type="submit">Save VM</button>
            <button type="button" id="powerOnVmBtn" onclick="powerOnVm()">开启</button>
            <button type="button" id="updateVmSnapshotsBtn" onclick="updateVmSnapshots()">升级快照</button>
            <button type="button" id="updateInitBtn" onclick="updateInitWorkerId()">更新机器码</button>
            <button type="button" onclick="clearVmForm()">Clear</button>
          </div>
        </form>
        <p class="muted">Changes are saved to local SQLite. Restart the service to apply runtime changes.</p>
        <div id="status" class="status"></div>
      </section>
      <section class="fixed-panel editor-panel profile-editor">
        <h2>Profile</h2>
        <form id="profileForm">
          <label>VM Name</label><input name="vmName" required>
          <label>Profile ID</label><input name="profileId" required>
          <label>Profile Name</label><input name="profileName" required>
          <label>Snapshot Name</label><input name="snapshotName">
          <div class="row">
            <button class="primary" type="submit">Save Profile</button>
            <button type="button" onclick="switchSelectedSnapshot()">切换</button>
            <button type="button" onclick="updateSelectedSnapshot()">升级</button>
            <button type="button" onclick="clearProfileForm()">Clear</button>
          </div>
        </form>
      </section>
    </div>
  </main>
  <script>
    const qs = new URLSearchParams(location.search);
    const apiKey = qs.get('apiKey') || '';
    const api = path => '/operations' + path + (apiKey ? (path.includes('?') ? '&' : '?') + 'apiKey=' + encodeURIComponent(apiKey) : '');
    const status = message => document.getElementById('status').textContent = message || '';
    let vms = [];
    let selectedVmName = '';

    async function request(path, options) {
      const response = await fetch(api(path), {
        headers: { 'Content-Type': 'application/json' },
        ...options
      });
      if (!response.ok) {
        const text = await response.text();
        throw new Error(text || response.statusText);
      }
      return response.headers.get('content-type')?.includes('application/json') ? response.json() : response.text();
    }

    async function load() {
      vms = await request('/vms/overview');
      if (selectedVmName && !vms.some(vm => vm.name === selectedVmName)) {
        selectedVmName = '';
      }
      document.getElementById('vmRows').innerHTML = vms.map(vm => {
        const vmName = vm.name || '';
        const escapedName = escapeHtml(vmName);
        const isBaseVm = vmName.toUpperCase().endsWith('BASE');
        return `
        <tr class="${vmName === selectedVmName ? 'selected-row' : ''}" data-vm-name="${escapedName}">
          <td>${escapeHtml(vm.name)}<div class="muted">${escapeHtml(vm.vmxPath)}</div></td>
          <td>${escapeHtml(vm.workerId)}</td>
          <td>${renderEnabled(vm)}</td>
          <td>${escapeHtml(vm.currentProfileId || '')}<div class="muted">${escapeHtml(vm.currentSnapshotName || '')}</div></td>
          <td>
            <button data-action="edit-vm" data-vm-name="${escapedName}">Edit</button>
            ${isBaseVm ? `<button data-action="copy-vm" data-vm-name="${escapedName}">Copy</button>` : ''}
            ${vm.isQuarantined
              ? `<button data-action="unquarantine-vm" data-vm-name="${escapedName}">Unquarantine</button>`
              : `<button data-action="quarantine-vm" data-vm-name="${escapedName}">Quarantine</button>`}
            ${isBaseVm ? '' : `<button class="danger" data-action="delete-vm" data-vm-name="${escapedName}">Delete</button>`}
          </td>
        </tr>`;
      }).join('');
      updateSelectedVmRow();
      renderSelectedProfiles();
    }

    function updateSelectedVmRow() {
      document.querySelectorAll('#vmRows tr[data-vm-name]').forEach(row => {
        row.classList.toggle('selected-row', row.dataset.vmName === selectedVmName);
      });
    }

    function showProfiles(vmName) {
      selectedVmName = vmName;
      document.getElementById('profileForm').elements.vmName.value = vmName;
      updateSelectedVmRow();
      renderSelectedProfiles();
    }

    function renderSelectedProfiles() {
      const vm = vms.find(item => item.name === selectedVmName);
      const rows = vm
        ? (vm.profiles || []).map(profile => `
        <tr>
          <td>${escapeHtml(vm.name)}</td>
          <td>${escapeHtml(profile.profileId)}</td>
          <td>${escapeHtml(profile.profileName)}</td>
          <td>${escapeHtml(snapshotForProfile(vm, profile))}</td>
          <td>${escapeHtml(formatDateTime(profile.updatedAt))}</td>
          <td>
            <button data-action="edit-profile" data-vm-name="${escapeHtml(vm.name)}" data-profile-id="${escapeHtml(profile.profileId)}">Edit</button>
            <button class="danger" data-action="delete-profile" data-vm-name="${escapeHtml(vm.name)}" data-profile-id="${escapeHtml(profile.profileId)}">Delete</button>
          </td>
        </tr>`)
        : [];

      document.getElementById('profileRows').innerHTML = rows.join('');
    }

    document.getElementById('vmRows').addEventListener('click', async event => {
      const target = event.target;
      if (!(target instanceof Element)) return;
      const button = target.closest('button[data-action]');
      if (!button) return;

      const vmName = button.dataset.vmName || '';
      const vm = vms.find(item => item.name === vmName);
      if (button.dataset.action === 'edit-vm' && vm) {
        selectedVmName = vmName;
        editVm(vm);
      } else if (button.dataset.action === 'copy-vm' && vm) {
        await copyVm(vm);
      } else if (button.dataset.action === 'quarantine-vm') {
        await quarantineVm(vmName, true);
      } else if (button.dataset.action === 'unquarantine-vm') {
        await quarantineVm(vmName, false);
      } else if (button.dataset.action === 'delete-vm') {
        await deleteVm(vmName);
      }
    });

    document.getElementById('profileRows').addEventListener('click', async event => {
      const target = event.target;
      if (!(target instanceof Element)) return;
      const button = target.closest('button[data-action]');
      if (!button) return;

      const vmName = button.dataset.vmName || '';
      const profileId = button.dataset.profileId || '';
      const vm = vms.find(item => item.name === vmName);
      const profile = vm?.profiles?.find(item => item.profileId === profileId);
      if (button.dataset.action === 'edit-profile' && profile) {
        editProfile(vmName, profile);
      } else if (button.dataset.action === 'delete-profile') {
        await deleteProfile(vmName, profileId);
      }
    });

    document.getElementById('vmForm').addEventListener('submit', async event => {
      event.preventDefault();
      const data = Object.fromEntries(new FormData(event.target).entries());
      data.enabled = event.target.elements.enabled.checked;
      data.profiles = [];
      await request('/vms', { method: 'POST', body: JSON.stringify(data) });
      status('VM saved. Restart required.');
      await load();
    });

    document.getElementById('profileForm').addEventListener('submit', async event => {
      event.preventDefault();
      const data = Object.fromEntries(new FormData(event.target).entries());
      await request(`/vms/${encodeURIComponent(data.vmName)}/profiles`, {
        method: 'POST',
        body: JSON.stringify({ profileId: data.profileId, profileName: data.profileName, snapshotName: data.snapshotName || '' })
      });
      status('Profile saved. Restart required.');
      selectedVmName = data.vmName;
      await load();
    });

    async function updateInitWorkerId() {
      const form = document.getElementById('vmForm');
      const vmName = form.elements.name?.value || '';
      if (!vmName) { status('Please save or select a VM first.'); return; }
      if (!confirm(`Update WorkerId in all snapshots for VM "${vmName}"?\n\nThis will revert each snapshot, start the VM, update rpa.init, then rebuild the snapshot.\nThe VM will be powered off when done.\nThis may take several minutes per profile.`)) return;

      const btn = document.getElementById('updateInitBtn');
      if (btn) { btn.disabled = true; btn.textContent = 'Updating...'; }
      status(`Updating WorkerId in snapshots for "${vmName}"...`);

      try {
        const result = await request(`/vms/${encodeURIComponent(vmName)}/update-init-workerid`, { method: 'POST' });
        const lines = (result.profiles || []).map(p =>
          `  ${p.profileId}: ${p.success ? `OK -> ${p.newSnapshotName}` : `FAILED (${p.errorCode}: ${p.errorMessage})`}`
        );
        status((result.success ? 'All profiles updated.' : `Partial failure: ${result.errorMessage}`) + (lines.length ? '\n' + lines.join('\n') : ''));
        await load();
      } catch (err) {
        status(`Update failed: ${err.message}`);
      } finally {
        if (btn) { btn.disabled = false; btn.textContent = '更新机器码'; }
      }
    }

    async function powerOnVm() {
      const form = document.getElementById('vmForm');
      const vmName = form.elements.name?.value || selectedVmName || '';
      if (!vmName) { status('Please save or select a VM first.'); return; }
      if (!confirm(`Power on or recover VM "${vmName}"?`)) return;

      const btn = document.getElementById('powerOnVmBtn');
      if (btn) { btn.disabled = true; btn.textContent = 'Processing...'; }
      status(`Checking VM "${vmName}"...`);
      try {
        const result = await request(`/vms/${encodeURIComponent(vmName)}/power-on`, { method: 'POST' });
        status(result.message || `VM action completed: ${result.action}`);
        await load();
      } catch (err) {
        status(`Power-on failed: ${err.message}`);
      } finally {
        if (btn) { btn.disabled = false; btn.textContent = '开启'; }
      }
    }

    async function updateVmSnapshots() {
      const form = document.getElementById('vmForm');
      const vmName = form.elements.name?.value || selectedVmName || '';
      if (!vmName) { status('Please save or select a VM first.'); return; }

      const vm = vms.find(item => item.name === vmName);
      const profiles = vm?.profiles || [];
      if (profiles.length === 0) {
        status(`VM "${vmName}" has no profiles to update.`);
        return;
      }

      if (!confirm(`Update all snapshots for VM "${vmName}"?\n\nThis will run the existing Upgrade Snapshot flow for ${profiles.length} profile(s), one after another.`)) return;

      const btn = document.getElementById('updateVmSnapshotsBtn');
      if (btn) { btn.disabled = true; btn.textContent = 'Updating...'; }

      const results = [];
      try {
        for (let index = 0; index < profiles.length; index++) {
          const profile = profiles[index];
          status(`Updating snapshots for "${vmName}" (${index + 1}/${profiles.length})...\n${profile.profileId}`);
          try {
            const result = await executeSnapshotUpdate(vmName, profile.profileId);
            results.push({ profileId: profile.profileId, success: true, newSnapshotName: result.newSnapshotName || '' });
          } catch (err) {
            results.push({ profileId: profile.profileId, success: false, errorMessage: err.message || String(err) });
          }
        }

        const successCount = results.filter(item => item.success).length;
        const lines = results.map(item =>
          `  ${item.profileId}: ${item.success ? `OK -> ${item.newSnapshotName}` : `FAILED (${item.errorMessage})`}`
        );
        status(`Snapshot updates completed for "${vmName}". Success: ${successCount}/${profiles.length}.` + (lines.length ? '\n' + lines.join('\n') : ''));
        await load();
      } finally {
        if (btn) { btn.disabled = false; btn.textContent = '升级快照'; }
      }
    }

    function editVm(vm) {
      const form = document.getElementById('vmForm');
      for (const [key, value] of Object.entries(vm)) {
        if (form.elements[key] && key !== 'profiles' && key !== 'enabled') form.elements[key].value = value || '';
      }
      form.elements.enabled.checked = vm.enabled !== false;
      showProfiles(vm.name);
    }

    function editProfile(vmName, profile) {
      const form = document.getElementById('profileForm');
      form.elements.vmName.value = vmName;
      form.elements.profileId.value = profile.profileId || '';
      form.elements.profileName.value = profile.profileName || '';
      form.elements.snapshotName.value = profile.snapshotName || '';
    }

    async function copyVm(vm) {
      const newName = prompt(`Copy VM "${vm.name}"\n\nEnter new VM name:`, '');
      if (newName === null) return;
      if (!newName.trim()) { status('VM name cannot be empty.'); return; }
      if (vms.some(v => v.name === newName.trim())) { status(`VM "${newName.trim()}" already exists.`); return; }
      const trimmedName = newName.trim();
      const newWorkerId = vm.workerId && vm.workerId.length > 4 ? vm.workerId.slice(0, -4) + 'COPY' : 'COPY';
      const newVm = {
        name: trimmedName,
        vmxPath: vm.vmxPath || '',
        baseSnapshotName: vm.baseSnapshotName || '',
        workerId: newWorkerId,
        guestUser: vm.guestUser || '',
        guestPasswordSecret: '',
        guestWorkPath: vm.guestWorkPath || '',
        guestBackupPaths: vm.guestBackupPaths || 'cache,db,file,logs',
        enabled: false,
        profiles: []
      };
      await request('/vms', { method: 'POST', body: JSON.stringify(newVm) });
      for (const profile of (vm.profiles || [])) {
        await request(`/vms/${encodeURIComponent(trimmedName)}/profiles`, {
          method: 'POST',
          body: JSON.stringify({ profileId: profile.profileId, profileName: profile.profileName, snapshotName: profile.snapshotName || '' })
        });
      }
      status(`VM copied as "${trimmedName}". Restart required.`);
      selectedVmName = trimmedName;
      await load();
    }

    async function deleteVm(name) {
      if (!confirm(`Delete VM ${name}?`)) return;
      await request(`/vms/${encodeURIComponent(name)}`, { method: 'DELETE' });
      status('VM deleted. Restart required.');
      if (selectedVmName === name) selectedVmName = '';
      await load();
    }

    async function quarantineVm(name, quarantined) {
      const action = quarantined ? 'quarantine' : 'unquarantine';
      if (!confirm(`${quarantined ? 'Quarantine' : 'Unquarantine'} VM ${name}?`)) return;
      await request(`/vms/${encodeURIComponent(name)}/${action}`, { method: 'POST' });
      status(quarantined ? 'VM quarantined.' : 'VM unquarantined.');
      await load();
    }

    async function deleteProfile(vmName, profileId) {
      if (!confirm(`Delete profile ${profileId}?`)) return;
      await request(`/vms/${encodeURIComponent(vmName)}/profiles/${encodeURIComponent(profileId)}`, { method: 'DELETE' });
      status('Profile deleted. Restart required.');
      selectedVmName = vmName;
      await load();
    }

    async function executeSnapshotUpdate(vmName, profileId) {
      return request(`/snapshots/${encodeURIComponent(vmName)}/${encodeURIComponent(profileId)}/update`, { method: 'POST' });
    }

    async function updateSnapshot(vmName, profileId) {
      if (!confirm(`Update snapshot for ${vmName} / ${profileId}?`)) return;
      status(`Updating snapshot for ${vmName} / ${profileId}...`);
      const result = await executeSnapshotUpdate(vmName, profileId);
      status(result.success ? `Snapshot updated: ${result.newSnapshotName || ''}` : `Snapshot update failed: ${result.errorCode || result.errorMessage || 'unknown error'}`);
    }

    async function updateSelectedSnapshot() {
      const form = document.getElementById('profileForm');
      const vmName = form.elements.vmName.value;
      const profileId = form.elements.profileId.value;
      if (!vmName || !profileId) {
        status('Select a profile first.');
        return;
      }

      await updateSnapshot(vmName, profileId);
    }

    async function switchSelectedSnapshot() {
      const form = document.getElementById('profileForm');
      const vmName = form.elements.vmName.value;
      const profileId = form.elements.profileId.value;
      if (!vmName || !profileId) {
        status('Select a profile first.');
        return;
      }

      if (!confirm(`Switch VM "${vmName}" to profile "${profileId}" snapshot?`)) return;

      status(`Switching snapshot for ${vmName} / ${profileId}...`);
      try {
        const result = await request(`/snapshots/${encodeURIComponent(vmName)}/${encodeURIComponent(profileId)}/switch`, { method: 'POST' });
        status(result.success ? `Snapshot switched. TxId: ${result.txId || ''}` : `Snapshot switch failed: ${result.errorCode || result.errorMessage || 'unknown error'}`);
        await load();
      } catch (err) {
        status(`Snapshot switch failed: ${err.message}`);
      }
    }

    function renderEnabled(vm) {
      const configStatus = vm.enabled === false
        ? '<span class="muted">Disabled</span>'
        : '<span>Enabled</span>';
      const quarantineStatus = vm.isQuarantined
        ? '<div class="muted">Quarantined</div>'
        : '';
      return configStatus + quarantineStatus;
    }

    function snapshotForProfile(vm, profile) {
      return profile.snapshotName || (profile.profileId === vm.currentProfileId ? vm.currentSnapshotName || '' : '');
    }

    function formatDateTime(value) {
      if (!value) return '';
      const date = new Date(value);
      return Number.isNaN(date.getTime()) ? value : date.toLocaleString();
    }

    function clearVmForm() { document.getElementById('vmForm').reset(); }
    function clearProfileForm() { document.getElementById('profileForm').reset(); }
    function escapeHtml(value) {
      return String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
    }

    load().catch(error => status(error.message));
  </script>
</body>
</html>
""";
    }

    private static async ValueTask<object?> ApiKeyFilter(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var options = context.HttpContext.RequestServices
            .GetRequiredService<IOptions<OperationsApiOptions>>().Value;

        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            var apiKey = context.HttpContext.Request.Headers["X-Api-Key"].FirstOrDefault()
                ?? context.HttpContext.Request.Query["apiKey"].FirstOrDefault();

            if (!string.Equals(apiKey, options.ApiKey, StringComparison.Ordinal))
            {
                return Results.Unauthorized();
            }
        }

        return await next(context);
    }
}
