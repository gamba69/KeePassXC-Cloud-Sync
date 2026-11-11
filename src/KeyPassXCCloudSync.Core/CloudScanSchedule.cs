namespace KeyPassXCCloudSync;

internal static class CloudScanSchedule
{
    internal static bool IsDue(string cloudId,DateTimeOffset? nextPollUtc,
        DateTimeOffset nowUtc,bool fullSafetyScan,IReadOnlySet<string>? forcedCloudIds)
        =>fullSafetyScan || forcedCloudIds?.Contains(cloudId)==true ||
            nextPollUtc is null || nextPollUtc<=nowUtc;
}
