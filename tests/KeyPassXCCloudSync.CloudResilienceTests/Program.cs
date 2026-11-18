using System.Net;
using System.Text.Json;
using KeyPassXCCloudSync;
using CloudApi = KeePassXC.CloudTransport;

// Deterministic fault matrix: fake exceptions and local state only. No live
// credentials, cloud accounts, KeePassXC secrets or platform-specific APIs.
static void Verify(string label,bool condition)
    =>TestReporter.Check("CloudResilienceTests",label,condition);
static CloudFailure Failure(string provider,Exception ex)
    =>CloudErrorPolicy.Classify(provider,ex);
static HttpRequestException Http(int status)=>new("synthetic status",null,(HttpStatusCode)status);

foreach(var (provider,code,expected) in new (string,int,CloudFailureKind)[]
{
    ("GoogleDrive",401,CloudFailureKind.Authentication),
    ("WebDAV",401,CloudFailureKind.Authentication),
    ("WebDAV",403,CloudFailureKind.Authentication),
    ("S3",403,CloudFailureKind.Authentication),
    ("CloudflareR2",403,CloudFailureKind.Authentication),
    ("GoogleDrive",429,CloudFailureKind.RateLimited),
    ("S3",408,CloudFailureKind.Transient),
    ("CloudflareR2",500,CloudFailureKind.Transient),
    ("WebDAV",503,CloudFailureKind.Transient),
    ("S3",400,CloudFailureKind.Configuration),
    ("WebDAV",404,CloudFailureKind.Configuration),
    ("S3",409,CloudFailureKind.Safety),
    ("WebDAV",412,CloudFailureKind.Safety),
    ("WebDAV",423,CloudFailureKind.Safety)
})
    Verify(provider+" HTTP "+code+" classification",
        Failure(provider,Http(code)).Kind==expected);

foreach(var (provider,error,expected) in new (string,Exception,CloudFailureKind)[]
{
    ("GoogleDrive",new TimeoutException(),CloudFailureKind.Transient),
    ("GoogleDrive",new HttpRequestException(),CloudFailureKind.Transient),
    ("WebDAV",new System.Net.Sockets.SocketException(),CloudFailureKind.Transient),
    ("ProtonDrive",new IOException("Proton Drive CLI exit 1"),CloudFailureKind.Transient),
    ("ProtonDrive",new FileNotFoundException(),CloudFailureKind.Configuration),
    ("FileSystemTest",new IOException("synthetic blocked root"),CloudFailureKind.Transient),
    ("FileSystemTestNoCas",new IOException("synthetic blocked root"),CloudFailureKind.Transient),
    ("WebDAV",new IOException("unclassified local staging failure"),CloudFailureKind.Unknown),
    ("ProtonDrive",new SyncSafetyException("Corrupt version graph"),CloudFailureKind.Safety),
    ("S3",new SyncSafetyException("Different VaultId"),CloudFailureKind.Safety),
    ("GoogleDrive",new CloudTransportException("OAUTH_INVALID_GRANT",CloudFailureKind.Authentication,"expired"),CloudFailureKind.Authentication)
})
    Verify(provider+" "+error.GetType().Name+" classification",
        Failure(provider,error).Kind==expected);

Verify("isolated filesystem outage never blocks peer publication",
    !Failure("FileSystemTest",new IOException("blocked root")).BlocksPublication &&
    !Failure("FileSystemTestNoCas",new IOException("blocked root")).BlocksPublication);
Verify("unclassified production local I/O remains fail closed",
    Failure("S3",new IOException("unclassified local write")).BlocksPublication);

var secret="a-real-token-like-secret-DO-NOT-LOG";
foreach(var (body,code,kind) in new (string,string,CloudFailureKind)[]
{
    ("{\"error\":\"invalid_grant\",\"error_description\":\""+secret+"\"}","OAUTH_INVALID_GRANT",CloudFailureKind.Authentication),
    ("{\"error\":\"invalid_client\"}","OAUTH_INVALID_CLIENT",CloudFailureKind.Configuration),
    ("{\"error\":\"unauthorized_client\"}","OAUTH_INVALID_CLIENT",CloudFailureKind.Configuration),
    ("{\"error\":\"invalid_request\"}","OAUTH_CONFIGURATION",CloudFailureKind.Configuration),
    ("{\"error\":\"access_denied\"}","OAUTH_ACCESS_DENIED",CloudFailureKind.Authentication),
    ("{\"error\":\"temporarily_unavailable\"}","OAUTH_TEMPORARY",CloudFailureKind.Transient),
    ("{\"error\":\"slow_down\"}","RATE_LIMIT",CloudFailureKind.RateLimited),
    ("{\"error\":\"malformed-secret-"+secret+"\"}","OAUTH_CONFIGURATION",CloudFailureKind.Configuration),
    ("<not-json>","OAUTH_CONFIGURATION",CloudFailureKind.Configuration)
})
{
    var result=GoogleOAuthFailure.FromResponse(400,body);
    Verify("OAuth code "+code+" recognized",result.Code==code && result.Kind==kind);
    Verify("OAuth response never echoed",!result.Message.Contains(secret,StringComparison.Ordinal));
}
Verify("OAuth rate status",GoogleOAuthFailure.FromResponse(429,"{}").Kind==CloudFailureKind.RateLimited);
Verify("OAuth server failure",GoogleOAuthFailure.FromResponse(503,"{}").Kind==CloudFailureKind.Transient);
Verify("OAuth oversized body fails closed",GoogleOAuthFailure.FromResponse(400,new string('x',9000)).Code=="OAUTH_CONFIGURATION");

// Exercise the package's actual refresh-token flow over an isolated HTTP handler.
// The host supplies synthetic secrets; no browser, real account or OS store is touched.
var oauthOptions=new CloudApi.CloudConnectionOptions
{
    Provider=CloudApi.CloudProvider.GoogleDrive,GoogleClientId="synthetic-client",
    GoogleRefreshTokenTarget="test/refresh",GoogleExpectedAccount="test@example.invalid"
};
var oauthServices=new CloudApi.CloudTransportServices{Secrets=new OAuthSecrets()};
foreach(var (code,body,kind) in new[]
{
    (HttpStatusCode.BadRequest,"""{"error":"invalid_grant"}""",CloudFailureKind.Authentication),
    (HttpStatusCode.BadRequest,"""{"error":"invalid_client"}""",CloudFailureKind.Configuration),
    (HttpStatusCode.TooManyRequests,"{}",CloudFailureKind.RateLimited),
    (HttpStatusCode.ServiceUnavailable,"{}",CloudFailureKind.Transient)
})
{
    using var fakeClient=new HttpClient(new OAuthHttpHandler(code,body));
    var oauth=new CloudApi.GoogleOAuthSession(oauthOptions,oauthServices,fakeClient);
    try
    {
        _=await oauth.AccessTokenAsync(CancellationToken.None);
        Verify("Package OAuth HTTP exchange must reject "+code,false);
    }
    catch(CloudApi.CloudTransportException exception)
    {
        var mapped=CloudStoreFactory.Convert(exception);
        Verify("Package OAuth HTTP exchange classifies "+code,mapped.Kind==kind);
        Verify("Package OAuth response never reveals the synthetic refresh token",
            !mapped.Message.Contains("SYNTHETIC_ONLY",StringComparison.Ordinal));
    }
}
using(var fakeClient=new HttpClient(new OAuthHttpHandler(HttpStatusCode.OK,
    """{"access_token":"FAKE","expires_in":3600}""")))
{
    var oauth=new CloudApi.GoogleOAuthSession(oauthOptions,oauthServices,fakeClient);
    Verify("Package OAuth refresh verifies account and returns access token",
        await oauth.AccessTokenAsync(CancellationToken.None)=="FAKE");
}
Verify("Package concurrent writes block publication",
    CloudStoreFactory.Convert(new CloudApi.CloudTransportException(
        "CONCURRENT_WRITE",CloudApi.CloudFailureKind.ConcurrentWrite,"modified"))
        .Kind==CloudFailureKind.Safety);
Verify("Package unknown failures remain unsafe",
    CloudStoreFactory.Convert(new CloudApi.CloudTransportException(
        "UNKNOWN",CloudApi.CloudFailureKind.Unknown,"unknown"))
        .Kind==CloudFailureKind.Unknown);

Verify("startup preflight tolerates invalid OAuth token",
    PreflightAvailability.IsTemporary(TestCloud("GoogleDrive","OAuth"),expiredOauth()));
Verify("startup preflight tolerates provider rate limit",
    PreflightAvailability.IsTemporary(TestCloud("CloudflareR2","R2"),Http(429)));
Verify("startup preflight refuses corrupt remote metadata",
    !PreflightAvailability.IsTemporary(TestCloud("WebDAV","Damaged"),
        new SyncSafetyException("Damaged remote topology")));
Verify("unknown transport errors fail closed",
    Failure("GoogleDrive",new InvalidOperationException("unexpected JSON structure")).BlocksPublication);
Verify("unclassified startup failures fail closed",
    !PreflightAvailability.IsTemporary(TestCloud("GoogleDrive","Unknown"),
        new InvalidOperationException("unclassified data")));
Verify("local access failures are configuration not global safety",
    Failure("S3",new UnauthorizedAccessException()).Kind==CloudFailureKind.Configuration);


foreach(var provider in new[]{"GoogleDrive","ProtonDrive","WebDAV","S3","CloudflareR2"})
{
    var safety=Failure(provider,new SyncSafetyException("Different vault identities"));
    Verify(provider+" refuses foreign vault",safety.BlocksPublication);
    var auth=Failure(provider,Http(401));
    Verify(provider+" authorization never stops other clouds",!auth.BlocksPublication && auth.WaitsForUser);
    var disconnected=Failure(provider,new TimeoutException());
    Verify(provider+" offline never stops other clouds",!disconnected.BlocksPublication && !disconnected.WaitsForUser);
}

var utc=new DateTimeOffset(2026,11,14,12,0,0,TimeSpan.Zero);
foreach(var (n,minimum,maximum) in new[]{(1,60,60),(2,120,120),(3,240,240),(1000,3600,3600)})
{
    var duration=CloudErrorPolicy.Backoff(n,60,CloudFailureKind.Transient);
    Verify("exponential retry attempt "+n,duration.TotalSeconds>=minimum && duration.TotalSeconds<=maximum);
}
Verify("retry floor",CloudErrorPolicy.Backoff(1,1,CloudFailureKind.Transient)==TimeSpan.FromSeconds(5));
Verify("rate limit floor",CloudErrorPolicy.Backoff(1,1,CloudFailureKind.RateLimited)==TimeSpan.FromSeconds(60));
Verify("retry is capped",CloudErrorPolicy.Backoff(1024,3600,CloudFailureKind.Transient)==TimeSpan.FromHours(1));

// Exercise durable state changes through the exact production policy.
var state=new AppState();
var replica=new ReplicaStatus();
var cloud=TestCloud("GoogleDrive","Google");
state.Clouds[cloud.Name]=replica;
var expired=GoogleOAuthFailure.FromResponse(400,"{\"error\":\"invalid_grant\"}");
Verify("first invalid grant is not global safety",!CloudErrorPolicy.RecordFailure(state,cloud,replica,expired,60,utc));
Verify("invalid grant pauses polling",replica.State=="AUTH_ERROR" && replica.RetryPaused &&
    state.NextCloudPollUtc[cloud.CloudId]==DateTimeOffset.MaxValue && replica.ConsecutiveFailures==1);
Verify("paused provider excluded from automatic full sweep",
    !CloudErrorPolicy.MayAutoScan(state,cloud.Name,cloud.CloudId,null));
Verify("manual request overrides paused provider",
    CloudErrorPolicy.MayAutoScan(state,cloud.Name,cloud.CloudId,new HashSet<string>{cloud.CloudId}));
var secondary=TestCloud("WebDAV","Other");
Verify("peer remains eligible for scan",CloudErrorPolicy.MayAutoScan(state,secondary.Name,secondary.CloudId,null));
var unconfigured=new AppState();
var missingStatus=new ReplicaStatus();
CloudErrorPolicy.RecordMissingCredentials(unconfigured,secondary,missingStatus,utc);
Verify("missing credentials are terminal without a request",missingStatus.State=="NEEDS_AUTH" &&
    missingStatus.RetryPaused && unconfigured.NextCloudPollUtc[secondary.CloudId]==DateTimeOffset.MaxValue);
Verify("manual check is allowed after credentials are repaired",CloudErrorPolicy.MayAutoScan(
    unconfigured,secondary.Name,secondary.CloudId,new HashSet<string>{secondary.CloudId}));

// The JSON schema is 2 and existing version/pointer identities are untouched.
state.VaultId=Guid.NewGuid().ToString("N");
state.LocalVersionId=Guid.NewGuid().ToString("N");
state.V2Cursors[cloud.CloudId]=new V2CloudCursor{WorkingRevision="opaque-old-etag"};
var serialized=JsonSerializer.Serialize(state,SyncJson.Default.AppState);
var roundTrip=JsonSerializer.Deserialize(serialized,SyncJson.Default.AppState)!;
Verify("local retry state survives restart",roundTrip.Clouds[cloud.Name].RetryPaused &&
    roundTrip.Clouds[cloud.Name].ConsecutiveFailures==1);
Verify("schema 2 and history cursor remain unchanged",roundTrip.SchemaVersion==2 &&
    roundTrip.VaultId==state.VaultId && roundTrip.LocalVersionId==state.LocalVersionId &&
    roundTrip.V2Cursors[cloud.CloudId].WorkingRevision=="opaque-old-etag");
CloudErrorPolicy.ResumeAfterLogin(roundTrip,cloud.Name,cloud.CloudId,utc.AddHours(2));
Verify("login clears pause and immediately schedules retry",!roundTrip.Clouds[cloud.Name].RetryPaused &&
    roundTrip.Clouds[cloud.Name].State=="UNKNOWN" &&
    roundTrip.NextCloudPollUtc[cloud.CloudId]==utc.AddHours(2));
Verify("login forces full safety reconciliation",roundTrip.Clouds[cloud.Name].RecoveryPending &&
    roundTrip.V2Cursors[cloud.CloudId].WorkingRevision=="opaque-old-etag");
var credentialsChanged=new AppState();
var blocked=new ReplicaStatus();
credentialsChanged.Clouds[cloud.Name]=blocked;
CloudErrorPolicy.RecordFailure(credentialsChanged,cloud,blocked,expired,60,utc);
Verify("credential edit without saved credential keeps the error",
    !CloudErrorPolicy.ResumeAfterCredentialEdit(credentialsChanged,cloud,false,utc.AddMinutes(1)) &&
    blocked.State=="AUTH_ERROR" && blocked.RetryPaused);
Verify("Google credential edit clears stale auth pause without discarding identity",
    CloudErrorPolicy.ResumeAfterCredentialEdit(credentialsChanged,cloud,true,utc.AddMinutes(2)) &&
    blocked.State=="UNKNOWN" && blocked.RecoveryPending && !blocked.RetryPaused &&
    credentialsChanged.NextCloudPollUtc[cloud.CloudId]==utc.AddMinutes(2));
Verify("credential-edit retry does not clear non-auth failures",
    !CloudErrorPolicy.ResumeAfterCredentialEdit(credentialsChanged,cloud,true,utc.AddMinutes(3)));
var protonCloud=TestCloud("ProtonDrive","Proton");
var protonStatus=new ReplicaStatus{State="NEEDS_AUTH",RetryPaused=true};
credentialsChanged.Clouds[protonCloud.Name]=protonStatus;
Verify("a Proton CLI executable alone cannot prove a logged-in session",
    !CloudErrorPolicy.ResumeAfterCredentialEdit(credentialsChanged,protonCloud,true,utc.AddMinutes(4)) &&
    protonStatus.State=="NEEDS_AUTH" && protonStatus.RetryPaused);
CloudErrorPolicy.RecordSuccess(roundTrip.Clouds[cloud.Name]);
Verify("verified scan clears all failure state",!roundTrip.Clouds[cloud.Name].RecoveryPending &&
    roundTrip.Clouds[cloud.Name].ConsecutiveFailures==0 &&
    roundTrip.Clouds[cloud.Name].FailureSinceUtc is null);

var temporary=new AppState();
var web=TestCloud("WebDAV","Web");
var webStatus=new ReplicaStatus();
temporary.Clouds[web.Name]=webStatus;
CloudErrorPolicy.RecordFailure(temporary,web,webStatus,Http(503),60,utc);
var firstRetry=temporary.NextCloudPollUtc[web.CloudId];
CloudErrorPolicy.RecordFailure(temporary,web,webStatus,Http(503),60,utc.AddMinutes(1));
Verify("second temporary failure increases deadline",temporary.NextCloudPollUtc[web.CloudId]>firstRetry);
Verify("transient does not pause",!webStatus.RetryPaused && webStatus.ConsecutiveFailures==2);
Verify("temporary transport outage is a warning state",webStatus.State=="OFFLINE");
CloudErrorPolicy.RecordSuccess(webStatus);
Verify("success resets backoff",webStatus.ConsecutiveFailures==0 && webStatus.ProblemCode is null);
webStatus.ConsecutiveFailures=int.MaxValue;
CloudErrorPolicy.RecordFailure(temporary,web,webStatus,Http(503),60,utc);
Verify("corrupted persisted retry count is safely clamped",webStatus.ConsecutiveFailures==1024);
CloudErrorPolicy.RecordSuccess(webStatus);


// Test the real scheduler contract without a cloud: a permanent deadline must
// not spin, while manual wakeups still run and another cloud progresses.
var due=new System.Collections.Concurrent.ConcurrentDictionary<string,DateTimeOffset>();
due["google"]=DateTimeOffset.MaxValue;
due["webdav"]=DateTimeOffset.UtcNow;
int googleRuns=0,webRuns=0;
var webReady=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var googleReady=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
using(var scheduler=new IndependentCloudScheduler(new[]{"google","webdav"},
    id=>due[id],(id,ct)=>
    {
        if(id=="google") {Interlocked.Increment(ref googleRuns);googleReady.TrySetResult();}
        else {Interlocked.Increment(ref webRuns);webReady.TrySetResult();}
        due[id]=DateTimeOffset.MaxValue;
        return Task.CompletedTask;
    },TimeSpan.FromSeconds(1),CancellationToken.None))
{
    await webReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Verify("healthy peer runs while OAuth peer paused",webRuns==1 && googleRuns==0);
    await Task.Delay(150);
    Verify("permanent failure does not hot-spin",googleRuns==0);
    Verify("manual request reaches paused cloud",scheduler.Request("google"));
    await googleReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Verify("manual wakeup runs exactly once",googleRuns==1);
}

Console.WriteLine("[SUMMARY] CloudResilienceTests completed.");

static CloudTransportException expiredOauth()
    =>GoogleOAuthFailure.FromResponse(400,"""{"error":"invalid_grant"}""");

static CloudOptions TestCloud(string provider,string name)
{
    // All members of this record are intentionally supplied as synthetic
    // placeholders; no real provider access is attempted by this test.
    return new CloudOptions(name,provider,"test","","","","","","","",
        "",30,10,"auto",60,Guid.NewGuid().ToString("N"),false);
}

internal sealed class OAuthSecrets : CloudApi.ICloudSecretStore
{
    public (string User,string Password) Read(string target)
    {
        if(target!="test/refresh")throw new InvalidOperationException("Unexpected test credential target");
        return ("synthetic-client","SYNTHETIC_ONLY");
    }
    public void Write(string target,string user,string secret)
        =>throw new InvalidOperationException("No credential writes allowed in refresh tests");
}
internal sealed class OAuthHttpHandler(HttpStatusCode status,string responseBody) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if(request.Method==HttpMethod.Get &&
           request.RequestUri?.AbsoluteUri=="https://www.googleapis.com/drive/v3/about?fields=user(emailAddress)")
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){
                Content=new StringContent("""{"user":{"emailAddress":"test@example.invalid"}}""")});
        if(request.Method!=HttpMethod.Post ||
           request.RequestUri?.AbsoluteUri!="https://oauth2.googleapis.com/token")
            throw new InvalidOperationException("OAuth test sent a request to an unexpected endpoint");
        return Task.FromResult(new HttpResponseMessage(status){
            Content=new StringContent(responseBody,System.Text.Encoding.UTF8,"application/json")});
    }
}
