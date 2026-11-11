namespace KeyPassXCCloudSync;

// These are display-only names. Do not change persisted Provider identifiers:
// GoogleDrive, WebDAV, ProtonDrive, S3, and CloudflareR2.
internal static class CloudProviderNames
{
    internal static string Display(string provider) => provider switch
    {
        "WebDAV" => "WebDAV Storage",
        "S3" or "CloudflareR2" => "S3 Storage",
        "GoogleDrive" => "Google Drive",
        "ProtonDrive" => "Proton Drive",
        _ => provider
    };
}
