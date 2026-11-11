using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KeyPassXCCloudSync;

public sealed class DatabaseIdentity
{
    public int SchemaVersion {get;set;}=1;
    public string VaultId {get;set;}="";
    public DateTimeOffset CreatedUtc {get;set;}
}

public sealed class VersionInfo
{
    public string VersionId {get;set;}="";
    public string VaultId {get;set;}="";
    public string[] Parents {get;set;}=[];
    public string Sha256 {get;set;}="";
    public long Size {get;set;}
    public DateTimeOffset CreatedUtc {get;set;}
    public string InstallationId {get;set;}="";
    public string Source {get;set;}="";
    public string Provenance {get;set;}="proven";
    public string BackupName {get;set;}="";
}

public sealed class DeviceRecord
{
    public string VaultId {get;set;}="";
    public string InstallationId {get;set;}="";
    public string AcknowledgedVersionId {get;set;}="";
    public string? AcknowledgedCheckpointId {get;set;}
    public DateTimeOffset UpdatedUtc {get;set;}
}

public sealed class ReplicaStatus
{
    public string State {get;set;}="UNKNOWN";
    public string? Error {get;set;}
    public string? ProblemCode {get;set;}
    public string? SuggestedAction {get;set;}
    public DateTimeOffset? FailureSinceUtc {get;set;}
    public DateTimeOffset? LastCheckedUtc {get;set;}
    public string? CurrentSha256 {get;set;}
    public string? CurrentVersionId {get;set;}
}

public sealed class AppState
{
    public int SchemaVersion {get;set;}=2;
    public string InstallationId {get;set;}=Guid.NewGuid().ToString("N");
    public string? VaultId {get;set;}
    public DateTimeOffset? VaultCreatedUtc {get;set;}
    public string? LocalVersionId {get;set;}
    public string? LocalSha256 {get;set;}
    public DateTimeOffset? LastAttemptUtc {get;set;}
    public DateTimeOffset? LastSuccessUtc {get;set;}
    public DateTimeOffset? LastChangeUtc {get;set;}
    public string Result {get;set;}="NeverRun";
    public string? Error {get;set;}
    public bool Conflict {get;set;}
    public string[] ConflictVersionIds {get;set;}=[];
    public Dictionary<string,ReplicaStatus> Clouds {get;set;}=new(StringComparer.OrdinalIgnoreCase);
    // V2: small persistent cursors. No remote KDBX or history download on unchanged polls.
    public Dictionary<string,DateTimeOffset> NextCloudPollUtc {get;set;}=new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string,V2CloudCursor> V2Cursors {get;set;}=new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string,V2OperationTotals> V2Totals {get;set;}=new(StringComparer.OrdinalIgnoreCase);
    public int RemoteTopologyRevision {get;set;}
    public bool TopologyRefreshNeeded {get;set;}
    public bool TopologyDisabled {get;set;}
    public string? TopologyError {get;set;}
    public long LocalFileStamp {get;set;}
    public long LocalFileLength {get;set;}
    public DateTimeOffset? LastLocalContentCheckUtc {get;set;}

}

public sealed class V2CloudCursor
{
    public string? WorkingRevision {get;set;}
    public string? TopologyRevisionToken {get;set;}
    public string? WorkingVersionId {get;set;}
    public string? AnnouncedVersionId {get;set;}
    public DateTimeOffset? LastDeviceDiscoveryUtc {get;set;}
    public DateTimeOffset? LastCleanupUtc {get;set;}
    // Local scan diagnostics; not shared with peers and not part of remote protocol.
    public DateTimeOffset? LastScanUtc {get;set;}
    public DateTimeOffset? LastSuccessfulScanUtc {get;set;}
    public long? LastScanElapsedMilliseconds {get;set;}
    public Dictionary<string,string> DeviceRevisions {get;set;}=new(StringComparer.Ordinal);
    public HashSet<string> KnownVersions {get;set;}=new(StringComparer.Ordinal);
}

public sealed class V2DevicePointer
{
    public int SchemaVersion {get;set;}=2;
    public string VaultId {get;set;}="";
    public string InstallationId {get;set;}="";
    public string VersionId {get;set;}="";
    public DateTimeOffset UpdatedUtc {get;set;}
    public int TopologyRevision {get;set;}
}

// Durable local intent for a KDBX replacement. This is NOT a cloud lock or a DAG head.
// The staged merge content is kept under backup/ until the local replacement and
// history record have been durably finished (or an operator intervenes).
public sealed class LocalInstallIntent
{
    public int SchemaVersion {get;set;}=1;
    public string Kind {get;set;}=""; // "pull" or "merge"
    public string VaultId {get;set;}="";
    public string InstallationId {get;set;}="";
    public string? OldVersionId {get;set;}
    public string? OldSha256 {get;set;}
    public VersionInfo NewVersion {get;set;}=new();
}

public sealed class DeviceRevocation
{
    public string VaultId {get;set;}="";
    public string InstallationId {get;set;}="";
    public string RevokedBy {get;set;}="";
    public DateTimeOffset RevokedUtc {get;set;}
}

public sealed class CheckpointInfo
{
    public int SchemaVersion {get;set;}=1;
    public string CheckpointId {get;set;}="";
    public string VaultId {get;set;}="";
    public string AnchorVersionId {get;set;}="";
    public string[] PrunedVersions {get;set;}=[];
    public DateTimeOffset CreatedUtc {get;set;}
}

[JsonSerializable(typeof(V2OperationTotals))]
[JsonSerializable(typeof(V2DevicePointer))]
[JsonSerializable(typeof(V2CloudCursor))]
[JsonSerializable(typeof(LocalInstallIntent))]
[JsonSerializable(typeof(DeviceRevocation))]
[JsonSerializable(typeof(CheckpointInfo))]
[JsonSerializable(typeof(DeviceRecord))]
[JsonSerializable(typeof(DatabaseIdentity))]
[JsonSerializable(typeof(VersionInfo))]
[JsonSerializable(typeof(AppState))]
[JsonSerializable(typeof(string))]
internal partial class SyncJson : JsonSerializerContext { }

public static class SyncStorage
{
    public static AppState LoadState(string path)
    {
        if(!File.Exists(path))return new AppState();
        var state=JsonSerializer.Deserialize(File.ReadAllText(path),SyncJson.Default.AppState)
            ?? throw new SyncSafetyException("Local state is empty");
        if(state.SchemaVersion!=2 || !Guid.TryParseExact(state.InstallationId,"N",out _) ||
           state.LocalSha256 is not null && !Hashes.Valid(state.LocalSha256))
            throw new SyncSafetyException("Invalid local state: refusing automatic reset");
        return state;
    }
    public static void SaveState(string path,AppState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                JsonSerializer.Serialize(file,state,SyncJson.Default.AppState);
                file.Flush(true);
            }
            if(File.Exists(path))File.Replace(temp,path,null);
            else File.Move(temp,path);
        }
        finally { if(File.Exists(temp))File.Delete(temp); }
    }
    public static void SaveJson<T>(string path,T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> kind)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        JsonSerializer.Serialize(file,value,kind);
        file.Flush(true);
    }
    // The intent is the write-ahead record: never expose a truncated JSON journal
    // after abrupt process termination, and flush before replacing the KDBX.
    public static void SaveJsonAtomic<T>(string path,T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> kind)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                JsonSerializer.Serialize(file,value,kind);
                file.Flush(true);
            }
            if(File.Exists(path))File.Replace(temp,path,null);
            else File.Move(temp,path);
        }
        finally {if(File.Exists(temp))File.Delete(temp);}
    }
    public static string NewName()
    {
        const string alphabet="ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var random=RandomNumberGenerator.GetBytes(8);
        return DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+new string(random.Select(x=>alphabet[x%alphabet.Length]).ToArray());
    }
    public static VersionInfo NewVersion(string vault,string installation,string sha,long size,string[] parents,string source,string provenance)
        =>new() { VersionId=Guid.NewGuid().ToString("N"),VaultId=vault,InstallationId=installation,
            Sha256=sha,Size=size,Parents=parents,Source=source,Provenance=provenance,
            CreatedUtc=DateTimeOffset.UtcNow,BackupName=NewName()+".kdbx" };
}

public static class Graph
{
    public static bool IsAncestor(string ancestor,string descendant,IReadOnlyDictionary<string,VersionInfo> versions)
    {
        if(ancestor==descendant)return true;
        var stack=new Stack<string>();stack.Push(descendant);
        var seen=new HashSet<string>(StringComparer.Ordinal);
        while(stack.TryPop(out var next))
        {
            if(!seen.Add(next))continue;
            if(!versions.TryGetValue(next,out var version))continue;
            foreach(var p in version.Parents)
            {
                if(p==ancestor)return true;
                stack.Push(p);
            }
        }
        return false;
    }
    public static string[] Heads(IReadOnlyDictionary<string,VersionInfo> versions)
    {
        var parents=versions.Values.SelectMany(v=>v.Parents).ToHashSet(StringComparer.Ordinal);
        return versions.Keys.Where(id=>!parents.Contains(id)).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
    }
    public static void Validate(IReadOnlyDictionary<string,VersionInfo> versions,string vault,
        IReadOnlyDictionary<string,CheckpointInfo>? checkpoints=null)
    {
        var backupNames=new HashSet<string>(StringComparer.Ordinal);
        foreach(var (key,v) in versions)
        {
            if(key!=v.VersionId || !Guid.TryParseExact(key,"N",out _) || v.VaultId!=vault ||
               !Hashes.Valid(v.Sha256) || v.Size<=0 || v.BackupName.Length==0 ||
               !v.BackupName.EndsWith(".kdbx",StringComparison.Ordinal) ||
               Path.GetFileName(v.BackupName)!=v.BackupName ||
               v.Parents.Distinct(StringComparer.Ordinal).Count()!=v.Parents.Length ||
               v.Parents.Contains(v.VersionId) || !backupNames.Add(v.BackupName) ||
               v.Parents.Any(parent=>!versions.ContainsKey(parent) &&
                   !(checkpoints?.Values.Any(cp=>cp.AnchorVersionId==v.VersionId &&
                       cp.PrunedVersions.Contains(parent,StringComparer.Ordinal))??false)))
                throw new SyncSafetyException("Invalid version graph metadata or missing ancestry");
        }
        // A remotely supplied cycle could otherwise make head detection inconclusive.
        var complete=new HashSet<string>(StringComparer.Ordinal);
        var active=new HashSet<string>(StringComparer.Ordinal);
        foreach(var id in versions.Keys)
        {
            if(complete.Contains(id))continue;
            var stack=new Stack<(string id,bool completed)>();
            stack.Push((id,false));
            while(stack.TryPop(out var entry))
            {
                if(entry.completed)
                {
                    active.Remove(entry.id);
                    complete.Add(entry.id);
                    continue;
                }
                if(complete.Contains(entry.id))continue;
                if(!active.Add(entry.id))
                    throw new SyncSafetyException("Version graph contains a cycle");
                stack.Push((entry.id,true));
                foreach(var parent in versions[entry.id].Parents)
                {
                    if(!versions.ContainsKey(parent))continue;
                    if(active.Contains(parent))
                        throw new SyncSafetyException("Version graph contains a cycle");
                    if(!complete.Contains(parent))stack.Push((parent,false));
                }
            }
        }
    }
}
