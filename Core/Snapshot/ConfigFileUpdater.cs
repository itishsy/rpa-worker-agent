using System.Text.Json;
using System.Text.Json.Nodes;

namespace Seebot.WorkerAgent.Core.Snapshot;

public sealed class ConfigFileUpdater : IConfigFileUpdater
{
    private readonly string _appSettingsPath;

    public ConfigFileUpdater(string appSettingsPath)
    {
        _appSettingsPath = appSettingsPath;
    }

    public async Task UpdateSnapshotNameAsync(
        string vmName,
        string profileId,
        string newSnapshotName,
        CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(_appSettingsPath, cancellationToken);
        var root = JsonNode.Parse(json)
            ?? throw new InvalidOperationException("appsettings.json parsed to null.");

        var vms = root["VirtualMachines"]?.AsArray()
            ?? throw new InvalidOperationException("VirtualMachines section missing.");

        foreach (var vmNode in vms)
        {
            if (vmNode is null)
            {
                continue;
            }

            var name = GetString(vmNode, "VmName") ?? GetString(vmNode, "Name") ?? "";
            if (!string.Equals(name, vmName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var workers = vmNode["Workers"]?.AsArray() ?? vmNode["Profiles"]?.AsArray();
            if (workers is null)
            {
                continue;
            }

            foreach (var worker in workers)
            {
                if (worker is null)
                {
                    continue;
                }

                var wProfileId = GetString(worker, "ProfileId") ?? "";
                if (!string.Equals(wProfileId, profileId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                worker["SnapshotName"] = newSnapshotName;
                break;
            }

            break;
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(_appSettingsPath, root.ToJsonString(options), cancellationToken);
    }

    private static string? GetString(JsonNode node, string key)
    {
        return node[key] is JsonValue val && val.TryGetValue<string>(out var s) ? s : null;
    }
}
