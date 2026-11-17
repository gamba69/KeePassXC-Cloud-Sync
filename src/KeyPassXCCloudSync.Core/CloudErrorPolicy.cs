using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace KeyPassXCCloudSync;

// Provider failures are not replication-integrity failures. Never convert a
// transport or credential rejection into SyncSafetyException.
internal enum CloudFailureKind { Safety, Authentication, Configuration, RateLimited, Transient, Unknown }

internal sealed class CloudTransportException(string code,CloudFailureKind kind,string message)
    : Exception(message)
{
    internal string Code {get;}=code;
    internal CloudFailureKind Kind {get;}=kind;
}

internal sealed record CloudFailure(CloudFailureKind Kind,string Code,string SafeMessage)
{
    // Unknown failures might be damaged remote metadata, not a network outage.
    // They must fail closed until a provider adapter classifies them safely.
    internal bool BlocksPublication=>Kind is CloudFailureKind.Safety or CloudFailureKind.Unknown;
    internal bool WaitsForUser=>Kind is CloudFailureKind.Authentication or CloudFailureKind.Configuration;
}

internal static class GoogleOAuthFailure
{
    // Google may return arbitrary text (and even secrets) in error_description.
    // Inspect known OAuth codes, but never include response text in a log/status.
    internal static CloudTransportException FromResponse(int httpStatus,string body)
    {
        string? code=null;
        if(body.Length<=8192)
        {
            try
            {
                using var doc=JsonDocument.Parse(body);
                if(doc.RootElement.ValueKind==JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("error",out var field) &&
                   field.ValueKind==JsonValueKind.String)
                    code=field.GetString();
                // Only known values are used; the free-form description is untrusted.
            }
            catch(JsonException) { /* Keep the HTTP status, not the untrusted body. */ }
        }
        return code switch
        {
            "invalid_grant" => new("OAUTH_INVALID_GRANT",CloudFailureKind.Authentication,
                "Google OAuth: refresh token expired, revoked or invalid"),
            "access_denied" => new("OAUTH_ACCESS_DENIED",CloudFailureKind.Authentication,
                "Google OAuth: access denied; sign in again"),
            "invalid_client" => new("OAUTH_INVALID_CLIENT",CloudFailureKind.Configuration,
                "Google OAuth: client configuration rejected"),
            "unauthorized_client" => new("OAUTH_INVALID_CLIENT",CloudFailureKind.Configuration,
                "Google OAuth: client is not authorized"),
            "invalid_request" => new("OAUTH_CONFIGURATION",CloudFailureKind.Configuration,
                "Google OAuth: request configuration rejected"),
            "temporarily_unavailable" => new("OAUTH_TEMPORARY",CloudFailureKind.Transient,
                "Google OAuth: service temporarily unavailable"),
            "slow_down" => new("RATE_LIMIT",CloudFailureKind.RateLimited,
                "Google OAuth: request rate limited"),
            _ when httpStatus==429 => new("RATE_LIMIT",CloudFailureKind.RateLimited,
                "Google OAuth: HTTP 429"),
            _ when httpStatus is >=500 and <=599 => new("OAUTH_TEMPORARY",CloudFailureKind.Transient,
                "Google OAuth: HTTP "+httpStatus),
            _ => new("OAUTH_CONFIGURATION",CloudFailureKind.Configuration,
                "Google OAuth rejected the request (HTTP "+httpStatus+")")
        };
    }
}

// All retry metadata is LOCAL state, never written to topology.json, the
// remote device pointers or immutable KDBX history. SchemaVersion stays 2.
internal static class CloudErrorPolicy
{
    internal static CloudFailure Classify(string provider,Exception error)
    {
        if(error is CloudTransportException typed)
            return new(typed.Kind,typed.Code,typed.Message);
        if(error is SyncSafetyException)
            return new(CloudFailureKind.Safety,"SAFETY",error.Message);
        if(error is HttpRequestException http && http.StatusCode is { } status)
        {
            var n=(int)status;
            if(n is 401 or 403)
                return new(CloudFailureKind.Authentication,"AUTH_REQUIRED",
                    provider+": access denied (HTTP "+n+")");
            if(n==429)
                return new(CloudFailureKind.RateLimited,"RATE_LIMIT",
                    provider+": rate limited (HTTP 429)");
            if(n is 409 or 412 or 423)
                return new(CloudFailureKind.Safety,"SAFETY",
                    provider+": concurrent write or locked object (HTTP "+n+")");
            if(n is 408 or 425 || n>=500 && n<=599)
                return new(CloudFailureKind.Transient,"TEMPORARY",
                    provider+": remote service unavailable (HTTP "+n+")");
            if(n is 400 or 404 or 405)
                return new(CloudFailureKind.Configuration,"CONFIGURATION",
                    provider+": endpoint or request rejected (HTTP "+n+")");
            return new(CloudFailureKind.Unknown,"UNKNOWN",
                provider+": request failed (HTTP "+n+")");
        }
        if(error is FileNotFoundException && provider=="ProtonDrive")
            return new(CloudFailureKind.Configuration,"PROTON_CLI_MISSING",
                "Proton Drive CLI executable not found");
        if(error is UnauthorizedAccessException)
            return new(CloudFailureKind.Configuration,"PERMISSION",
                provider+": local access or credential permission denied");
        // The synthetic filesystem stores model a cloud outage by placing a
        // regular file where the remote root directory should exist. Treat its
        // IOException as an unavailable TEST transport, not as damaged KDBX.
        // Do not generalize this to production providers: local I/O failures
        // may indicate unsafe staging or a genuine on-disk integrity problem.
        if(error is IOException &&
           (provider is "FileSystemTest" or "FileSystemTestNoCas"))
            return new(CloudFailureKind.Transient,"TEMPORARY",
                provider+": isolated test transport unavailable");
        if(error is TimeoutException or TaskCanceledException or SocketException ||
           error is HttpRequestException || error is IOException && provider=="ProtonDrive")
            return new(CloudFailureKind.Transient,"TEMPORARY",
                provider+": temporary transport or CLI failure ("+error.GetType().Name+")");
        return new(CloudFailureKind.Unknown,"UNKNOWN",
            provider+": "+error.GetType().Name);
    }

    internal static bool MayAutoScan(AppState state,string cloudName,string cloudId,
        IReadOnlySet<string>? requested)
    {
        if(requested?.Contains(cloudId)==true)return true;
        return !state.Clouds.TryGetValue(cloudName,out var status) || !status.RetryPaused;
    }

    internal static TimeSpan Backoff(int attempts,int retrySeconds,CloudFailureKind kind)
    {
        var baseline=Math.Max(5,retrySeconds);
        if(kind==CloudFailureKind.RateLimited)baseline=Math.Max(baseline,60);
        var seconds=(long)baseline << Math.Min(10,Math.Max(0,attempts-1));
        return TimeSpan.FromSeconds(Math.Min(3600L,seconds));
    }

    internal static bool RecordFailure(AppState state,CloudOptions cloud,ReplicaStatus status,
        Exception error,int retrySeconds,DateTimeOffset now)
    {
        var classified=Classify(cloud.Provider,error);
        status.State=classified.Kind switch
        {
            CloudFailureKind.Authentication=>"AUTH_ERROR",
            CloudFailureKind.Transient or CloudFailureKind.RateLimited=>"OFFLINE",
            _=>"ERROR"
        };
        status.Error=classified.SafeMessage;
        status.ProblemCode=classified.Code;
        status.SuggestedAction=null;
        status.LastCheckedUtc=now;
        status.FailureSinceUtc??=now;
        status.ConsecutiveFailures=status.ConsecutiveFailures>=1024?1024:
            Math.Max(0,status.ConsecutiveFailures)+1;
        status.RetryPaused=classified.WaitsForUser;
        state.NextCloudPollUtc[cloud.CloudId]=status.RetryPaused
            ?DateTimeOffset.MaxValue:now+Backoff(status.ConsecutiveFailures,retrySeconds,classified.Kind);
        return classified.BlocksPublication;
    }

    internal static void RecordMissingCredentials(AppState state,CloudOptions cloud,
        ReplicaStatus status,DateTimeOffset now)
    {
        status.State="NEEDS_AUTH";
        status.Error="Cloud credentials are not configured";
        status.ProblemCode="AUTH_REQUIRED";
        status.SuggestedAction=null;
        status.FailureSinceUtc??=now;
        status.LastCheckedUtc=now;
        status.RetryPaused=true;
        state.NextCloudPollUtc[cloud.CloudId]=DateTimeOffset.MaxValue;
    }

    internal static void RecordSuccess(ReplicaStatus status)
    {
        status.Error=null;
        status.ProblemCode=null;
        status.SuggestedAction=null;
        status.FailureSinceUtc=null;
        status.ConsecutiveFailures=0;
        status.RetryPaused=false;
        status.RecoveryPending=false;
    }

    // Saving repaired OS-backed credentials does not automatically unpause an
    // old NEEDS_AUTH/AUTH_ERROR state. Proton is deliberately excluded: merely
    // locating the CLI does not prove that its external session is authorized.
    internal static bool ResumeAfterCredentialEdit(AppState state,CloudOptions cloud,
        bool credentialsPresent,DateTimeOffset now)
    {
        if(!credentialsPresent || cloud.Provider=="ProtonDrive" ||
           !state.Clouds.TryGetValue(cloud.Name,out var status) ||
           status.State is not ("NEEDS_AUTH" or "AUTH_ERROR"))return false;
        ResumeAfterLogin(state,cloud.Name,cloud.CloudId,now);
        return true;
    }

    // A successful login changes only the LOCAL scheduling state. The cursor,
    // DAG and backups survive; the next cycle is a mandatory full safety sweep.
    internal static void ResumeAfterLogin(AppState state,string cloudName,string cloudId,
        DateTimeOffset now)
    {
        if(!state.Clouds.TryGetValue(cloudName,out var status))
            state.Clouds[cloudName]=status=new ReplicaStatus();
        RecordSuccess(status);
        status.State="UNKNOWN";
        status.RecoveryPending=true;
        state.NextCloudPollUtc[cloudId]=now;
    }
}
