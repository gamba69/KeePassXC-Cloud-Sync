using System.Security.Cryptography;
using System.Text;

namespace KeyPassXCCloudSync;

/// <summary>
/// Local, ephemeral loopback OAuth callback response. No remote resources or third-party scripts,
/// tokens, authorization codes or the OAuth query string are included in its body.
/// Browsers decide whether window.close() is allowed; the fallback is a safe page.
/// </summary>
internal static class GoogleOAuthCallbackPage
{
    private static string Language(string? acceptLanguage)
    {
        if(string.IsNullOrWhiteSpace(acceptLanguage))return "en";
        foreach(var item in acceptLanguage.Split(','))
        {
            var tag=item.Split(';',2)[0].Trim();
            if(tag.Equals("ru",StringComparison.OrdinalIgnoreCase) || tag.StartsWith("ru-",StringComparison.OrdinalIgnoreCase))return "ru";
            if(tag.Equals("uk",StringComparison.OrdinalIgnoreCase) || tag.StartsWith("uk-",StringComparison.OrdinalIgnoreCase))return "uk";
            if(tag.Equals("en",StringComparison.OrdinalIgnoreCase) || tag.StartsWith("en-",StringComparison.OrdinalIgnoreCase))return "en";
        }
        return "en";
    }

    internal static async Task SendAsync(Stream stream,bool success,string? acceptLanguage,CancellationToken ct)
    {
        var language=Language(acceptLanguage);
        var (title,detail)=language switch
        {
            "ru" when success => ("Авторизация завершена", "Можно вернуться в KeyPassXC Cloud Sync. Если вкладка не закрылась автоматически, закройте её вручную."),
            "ru" => ("Авторизация не завершена", "Вернитесь в KeyPassXC Cloud Sync и проверьте сообщение об ошибке."),
            "uk" when success => ("Авторизацію завершено", "Можна повернутися до KeyPassXC Cloud Sync. Якщо вкладка не закрилася автоматично, закрийте її вручну."),
            "uk" => ("Авторизацію не завершено", "Поверніться до KeyPassXC Cloud Sync і перевірте повідомлення про помилку."),
            "en" when success => ("Authorization complete", "Return to KeyPassXC Cloud Sync. If this tab does not close automatically, you can close it yourself."),
            _ => ("Authorization incomplete", "Return to KeyPassXC Cloud Sync and check the error message.")
        };
        // CSP nonce prevents any other inline script from being executed.
        var nonce=Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        // Clear the callback's one-time code from browser history even on error.
        // Only the successful response attempts to close the tab.
        var script="""
<script nonce="__NONCE__">
'use strict';
try { window.history.replaceState(null, '', window.location.pathname); } catch (_) { }
__CLOSE__
</script>
""".Replace("__CLOSE__",success
            ? "window.setTimeout(() => { window.close(); }, 250);"
            : string.Empty,StringComparison.Ordinal);
        var html="""
<!doctype html>
<html lang="__LANG__">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>KeyPassXC Cloud Sync</title>
<style nonce="__NONCE__">
:root{color-scheme:light dark;font-family:system-ui,-apple-system,Segoe UI,sans-serif}
body{display:grid;place-items:center;min-height:90vh;margin:0;background:#f4f7fa;color:#243142}
main{box-sizing:border-box;width:min(440px,calc(100vw - 32px));padding:28px;border:1px solid #d6dfe7;border-radius:14px;background:white;box-shadow:0 8px 30px #0001}
h1{font-size:1.25rem;margin:0 0 12px}p{line-height:1.5;margin:0;color:#52647a}
@media(prefers-color-scheme:dark){body{background:#111820;color:#edf2f8}main{background:#1c2735;border-color:#334457}p{color:#bfd0e4}}
</style>
</head>
<body><main><h1>__TITLE__</h1><p>__DETAIL__</p></main>
__SCRIPT__
</body>
</html>
""".Replace("__LANG__",language,StringComparison.Ordinal)
    .Replace("__TITLE__",title,StringComparison.Ordinal)
    .Replace("__DETAIL__",detail,StringComparison.Ordinal)
    .Replace("__SCRIPT__",script,StringComparison.Ordinal)
    .Replace("__NONCE__",nonce,StringComparison.Ordinal);
        var body=Encoding.UTF8.GetBytes(html);
        var status=success?"200 OK":"400 Bad Request";
        var headers=$"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\n"+
            $"Content-Length: {body.Length}\r\nCache-Control: no-store\r\nPragma: no-cache\r\n"+
            "Referrer-Policy: no-referrer\r\nX-Content-Type-Options: nosniff\r\n"+
            $"Content-Security-Policy: default-src 'none'; script-src 'nonce-{nonce}'; style-src 'nonce-{nonce}'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'\r\n"+
            "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers),ct);
        await stream.WriteAsync(body,ct);
        await stream.FlushAsync(ct);
    }
}
