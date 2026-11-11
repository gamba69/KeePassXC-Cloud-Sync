using KeyPassXCCloudSync;

// Pure in-memory regression tests: no cloud access, no KDBX, no Python or Windows API.
static void Check(bool ok,string description)
{
    TestReporter.Check("CloudStatusTests",description,ok);
}
static ReplicaStatus State(string value)=>new(){State=value};

var saved=new AppState
{
    // Case-sensitive on purpose: deserialized state may not retain
    // a case-insensitive comparer. Historical status names are not live clouds.
    Clouds=new Dictionary<string,ReplicaStatus>(StringComparer.Ordinal)
    {
        ["Google"]=State("SYNCED"),
        ["WebDAV-Home"]=State("NEEDS_AUTH"),
        ["S3"]=State("TOPOLOGY_OK"),
        ["Old Removed Cloud"]=State("SYNCED"),
        ["Even Older Cloud"]=State("ERROR")
    }
};
var current=new[]{"Google","WebDAV-Home","S3"};
var active=ConfiguredCloudStatuses.Select(current,saved);
Check(active.Count==3,"three configured clouds, not five cached clouds");
Check(active.Count(x=>x.Status.State is "SYNCED" or "TOPOLOGY_OK")==2,
    "healthy cloud count includes only active connections");
Check(active.Count(x=>x.Status.State is "NEEDS_AUTH" or "AUTH_ERROR")==1,
    "authorization prompts include only active connections");
Check(active.All(x=>x.Name is not "Old Removed Cloud" and not "Even Older Cloud"),
    "historical clouds do not appear in tray summary");

// Removing an active connection must not keep it in the denominator or notify
// about its stale authorization failure.
var reduced=ConfiguredCloudStatuses.Select(new[]{"Google","S3"},saved);
Check(reduced.Count==2 && reduced.All(x=>x.Status.State is "SYNCED" or "TOPOLOGY_OK"),
    "removed cloud with NEEDS_AUTH does not make the tray degraded");
Check(!reduced.Any(x=>x.Status.State is "NEEDS_AUTH" or "AUTH_ERROR"),
    "removed cloud never generates an authorization popup");

var renamed=ConfiguredCloudStatuses.Select(new[]{"Google","Home Assistant","S3"},saved);
Check(renamed.Count==3 && renamed[1].Status.State=="UNKNOWN",
    "renamed cloud is untested until the new label receives a status");
Check(!renamed.Any(x=>x.Name=="WebDAV-Home"),
    "old name cannot inflate the tray count after rename");

var casing=ConfiguredCloudStatuses.Select(new[]{"google","s3"},saved);
Check(casing.All(x=>x.Status.State is "SYNCED" or "TOPOLOGY_OK"),
    "case-insensitive lookup works with a case-sensitive persisted dictionary");
Check(ConfiguredCloudStatuses.Select(new[]{"S3","s3"},saved).Count==1,
    "name-only projection does not duplicate case variants");
Check(ConfiguredCloudStatuses.Select(current,null).Count==3 &&
    ConfiguredCloudStatuses.Select(current,null).All(x=>x.Status.State=="UNKNOWN"),
    "before the first check every configured cloud is counted but not called healthy");
Check(ConfiguredCloudStatuses.Select(Array.Empty<string>(),saved).Count==0,
    "empty configuration cannot inherit phantom cached clouds");
// A successful individual retry must repair a cached global PARTIAL only
// when all CURRENT connections acknowledge the same immutable local head.
var reconciled=new AppState
{
    LocalVersionId="head-v3",
    Result="PARTIAL",
    Clouds=new Dictionary<string,ReplicaStatus>(StringComparer.Ordinal)
    {
        ["Google"]=new(){State="SYNCED",CurrentVersionId="head-v3"},
        ["S3"]=new(){State="SYNCED",CurrentVersionId="head-v3"},
        ["Removed"]=new(){State="ERROR",CurrentVersionId="head-v2"}
    }
};
Check(ConfiguredCloudStatuses.AllConfirmedAtLocalHead(new[]{"Google","S3"},reconciled),
    "stale PARTIAL may become SYNCED after all active peers confirm the local head");
reconciled.Clouds["S3"].CurrentVersionId="head-v2";
Check(!ConfiguredCloudStatuses.AllConfirmedAtLocalHead(new[]{"Google","S3"},reconciled),
    "green cloud with stale remote head must not promote global result");
reconciled.Clouds["S3"].CurrentVersionId="head-v3";
reconciled.Clouds["S3"].State="ERROR";
Check(!ConfiguredCloudStatuses.AllConfirmedAtLocalHead(new[]{"Google","S3"},reconciled),
    "a failed peer is not a successful global synchronization");
reconciled.Clouds["S3"].State="SYNCED";
reconciled.Conflict=true;
Check(!ConfiguredCloudStatuses.AllConfirmedAtLocalHead(new[]{"Google","S3"},reconciled),
    "an outstanding conflict always blocks global success");
reconciled.Conflict=false;
Check(!ConfiguredCloudStatuses.AllConfirmedAtLocalHead(Array.Empty<string>(),reconciled),
    "a configuration with no clouds is not synchronized");
Console.WriteLine("[SUMMARY] CloudStatusTests completed.");
