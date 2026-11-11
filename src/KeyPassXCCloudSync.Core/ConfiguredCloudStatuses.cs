namespace KeyPassXCCloudSync;

/// <summary>
/// Projects the persistent replica-status cache onto the CURRENT local cloud
/// configuration. The cache intentionally retains historical names (including
/// removed and renamed connections); these must not count as active clouds or
/// generate tray warnings / authorization prompts.
/// </summary>
public sealed record ConfiguredCloudStatus(string Name, ReplicaStatus Status);

public static class ConfiguredCloudStatuses
{
    // Only a positive per-cloud acknowledgement of the current local head can
    // promote an old PARTIAL result to SYNCED. Green network health alone is
    // insufficient: a replica may still hold an older immutable KDBX version.
    public static bool AllConfirmedAtLocalHead(
        IEnumerable<string> configuredNames, AppState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if(state.Conflict || state.TopologyDisabled || state.TopologyRefreshNeeded ||
           string.IsNullOrWhiteSpace(state.LocalVersionId))return false;
        var live=Select(configuredNames,state);
        return live.Count>0 && live.All(cloud=>
            cloud.Status.State=="SYNCED" &&
            string.IsNullOrEmpty(cloud.Status.Error) &&
            string.Equals(cloud.Status.CurrentVersionId,state.LocalVersionId,
                StringComparison.Ordinal));
    }

    public static IReadOnlyList<ConfiguredCloudStatus> Select(
        IEnumerable<string> configuredNames, AppState? state)
    {
        ArgumentNullException.ThrowIfNull(configuredNames);
        var result=new List<ConfiguredCloudStatus>();
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var name in configuredNames)
        {
            if(!seen.Add(name))continue;
            ReplicaStatus? status=null;
            if(state is not null)
            {
                if(!state.Clouds.TryGetValue(name,out status))
                {
                    // Deserialized JSON dictionaries may have different case comparers.
                    foreach(var entry in state.Clouds)
                        if(string.Equals(entry.Key,name,StringComparison.OrdinalIgnoreCase))
                        {status=entry.Value;break;}
                }
            }
            result.Add(new ConfiguredCloudStatus(name,status??new ReplicaStatus()));
        }
        return result;
    }
}
