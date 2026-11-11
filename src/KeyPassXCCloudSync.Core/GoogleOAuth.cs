using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KeyPassXCCloudSync;

// Desktop OAuth authorization-code + PKCE flow, tokens protected by the OS secret store.
public sealed class GoogleOAuth(Config cfg,HttpClient client)
{
    private string? _accessToken;
    private DateTimeOffset _expires;
    private readonly SemaphoreSlim _gate=new(1,1);
    private const string TokenEndpoint="https://oauth2.googleapis.com/token";
    private static string Base64Url(byte[] bytes)=>Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');
    private string Secret()
    {
        if(string.IsNullOrWhiteSpace(cfg.GoogleClientId))
            throw new SyncSafetyException("Google OAuth publisher client is not configured; use a custom Desktop client or build with publisher credentials");
        if(string.IsNullOrWhiteSpace(cfg.GoogleClientSecretCredential))
        {
            if(!string.Equals(cfg.GoogleClientId,GoogleOAuthClientDefaults.ClientId,StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(GoogleOAuthClientDefaults.ClientSecret))
                throw new SyncSafetyException("Google OAuth client secret missing; configure the publisher's client or your own Desktop client");
            return GoogleOAuthClientDefaults.ClientSecret;
        }
        var credential=PlatformServices.Current.Secrets.Read(cfg.GoogleClientSecretCredential);
        if(!string.Equals(credential.User,cfg.GoogleClientId,StringComparison.Ordinal))
            throw new SyncSafetyException("Google OAuth client ID differs from stored Desktop client secret");
        return credential.Password;
    }
    public async Task<string> TokenAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if(_accessToken!=null && _expires>DateTimeOffset.UtcNow.AddSeconds(90))return _accessToken;
            var stored=PlatformServices.Current.Secrets.Read(cfg.GoogleRefreshCredential);
            if(!string.Equals(stored.User,cfg.GoogleClientId,StringComparison.Ordinal))
                throw new SyncSafetyException("Google refresh-token credential belongs to another OAuth client");
            var refresh=stored.Password;
            using var doc=await ExchangeAsync(new Dictionary<string,string> {
                ["client_id"]=cfg.GoogleClientId,["client_secret"]=Secret(),
                ["refresh_token"]=refresh,["grant_type"]="refresh_token"
            },ct);
            StoreAccess(doc.RootElement);
            await VerifyAccountAsync(ct);
            if(doc.RootElement.TryGetProperty("refresh_token",out var refreshed) && refreshed.GetString() is {Length:>0} newRefresh)
                PlatformServices.Current.Secrets.Write(cfg.GoogleRefreshCredential,cfg.GoogleClientId,newRefresh);
            return _accessToken!;
        }
        finally { _gate.Release(); }
    }
    private async Task<string> VerifyAccountAsync(CancellationToken ct)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,"https://www.googleapis.com/drive/v3/about?fields=user(emailAddress)");
        request.Headers.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",_accessToken);
        using var response=await client.SendAsync(request,ct);
        if(!response.IsSuccessStatusCode)
            throw new SyncSafetyException($"Cannot confirm Google Drive account (HTTP {(int)response.StatusCode})");
        using var doc=JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var account=doc.RootElement.GetProperty("user").GetProperty("emailAddress").GetString();
        if(string.IsNullOrWhiteSpace(account))throw new SyncSafetyException("Google did not return an account address");
        if(cfg.GoogleAccountLabel.Length>0 && !string.Equals(account,cfg.GoogleAccountLabel,StringComparison.OrdinalIgnoreCase))
            throw new SyncSafetyException("Google Drive account differs from configured account; refusing to synchronize");
        return account;
    }
    private void StoreAccess(JsonElement root)
    {
        _accessToken=root.GetProperty("access_token").GetString() ?? throw new SyncSafetyException("OAuth token response incomplete");
        var seconds=root.TryGetProperty("expires_in",out var n)?n.GetInt32():3600;
        _expires=DateTimeOffset.UtcNow.AddSeconds(seconds);
    }
    private async Task<JsonDocument> ExchangeAsync(Dictionary<string,string> fields,CancellationToken ct)
    {
        using var request=new HttpRequestMessage(HttpMethod.Post,TokenEndpoint);
        request.Content=new FormUrlEncodedContent(fields);
        using var reply=await client.SendAsync(request,ct);
        if(!reply.IsSuccessStatusCode)
            throw new SyncSafetyException($"Google OAuth failed (HTTP {(int)reply.StatusCode}); check credentials and consent");
        return JsonDocument.Parse(await reply.Content.ReadAsStreamAsync(ct));
    }
    private static async Task ReplyBestEffortAsync(Stream stream,bool success,string? acceptLanguage)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await GoogleOAuthCallbackPage.SendAsync(stream,success,acceptLanguage,timeout.Token); }
        catch(Exception ex) when(ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        { /* The browser may already have closed the tab. */ }
    }

    public async Task<string> LoginAsync(CancellationToken ct)
    {
        var verifier=Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge=Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state=Base64Url(RandomNumberGenerator.GetBytes(24));
        var tcp=new TcpListener(IPAddress.Loopback,0);
        tcp.Start(1);
        try
        {
        var port=((IPEndPoint)tcp.LocalEndpoint).Port;
        string redirect=$"http://127.0.0.1:{port}/";
        string scope="https://www.googleapis.com/auth/drive.file";
        var url="https://accounts.google.com/o/oauth2/v2/auth?"+
            "response_type=code&access_type=offline&prompt=consent"+
            "&client_id="+Uri.EscapeDataString(cfg.GoogleClientId)+
            "&redirect_uri="+Uri.EscapeDataString(redirect)+
            "&scope="+Uri.EscapeDataString(scope)+
            "&code_challenge_method=S256&code_challenge="+Uri.EscapeDataString(challenge)+
            "&state="+Uri.EscapeDataString(state);
        PlatformServices.Current.Browser.Open(url);
        using var limit=CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromMinutes(5));
        using var clientSocket=await tcp.AcceptTcpClientAsync(limit.Token);
        using var stream=clientSocket.GetStream();
        using var reader=new StreamReader(stream,Encoding.ASCII,leaveOpen:true);
        var line=await reader.ReadLineAsync(limit.Token) ?? throw new SyncSafetyException("OAuth redirect disconnected");
        if(line.Length>8192 || !line.StartsWith("GET /?",StringComparison.Ordinal))
            throw new SyncSafetyException("Unexpected OAuth callback");
        // Consume HTTP headers with a hard cap; Accept-Language is used only to
        // display a localized confirmation page, never for authentication.
        string? acceptLanguage=null;
        for(int i=0;i<64;i++)
        {
            var header=await reader.ReadLineAsync(limit.Token);
            if(header is null or "")break;
            if(header.Length>8192)throw new SyncSafetyException("Invalid OAuth header");
            if(header.StartsWith("Accept-Language:",StringComparison.OrdinalIgnoreCase))
                acceptLanguage=header["Accept-Language:".Length..].Trim();
        }
        var target=line.Split(' ')[1];
        var query=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var pair in target.Split('?',2).Last().Split('&'))
        {
            var k=pair.Split('=',2);
            if(k.Length==2)query[Uri.UnescapeDataString(k[0])]=Uri.UnescapeDataString(k[1].Replace('+',' '));
        }
        if(!query.TryGetValue("state",out var returned)||returned!=state)
        {
            await ReplyBestEffortAsync(stream,false,acceptLanguage);
            throw new SyncSafetyException("Google OAuth state mismatch");
        }
        if(!query.TryGetValue("code",out var code)||string.IsNullOrWhiteSpace(code))
        {
            await ReplyBestEffortAsync(stream,false,acceptLanguage);
            throw new SyncSafetyException("Google OAuth authorization was denied");
        }
        string account;
        try
        {
            // Do not tell the browser authorization succeeded until the token has
            // been exchanged, the account verified and the refresh token persisted.
            using var token=await ExchangeAsync(new Dictionary<string,string> {
                ["client_id"]=cfg.GoogleClientId,["client_secret"]=Secret(),
                ["redirect_uri"]=redirect,["code"]=code,["code_verifier"]=verifier,
                ["grant_type"]="authorization_code"
            },ct);
            StoreAccess(token.RootElement);
            account=await VerifyAccountAsync(ct);
            var refresh=token.RootElement.TryGetProperty("refresh_token",out var r)?r.GetString():null;
            if(string.IsNullOrWhiteSpace(refresh))throw new SyncSafetyException("Google did not return an offline refresh token");
            PlatformServices.Current.Secrets.Write(cfg.GoogleRefreshCredential,cfg.GoogleClientId,refresh);
        }
        catch
        {
            await ReplyBestEffortAsync(stream,false,acceptLanguage);
            throw;
        }
        // Authentication must not be marked failed only because a user closed
        // the tab, or because a browser disconnected before receiving the page.
        await ReplyBestEffortAsync(stream,true,acceptLanguage);
        return account;
        }
        finally { tcp.Stop(); }
    }
}
