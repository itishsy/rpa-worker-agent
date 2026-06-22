namespace Seebot.WorkerAgent.Core.Configuration;

public static class WorkerAgentOptionsValidator
{
    private const int RunnerControlPort = 9090;

    public static ValidationResult Validate(WorkerAgentOptions options)
    {
        var errors = new List<string>();

        Require(options.Agent.HostId, "Agent.HostId", errors);
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
            Require(vm.RunnerStatusUrl, $"{vmPath}.RunnerStatusUrl", errors);
            Require(vm.RunnerKillUrl, $"{vmPath}.RunnerKillUrl", errors);
            Require(vm.HostWorkPath, $"{vmPath}.HostWorkPath", errors);

            if (!string.IsNullOrWhiteSpace(vm.WorkerId) && !workerIds.Add(vm.WorkerId))
            {
                errors.Add($"{vmPath}.WorkerId must be unique across the host.");
            }

            RequireRunnerPort(vm.RunnerStatusUrl, $"{vmPath}.RunnerStatusUrl", errors);
            RequireRunnerPort(vm.RunnerKillUrl, $"{vmPath}.RunnerKillUrl", errors);
            ValidateGuestBackupPaths(vm.GuestBackupPaths, vmPath, errors);
            ValidateProfiles(vm.Profiles, vmPath, errors);
        }

        return new ValidationResult(errors);
    }

    private static void ValidateGuestBackupPaths(GuestBackupPathsOptions paths, string vmPath, List<string> errors)
    {
        Require(paths.Cache, $"{vmPath}.GuestBackupPaths.Cache", errors);
        Require(paths.Db, $"{vmPath}.GuestBackupPaths.Db", errors);
        Require(paths.File, $"{vmPath}.GuestBackupPaths.File", errors);
        Require(paths.Logs, $"{vmPath}.GuestBackupPaths.Logs", errors);
    }

    private static void ValidateProfiles(IReadOnlyList<ProfileOptions> profiles, string vmPath, List<string> errors)
    {
        var profileIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var profileIndex = 0; profileIndex < profiles.Count; profileIndex++)
        {
            var profile = profiles[profileIndex];
            var profilePath = $"{vmPath}.Profiles[{profileIndex}]";

            Require(profile.ProfileId, $"{profilePath}.ProfileId", errors);
            Require(profile.SnapshotName, $"{profilePath}.SnapshotName", errors);

            if (!string.IsNullOrWhiteSpace(profile.ProfileId) && !profileIds.Add(profile.ProfileId))
            {
                errors.Add($"{profilePath}.ProfileId must be unique inside the VM.");
            }
        }
    }

    private static void RequireRunnerPort(string url, string name, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            errors.Add($"{name} must be an absolute URL using port {RunnerControlPort}.");
            return;
        }

        if (uri.Port != RunnerControlPort)
        {
            errors.Add($"{name} must use port {RunnerControlPort}.");
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
