using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace KeyPassXCCloudSync;

// Google Drive v3 adapter. Google file names are NOT unique; never choose an arbitrary duplicate.
public sealed class GoogleDriveStore : IRemoteStore
{
    private const string FilesApi="https://www.googleapis.com/drive/v3/files";
    private const string UploadApi="https://www.googleapis.com/upload/drive/v3/files";
    private readonly Config _cfg;
    private readonly HttpClient _http;
    private readonly GoogleOAuth _auth;
    private readonly Action<string>? _diagnostics;
    // Positive folder-ID lookup cache only. Names of files and their revisions
    // are never cached. Write/delete/cleanup paths always resolve folders afresh.
    private readonly ConcurrentDictionary<string,(string Id,DateTimeOffset CheckedUtc)> _folderCache=new(StringComparer.Ordinal);
    private long _requestCount,_requestMilliseconds,_folderCacheHits;
    private sealed class RequestStats { public long Count,ElapsedMs; }
    private readonly ConcurrentDictionary<string,RequestStats> _requestByType=new(StringComparer.Ordinal);
    private readonly int _cacheSeconds;

    private static string OperationLabel(HttpRequestMessage req)
    {
        var path=req.RequestUri?.AbsolutePath ?? "";
        if(path.Contains("/upload/",StringComparison.Ordinal))return "передача KDBX/метаданных";
        if(path.EndsWith("/files",StringComparison.Ordinal))return "список файлов";
        return req.Method==HttpMethod.Get ? "чтение файла/свойств" : "операция с файлом";
    }
    private async Task<HttpResponseMessage> SendMeasuredAsync(HttpRequestMessage req,CancellationToken ct,bool addAuthorization)
    {
        var label=req.Method.Method+" "+OperationLabel(req);
        var stopwatch=Stopwatch.StartNew();
        _diagnostics?.Invoke(label+" — подготовка / OAuth");
        try
        {
            if(addAuthorization)
                req.Headers.Authorization=new AuthenticationHeaderValue("Bearer",await _auth.TokenAsync(ct));
            _diagnostics?.Invoke(label+" — отправка");
            var response=await _http.SendAsync(req,HttpCompletionOption.ResponseHeadersRead,ct);
            if((int)response.StatusCode is >=300 and <400)
            {
                response.Dispose();
                throw new SyncSafetyException("Google Drive returned an unexpected redirect");
            }
            if(response.StatusCode==HttpStatusCode.NotFound)_folderCache.Clear();
            _diagnostics?.Invoke($"{label} — HTTP {(int)response.StatusCode}, {stopwatch.ElapsedMilliseconds} мс"+
                (stopwatch.Elapsed>=TimeSpan.FromSeconds(5)?" [МЕДЛЕННО]":""));
            return response;
        }
        catch(Exception e)
        {
            _diagnostics?.Invoke($"{label} — {e.GetType().Name}, {stopwatch.ElapsedMilliseconds} мс");
            throw;
        }
        finally
        {
            Interlocked.Increment(ref _requestCount);
            Interlocked.Add(ref _requestMilliseconds,stopwatch.ElapsedMilliseconds);
            var stats=_requestByType.GetOrAdd(label,static _=>new RequestStats());
            Interlocked.Increment(ref stats.Count);
            Interlocked.Add(ref stats.ElapsedMs,stopwatch.ElapsedMilliseconds);
        }
    }

    public GoogleDriveStore(Config config,Action<string>? diagnostics=null)
    {
        _cfg=config;
        _diagnostics=diagnostics;
        _cacheSeconds=Math.Clamp(config.GoogleFolderCacheSeconds,0,60);
        _http=new HttpClient(new HttpClientHandler {AllowAutoRedirect=false,UseCookies=false}) {
            Timeout=TimeSpan.FromSeconds(config.OperationTimeoutSec)
        };
        _auth=new GoogleOAuth(config,_http);
    }

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage req,CancellationToken ct)
        =>SendMeasuredAsync(req,ct,true);
    private static void Ensure(HttpResponseMessage res,string action)
    {
        if(res.IsSuccessStatusCode) return;
        if(res.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
            throw new SyncSafetyException($"Google Drive {action}: write conflict (HTTP {(int)res.StatusCode})");
        throw new HttpRequestException($"Google Drive {action}: HTTP {(int)res.StatusCode}",null,res.StatusCode);
    }
    private static string QuoteJson(string s)=>JsonSerializer.Serialize(s,StateJsonContext.Default.String);
    private static string EscapeQ(string s)=>s.Replace("\\","\\\\").Replace("'","\\'");
    private static string[] Components(string path)
    {
        var p=path.Split('/',StringSplitOptions.RemoveEmptyEntries);
        if(p.Length==0 || p.Any(x=>x is ".." or "." or "" || x.Contains('\\') || x.Contains(':')))
            throw new SyncSafetyException("Invalid Google Drive path");
        return p;
    }
    private sealed record GoogleItem(string Id,string Name,string Mime,DateTimeOffset Modified);

    private async Task<IReadOnlyList<GoogleItem>> ChildrenAsync(string parent,string? name,CancellationToken ct)
    {
        string q=$"'{EscapeQ(parent)}' in parents and trashed = false";
        if(name!=null) q+=$" and name = '{EscapeQ(name)}'";
        var items=new List<GoogleItem>();
        string? page=null;
        do
        {
            var url=FilesApi+"?fields="+Uri.EscapeDataString("nextPageToken,files(id,name,mimeType,modifiedTime)")+
                "&pageSize=1000&q="+Uri.EscapeDataString(q)+"&spaces=drive"+
                (page==null?"":"&pageToken="+Uri.EscapeDataString(page));
            using var req=new HttpRequestMessage(HttpMethod.Get,url);
            using var res=await SendAsync(req,ct);
            Ensure(res,"list");
            using var doc=JsonDocument.Parse(await res.Content.ReadAsStreamAsync(ct));
            var root=doc.RootElement;
            if(!root.TryGetProperty("files",out var files)||files.ValueKind!=JsonValueKind.Array)
                throw new SyncSafetyException("Google Drive list returned invalid JSON");
            foreach(var item in files.EnumerateArray())
            {
                string id=item.GetProperty("id").GetString()!;
                string nm=item.GetProperty("name").GetString()!;
                string mime=item.GetProperty("mimeType").GetString()!;
                DateTimeOffset modified=default;
                if(item.TryGetProperty("modifiedTime",out var m)) DateTimeOffset.TryParse(m.GetString(),out modified);
                items.Add(new GoogleItem(id,nm,mime,modified));
            }
            page=root.TryGetProperty("nextPageToken",out var p)?p.GetString():null;
        } while(!string.IsNullOrEmpty(page));
        return items;
    }
    private async Task<GoogleItem?> OneAsync(string parent,string name,CancellationToken ct)
    {
        var found=await ChildrenAsync(parent,name,ct);
        if(found.Count>1) throw new SyncSafetyException($"Google Drive contains duplicate name '{name}'; refusing to choose a file");
        return found.Count==0?null:found[0];
    }
    private async Task<string?> FolderIdAsync(string[] components,bool create,CancellationToken ct,bool forceFresh=false)
    {
        string parent="root";
        string prefix="";
        foreach(var segment in components)
        {
            prefix=prefix.Length==0?segment:prefix+"/"+segment;
            // Read-only lookups may reuse a recently verified directory ID.
            // Never cache missing folders or the existence of any file.
            if(!create && !forceFresh && _cacheSeconds>0 &&
                _folderCache.TryGetValue(prefix,out var cached) &&
                DateTimeOffset.UtcNow-cached.CheckedUtc<TimeSpan.FromSeconds(_cacheSeconds))
            {
                parent=cached.Id;
                Interlocked.Increment(ref _folderCacheHits);
                continue;
            }
            var item=await OneAsync(parent,segment,ct);
            if(item is null)
            {
                if(!create) return null;
                string json="{\"name\":"+QuoteJson(segment)+",\"mimeType\":\"application/vnd.google-apps.folder\",\"parents\":["+QuoteJson(parent)+"]}";
                using var req=new HttpRequestMessage(HttpMethod.Post,FilesApi+"?fields=id,name,mimeType");
                req.Content=new StringContent(json,Encoding.UTF8,"application/json");
                using var res=await SendAsync(req,ct);
                Ensure(res,"create folder");
                using var doc=JsonDocument.Parse(await res.Content.ReadAsStreamAsync(ct));
                var createdId=doc.RootElement.GetProperty("id").GetString()!;
                // A simultaneous folder create can cause duplicate paths; subsequent lookup catches it.
                var check=await OneAsync(parent,segment,ct);
                if(check==null || check.Id!=createdId) throw new SyncSafetyException("Google Drive folder changed during creation");
                parent=createdId;
            }
            else
            {
                if(item.Mime!="application/vnd.google-apps.folder") throw new SyncSafetyException("Google Drive path component is not a folder");
                parent=item.Id;
            }
            _folderCache[prefix]=(parent,DateTimeOffset.UtcNow);
        }
        return parent;
    }
    private async Task<string> GetParentIdAsync(string id,CancellationToken ct)
    {
        using var req=new HttpRequestMessage(HttpMethod.Get,FilesApi+"/"+Uri.EscapeDataString(id)+"?fields=parents");
        using var res=await SendAsync(req,ct);Ensure(res,"folder parents");
        using var doc=JsonDocument.Parse(await res.Content.ReadAsStreamAsync(ct));
        var p=doc.RootElement.GetProperty("parents");
        if(p.GetArrayLength()!=1)throw new SyncSafetyException("Google Drive folder has ambiguous parent");
        return p[0].GetString()!;
    }
    private async Task<GoogleItem?> FindAsync(string path,CancellationToken ct,bool forceFresh=false)
    {
        var parts=Components(path);
        var parent=await FolderIdAsync(parts[..^1],false,ct,forceFresh);
        if(parent==null) return null;
        return await OneAsync(parent,parts[^1],ct);
    }
    public async Task<bool> DownloadAsync(string path,string destination,CancellationToken ct)
    {
        var node=await FindAsync(path,ct);
        if(node==null) return false;
        if(node.Mime=="application/vnd.google-apps.folder" || node.Mime.StartsWith("application/vnd.google-apps."))
            throw new SyncSafetyException("Google Drive path references a non-binary file");
        using var req=new HttpRequestMessage(HttpMethod.Get,FilesApi+"/"+Uri.EscapeDataString(node.Id)+"?alt=media");
        using var res=await SendAsync(req,ct);
        if(res.StatusCode==HttpStatusCode.NotFound)
            throw new SyncSafetyException("Google Drive file vanished after lookup (race), retry required");
        Ensure(res,"download");
        var transfer=Stopwatch.StartNew();
        _diagnostics?.Invoke("Получение содержимого — начало");
        try
        {
            using var transferTimeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
            transferTimeout.CancelAfter(TimeSpan.FromSeconds(_cfg.OperationTimeoutSec));
            await using var output=new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None);
            await using var input=await res.Content.ReadAsStreamAsync(transferTimeout.Token);
            await input.CopyToAsync(output,transferTimeout.Token);
            await output.FlushAsync(transferTimeout.Token);
            _diagnostics?.Invoke($"Получение содержимого — {output.Length} байт, {transfer.ElapsedMilliseconds} мс");
            return true;
        }
        catch(Exception e)
        {
            _diagnostics?.Invoke($"Получение содержимого — {e.GetType().Name}, {transfer.ElapsedMilliseconds} мс");
            throw;
        }
    }
    public async Task UploadAsync(string source,string path,WriteMode mode,CancellationToken ct)
    {
        var parts=Components(path);
        var parent=await FolderIdAsync(parts[..^1],true,ct) ?? throw new SyncSafetyException("Google parent folder missing");
        var existing=await OneAsync(parent,parts[^1],ct);
        if(mode==WriteMode.CreateNew && existing!=null) throw new SyncSafetyException("Refusing to replace existing cloud backup");
        if(existing!=null && existing.Mime=="application/vnd.google-apps.folder") throw new SyncSafetyException("Cloud path is a directory");
        var length=new FileInfo(source).Length;
        var url=existing==null ? UploadApi+"?uploadType=resumable&fields=id" :
            UploadApi+"/"+Uri.EscapeDataString(existing.Id)+"?uploadType=resumable&fields=id";
        using var initiation=new HttpRequestMessage(existing==null?HttpMethod.Post:HttpMethod.Patch,url);
        initiation.Headers.TryAddWithoutValidation("X-Upload-Content-Type","application/octet-stream");
        initiation.Headers.TryAddWithoutValidation("X-Upload-Content-Length",length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if(existing==null)
            initiation.Content=new StringContent("{\"name\":"+QuoteJson(parts[^1])+
                ",\"parents\":["+QuoteJson(parent)+"],\"mimeType\":\"application/octet-stream\"}",Encoding.UTF8,"application/json");
        else initiation.Content=new StringContent("{}",Encoding.UTF8,"application/json");
        using var init=await SendAsync(initiation,ct);
        Ensure(init,"start upload");
        var session=init.Headers.Location ?? throw new SyncSafetyException("Google upload session URL missing");
        if(session.Scheme!="https" || !(session.Host=="www.googleapis.com" || session.Host.EndsWith(".googleapis.com",StringComparison.OrdinalIgnoreCase)))
            throw new SyncSafetyException("Untrusted Google Drive upload session URL");
        using var put=new HttpRequestMessage(HttpMethod.Put,session);
        put.Headers.Authorization=new AuthenticationHeaderValue("Bearer",await _auth.TokenAsync(ct));
        put.Content=new StreamContent(new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read));
        put.Content.Headers.ContentType=new MediaTypeHeaderValue("application/octet-stream");
        put.Content.Headers.ContentLength=length;
        using var answer=await SendMeasuredAsync(put,ct,false);
        Ensure(answer,"upload content");
        if(mode==WriteMode.CreateNew && existing==null)
        {
            // Drive filenames are not unique. A second writer could have created
            // the same name during our resumable upload: refuse ambiguous paths.
            using var reply=JsonDocument.Parse(await answer.Content.ReadAsStreamAsync(ct));
            var newId=reply.RootElement.GetProperty("id").GetString();
            var unique=await OneAsync(parent,parts[^1],ct);
            if(newId is null || unique is null || unique.Id!=newId)
                throw new SyncSafetyException("Google Drive immutable object name is ambiguous after creation");
        }
        // The engine additionally reads back and hashes immutable content.
        // Creation cannot be made atomic by name using this API.
    }
    public async Task EnsureDirectoryAsync(string directory,CancellationToken ct)
    {
        _=await FolderIdAsync(Components(directory),true,ct);
    }
    public async Task<IReadOnlyList<RemoteFile>> ListAsync(string directory,CancellationToken ct)
    {
        var parent=await FolderIdAsync(Components(directory),false,ct);
        if(parent==null)return Array.Empty<RemoteFile>();
        return (await ChildrenAsync(parent,null,ct)).Where(x=>x.Mime!="application/vnd.google-apps.folder")
            .Select(x=>new RemoteFile(directory+"/"+x.Name,x.Modified)).ToArray();
    }
    public async Task DeleteAsync(string path,CancellationToken ct)
    {
        var existing=await FindAsync(path,ct,true);
        if(existing==null) throw new SyncSafetyException("Google Drive backup disappeared before cleanup");
        using var req=new HttpRequestMessage(HttpMethod.Delete,FilesApi+"/"+Uri.EscapeDataString(existing.Id));
        using var res=await SendAsync(req,ct);
        Ensure(res,"delete backup");
    }
    public async Task DeleteTestDirectoryAsync(string directory,CancellationToken ct)
    {
        if(!IntegrationIsolation.IsTestRunRoot(directory))
            throw new SyncSafetyException("Unsafe integration test cleanup path");
        var node=await FindAsync(directory,ct,true);
        if(node is null) return;
        if(node.Mime!="application/vnd.google-apps.folder")
            throw new SyncSafetyException("Integration cleanup target is not a folder");
        using var req=new HttpRequestMessage(HttpMethod.Delete,FilesApi+"/"+Uri.EscapeDataString(node.Id));
        using var res=await SendAsync(req,ct);
        Ensure(res,"delete test folder");
    }
    public void Dispose()
    {
        if(_diagnostics is not null)
        {
            _diagnostics($"Итого Google Drive: HTTP-запросов {Interlocked.Read(ref _requestCount)}, "+
                $"сумма длительностей {Interlocked.Read(ref _requestMilliseconds)} мс, попаданий кэша каталогов {Interlocked.Read(ref _folderCacheHits)}");
            foreach(var (kind,stats) in _requestByType.OrderBy(x=>x.Key,StringComparer.Ordinal))
                _diagnostics($"  {kind}: {Interlocked.Read(ref stats.Count)} запросов, "+
                    $"{Interlocked.Read(ref stats.ElapsedMs)} мс суммарно");
        }
        _http.Dispose();
    }
}
