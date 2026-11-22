using KeyPassXCCloudSync;

internal static class Program
{
    private static int _passed;

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task Run(string name, Func<Task> test)
    {
        await TestReporter.RunAsync("OAuthCallbackTests", name, test);
        _passed++;
    }

    private static Task LocalizedResults()
    {
        foreach (var (language, success, failure) in new[]
        {
            ("ru", "Авторизация завершена", "Авторизация не завершена"),
            ("uk", "Авторизацію завершено", "Авторизацію не завершено"),
            ("en", "Authorization complete", "Authorization not completed")
        })
        {
            var successHtml = GoogleOAuthCallbackPage.Generate(language, false, true);
            var failureHtml = GoogleOAuthCallbackPage.Generate(language, false, false);
            Assert(successHtml.Contains(success, StringComparison.Ordinal),
                $"Missing {language} success translation");
            Assert(failureHtml.Contains(failure, StringComparison.Ordinal),
                $"Missing {language} failure translation");
            Assert(successHtml.Contains($"<html lang=\"{language}\">", StringComparison.Ordinal) &&
                   failureHtml.Contains($"<html lang=\"{language}\">", StringComparison.Ordinal),
                $"Wrong HTML language for {language}");
            Assert(successHtml.Contains("Google Drive · OAuth 2.0", StringComparison.Ordinal) &&
                   failureHtml.Contains("Google Drive · OAuth 2.0", StringComparison.Ordinal),
                $"Missing Google Drive branding for {language}");
        }
        return Task.CompletedTask;
    }

    private static Task ThemesAndOutcome()
    {
        var light = GoogleOAuthCallbackPage.Generate("en", false, true);
        var dark = GoogleOAuthCallbackPage.Generate("en", true, true);
        var failure = GoogleOAuthCallbackPage.Generate("en", true, false);
        Assert(light.Contains("color-scheme: light", StringComparison.Ordinal) &&
               light.Contains("#eef3fa", StringComparison.Ordinal), "Light theme missing");
        Assert(dark.Contains("color-scheme: dark", StringComparison.Ordinal) &&
               dark.Contains("#10151e", StringComparison.Ordinal), "Dark theme missing");
        Assert(dark.Contains("#48ccad", StringComparison.Ordinal) &&
               dark.Contains("CONNECTED", StringComparison.Ordinal), "Success state missing");
        Assert(failure.Contains("#ffa870", StringComparison.Ordinal) &&
               failure.Contains("NOT CONNECTED", StringComparison.Ordinal) &&
               failure.Contains("try again", StringComparison.Ordinal), "Failure state missing");
        Assert(!light.Equals(dark, StringComparison.Ordinal), "Theme switching has no effect");
        Assert(!dark.Equals(failure, StringComparison.Ordinal), "Result switching has no effect");
        return Task.CompletedTask;
    }

    private static Task StaticAndPrivate()
    {
        foreach (var language in new[] { "ru", "uk", "en", "auto", "<script>" })
        foreach (var dark in new[] { false, true })
        foreach (var succeeded in new[] { false, true })
        {
            var html = GoogleOAuthCallbackPage.Generate(language, dark, succeeded);
            Assert(html.StartsWith("<!doctype html>", StringComparison.Ordinal), "Not standalone HTML");
            Assert(html.Contains("<meta name=\"referrer\" content=\"no-referrer\">",
                       StringComparison.Ordinal), "Referrer privacy policy missing");
            Assert(html.Contains("<meta name=\"viewport\"", StringComparison.Ordinal),
                "Mobile viewport missing");
            Assert(!html.Contains("<script", StringComparison.OrdinalIgnoreCase) &&
                   !html.Contains("<iframe", StringComparison.OrdinalIgnoreCase) &&
                   !html.Contains("<img", StringComparison.OrdinalIgnoreCase) &&
                   !html.Contains("https://", StringComparison.OrdinalIgnoreCase) &&
                   !html.Contains("http://", StringComparison.OrdinalIgnoreCase) &&
                   !html.Contains("access_token", StringComparison.OrdinalIgnoreCase) &&
                   !html.Contains("refresh_token", StringComparison.OrdinalIgnoreCase) &&
                   !html.Contains("?code=", StringComparison.OrdinalIgnoreCase),
                "OAuth HTML must not contain scripts, remote resources, or credentials");
            Assert(!html.Contains("<script>", StringComparison.Ordinal) &&
                   !html.Contains("<html lang=\"<", StringComparison.Ordinal),
                "Caller-supplied language is not normalized");
        }
        Assert(GoogleOAuthCallbackPage.Generate("auto", false, true).Contains(
            "<html lang=\"en\">", StringComparison.Ordinal), "Unknown-language fallback");
        return Task.CompletedTask;
    }

    private static Task PackagePages()
    {
        // Compile-time guard for the new CloudTransport 1.0.4 callback-pages API.
        var pages = GoogleOAuthCallbackPage.Create("en", false);
        Assert(pages is not null, "Package callback pages were not created");
        return Task.CompletedTask;
    }

    private static Task EmbeddedDesktopClient()
    {
        Assert(GoogleOAuthClientDefaults.ClientId.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal) &&
               GoogleOAuthClientDefaults.ClientId.Length > ".apps.googleusercontent.com".Length,
               "Published Google Desktop OAuth client ID is missing or malformed");
        Assert(GoogleOAuthClientDefaults.ClientSecret.StartsWith("GOCSPX-", StringComparison.Ordinal) &&
               GoogleOAuthClientDefaults.ClientSecret.Length > "GOCSPX-".Length,
               "Published Google Desktop OAuth client secret is missing or malformed");
        return Task.CompletedTask;
    }

    public static async Task<int> Main()
    {
        try
        {
            await Run("All supported callback locales", LocalizedResults);
            await Run("Success/failure presentation and both themes", ThemesAndOutcome);
            await Run("No callback secrets or remote resources", StaticAndPrivate);
            await Run("CloudTransport callback-pages contract", PackagePages);
            await Run("Embedded Desktop OAuth client", EmbeddedDesktopClient);
            Console.WriteLine($"[SUMMARY] OAuthCallbackTests: {_passed}/5 passed.");
            return 0;
        }
        catch (Exception ex) { TestReporter.Unhandled("OAuthCallbackTests", ex); return 1; }
    }
}
