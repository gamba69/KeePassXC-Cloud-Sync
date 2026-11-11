namespace KeyPassXCCloudSync;

// Keep the displayed and diagnostic-report version tied to assembly metadata.
// Avoid hard-coded version strings in individual report writers.
internal static class BuildInfo
{
    internal static string Version =>
        typeof(BuildInfo).Assembly.GetName().Version?.ToString(3) ?? "unknown";
}
