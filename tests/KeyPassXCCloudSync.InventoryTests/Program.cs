using KeyPassXCCloudSync;
using System.Text.Json;

internal static class Program
{
    private static int _passed;
    public static int Main()
    {
        try
        {
            Check("missing directories", Missing);
            Check("file counts, bytes and nested folders", Nested);
            Check("independent categories", Independent);
            Check("count all file types including JSON and temporary", AllFiles);
            Check("read-only scanner", ReadOnly);
            Check("cancellation", Cancelled);
            Check("active conflict ignores archived files", ActiveConflict);
            Check("missing conflict copy is shown as an error", MissingConflictCopy);
            Check("tampered conflict copy is rejected", TamperedConflictCopy);
            Check("invalid conflict identifier never becomes a path", InvalidConflictId);
            Check("merge names sort by original version time", MergeNamesChronological);
            Check("same-second merge names cannot collide", MergeNamesUnique);
            Console.WriteLine($"[SUMMARY] InventoryTests: {_passed}/12 passed.");
            return 0;
        }
        catch(Exception ex){TestReporter.Unhandled("InventoryTests",ex);return 1;}
    }
    private static void Check(string name,Action test)
    {
        TestReporter.Run("InventoryTests",name,()=>WithFixture(test));
        _passed++;
    }
    private static string _data="";
    private static void WithFixture(Action test)
    {
        var root=Path.Combine(Path.GetTempPath(),"KPCS-inventory-"+Guid.NewGuid().ToString("N"));
        _data=Path.Combine(root,"data");
        Directory.CreateDirectory(_data);
        try{test();}
        finally{Directory.Delete(root,true);}
    }
    private static void Assert(bool ok,string description)
    {
        if(!ok)throw new InvalidOperationException(description);
    }
    private static void Write(string category,string relative,int length)
    {
        var path=Path.Combine(_data,category,relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path,new byte[length]);
    }
    private static LocalProtectedDataInventory.FolderResult[] Scan()=>LocalProtectedDataInventory.Scan(_data);
    private static void Missing()
    {
        var result=Scan();
        Assert(result.Length==4,"expected four categories");
        Assert(result.All(x=>x.Complete && x.FileCount==0 && x.SizeBytes==0),"missing is empty");
    }
    private static void Nested()
    {
        Write("startup-backups","Vault-2026-10-29-01.kdbx",100);
        Write("startup-backups",Path.Combine("nested","extra.kdbx"),200);
        var item=Scan().Single(x=>x.Name=="startup-backups");
        Assert(item.Complete && item.FileCount==2 && item.SizeBytes==300,"recursive totals");
    }
    private static void Independent()
    {
        Write("backup","version.kdbx",40);
        Write("conflicts","conflict.kdbx",70);
        Write("history","version.json",10);
        var results=Scan().ToDictionary(x=>x.Name);
        Assert(results["backup"].SizeBytes==40 && results["conflicts"].SizeBytes==70 &&
            results["history"].SizeBytes==10 && results["startup-backups"].FileCount==0,
            "categories must not be merged");
        Assert(results.Sum(x=>x.Value.SizeBytes)==120,"total bytes must not double-count");
    }
    private static void AllFiles()
    {
        Write("history","a.json",15);
        Write("history","temporary.tmp",5);
        var history=Scan().Single(x=>x.Name=="history");
        Assert(history.FileCount==2 && history.SizeBytes==20,"all actual files must be measured");
    }
    private static void ReadOnly()
    {
        Write("backup","version.kdbx",3);
        var before=Directory.EnumerateFileSystemEntries(_data,"*",SearchOption.AllDirectories).Order().ToArray();
        _=Scan();
        var after=Directory.EnumerateFileSystemEntries(_data,"*",SearchOption.AllDirectories).Order().ToArray();
        Assert(before.SequenceEqual(after),"the scanner must never create folders or modify data");
    }
    private static void Cancelled()
    {
        using var cancel=new CancellationTokenSource();
        cancel.Cancel();
        var raised=false;
        try{LocalProtectedDataInventory.Scan(_data,cancel.Token);}
        catch(OperationCanceledException){raised=true;}
        Assert(raised,"cancelled scans must stop promptly");
    }
    private static (AppState State,string Id,string Copy) FakeConflict(bool createCopy=true)
    {
        var id=Guid.NewGuid().ToString("N");
        var vault=Guid.NewGuid().ToString("N");
        var content=Path.Combine(_data,"backup","test.kdbx");
        Directory.CreateDirectory(Path.GetDirectoryName(content)!);
        File.WriteAllBytes(content,new byte[]{1,2,3,4,5});
        var copy=Path.Combine(_data,"conflicts",id+".kdbx");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        if(createCopy)File.Copy(content,copy);
        var meta=new VersionInfo{VersionId=id,VaultId=vault,BackupName=id+".kdbx",
            Size=5,Sha256=Hashes.Sha256(content),CreatedUtc=DateTimeOffset.UtcNow};
        Directory.CreateDirectory(Path.Combine(_data,"history"));
        File.WriteAllText(Path.Combine(_data,"history",id+".json"),
            JsonSerializer.Serialize(meta,SyncJson.Default.VersionInfo));
        return (new AppState{VaultId=vault,Conflict=true,ConflictVersionIds=[id]},id,copy);
    }
    private static void ActiveConflict()
    {
        var (state,id,_)=FakeConflict();
        Write("conflicts",Guid.NewGuid().ToString("N")+".kdbx",400);
        var active=ActiveConflictFiles.Read(_data,state);
        Assert(active.Length==1 && active[0].Ready && active[0].FileName==id+".kdbx",
            "Only current ConflictVersionIds must be listed, not archived copies");
        state.Conflict=false;
        Assert(ActiveConflictFiles.Read(_data,state).Length==0,
            "After resolution, retained copies are no longer active conflicts");
    }
    private static void MissingConflictCopy()
    {
        var (state,_,copy)=FakeConflict();
        File.Delete(copy);
        var active=ActiveConflictFiles.Read(_data,state);
        Assert(active.Length==1 && !active[0].Ready && active[0].Error is not null,
            "Missing current copy must not be silently considered ready");
    }
    private static void TamperedConflictCopy()
    {
        var (state,_,copy)=FakeConflict();
        File.AppendAllText(copy,"tampered");
        Assert(!ActiveConflictFiles.Read(_data,state).Single().Ready,
            "Incorrect KDBX size must prevent merge preparation");
    }
    private static void MergeNamesChronological()
    {
        var early=DateTimeOffset.Parse("2026-10-29T10:00:00+00:00");
        var late=early.AddMinutes(12);
        var first=new ActiveConflictFiles.Entry(Guid.NewGuid().ToString("N"),"first.kdbx",early,10,"local",null);
        var second=new ActiveConflictFiles.Entry(Guid.NewGuid().ToString("N"),"second.kdbx",late,10,"external:cloud",null);
        var planned=MergeWorkspaceFileNames.Plan([second,first]);
        Assert(planned.Length==2 && planned[0].Version.VersionId==first.VersionId &&
            planned[1].Version.VersionId==second.VersionId,"source versions must be ordered by metadata timestamp");
        Assert(string.CompareOrdinal(planned[0].FileName,planned[1].FileName)<0,
            "filename order must follow chronological version timestamps");
        Assert(planned.All(p=>System.Text.RegularExpressions.Regex.IsMatch(p.FileName,
            @"^Conflict-\d{4}-\d{2}-\d{2}-\d{2}-\d{2}-\d{2}-(Local|External)-\d{2}\.kdbx$")),
            "timestamps must use YYYY-MM-DD-HH-MM-SS");
    }
    private static void MergeNamesUnique()
    {
        var time=DateTimeOffset.UtcNow;
        var entries=Enumerable.Range(0,3).Select(_=>new ActiveConflictFiles.Entry(
            Guid.NewGuid().ToString("N"),"source.kdbx",time,10,"local",null)).ToArray();
        var planned=MergeWorkspaceFileNames.Plan(entries);
        Assert(planned.Select(p=>p.FileName).Distinct(StringComparer.Ordinal).Count()==3,
            "same-second versions must have unique names");
        Assert(planned.All(p=>p.FileName.Contains("-Local-",StringComparison.Ordinal)),
            "source description must be preserved");
    }

    private static void InvalidConflictId()
    {
        var state=new AppState{Conflict=true,ConflictVersionIds=["../outside"]};
        var active=ActiveConflictFiles.Read(_data,state);
        Assert(active.Length==1 && !active[0].Ready && active[0].FileName=="—",
            "Invalid id must not become a filesystem path");
    }
}
