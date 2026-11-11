using System.Text;
using System.Text.RegularExpressions;
using KeyPassXCCloudSync;

internal static class Program
{
    private static int _passed;
    private static void Assert(bool condition,string message)
    {
        if(!condition)throw new InvalidOperationException(message);
    }
    private static async Task<(string Headers,string Body)> Response(bool success,string? language)
    {
        using var stream=new MemoryStream();
        await GoogleOAuthCallbackPage.SendAsync(stream,success,language,CancellationToken.None);
        var wire=Encoding.UTF8.GetString(stream.ToArray());
        var separator=wire.IndexOf("\r\n\r\n",StringComparison.Ordinal);
        Assert(separator>0,"Missing HTTP response delimiter");
        var headers=wire[..separator];
        var body=wire[(separator+4)..];
        var length=Regex.Match(headers,@"Content-Length: (\d+)");
        Assert(length.Success && int.Parse(length.Groups[1].Value)==Encoding.UTF8.GetByteCount(body),
            "HTTP content length is incorrect");
        Assert(headers.Contains("Cache-Control: no-store",StringComparison.Ordinal) &&
               headers.Contains("Referrer-Policy: no-referrer",StringComparison.Ordinal),
            "OAuth callback must not be cached or leak referrers");
        Assert(!wire.Contains("access_token",StringComparison.OrdinalIgnoreCase) &&
               !wire.Contains("refresh_token",StringComparison.OrdinalIgnoreCase),
            "OAuth tokens must never be in browser response");
        return (headers,body);
    }
    private static async Task Run(string name,Func<Task> test)
    {
        await TestReporter.RunAsync("OAuthCallbackTests",name,test);_passed++;
    }
    private static async Task Success()
    {
        var (headers,body)=await Response(true,"ru-RU,ru;q=0.9,en;q=0.8");
        Assert(headers.StartsWith("HTTP/1.1 200 OK",StringComparison.Ordinal),"Success status");
        Assert(body.Contains("Авторизация завершена",StringComparison.Ordinal),"Russian success locale");
        Assert(body.Contains("window.close()",StringComparison.Ordinal),"Tab close attempt is missing");
        Assert(body.Contains("history.replaceState",StringComparison.Ordinal),"OAuth code remains in history");
        Assert(body.Contains("закройте её вручную",StringComparison.Ordinal),"Browser fallback is missing");
        var match=Regex.Match(headers,@"script-src 'nonce-([^']+)'",RegexOptions.CultureInvariant);
        Assert(match.Success,"No CSP nonce");
        var nonce=match.Groups[1].Value;
        Assert(body.Contains("<script nonce=\""+nonce+"\">",StringComparison.Ordinal) &&
               body.Contains("<style nonce=\""+nonce+"\">",StringComparison.Ordinal),
               "CSP does not match generated scripts and styles");
        Assert(!body.Contains("__NONCE__",StringComparison.Ordinal),"Unresolved script nonce");
    }
    private static async Task Failure()
    {
        var (headers,body)=await Response(false,"uk-UA,uk;q=0.8");
        Assert(headers.StartsWith("HTTP/1.1 400 Bad Request",StringComparison.Ordinal),"Failure status");
        Assert(body.Contains("Авторизацію не завершено",StringComparison.Ordinal),"Ukrainian error locale");
        Assert(!body.Contains("window.close()",StringComparison.Ordinal),"Error tab should remain visible");
        Assert(body.Contains("history.replaceState",StringComparison.Ordinal),"Error callback code remains in browser history");
    }
    private static async Task English()
    {
        var (_,body)=await Response(true,"en-US,en;q=0.7");
        Assert(body.Contains("Authorization complete",StringComparison.Ordinal),"English locale");
    }
    private static async Task Fallback()
    {
        var (_,body)=await Response(true,null);
        Assert(body.Contains("Authorization complete",StringComparison.Ordinal),"Unknown language fallback");
    }
    private static Task EmbeddedDesktopClient()
    {
        Assert(GoogleOAuthClientDefaults.ClientId.EndsWith(".apps.googleusercontent.com",StringComparison.Ordinal) &&
               GoogleOAuthClientDefaults.ClientId.Length>".apps.googleusercontent.com".Length,
               "Published Google Desktop OAuth client ID is missing or malformed");
        Assert(GoogleOAuthClientDefaults.ClientSecret.StartsWith("GOCSPX-",StringComparison.Ordinal) &&
               GoogleOAuthClientDefaults.ClientSecret.Length>"GOCSPX-".Length,
               "Published Google Desktop OAuth client secret is missing or malformed");
        return Task.CompletedTask;
    }
    public static async Task<int> Main()
    {
        try
        {
            await Run("Successful callback and CSP",Success);
            await Run("Failed callback must not auto-close",Failure);
            await Run("English locale",English);
            await Run("Unknown language fallback",Fallback);
            await Run("Embedded Desktop OAuth client",EmbeddedDesktopClient);
            Console.WriteLine($"[SUMMARY] OAuthCallbackTests: {_passed}/5 passed.");
            return 0;
        }
        catch(Exception ex){TestReporter.Unhandled("OAuthCallbackTests",ex);return 1;}
    }
}
