using System.Globalization;

namespace KeyPassXCCloudSync;

/// <summary>
/// Human-readable names are for NEW, isolated merge workspaces only. The
/// immutable protocol copies in conflicts/ retain their VersionId.kdbx names.
/// The timestamp is taken from version metadata (displayed in local time),
/// not from file creation time or the merge-preparation time.
/// </summary>
internal static class MergeWorkspaceFileNames
{
    internal sealed record Prepared(ActiveConflictFiles.Entry Version, string FileName);

    internal static Prepared[] Plan(IEnumerable<ActiveConflictFiles.Entry> entries)
    {
        var ordered=entries.OrderBy(v=>v.CreatedUtc)
            .ThenBy(v=>v.VersionId,StringComparer.Ordinal).ToArray();
        if(ordered.Any(v=>!v.Ready || v.CreatedUtc is null || v.CreatedUtc.Value==default ||
            !Guid.TryParseExact(v.VersionId,"N",out _)))
            throw new SyncSafetyException("Current conflict has missing or invalid version metadata");
        return ordered.Select((v,index)=>
        {
            var timestamp=v.CreatedUtc!.Value.ToLocalTime().ToString(
                "yyyy-MM-dd-HH-mm-ss",CultureInfo.InvariantCulture);
            var origin=v.Source switch
            {
                "local"=>"Local",
                "merge"=>"Merge",
                { } value when value.StartsWith("external:",StringComparison.Ordinal)=>"External",
                _=>"Other"
            };
            // The ordinal guarantees uniqueness even if two versions share
            // the same local timestamp, origin, or a DST-ambiguous clock time.
            return new Prepared(v,$"Conflict-{timestamp}-{origin}-{index+1:D2}.kdbx");
        }).ToArray();
    }
}
