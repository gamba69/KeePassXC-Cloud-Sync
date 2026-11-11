namespace KeyPassXCCloudSync;

// Proton Drive exposes a writable personal-files root. Users configure only the
// directory beneath it; executable discovery belongs to the platform adapter.
internal static class ProtonConfiguration
{
    internal const string Root = "/my-files";
}
