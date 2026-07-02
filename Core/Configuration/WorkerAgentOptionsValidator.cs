using System.Text.RegularExpressions;

namespace Seebot.WorkerAgent.Core.Configuration;

public static class WorkerAgentOptionsValidator
{
    private static readonly Regex VersionedSnapshotSuffix =
        new(@"^.+\.v\d{6}\.\d+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static ValidationResult Validate(WorkerAgentOptions options)
    {
        var errors = new List<string>();

        Require(options.Agent.HostId, "Agent.HostId", errors);
        Require(options.Agent.HostWorkPath, "Agent.HostWorkPath", errors);
        Require(options.Vmrun.VmrunPath, "Vmrun.VmrunPath", errors);

        var workerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var vmIndex = 0; vmIndex < options.VirtualMachines.Count; vmIndex++)
        {
            var vm = options.VirtualMachines[vmIndex];
            var vmPath = $"VirtualMachines[{vmIndex}]";

            Require(vm.Name, $"{vmPath}.Name", errors);
            Require(vm.VmxPath, $"{vmPath}.VmxPath", errors);
            Require(vm.BaseSnapshotName, $"{vmPath}.BaseSnapshotName", errors);
            Require(vm.WorkerId, $"{vmPath}.WorkerId", errors);
            Require(vm.GuestWorkPath, $"{vmPath}.GuestWorkPath", errors);

            if (!string.IsNullOrWhiteSpace(vm.WorkerId) && !workerIds.Add(vm.WorkerId))
            {
                errors.Add($"{vmPath}.WorkerId must be unique across the host.");
            }

            ValidateGuestBackupPaths(vm.GuestBackupPaths, vmPath, errors);
            ValidateProfiles(vm.Profiles, vmPath, errors);
        }

        return new ValidationResult(errors);
    }

    private static void ValidateGuestBackupPaths(string paths, string vmPath, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(paths))
        {
            errors.Add($"{vmPath}.GuestBackupPaths is required.");
            return;
        }

        var names = paths
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();
        if (names.Count == 0)
        {
            errors.Add($"{vmPath}.GuestBackupPaths must contain at least one directory name.");
        }
    }

    private static void ValidateProfiles(IReadOnlyList<ProfileOptions> profiles, string vmPath, List<string> errors)
    {
        var profileIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var profileIndex = 0; profileIndex < profiles.Count; profileIndex++)
        {
            var profile = profiles[profileIndex];
            var profilePath = $"{vmPath}.Profiles[{profileIndex}]";

            Require(profile.ProfileId, $"{profilePath}.ProfileId", errors);
            Require(profile.ProfileName, $"{profilePath}.ProfileName", errors);
            Require(profile.SnapshotName, $"{profilePath}.SnapshotName", errors);
            if (!string.IsNullOrWhiteSpace(profile.SnapshotName))
            {
                var expectedPrefix = profile.ProfileId + ".v";
                if (!VersionedSnapshotSuffix.IsMatch(profile.SnapshotName)
                    || !profile.SnapshotName.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"{profilePath}.SnapshotName must match format ProfileId.vYYMMDD.No (e.g. {profile.ProfileId}.v260624.1).");
                }
            }

            if (!string.IsNullOrWhiteSpace(profile.ProfileId) && !profileIds.Add(profile.ProfileId))
            {
                errors.Add($"{profilePath}.ProfileId must be unique inside the VM.");
            }
        }
    }

    private static void Require(string value, string name, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{name} is required.");
        }
    }
}
