namespace KeyPassXCCloudSync;

// Normal protocol stages never imply degraded health. Only an actionable
// failure, a conflict, or an explicit user pause changes the tray/taskbar color.
internal static class CloudHealthPresentation
{
    internal static bool IsHealthy(string status)=>status is
        "SYNCED" or "TOPOLOGY_OK" or "BEHIND" or "SYNCING";

    internal static bool RequiresAttention(string status)=>status is
        "ERROR" or "AUTH_ERROR" or "NEEDS_AUTH" or "OFFLINE";

    internal static BrandStatus Severity(AppState? state,
        IReadOnlyList<ConfiguredCloudStatus> active,bool paused)
    {
        var failures=active.Count(x=>RequiresAttention(x.Status.State));
        if(state?.Conflict==true || state?.Result is "CONFLICT" or "ERROR" ||
           (active.Count>0 && failures==active.Count))return BrandStatus.Error;
        if(failures>0 || !string.IsNullOrWhiteSpace(state?.TopologyError) ||
           (paused && active.Count>0))return BrandStatus.Warning;
        // PARTIAL can describe in-flight reconciliation, not a failed replica.
        // SYNCING and BEHIND are normal stages, not warnings.
        return BrandStatus.Normal;
    }
}
