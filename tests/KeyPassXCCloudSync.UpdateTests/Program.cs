using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using KeyPassXCCloudSync;

static void Check(bool condition,string message)=>TestReporter.Check("UpdateTests",message,condition);
static void Rejected(Action action,string message)
{
    bool failed=false;
    try { action(); }
    catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException)
        { failed=true; }
    Check(failed,message);
}
static string FreshFolder()
{
    var dir=Path.Combine(Path.GetTempPath(),"KpSync-UpdateTests-"+Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);return dir;
}
static byte[] Zip(params (string Name,string Text)[] files)
{
    using var stream=new MemoryStream();
    using (var zip=new ZipArchive(stream,ZipArchiveMode.Create,true))
    {
        foreach (var (name,text) in files)
        {
            var entry=zip.CreateEntry(name);
            using var output=new StreamWriter(entry.Open(),new UTF8Encoding(false));
            output.Write(text);
        }
    }
    return stream.ToArray();
}
static byte[] GoodZip()=>Zip(
    ("KeyPassXCCloudSync.exe","fake-native-aot"),
    ("KeyPassXCCloudSync.ini.example","example"),
    ("USER_GUIDE.md","guide"),
    ("LICENSE","MIT"),
    ("libSkiaSharp.dll","native binary"));
static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

Check(ReleaseUpdatePolicy.ParseVersion("v7.2.0")==new Version(7,2,0),"optional v tag");
Check(ReleaseUpdatePolicy.ParseVersion("7.12.3")==new Version(7,12,3),"numeric tag parsing");
Check(ReleaseUpdatePolicy.IsNewer("7.10.0","7.9.99"),"numeric version ordering");
Check(!ReleaseUpdatePolicy.IsNewer("7.2.0","7.2.0"),"same version excluded");
Check(!ReleaseUpdatePolicy.IsNewer("7.1.99","7.2.0"),"downgrade excluded");
foreach (var tag in new[]{"v7.2", "7.2.0-rc1","v7.2.0+local","../7.2.0","7.2.0/evil","7.2.0.1","2147483648.1.1",""})
    Rejected(()=>ReleaseUpdatePolicy.ParseVersion(tag),"reject malformed tag: "+tag);
Check(ReleaseUpdatePolicy.ArchiveName("v7.2.1")=="KeyPassXCCloudSync-7.2.1-win-x64.zip",
    "release name independent of optional v");
Check(ReleaseUpdatePolicy.ChecksumName("7.2.1")=="KeyPassXCCloudSync-7.2.1-win-x64.sha256",
    "checksum filename binding");
var zipData=GoodZip();var hash=Hash(zipData);var zipName=ReleaseUpdatePolicy.ArchiveName("7.2.1");
Check(ReleaseUpdatePolicy.ParseChecksum(hash+"  "+zipName+"\n",zipName)==hash,
    "valid SHA-256 release entry");
Rejected(()=>ReleaseUpdatePolicy.ParseChecksum(hash+"  other.zip",zipName),"wrong checksum filename rejected");
Rejected(()=>ReleaseUpdatePolicy.ParseChecksum(hash+" *"+zipName,zipName),"checksum alternate format rejected");
Rejected(()=>ReleaseUpdatePolicy.ParseChecksum("sha256:"+hash+"  "+zipName,zipName),"checksum prefix rejected");
Rejected(()=>ReleaseUpdatePolicy.ParseChecksum(hash+"  "+zipName+"\nextra",zipName),"checksum extra line rejected");
Check(ReleaseUpdatePolicy.IsOwnedFile("SkiaSharp.dll"),"runtime DLL allowed");
Check(ReleaseUpdatePolicy.IsOwnedFile("KeyPassXCCloudSync.runtimeconfig.json"),
    "only application runtime configuration may be updated");
foreach (var bad in new[]{"KeyPassXCCloudSync.ini","foo.kdbx","state.json","tools/proton-drive.exe","proton-drive.exe","../good.dll","folder/good.dll","evil.dll/../good.dll","other.exe","file.pdb"})
    Check(!ReleaseUpdatePolicy.IsOwnedFile(bad),"never install private/user file: "+bad);
var temp=FreshFolder();
try
{
    var archive=Path.Combine(temp,"release.zip");File.WriteAllBytes(archive,zipData);
    ReleaseUpdatePolicy.VerifyChecksum(archive,hash+"  "+zipName+"\n",zipName,"sha256:"+hash);
    Check(true,"ZIP and API/checksum digests agree");
    Rejected(()=>ReleaseUpdatePolicy.VerifyChecksum(archive,hash+"  "+zipName,zipName,"sha256:"+new string('0',64)),
        "API asset digest mismatch rejected");
    Rejected(()=>ReleaseUpdatePolicy.VerifyChecksum(archive,new string('0',64)+"  "+zipName,zipName,null),
        "modified archive rejected by published checksum");
    var output=Path.Combine(temp,"extract");
    var entries=ReleaseUpdatePolicy.ExtractVerifiedArchive(archive,output);
    Check(entries.Count==5 && File.ReadAllText(Path.Combine(output,"LICENSE"))=="MIT",
        "extract exactly permitted application files");
    Check(!File.Exists(Path.Combine(output,"state.json")),"never create sync state");
    File.WriteAllBytes(archive,Zip(("KeyPassXCCloudSync.exe","x"),("LICENSE","l"),
        ("USER_GUIDE.md","g"),("KeyPassXCCloudSync.ini.example","i"),("../outside.dll","evil")));
    Rejected(()=>ReleaseUpdatePolicy.ExtractVerifiedArchive(archive,Path.Combine(temp,"bad")),
        "path traversal rejected before extraction");
    File.WriteAllBytes(archive,Zip(("KeyPassXCCloudSync.exe","x"),("LICENSE","l"),
        ("USER_GUIDE.md","g"),("KeyPassXCCloudSync.ini.example","i"),("state.json","evil")));
    Rejected(()=>ReleaseUpdatePolicy.ExtractVerifiedArchive(archive,Path.Combine(temp,"bad2")),
        "user state entry rejected before extraction");
    File.WriteAllBytes(archive,Zip(("KeyPassXCCloudSync.exe","x"),("LICENSE","l"),
        ("USER_GUIDE.md","g"),("KeyPassXCCloudSync.ini.example","i"),("LIB.dll","a"),("lib.DLL","b")));
    Rejected(()=>ReleaseUpdatePolicy.ExtractVerifiedArchive(archive,Path.Combine(temp,"bad3")),
        "case-insensitive duplicate DLL rejected");
    File.WriteAllBytes(archive,Zip(("KeyPassXCCloudSync.exe","x")));
    Rejected(()=>ReleaseUpdatePolicy.ExtractVerifiedArchive(archive,Path.Combine(temp,"bad4")),
        "incomplete release rejected");

    var statement="""
      {"_type":"https://in-toto.io/Statement/v1",
       "predicateType":"https://in-toto.io/attestation/release/v0.1",
       "predicate":{"tag":"7.2.1","repository":"gamba69/KeePassXC-Cloud-Sync",
                    "repositoryId":"1454413194",
                    "purl":"pkg:github/gamba69/KeePassXC-Cloud-Sync@7.2.1"},
       "subject":[{"uri":"pkg:github/gamba69/KeePassXC-Cloud-Sync@7.2.1",
                   "digest":{"sha1":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}},
                  {"name":"KeyPassXCCloudSync-7.2.1-win-x64.zip","digest":{"sha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}}]}
      """;
    ReleaseUpdatePolicy.VerifySignedStatement(statement,"7.2.1",new string('a',40),zipName,new string('b',64));
    Check(true,"signed statement binds tag, ref SHA and asset digest");
    // GitHub Release Attestations use distinct subject shapes: tag = uri + sha1,
    // archive = name + sha256. Missing optional names must not throw.
    Check(!statement.Contains("\"name\":\"pkg:github",StringComparison.Ordinal),
        "release tag subject uses URI rather than name");
    Rejected(()=>ReleaseUpdatePolicy.VerifySignedStatement("{invalid-json", "7.2.1",
        new string('a',40),zipName,new string('b',64)),
        "malformed attestation JSON rejected as invalid data");
    Rejected(()=>ReleaseUpdatePolicy.VerifySignedStatement("[]", "7.2.1",
        new string('a',40),zipName,new string('b',64)),
        "attestation with array root rejected");
    Rejected(()=>ReleaseUpdatePolicy.VerifySignedStatement(
        statement.Replace("\"predicate\":", "\"other\":",StringComparison.Ordinal),
        "7.2.1",new string('a',40),zipName,new string('b',64)),
        "missing release predicate rejected without unhandled exception");
    Rejected(()=>ReleaseUpdatePolicy.VerifySignedStatement(
        statement.Replace("\"subject\":[", "\"subject\":{},\"other\":[",StringComparison.Ordinal),
        "7.2.1",new string('a',40),zipName,new string('b',64)),
        "non-array release subjects rejected");
    Rejected(()=>ReleaseUpdatePolicy.VerifySignedStatement(
        statement.Replace("\"digest\":{\"sha1\":", "\"notDigest\":{\"sha1\":",StringComparison.Ordinal),
        "7.2.1",new string('a',40),zipName,new string('b',64)),
        "missing tag subject digest rejected");
    Rejected(()=>ReleaseUpdatePolicy.VerifySignedStatement(
        statement.Replace("\"name\":\""+zipName+"\"", "\"name\":123",StringComparison.Ordinal),
        "7.2.1",new string('a',40),zipName,new string('b',64)),
        "non-string release asset name rejected");
    Rejected(()=>ReleaseUpdatePolicy.VerifySignedStatement(
        statement.Replace("\"sha1\":\""+new string('a',40)+"\"", "\"sha1\":false",StringComparison.Ordinal),
        "7.2.1",new string('a',40),zipName,new string('b',64)),
        "non-string Git ref digest rejected");

    Rejected(()=>ReleaseUpdatePolicy.VerifySignedStatement(statement,"7.2.2",new string('a',40),zipName,new string('b',64)),
        "signed statement mismatched tag rejected");
    Rejected(()=>ReleaseUpdatePolicy.VerifySignedStatement(statement,"7.2.1",new string('c',40),zipName,new string('b',64)),
        "signed statement mismatched Git ref rejected");
    Rejected(()=>ReleaseUpdatePolicy.VerifySignedStatement(statement,"7.2.1",new string('a',40),zipName,new string('c',64)),
        "signed statement mismatched ZIP digest rejected");
    Rejected(()=>ReleaseUpdatePolicy.VerifySignedStatement(statement,"7.2.1",new string('a',40),"wrong.zip",new string('b',64)),
        "signed statement mismatched asset filename rejected");
    Rejected(()=>ReleaseUpdatePolicy.VerifySignedStatement(
        statement.Replace("1454413194","9999999999",StringComparison.Ordinal),
        "7.2.1",new string('a',40),zipName,new string('b',64)),
        "signed release from a different repository ID rejected");
    Rejected(()=>ReleaseUpdatePolicy.VerifySignedStatement(
        statement.Replace("gamba69/KeePassXC-Cloud-Sync","other/fork",StringComparison.Ordinal),
        "7.2.1",new string('a',40),zipName,new string('b',64)),
        "signed release from a different repository rejected");

    Check(Encoding.UTF8.GetString(RawSnappy.Decode(new byte[]{3,8,97,98,99},100))=="abc",
        "raw Snappy literal");
    Check(Encoding.UTF8.GetString(RawSnappy.Decode(new byte[]{8,4,97,98,22,2,0},100))=="abababab",
        "overlapping raw Snappy back-reference");
    Rejected(()=>RawSnappy.Decode(new byte[]{3,22,0,0},100),"Snappy zero offset rejected");
    Rejected(()=>RawSnappy.Decode(new byte[]{3,8,97},100),"Snappy truncated literal rejected");
    Rejected(()=>RawSnappy.Decode(new byte[]{255,255,255,255,127},100),"Snappy oversized declared output rejected");

    // Network fixtures must be newer than the compiled updater. A fixed
    // version here becomes stale on the next patch release and skips download.
    var installedVersion=typeof(WindowsUpdateService).Assembly.GetName().Version
        ?? throw new InvalidOperationException("Application assembly version is unavailable.");
    var installedTag=installedVersion.ToString(3);
    var releaseTag=new Version(installedVersion.Major,installedVersion.Minor,
        checked(installedVersion.Build+1)).ToString(3);
    Check(ReleaseUpdatePolicy.IsNewer(releaseTag,installedTag),
        "test release is newer than the compiled application");
    using (var current=new WindowsUpdateService(
        new ScriptedHandler(zipData,hash,installedTag,false),
        Path.Combine(temp,"same-version")))
    {
        await current.CheckAsync(true);
        Check(!current.IsReady && current.Snapshot.Status=="current",
            "same installed release is not downloaded");
    }
    var root=Path.Combine(temp,"updater");
    var pending=Path.Combine(root,"pending-"+releaseTag);
    var handler=new ScriptedHandler(zipData,hash,releaseTag,immutable:false);
    using var updates=new WindowsUpdateService(handler,root);
    Check(updates.Preferences.CheckAutomatically && updates.Preferences.DownloadAutomatically &&
        updates.Preferences.Installation=="exit","safe default preferences");
    updates.SavePreferences(new UpdatePreferences(true,true,"manual"));
    using (var saved=new WindowsUpdateService(new ScriptedHandler(zipData,hash,releaseTag,false),root))
        Check(saved.Preferences.Installation=="manual","local installation preference survives restart");
    await updates.CheckAsync(true);
    Check(updates.IsReady && updates.Snapshot.Status=="ready" && updates.Snapshot.Unverified &&
        updates.Snapshot.Detail.Contains("Immutability",StringComparison.Ordinal),
        "mutable GitHub release downloads but explicitly warns user");
    Check(File.Exists(Path.Combine(root,"ready.txt")),"pending update survives restart");
    using (var restored=new WindowsUpdateService(new ScriptedHandler(zipData,hash,releaseTag,false),root))
        Check(restored.IsReady && restored.Snapshot.Unverified,"restart restores mutable warning and verified package");
    Check(Directory.GetFiles(Path.Combine(pending,"payload")).Length==5,
        "only bundled runtime files were staged");
    await updates.CheckAsync(true);
    Check(updates.IsReady && updates.Snapshot.Status=="ready",
        "repeated check retains already staged package");
    var stagedExe=Path.Combine(pending,"payload",ReleaseUpdatePolicy.Executable);
    File.AppendAllText(stagedExe,"tamper");
    Rejected(()=>WindowsUpdateService.VerifyPayloadManifest(pending),
        "staged EXE modifications rejected before install");
    File.WriteAllText(stagedExe,"fake-native-aot");
    WindowsUpdateService.VerifyPayloadManifest(pending);
    Check(true,"verified manifest restores when original bytes are restored");

    // Full installation and rollback tests operate only in an isolated fake
    // installation directory; the real application and KDBX are never touched.
    string PrepareTransaction(string stage)
    {
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage,"verified.txt"),releaseTag);
        ReleaseUpdatePolicy.ExtractVerifiedArchive(Path.Combine(temp,"transaction.zip"),
            Path.Combine(stage,"payload"));
        WindowsUpdateService.WritePayloadManifest(stage);
        return stage;
    }
    File.WriteAllBytes(Path.Combine(temp,"transaction.zip"),zipData);
    var installRoot=Path.Combine(temp,"installed");Directory.CreateDirectory(installRoot);
    var userTools=Path.Combine(installRoot,"tools");Directory.CreateDirectory(userTools);
    File.WriteAllText(Path.Combine(installRoot,ReleaseUpdatePolicy.Executable),"old-app");
    File.WriteAllText(Path.Combine(installRoot,"KeyPassXCCloudSync.ini"),"SyncEnabled=true");
    File.WriteAllText(Path.Combine(installRoot,"state.json"),"persisted-state");
    File.WriteAllText(Path.Combine(installRoot,"Passwords.kdbx"),"encrypted-database");
    File.WriteAllText(Path.Combine(userTools,"proton-drive.exe"),"external-cli");
    var firstStage=PrepareTransaction(Path.Combine(temp,"first-stage"));
    var firstRoot=Path.Combine(temp,"first-root");
    WindowsUpdateService.ApplyVerifiedFiles(firstStage,installRoot,firstRoot);
    Check(File.ReadAllText(Path.Combine(installRoot,ReleaseUpdatePolicy.Executable))=="fake-native-aot",
        "transaction installs the verified new executable");
    var ownershipFirst=WindowsUpdateService.InstalledFilesManifestPath(firstRoot,installRoot);
    Check(File.Exists(ownershipFirst) && File.ReadAllText(ownershipFirst).Contains("libSkiaSharp.dll",StringComparison.Ordinal),
        "successful install records only the verified package files for later cleanup");
    Check(File.ReadAllText(Path.Combine(firstRoot,"previous",ReleaseUpdatePolicy.Executable))=="old-app",
        "transaction preserves exactly one previous EXE");
    Check(!File.Exists(Path.Combine(firstRoot,"installing.txt")) &&
        File.Exists(Path.Combine(firstRoot,"completed.txt")),
        "completed transaction clears recovery journal");
    Check(File.ReadAllText(Path.Combine(installRoot,"KeyPassXCCloudSync.ini"))=="SyncEnabled=true" &&
        File.ReadAllText(Path.Combine(installRoot,"Passwords.kdbx"))=="encrypted-database" &&
        File.ReadAllText(Path.Combine(installRoot,"state.json"))=="persisted-state" &&
        File.ReadAllText(Path.Combine(userTools,"proton-drive.exe"))=="external-cli",
        "KDBX, INI, sync state, settings and Proton are unchanged after update");
    // Reconstruct an interrupted startup: the installation committed its
    // backup, but the new EXE became unusable before health confirmation.
    File.WriteAllText(Path.Combine(installRoot,ReleaseUpdatePolicy.Executable),"broken-executable");
    WindowsUpdateService.RestoreBackup(firstStage,installRoot,firstRoot);
    Check(File.ReadAllText(Path.Combine(installRoot,ReleaseUpdatePolicy.Executable))=="old-app",
        "crash recovery restores original EXE from previous backup");
    Check(!File.Exists(Path.Combine(installRoot,"libSkiaSharp.dll")) &&
        File.ReadAllText(Path.Combine(userTools,"proton-drive.exe"))=="external-cli",
        "crash recovery removes introduced DLL but preserves Proton CLI");
    Check(!File.Exists(ownershipFirst),
        "rollback restores absence of the ownership manifest for an untracked old installation");
    // Inject an I/O failure AFTER the first file replacement, then assert
    // that originals are restored and newly introduced files are removed.
    File.WriteAllText(Path.Combine(installRoot,ReleaseUpdatePolicy.Executable),"old-again");
    File.Delete(Path.Combine(installRoot,"libSkiaSharp.dll"));
    var secondStage=PrepareTransaction(Path.Combine(temp,"second-stage"));
    var secondRoot=Path.Combine(temp,"second-root");
    int replaced=0;bool rollbackError=false;
    try
    {
        WindowsUpdateService.ApplyVerifiedFiles(secondStage,installRoot,secondRoot,_=>
        {
            if (++replaced==2) throw new IOException("Simulated disk failure");
        });
    }
    catch (IOException) { rollbackError=true; }
    Check(rollbackError && replaced==2,"injected mid-update failure propagates");
    Check(File.ReadAllText(Path.Combine(installRoot,ReleaseUpdatePolicy.Executable))=="old-again",
        "rollback restores previous executable after partial replacement");
    Check(!File.Exists(Path.Combine(installRoot,"libSkiaSharp.dll")),
        "rollback removes only DLL newly introduced by failed update");
    Check(File.ReadAllText(Path.Combine(installRoot,"KeyPassXCCloudSync.ini"))=="SyncEnabled=true" &&
        File.ReadAllText(Path.Combine(installRoot,"Passwords.kdbx"))=="encrypted-database",
        "rollback does not damage user configuration or database");
    Check(!File.Exists(Path.Combine(secondRoot,"installing.txt")),
        "successful automatic rollback clears installation journal");
    var thirdStage=PrepareTransaction(Path.Combine(temp,"third-stage"));
    var thirdRoot=Path.Combine(temp,"third-root");
    bool rejectedBoot=false;
    try
    {
        WindowsUpdateService.ApplyVerifiedFiles(thirdStage,installRoot,thirdRoot,
            validateNew:()=>false);
    }
    catch(IOException){rejectedBoot=true;}
    Check(rejectedBoot &&
        File.ReadAllText(Path.Combine(installRoot,ReleaseUpdatePolicy.Executable))=="old-again",
        "new EXE startup smoke failure automatically rolls back");
    Check(File.ReadAllText(Path.Combine(installRoot,"Passwords.kdbx"))=="encrypted-database" &&
        File.ReadAllText(Path.Combine(installRoot,"KeyPassXCCloudSync.ini"))=="SyncEnabled=true",
        "startup smoke rollback preserves KDBX and INI");


    // Ownership cleanup is exercised through real staged ZIPs and transactions.
    // An update from a legacy portable installation has no ownership manifest;
    // it must never guess which existing DLLs belong to an older release.
    string ManagedStage(string name,byte[] archiveBytes)
    {
        var path=Path.Combine(temp,name);
        Directory.CreateDirectory(path);
        var zipFile=Path.Combine(temp,name+".zip");
        File.WriteAllBytes(zipFile,archiveBytes);
        File.WriteAllText(Path.Combine(path,"verified.txt"),releaseTag);
        ReleaseUpdatePolicy.ExtractVerifiedArchive(zipFile,Path.Combine(path,"payload"));
        WindowsUpdateService.WritePayloadManifest(path);
        return path;
    }
    var managedRoot=Path.Combine(temp,"managed-root");
    var managedTarget=Path.Combine(temp,"managed-target");
    Directory.CreateDirectory(managedTarget);
    File.WriteAllText(Path.Combine(managedTarget,ReleaseUpdatePolicy.Executable),"manual-old-app");
    File.WriteAllText(Path.Combine(managedTarget,"user.dll"),"user-private-extension");
    File.WriteAllText(Path.Combine(managedTarget,"legacy.dll"),"untracked-legacy");
    File.WriteAllText(Path.Combine(managedTarget,"KeyPassXCCloudSync.ini"),"private-settings");
    File.WriteAllText(Path.Combine(managedTarget,"vault.kdbx"),"private-encrypted-vault");
    var managedOld=ManagedStage("managed-old",Zip(
        ("KeyPassXCCloudSync.exe","managed-app-v1"),
        ("KeyPassXCCloudSync.ini.example","example-v1"),
        ("USER_GUIDE.md","guide-v1"),("LICENSE","MIT"),
        ("libSkiaSharp.dll","native-v1"),("oldA.dll","old-a"),("oldB.dll","old-b"),
        ("KeyPassXCCloudSync.runtimeconfig.json","old-runtime")));
    WindowsUpdateService.ApplyVerifiedFiles(managedOld,managedTarget,managedRoot);
    Check(File.ReadAllText(Path.Combine(managedTarget,"legacy.dll"))=="untracked-legacy" &&
        File.ReadAllText(Path.Combine(managedTarget,"user.dll"))=="user-private-extension",
        "first managed update leaves unknown legacy and user DLLs untouched");
    var ownedManifest=WindowsUpdateService.InstalledFilesManifestPath(managedRoot,managedTarget);
    var ownershipOriginal=File.ReadAllText(ownedManifest);
    Check(ownershipOriginal.Contains("oldA.dll",StringComparison.Ordinal) &&
        ownershipOriginal.Contains("oldB.dll",StringComparison.Ordinal) &&
        !ownershipOriginal.Contains("legacy.dll",StringComparison.Ordinal),
        "ownership manifest lists only files installed by verified updater");
    var managedNew=ManagedStage("managed-new",GoodZip());
    var injectedDeletion=false;
    try
    {
        WindowsUpdateService.ApplyVerifiedFiles(managedNew,managedTarget,managedRoot,
            beforeDelete:name=>
            {
                if (name=="oldB.dll") throw new IOException("Simulated obsolete-file deletion failure");
            });
    }
    catch(IOException) { injectedDeletion=true; }
    Check(injectedDeletion &&
        File.ReadAllText(Path.Combine(managedTarget,"oldA.dll"))=="old-a" &&
        File.ReadAllText(Path.Combine(managedTarget,"oldB.dll"))=="old-b" &&
        File.ReadAllText(Path.Combine(managedTarget,"KeyPassXCCloudSync.runtimeconfig.json"))=="old-runtime" &&
        File.ReadAllText(ownedManifest)==ownershipOriginal,
        "partial obsolete-file removal rolls back deleted runtime files and manifest");
    Check(!File.Exists(Path.Combine(managedRoot,"installing.txt")) &&
        File.ReadAllText(Path.Combine(managedTarget,"user.dll"))=="user-private-extension",
        "failed cleanup leaves no recovery journal and preserves unowned DLL");
    WindowsUpdateService.ApplyVerifiedFiles(managedNew,managedTarget,managedRoot);
    Check(!File.Exists(Path.Combine(managedTarget,"oldA.dll")) &&
        !File.Exists(Path.Combine(managedTarget,"oldB.dll")) &&
        !File.Exists(Path.Combine(managedTarget,"KeyPassXCCloudSync.runtimeconfig.json")),
        "successful update removes obsolete DLLs and optional runtime configuration");
    Check(File.Exists(Path.Combine(managedRoot,"previous","oldA.dll")) &&
        File.Exists(Path.Combine(managedRoot,"previous","oldB.dll")) &&
        File.Exists(Path.Combine(managedRoot,"previous","KeyPassXCCloudSync.runtimeconfig.json")),
        "deleted DLLs and runtime files are backed up for recovery");
    Check(!File.ReadAllText(ownedManifest).Contains("oldA.dll",StringComparison.Ordinal) &&
        !File.ReadAllText(ownedManifest).Contains("oldB.dll",StringComparison.Ordinal),
        "ownership manifest advances to exactly the current release files");
    Check(File.ReadAllText(Path.Combine(managedTarget,"legacy.dll"))=="untracked-legacy" &&
        File.ReadAllText(Path.Combine(managedTarget,"user.dll"))=="user-private-extension" &&
        File.ReadAllText(Path.Combine(managedTarget,"KeyPassXCCloudSync.ini"))=="private-settings" &&
        File.ReadAllText(Path.Combine(managedTarget,"vault.kdbx"))=="private-encrypted-vault",
        "cleanup never touches unmanaged DLLs, INI or KDBX");
    WindowsUpdateService.RestoreBackup(managedNew,managedTarget,managedRoot);
    Check(File.ReadAllText(Path.Combine(managedTarget,"oldA.dll"))=="old-a" &&
        File.ReadAllText(Path.Combine(managedTarget,"oldB.dll"))=="old-b" &&
        File.ReadAllText(Path.Combine(managedTarget,"KeyPassXCCloudSync.runtimeconfig.json"))=="old-runtime" &&
        File.ReadAllText(ownedManifest)==ownershipOriginal,
        "interrupted-start recovery restores deleted runtime files and ownership");

    // A user-modified, previously owned DLL is intentionally left behind.
    File.WriteAllText(Path.Combine(managedTarget,"oldA.dll"),"customized-by-user");
    var managedModified=ManagedStage("managed-modified",GoodZip());
    WindowsUpdateService.ApplyVerifiedFiles(managedModified,managedTarget,managedRoot);
    Check(File.ReadAllText(Path.Combine(managedTarget,"oldA.dll"))=="customized-by-user" &&
        !File.Exists(Path.Combine(managedTarget,"oldB.dll")),
        "changed owned DLL survives cleanup while unchanged obsolete DLL is deleted");
    Check(!File.ReadAllText(ownedManifest).Contains("oldA.dll",StringComparison.Ordinal),
        "preserved customized DLL is no longer marked as updater-owned");
    WindowsUpdateService.RestoreBackup(managedModified,managedTarget,managedRoot);
    Check(File.ReadAllText(Path.Combine(managedTarget,"oldA.dll"))=="customized-by-user" &&
        File.ReadAllText(Path.Combine(managedTarget,"oldB.dll"))=="old-b",
        "recovery keeps modified DLL bytes and restores removed unchanged DLL");

    // Ownership belongs to one portable directory, not the entire Windows user.
    var anotherTarget=Path.Combine(temp,"another-portable-copy");
    Directory.CreateDirectory(anotherTarget);
    File.WriteAllText(Path.Combine(anotherTarget,ReleaseUpdatePolicy.Executable),"second-old-app");
    File.WriteAllText(Path.Combine(anotherTarget,"oldB.dll"),"another-user-file");
    var anotherStage=ManagedStage("another-stage",GoodZip());
    WindowsUpdateService.ApplyVerifiedFiles(anotherStage,anotherTarget,managedRoot);
    Check(File.ReadAllText(Path.Combine(anotherTarget,"oldB.dll"))=="another-user-file" &&
        WindowsUpdateService.InstalledFilesManifestPath(managedRoot,anotherTarget)!=ownedManifest,
        "ownership is isolated between independent portable installs");

    // Corrupt ownership cannot authorize deletion or modify target files.
    File.WriteAllText(ownedManifest,ownershipOriginal+"../outside.dll\t"+new string('a',64)+"\n");
    var rejectedOwnership=false;
    try { WindowsUpdateService.ApplyVerifiedFiles(
        ManagedStage("invalid-manifest",GoodZip()),managedTarget,managedRoot); }
    catch(InvalidDataException) { rejectedOwnership=true; }
    Check(rejectedOwnership && File.Exists(Path.Combine(managedTarget,"oldB.dll")) &&
        !File.Exists(Path.Combine(managedRoot,"installing.txt")),
        "malformed ownership manifest fails before any file is replaced or removed");
    File.WriteAllText(ownedManifest,ownershipOriginal);
    var duplicateLine=ownershipOriginal.Split('\n',StringSplitOptions.RemoveEmptyEntries)[0];
    File.AppendAllText(ownedManifest,duplicateLine+"\n");
    Rejected(()=>WindowsUpdateService.ApplyVerifiedFiles(
        ManagedStage("duplicate-manifest",GoodZip()),managedTarget,managedRoot),
        "duplicate ownership manifest entry is rejected");

    async Task RunBad(string label,ScriptedHandler provider)
    {
        var badRoot=Path.Combine(temp,Guid.NewGuid().ToString("N"));
        using var service=new WindowsUpdateService(provider,badRoot);
        await service.CheckAsync(true);
        Check(!service.IsReady && service.Snapshot.Status=="error",label);
    }
    await RunBad("missing immutable status fails closed",new ScriptedHandler(zipData,hash,releaseTag,null));
    await RunBad("mismatched API SHA fails closed",new ScriptedHandler(zipData,new string('0',64),releaseTag,false));
    await RunBad("malformed release URL rejected",new ScriptedHandler(zipData,hash,releaseTag,false,true));
    await RunBad("missing SHA asset rejected",new ScriptedHandler(zipData,hash,releaseTag,false,false,true));
    // Immutable=true must never silently turn into mutable on an attestation error.
    await RunBad("immutable release with missing attestation blocked",new ScriptedHandler(zipData,hash,releaseTag,true));
}
finally { Directory.Delete(temp,true); }
Console.WriteLine("[SUMMARY] UpdateTests completed.");

sealed class ScriptedHandler(byte[] zip,string checksum,string tag,bool? immutable,
    bool offHost=false,bool omitChecksum=false):HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        var url=request.RequestUri?.AbsoluteUri??"";
        if (url.EndsWith("/releases/latest",StringComparison.Ordinal))
        {
            string zipName=ReleaseUpdatePolicy.ArchiveName(tag);
            string shaName=ReleaseUpdatePolicy.ChecksumName(tag);
            string host=offHost?"https://example.org/evil.zip":
                "https://github.com/gamba69/KeePassXC-Cloud-Sync/releases/download/"+tag+"/"+zipName;
            string shaUrl="https://github.com/gamba69/KeePassXC-Cloud-Sync/releases/download/"+tag+"/"+shaName;
            var assets="{\"name\":\""+zipName+"\",\"browser_download_url\":\""+host+"\",\"digest\":\"sha256:"+checksum+"\"}"+
                (omitChecksum?"":",{\"name\":\""+shaName+"\",\"browser_download_url\":\""+shaUrl+"\"}");
            var json="{\"tag_name\":\""+tag+"\",\"draft\":false,\"prerelease\":false,"+
                (immutable is null?"":"\"immutable\":"+(immutable.Value?"true":"false")+",")+"\"assets\":["+assets+"]}";
            return Done(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json)});
        }
        if (url.EndsWith(".zip",StringComparison.Ordinal))
            return Done(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(zip)});
        if (url.EndsWith(".sha256",StringComparison.Ordinal))
        {
            var file=ReleaseUpdatePolicy.ArchiveName(tag);
            return Done(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(Hash(zip)+"  "+file+"\n")});
        }
        return Done(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
    private static Task<HttpResponseMessage> Done(HttpResponseMessage response)=>Task.FromResult(response);
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
