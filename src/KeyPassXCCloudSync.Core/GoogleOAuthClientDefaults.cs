namespace KeyPassXCCloudSync;

// Published Google OAuth Desktop-app client identity. Google treats installed apps
// as public clients: these two values are embedded and are not confidential.
// Do not add refresh/access tokens, private user credentials or service-account keys.
internal static class GoogleOAuthClientDefaults
{
    internal const string ClientId = "380962712725-i5li1bhaimd0mreeufe0vto8fmtamg14.apps.googleusercontent.com";
    internal const string ClientSecret = "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";
}
