using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace KeyPassXCCloudSync;

// S3 REST (Signature V4) without an additional SDK dependency. The R2 profile
// uses precisely this transport. Object keys, including immutable history, are
// never cached. CAS is enforced by conditional PutObject, not client-side HEAD+PUT.
public sealed class S3Store : IRemoteStore, IConditionalWriteStore
{
    private readonly Uri _endpoint;
    private readonly string _bucket,_region,_accessKey,_secret;
    private readonly HttpClient _http;
    private readonly Action<string>? _progress;

    public S3Store(Config cfg,Action<string>? diagnostics=null)
    {
        _progress=diagnostics;
        if(!Uri.TryCreate(cfg.WebDavUrl,UriKind.Absolute,out var endpoint) ||
            endpoint.Scheme!=Uri.UriSchemeHttps || endpoint.UserInfo.Length>0 ||
            endpoint.Query.Length>0 || endpoint.Fragment.Length>0 || endpoint.AbsolutePath!="/")
            throw new SyncSafetyException("S3: endpoint must be an HTTPS origin without a path, query or credentials");
        _endpoint=endpoint;
        _bucket=cfg.ProtonRootPath;
        if(!ValidBucket(_bucket))throw new SyncSafetyException("S3: unsafe bucket name (DNS-compatible lowercase name required)");
        _region=cfg.S3Region;
        if(!ValidRegion(_region))throw new SyncSafetyException("S3: invalid signing region");
        if(cfg.Provider=="CloudflareR2" && _region!="auto")
            throw new SyncSafetyException("Cloudflare R2 signing region must be auto");
        if(cfg.Provider=="CloudflareR2" && !(_endpoint.Host.EndsWith(".r2.cloudflarestorage.com",StringComparison.OrdinalIgnoreCase) ||
            _endpoint.Host.EndsWith(".eu.r2.cloudflarestorage.com",StringComparison.OrdinalIgnoreCase) ||
            _endpoint.Host.EndsWith(".us.r2.cloudflarestorage.com",StringComparison.OrdinalIgnoreCase) ||
            _endpoint.Host.EndsWith(".fedramp.r2.cloudflarestorage.com",StringComparison.OrdinalIgnoreCase)))
            throw new SyncSafetyException("Cloudflare R2: use the official account endpoint");
        (_accessKey,_secret)=PlatformServices.Current.Secrets.Read(cfg.WebDavCredentialTarget);
        if(_accessKey.Length==0 || _secret.Length==0 || _accessKey.Any(char.IsWhiteSpace))
            throw new SyncSafetyException("S3: invalid Access Key ID / Secret Access Key");
        _http=new HttpClient(new HttpClientHandler {AllowAutoRedirect=false,UseCookies=false})
        {Timeout=TimeSpan.FromSeconds(cfg.OperationTimeoutSec)};
    }

    internal static bool ValidBucket(string name)
    {
        if(name.Length is <3 or >63 || name[0] is '.' or '-' || name[^1] is '.' or '-')return false;
        if(name.Contains("..",StringComparison.Ordinal) || name.Contains(".-",StringComparison.Ordinal) || name.Contains("-.",StringComparison.Ordinal))return false;
        return name.All(c=>char.IsAsciiLetterLower(c)||char.IsAsciiDigit(c)||c is '-' or '.');
    }
    internal static bool ValidRegion(string region)=>region.Length is >0 and <=64 &&
        region.All(c=>char.IsAsciiLetterLower(c)||char.IsAsciiDigit(c)||c=='-');

    private static string[] Segments(string path)
    {
        var parts=path.Split('/');
        if(parts.Length==0 || parts.Any(s=>s.Length==0 || s is "." or ".." || s.Contains('\\') || s.Contains(':') ||
            s.Any(c=>char.IsControl(c))))
            throw new SyncSafetyException("S3: unsafe object key");
        return parts;
    }
    private static string Escape(string s)=>Uri.EscapeDataString(s).Replace("%7E","~",StringComparison.OrdinalIgnoreCase);
    private string Resource(string key)=>"/"+Escape(_bucket)+(key.Length==0?"":"/"+string.Join("/",Segments(key).Select(Escape)));

    // SigV4 canonical query must be sorted by the encoded key and value.
    private static string Query(IEnumerable<KeyValuePair<string,string>> pairs)=>string.Join("&",pairs
        .Select(kv=>(K:Escape(kv.Key),V:Escape(kv.Value)))
        .OrderBy(kv=>kv.K,StringComparer.Ordinal).ThenBy(kv=>kv.V,StringComparer.Ordinal)
        .Select(kv=>kv.K+"="+kv.V));
    private static string Hex(byte[] bytes)=>Convert.ToHexStringLower(bytes);
    private static byte[] Hmac(byte[] key,string message)=>HMACSHA256.HashData(key,Encoding.UTF8.GetBytes(message));
    private static string Hash(string message)=>Hex(SHA256.HashData(Encoding.UTF8.GetBytes(message)));
    private static string PayloadHash(string? source)
    {
        if(source is null)return Hash("");
        using var stream=File.OpenRead(source);
        return Hex(SHA256.HashData(stream));
    }
    private HttpRequestMessage SignedRequest(HttpMethod method,string? key,string? source=null,
        IReadOnlyDictionary<string,string>? query=null,string? condition=null,string? value=null)
    {
        var path=Resource(key??"");
        var canonicalQuery=query is null?"":Query(query);
        var url=new Uri(_endpoint.GetLeftPart(UriPartial.Authority)+path+(canonicalQuery.Length>0?"?"+canonicalQuery:""));
        var request=new HttpRequestMessage(method,url);
        var now=DateTimeOffset.UtcNow;
        var date=now.ToString("yyyyMMdd",CultureInfo.InvariantCulture);
        var timestamp=now.ToString("yyyyMMdd'T'HHmmss'Z'",CultureInfo.InvariantCulture);
        var hash=PayloadHash(source);
        var host=url.IsDefaultPort?url.Host:url.Authority;
        var headers=new SortedDictionary<string,string>(StringComparer.Ordinal){
            ["host"]=host,["x-amz-content-sha256"]=hash,["x-amz-date"]=timestamp};
        if(condition is not null)
        {
            if(condition is not ("if-match" or "if-none-match") || value is null)
                throw new SyncSafetyException("S3: invalid conditional write");
            headers[condition]=value;
            request.Headers.TryAddWithoutValidation(condition,value);
        }
        request.Headers.TryAddWithoutValidation("x-amz-date",timestamp);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256",hash);
        var signedHeaders=string.Join(";",headers.Keys);
        var canonicalHeaders=string.Concat(headers.Select(p=>p.Key+":"+p.Value.Trim()+"\n"));
        var canonicalRequest=method.Method+"\n"+path+"\n"+canonicalQuery+"\n"+
            canonicalHeaders+"\n"+signedHeaders+"\n"+hash;
        var scope=date+"/"+_region+"/s3/aws4_request";
        var toSign="AWS4-HMAC-SHA256\n"+timestamp+"\n"+scope+"\n"+Hash(canonicalRequest);
        var kDate=Hmac(Encoding.UTF8.GetBytes("AWS4"+_secret),date);
        var kRegion=Hmac(kDate,_region);
        var kService=Hmac(kRegion,"s3");
        var kSigning=Hmac(kService,"aws4_request");
        var signature=Hex(Hmac(kSigning,toSign));
        request.Headers.TryAddWithoutValidation("Authorization","AWS4-HMAC-SHA256 Credential="+
            _accessKey+"/"+scope+", SignedHeaders="+signedHeaders+", Signature="+signature);
        if(source is not null)
        {
            var stream=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read);
            request.Content=new StreamContent(stream);
            request.Content.Headers.ContentType=new MediaTypeHeaderValue("application/octet-stream");
        }
        return request;
    }
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req,string action,CancellationToken ct)
    {
        var sw=Stopwatch.StartNew();
        _progress?.Invoke(action+" — отправка");
        try
        {
            var res=await _http.SendAsync(req,HttpCompletionOption.ResponseHeadersRead,ct);
            _progress?.Invoke(action+" — HTTP "+(int)res.StatusCode+", "+sw.ElapsedMilliseconds+" мс");
            if((int)res.StatusCode is >=300 and <400)
            {
                res.Dispose();
                throw new SyncSafetyException("S3: unexpected redirect (not followed)");
            }
            return res;
        }
        catch(Exception e)
        {
            _progress?.Invoke(action+" — "+e.GetType().Name+", "+sw.ElapsedMilliseconds+" мс");
            throw;
        }
    }
    private static void Ensure(HttpResponseMessage res,string action)
    {
        if(res.IsSuccessStatusCode)return;
        if(res.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
            throw new SyncSafetyException($"S3 {action}: conditional write rejected (HTTP {(int)res.StatusCode})");
        throw new HttpRequestException($"S3 {action}: HTTP {(int)res.StatusCode}",null,res.StatusCode);
    }
    public async Task<bool> DownloadAsync(string path,string destination,CancellationToken ct)
    {
        using var req=SignedRequest(HttpMethod.Get,path);
        using var res=await SendAsync(req,"GET object",ct);
        if(res.StatusCode==HttpStatusCode.NotFound)return false;
        Ensure(res,"GET");
        await using var incoming=await res.Content.ReadAsStreamAsync(ct);
        await using var output=new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        await incoming.CopyToAsync(output,ct);
        await output.FlushAsync(ct);
        return true;
    }
    public async Task<string?> ReadTokenAsync(string path,CancellationToken ct)
    {
        using var req=SignedRequest(HttpMethod.Head,path);
        using var res=await SendAsync(req,"HEAD object",ct);
        if(res.StatusCode==HttpStatusCode.NotFound)return null;
        Ensure(res,"HEAD");
        var etag=res.Headers.ETag;
        if(etag is null || etag.IsWeak)
            throw new SyncSafetyException("S3: strong ETag required for safe CAS");
        return etag.ToString();
    }
    public Task UploadConditionalAsync(string source,string path,string? observedToken,CancellationToken ct)
        =>PutAsync(source,path,observedToken is null?"if-none-match":"if-match",observedToken??"*",ct);
    public Task UploadAsync(string source,string path,WriteMode mode,CancellationToken ct)
        =>PutAsync(source,path,mode==WriteMode.CreateNew?"if-none-match":null,
            mode==WriteMode.CreateNew?"*":null,ct);
    private async Task PutAsync(string source,string path,string? condition,string? value,CancellationToken ct)
    {
        // Do not retry an ambiguous success as a blind overwrite. Engine validates
        // immutable content and reconciles pending state after ambiguous outcomes.
        if(value is not null && condition=="if-match" &&
           (!EntityTagHeaderValue.TryParse(value,out var etag) || etag.IsWeak))
            throw new SyncSafetyException("S3: invalid strong ETag for CAS");
        using var req=SignedRequest(HttpMethod.Put,path,source,condition:condition,value:value);
        using var res=await SendAsync(req,"PUT object",ct);
        Ensure(res,"PUT");
    }
    private async Task<IReadOnlyList<(string Key,DateTimeOffset Modified)>> ListKeysAsync(string prefix,bool immediate,CancellationToken ct)
    {
        var results=new List<(string,DateTimeOffset)>();
        string? next=null;
        var pages=0;
        do
        {
            ct.ThrowIfCancellationRequested();
            var qs=new Dictionary<string,string>{["list-type"]="2",["max-keys"]="1000",["prefix"]=prefix};
            if(immediate)qs["delimiter"]="/";
            if(next is not null)qs["continuation-token"]=next;
            using var req=SignedRequest(HttpMethod.Get,null,query:qs);
            using var res=await SendAsync(req,"LIST objects",ct);
            Ensure(res,"LIST");
            await using var stream=await res.Content.ReadAsStreamAsync(ct);
            using var reader=XmlReader.Create(stream,new XmlReaderSettings {
                Async=true,DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,
                MaxCharactersInDocument=16_000_000
            });
            var xml=await XDocument.LoadAsync(reader,LoadOptions.None,ct);
            XNamespace ns="http://s3.amazonaws.com/doc/2006-03-01/";
            var contents=xml.Root?.Elements(ns+"Contents")??throw new SyncSafetyException("S3: invalid ListObjectsV2 response");
            foreach(var obj in contents)
            {
                var key=obj.Element(ns+"Key")?.Value;
                if(key is null || !key.StartsWith(prefix,StringComparison.Ordinal))
                    throw new SyncSafetyException("S3: object listing escaped requested prefix");
                DateTimeOffset.TryParse(obj.Element(ns+"LastModified")?.Value,CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,out var modified);
                results.Add((key,modified));
            }
            var truncated=xml.Root?.Element(ns+"IsTruncated")?.Value=="true";
            next=truncated?xml.Root?.Element(ns+"NextContinuationToken")?.Value:null;
            if(truncated && string.IsNullOrEmpty(next))throw new SyncSafetyException("S3: missing continuation token");
            if(++pages>10000)throw new SyncSafetyException("S3: excessive listing pagination");
        }while(next is not null);
        return results;
    }
    public async Task<IReadOnlyList<RemoteFile>> ListAsync(string directory,CancellationToken ct)
    {
        var prefix=string.Join("/",Segments(directory))+"/";
        var objects=await ListKeysAsync(prefix,true,ct);
        return objects.Where(x=>x.Key.Length>prefix.Length && !x.Key[prefix.Length..].Contains('/'))
            .Select(x=>new RemoteFile(x.Key,x.Modified)).ToArray();
    }
    public Task EnsureDirectoryAsync(string directory,CancellationToken ct)
    {
        _=Segments(directory);ct.ThrowIfCancellationRequested();
        // S3 has no folders. Buckets must exist before configuring a cloud.
        return Task.CompletedTask;
    }
    public async Task DeleteAsync(string path,CancellationToken ct)
    {
        using var req=SignedRequest(HttpMethod.Delete,path);
        using var res=await SendAsync(req,"DELETE object",ct);
        Ensure(res,"DELETE");
    }
    public async Task DeleteTestDirectoryAsync(string directory,CancellationToken ct)
    {
        if(!IntegrationIsolation.IsTestRunRoot(directory))
            throw new SyncSafetyException("S3: refusing to clean a non-test prefix");
        var prefix=string.Join("/",Segments(directory))+"/";
        var files=await ListKeysAsync(prefix,false,ct);
        foreach(var f in files)
        {
            if(!f.Key.StartsWith(prefix,StringComparison.Ordinal))
                throw new SyncSafetyException("S3: test cleanup scope violated");
            await DeleteAsync(f.Key,ct);
        }
        // S3 prefixes are virtual; no directory marker is created.
    }
    public void Dispose()=>_http.Dispose();
}
