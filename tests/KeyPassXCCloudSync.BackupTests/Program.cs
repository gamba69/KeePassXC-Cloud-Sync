using System.Security.Cryptography;
using KeyPassXCCloudSync;

internal static class Program
{
    private static int _passed;
    private static readonly DateTime FirstDay=new(2026,10,28,12,0,0);
    public static int Main()
    {
        try
        {
            Run("missing KDBX is skipped",MissingDatabase);
            Run("copies are named, verified, and pruned",NameAndRetention);
            Run("99 per-day limit with retention",Exhaustion);
            Run("broken source does not delete previous copy",InvalidSource);
            Run("unrelated files are never deleted",Unrelated);
            Run("database path must be a file",DirectoryNotDatabase);
            Console.WriteLine($"[SUMMARY] BackupTests: {_passed}/6 passed.");
            return 0;
        }
        catch(Exception ex){TestReporter.Unhandled("BackupTests",ex);return 1;}
    }
    private static void Run(string description,Action test)
    {
        TestReporter.Run("BackupTests",description,test);_passed++;
    }
    private static void WithFixture(Action<string,string,string> action)
    {
        var root=Path.Combine(Path.GetTempPath(),"KPCS-backuptest-"+Guid.NewGuid().ToString("N"));
        var local=Path.Combine(root,"Vault.kdbx");
        var data=Path.Combine(root,"data");
        try
        {
            Directory.CreateDirectory(root);
            action(root,local,data);
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    private static void Assert(bool condition,string message)
    {
        if(!condition)throw new InvalidOperationException(message);
    }
    // Synthetic header is *structurally* valid KDBX4; not an encrypted KeePass database.
    private static void WriteSyntheticHeader(string path)
    {
        using var h=new MemoryStream();
        using(var writer=new BinaryWriter(h,System.Text.Encoding.UTF8,leaveOpen:true))
        {
            writer.Write(0x9AA2D903u);writer.Write(0xB54BFB67u);
            writer.Write(0x00040000u);
            writer.Write((byte)0);writer.Write(4);writer.Write(new byte[]{13,10,13,10});
        }
        using var output=File.Create(path);
        var header=h.ToArray();
        output.Write(header);
        output.Write(SHA256.HashData(header));
        output.Write(new byte[32]); // test-only synthetic authentication bytes
        output.Write(new byte[128]);
    }
    private static void MissingDatabase()=>WithFixture((_,local,data)=>
    {
        Assert(StartupBackupService.CreateIfPresent(local,data,30,FirstDay) is null,
            "missing file must not be copied");
        Assert(!Directory.Exists(Path.Combine(data,"startup-backups")),
            "no destination directory for absent source");
    });
    private static void NameAndRetention()=>WithFixture((_,local,data)=>
    {
        WriteSyntheticHeader(local);
        for(int i=1;i<=4;i++)
        {
            var path=StartupBackupService.CreateIfPresent(local,data,3,FirstDay);
            Assert(path is not null && path.EndsWith($"Vault-2026-10-28-{i:D2}.kdbx",StringComparison.Ordinal),
                "incorrect dated sequence");
            Assert(Hashes.Sha256(local)==Hashes.Sha256(path!),"different bytes");
        }
        var dir=Path.Combine(data,"startup-backups");
        Assert(Directory.GetFiles(dir,"*.kdbx").Length==3,"retention not applied");
        Assert(!File.Exists(Path.Combine(dir,"Vault-2026-10-28-01.kdbx")),"oldest not pruned");
        Assert(File.Exists(Path.Combine(dir,"Vault-2026-10-28-04.kdbx")),"newest missing");
        Assert(Directory.GetFiles(dir,"*.tmp").Length==0,"staged files leaked");
    });
    private static void Exhaustion()=>WithFixture((_,local,data)=>
    {
        WriteSyntheticHeader(local);
        for(int i=1;i<=99;i++)
            StartupBackupService.CreateIfPresent(local,data,2,FirstDay);
        var directory=Path.Combine(data,"startup-backups");
        Assert(Directory.GetFiles(directory,"*.kdbx").Length==2,"incorrect retention");
        var exhausted=false;
        try{StartupBackupService.CreateIfPresent(local,data,2,FirstDay);}
        catch(IOException ex){exhausted=ex.Message.Contains("100",StringComparison.Ordinal);}
        Assert(exhausted,"counter 100 not rejected");
        Assert(Directory.GetFiles(directory,"*.kdbx").Length==2,"overflow deleted snapshots");
        var next=StartupBackupService.CreateIfPresent(local,data,2,FirstDay.AddDays(1));
        Assert(next is not null && next.EndsWith("Vault-2026-10-29-01.kdbx",StringComparison.Ordinal),
            "counter did not reset on a new day");
    });
    private static void InvalidSource()=>WithFixture((_,local,data)=>
    {
        WriteSyntheticHeader(local);
        var first=StartupBackupService.CreateIfPresent(local,data,1,FirstDay);
        Assert(first is not null,"initial valid snapshot missing");
        File.WriteAllText(local,"corrupted");
        var rejected=false;
        try{StartupBackupService.CreateIfPresent(local,data,1,FirstDay);}
        catch(SyncSafetyException){rejected=true;}
        Assert(rejected,"corrupt source accepted");
        Assert(File.Exists(first),"previous valid snapshot removed");
        Assert(Directory.GetFiles(Path.Combine(data,"startup-backups"),"*.kdbx").Length==1,
            "invalid snapshot published");
    });
    private static void DirectoryNotDatabase()=>WithFixture((_,local,data)=>
    {
        Directory.CreateDirectory(local);
        var rejected=false;
        try{StartupBackupService.CreateIfPresent(local,data,3,FirstDay);}
        catch(IOException){rejected=true;}
        Assert(rejected,"directory path silently treated as absent database");
    });
    private static void Unrelated()=>WithFixture((_,local,data)=>
    {
        WriteSyntheticHeader(local);
        var dir=Path.Combine(data,"startup-backups");Directory.CreateDirectory(dir);
        var keep=Path.Combine(dir,"Other-2026-10-28-01.kdbx");
        var weird=Path.Combine(dir,"Vault-not-a-backup.kdbx");
        File.WriteAllText(keep,"not our database");File.WriteAllText(weird,"not our format");
        for(int i=0;i<4;i++)StartupBackupService.CreateIfPresent(local,data,1,FirstDay);
        Assert(File.Exists(keep) && File.Exists(weird),"unrelated files removed");
        Assert(Directory.GetFiles(dir,"Vault-2026-10-28-*.kdbx").Length==1,"retention failed");
    });
}
