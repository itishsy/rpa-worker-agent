namespace Seebot.WorkerAgent.Core.Snapshot;

public static class SnapshotNameGenerator
{
    public static string Generate(string profileId, DateOnly date, IReadOnlyList<string> existingSnapshots)
    {
        var dateStr = date.ToString("yyMMdd");
        var prefix = $"{profileId}.v{dateStr}.";

        var maxNo = existingSnapshots
            .Where(s => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(s =>
            {
                var suffix = s[prefix.Length..];
                return int.TryParse(suffix, out var no) ? no : 0;
            })
            .DefaultIfEmpty(0)
            .Max();

        return $"{prefix}{maxNo + 1}";
    }
}
