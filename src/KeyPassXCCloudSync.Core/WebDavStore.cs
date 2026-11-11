using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace KeyPassXCCloudSync;

public sealed class WebDavStore : IRemoteStore, IConditionalWriteStore
{
    private readonly Uri _root;
    private readonly HttpClient _http;
    private readonly string _auth;

    public WebDavStore(Config config)
    {
        _root=new Uri(config.WebDavUrl.TrimEnd('/')+"/",UriKind.Absolute);
        var (user,password)=PlatformServices.Current.Secrets.Read(config.WebDavCredentialTarget);
        if(string.IsNullOrWhiteSpace(user)) throw new SyncSafetyException("WebDAV credential username is empty");
        _auth="Basic "+Convert.ToBase64String(Encoding.UTF8.GetBytes(user+":"+password));
        var handler=new HttpClientHandler { AllowAutoRedirect=false, UseCookies=false };
        _http=new HttpClient(handler) { Timeout=TimeSpan.FromSeconds(config.OperationTimeoutSec) };
    }

    private Uri Url(string path)
    {
        var components=path.Split('/',StringSplitOptions.RemoveEmptyEntries);
        if(components.Length==0 || components.Any(x => x is "." or ".." || x.Contains('\\')))
            throw new SyncSafetyException("Unsafe WebDAV remote path");
        return new Uri(_root,string.Join("/",components.Select(Uri.EscapeDataString)));
    }
    private HttpRequestMessage Request(HttpMethod method,string path)
    {
        var r=new HttpRequestMessage(method,Url(path));
        r.Headers.TryAddWithoutValidation("Authorization",_auth);
        return r;
    }
    private static void Success(HttpResponseMessage r,string operation)
    {
        if(r.IsSuccessStatusCode) return;
        if(r.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict or HttpStatusCode.Locked)
            throw new SyncSafetyException($"{operation}: concurrent write or precondition failed (HTTP {(int)r.StatusCode})");
        throw new HttpRequestException($"{operation}: HTTP {(int)r.StatusCode}",null,r.StatusCode);
    }
    public async Task<bool> DownloadAsync(string path,string destination,CancellationToken ct)
    {
        using var req=Request(HttpMethod.Get,path);
        using var res=await _http.SendAsync(req,HttpCompletionOption.ResponseHeadersRead,ct);
        if(res.StatusCode==HttpStatusCode.NotFound) return false;
        Success(res,"GET");
        await using var input=await res.Content.ReadAsStreamAsync(ct);
        await using var output=new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        await input.CopyToAsync(output,ct);
        await output.FlushAsync(ct);
        return true;
    }
    public async Task<string?> ReadTokenAsync(string path,CancellationToken ct)
    {
        using var req=Request(HttpMethod.Head,path);
        using var res=await _http.SendAsync(req,HttpCompletionOption.ResponseHeadersRead,ct);
        if(res.StatusCode==HttpStatusCode.NotFound)return null;
        Success(res,"HEAD");
        var tag=res.Headers.ETag;
        if(tag is null || tag.IsWeak)
            throw new SyncSafetyException("WebDAV did not provide a strong ETag: safe conditional update unavailable");
        return tag.ToString();
    }
    public async Task UploadConditionalAsync(string source,string path,string? observedToken,CancellationToken ct)
    {
        using var req=Request(HttpMethod.Put,path);
        if(observedToken is null)req.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        else
        {
            if(!EntityTagHeaderValue.TryParse(observedToken,out var tag) || tag.IsWeak)
                throw new SyncSafetyException("Invalid strong ETag for WebDAV CAS");
            req.Headers.IfMatch.Add(tag);
        }
        req.Content=new StreamContent(new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read));
        req.Content.Headers.ContentType=new MediaTypeHeaderValue("application/octet-stream");
        using var res=await _http.SendAsync(req,HttpCompletionOption.ResponseHeadersRead,ct);
        Success(res,"conditional PUT");
    }
    public async Task UploadAsync(string source,string path,WriteMode mode,CancellationToken ct)
    {
        using var req=Request(HttpMethod.Put,path);
        if(mode==WriteMode.CreateNew) req.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        var stream=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read);
        req.Content=new StreamContent(stream);
        req.Content.Headers.ContentType=new MediaTypeHeaderValue("application/octet-stream");
        using var res=await _http.SendAsync(req,HttpCompletionOption.ResponseHeadersRead,ct);
        Success(res,"PUT");
    }
    public async Task EnsureDirectoryAsync(string directory,CancellationToken ct)
    {
        var parts=directory.Split('/',StringSplitOptions.RemoveEmptyEntries);
        for(int i=1;i<=parts.Length;i++)
        {
            var path=string.Join("/",parts.Take(i));
            using var req=Request(new HttpMethod("MKCOL"),path);
            using var res=await _http.SendAsync(req,ct);
            if(res.IsSuccessStatusCode) continue;
            if(res.StatusCode==HttpStatusCode.MethodNotAllowed)
            {
                using var test=Request(new HttpMethod("PROPFIND"),path);
                test.Headers.TryAddWithoutValidation("Depth","0");
                test.Content=new StringContent("<?xml version=\"1.0\"?><d:propfind xmlns:d=\"DAV:\"><d:prop><d:resourcetype/></d:prop></d:propfind>",Encoding.UTF8,"application/xml");
                using var probe=await _http.SendAsync(test,ct);
                if((int)probe.StatusCode!=207)throw new SyncSafetyException("MKCOL returned 405 but path is not confirmed as a collection");
                var doc=XDocument.Load(await probe.Content.ReadAsStreamAsync(ct));
                if(!doc.Descendants(XName.Get("collection","DAV:")).Any())
                    throw new SyncSafetyException("WebDAV path exists but is not a collection");
                continue;
            }
            Success(res,"MKCOL");
        }
    }
    public async Task<IReadOnlyList<RemoteFile>> ListAsync(string directory,CancellationToken ct)
    {
        using var req=Request(new HttpMethod("PROPFIND"),directory);
        req.Headers.TryAddWithoutValidation("Depth","1");
        req.Content=new StringContent("<?xml version=\"1.0\"?><d:propfind xmlns:d=\"DAV:\"><d:prop><d:getlastmodified/></d:prop></d:propfind>",Encoding.UTF8,"application/xml");
        using var res=await _http.SendAsync(req,ct);
        if(res.StatusCode==HttpStatusCode.NotFound) return Array.Empty<RemoteFile>();
        if((int)res.StatusCode!=207) Success(res,"PROPFIND");
        var xml=await res.Content.ReadAsStringAsync(ct);
        var doc=XDocument.Parse(xml);
        XNamespace d="DAV:";
        var results=new List<RemoteFile>();
        foreach(var item in doc.Descendants(d+"response"))
        {
            var href=item.Element(d+"href")?.Value;
            if(href==null) continue;
            Uri url;
            if(!Uri.TryCreate(_root,href,out url!)) continue;
            var directoryUri=Url(directory);
            var parent=directoryUri.AbsoluteUri.TrimEnd('/')+"/";
            if(!url.AbsoluteUri.StartsWith(parent,StringComparison.Ordinal)) continue;
            var relative=url.AbsoluteUri[parent.Length..];
            if(relative.EndsWith('/')) continue;
            var name=Uri.UnescapeDataString(relative);
            if(name.Length==0 || name.Contains('/') || name.Contains('\\') || name is "." or "..") continue;
            var dateText=item.Descendants(d+"getlastmodified").FirstOrDefault()?.Value;
            DateTimeOffset.TryParse(dateText,out var date);
            results.Add(new RemoteFile(directory+"/"+name,date));
        }
        return results;
    }
    public async Task DeleteAsync(string path,CancellationToken ct)
    {
        using var req=Request(HttpMethod.Delete,path);
        using var res=await _http.SendAsync(req,ct);
        Success(res,"DELETE");
    }
    public async Task DeleteTestDirectoryAsync(string directory,CancellationToken ct)
    {
        if(!IntegrationIsolation.IsTestRunRoot(directory))
            throw new SyncSafetyException("Unsafe integration test cleanup path");
        using var req=Request(HttpMethod.Delete,directory);
        using var res=await _http.SendAsync(req,ct);
        if(res.StatusCode!=HttpStatusCode.NotFound) Success(res,"DELETE test folder");
    }
    public void Dispose()=>_http.Dispose();
}
