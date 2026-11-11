namespace KeyPassXCCloudSync;

// Shared by Core and the UI. The prepared workspace is editable; only the
// immutable copies in data/conflicts/ must never be accepted as live results.
internal static class MergeCandidatePaths
{
    internal static bool IsImmutableOriginal(string dataDirectory,string candidate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);
        var immutableRoot=Path.GetFullPath(Path.Combine(dataDirectory,"conflicts"));
        var relative=Path.GetRelativePath(immutableRoot,Path.GetFullPath(candidate));
        return relative=="." || (!Path.IsPathRooted(relative) && relative!=".." &&
            !relative.StartsWith(".."+Path.DirectorySeparatorChar,StringComparison.Ordinal) &&
            !relative.StartsWith(".."+Path.AltDirectorySeparatorChar,StringComparison.Ordinal));
    }
}
