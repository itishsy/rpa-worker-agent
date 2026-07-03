using System.Text.RegularExpressions;
using Seebot.WorkerAgent.Core.Configuration;

namespace Seebot.WorkerAgent.Core.Snapshot;

public sealed class ProfileSnapshotResolver : IProfileSnapshotResolver
{
    public ProfileSnapshotResolution Resolve(
        VirtualMachineOptions vm,
        ProfileOptions profile,
        IReadOnlyList<string> snapshots)
    {
        if (string.IsNullOrWhiteSpace(profile.ProfileId))
        {
            return new ProfileSnapshotResolution
            {
                Status = ProfileSnapshotResolutionStatus.InvalidProfileId,
                Message = "ProfileId is required."
            };
        }

        var matched = snapshots
            .Where(snapshot => IsProfileSnapshotName(profile.ProfileId, snapshot))
            .ToArray();

        return matched.Length switch
        {
            0 => new ProfileSnapshotResolution
            {
                Status = ProfileSnapshotResolutionStatus.Missing,
                Message = $"No snapshot matching profile '{profile.ProfileId}' was returned by vmrun listSnapshots."
            },
            1 => new ProfileSnapshotResolution
            {
                Status = ProfileSnapshotResolutionStatus.Ready,
                SnapshotName = matched[0],
                MatchedSnapshotNames = matched
            },
            _ => new ProfileSnapshotResolution
            {
                Status = ProfileSnapshotResolutionStatus.Duplicate,
                MatchedSnapshotNames = matched,
                Message = $"Multiple snapshots match profile '{profile.ProfileId}': {string.Join(", ", matched)}."
            }
        };
    }

    public string? ResolveProfileId(VirtualMachineOptions vm, string? snapshotName)
    {
        if (string.IsNullOrWhiteSpace(snapshotName))
        {
            return null;
        }

        return vm.Profiles
            .FirstOrDefault(profile => IsProfileSnapshotName(profile.ProfileId, snapshotName))
            ?.ProfileId;
    }

    public bool IsProfileSnapshotName(string profileId, string snapshotName)
    {
        if (string.IsNullOrWhiteSpace(profileId) || string.IsNullOrWhiteSpace(snapshotName))
        {
            return false;
        }

        var pattern = "^" + Regex.Escape(profileId) + @"\.v\d{6}\.\d+$";
        return Regex.IsMatch(snapshotName, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
