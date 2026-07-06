using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Seebot.WorkerAgent.Core.Configuration;
using Seebot.WorkerAgent.Core.Snapshot;
using Seebot.WorkerAgent.Core.Storage;

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
    main { padding: 20px 24px; display: grid; gap: 18px; grid-template-columns: minmax(360px, 520px) 1fr; }
    section { background: white; border: 1px solid #d8dee8; border-radius: 6px; padding: 16px; }
    h1 { font-size: 20px; margin: 0; }
    h2 { font-size: 16px; margin: 0 0 12px; }
    label { display: block; font-size: 12px; font-weight: 600; margin: 10px 0 4px; }
    input { box-sizing: border-box; width: 100%; padding: 8px; border: 1px solid #c8d0dc; border-radius: 4px; font: inherit; }
    button { padding: 8px 11px; border: 1px solid #9aa7b8; border-radius: 4px; background: #fff; cursor: pointer; }
    button.primary { background: #2563eb; color: white; border-color: #2563eb; }
    button.danger { color: #b91c1c; border-color: #f0b4b4; }
    table { width: 100%; border-collapse: collapse; font-size: 13px; }
    th, td { border-bottom: 1px solid #e5e7eb; padding: 8px; text-align: left; vertical-align: top; }
    .row { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; margin-top: 12px; }
    .status { min-height: 20px; font-size: 13px; color: #475569; }
    .muted { color: #64748b; font-size: 12px; }
    @media (max-width: 900px) { main { grid-template-columns: 1fr; } }
  </style>
</head>
<body>
  <header><h1>Worker Agent VM Profiles</h1></header>
  <main>
    <section>
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
        <div class="row">
          <button class="primary" type="submit">Save VM</button>
          <button type="button" onclick="clearVmForm()">Clear</button>
        </div>
      </form>
      <p class="muted">Changes are saved to local SQLite. Restart the service to apply runtime changes.</p>
      <div id="status" class="status"></div>
    </section>
    <section>
      <h2>Registered VMs</h2>
      <table>
        <thead><tr><th>VM</th><th>Worker</th><th>Profiles</th><th>Actions</th></tr></thead>
        <tbody id="vmRows"></tbody>
      </table>
    </section>
    <section>
      <h2>Profile</h2>
      <form id="profileForm">
        <label>VM Name</label><input name="vmName" required>
        <label>Profile ID</label><input name="profileId" required>
        <label>Profile Name</label><input name="profileName" required>
        <div class="row">
          <button class="primary" type="submit">Save Profile</button>
          <button type="button" onclick="clearProfileForm()">Clear</button>
        </div>
      </form>
    </section>
    <section>
      <h2>Profiles</h2>
      <table>
        <thead><tr><th>VM</th><th>Profile ID</th><th>Profile Name</th><th>Actions</th></tr></thead>
        <tbody id="profileRows"></tbody>
      </table>
    </section>
  </main>
  <script>
    const qs = new URLSearchParams(location.search);
    const apiKey = qs.get('apiKey') || '';
    const api = path => '/operations' + path + (apiKey ? (path.includes('?') ? '&' : '?') + 'apiKey=' + encodeURIComponent(apiKey) : '');
    const status = message => document.getElementById('status').textContent = message || '';

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
      const vms = await request('/vms');
      document.getElementById('vmRows').innerHTML = vms.map(vm => `
        <tr>
          <td>${escapeHtml(vm.name)}<div class="muted">${escapeHtml(vm.vmxPath)}</div></td>
          <td>${escapeHtml(vm.workerId)}</td>
          <td>${renderVmProfiles(vm)}</td>
          <td>
            <button onclick='editVm(${JSON.stringify(vm)})'>Edit</button>
            <button class="danger" onclick='deleteVm(${JSON.stringify(vm.name)})'>Delete</button>
          </td>
        </tr>`).join('');
      document.getElementById('profileRows').innerHTML = vms.flatMap(vm => (vm.profiles || []).map(profile => `
        <tr>
          <td>${escapeHtml(vm.name)}</td>
          <td>${escapeHtml(profile.profileId)}</td>
          <td>${escapeHtml(profile.profileName)}</td>
          <td>
            <button onclick='editProfile(${JSON.stringify(vm.name)}, ${JSON.stringify(profile)})'>Edit</button>
            <button class="primary" onclick='updateSnapshot(${JSON.stringify(vm.name)}, ${JSON.stringify(profile.profileId)})'>Update Snapshot</button>
            <button class="danger" onclick='deleteProfile(${JSON.stringify(vm.name)}, ${JSON.stringify(profile.profileId)})'>Delete</button>
          </td>
        </tr>`)).join('');
    }

    document.getElementById('vmForm').addEventListener('submit', async event => {
      event.preventDefault();
      const data = Object.fromEntries(new FormData(event.target).entries());
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
        body: JSON.stringify({ profileId: data.profileId, profileName: data.profileName })
      });
      status('Profile saved. Restart required.');
      await load();
    });

    function editVm(vm) {
      const form = document.getElementById('vmForm');
      for (const [key, value] of Object.entries(vm)) {
        if (form.elements[key] && key !== 'profiles') form.elements[key].value = value || '';
      }
    }

    function editProfile(vmName, profile) {
      const form = document.getElementById('profileForm');
      form.elements.vmName.value = vmName;
      form.elements.profileId.value = profile.profileId || '';
      form.elements.profileName.value = profile.profileName || '';
    }

    async function deleteVm(name) {
      if (!confirm(`Delete VM ${name}?`)) return;
      await request(`/vms/${encodeURIComponent(name)}`, { method: 'DELETE' });
      status('VM deleted. Restart required.');
      await load();
    }

    async function deleteProfile(vmName, profileId) {
      if (!confirm(`Delete profile ${profileId}?`)) return;
      await request(`/vms/${encodeURIComponent(vmName)}/profiles/${encodeURIComponent(profileId)}`, { method: 'DELETE' });
      status('Profile deleted. Restart required.');
      await load();
    }

    async function updateSnapshot(vmName, profileId) {
      if (!confirm(`Update snapshot for ${vmName} / ${profileId}?`)) return;
      status(`Updating snapshot for ${vmName} / ${profileId}...`);
      const result = await request(`/snapshots/${encodeURIComponent(vmName)}/${encodeURIComponent(profileId)}/update`, { method: 'POST' });
      status(result.success ? `Snapshot updated: ${result.newSnapshotName || ''}` : `Snapshot update failed: ${result.errorCode || result.errorMessage || 'unknown error'}`);
    }

    function renderVmProfiles(vm) {
      const profiles = vm.profiles || [];
      if (profiles.length === 0) return '<span class="muted">No profiles</span>';
      return profiles.map(profile => `
        <div class="row">
          <span>${escapeHtml(profile.profileId)}</span>
          <button class="primary" onclick='updateSnapshot(${JSON.stringify(vm.name)}, ${JSON.stringify(profile.profileId)})'>Update Snapshot</button>
        </div>`).join('');
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
