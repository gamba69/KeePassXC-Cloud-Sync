using System.Globalization;

namespace KeyPassXCCloudSync;

/// <summary>
/// Local-only point-in-time KDBX copy on process startup. It never participates in
/// the V2 replication protocol, history retention, or distributed topology.
/// </summary>
internal static class StartupBackupService
{
    private sealed record Entry(string Path,DateOnly Day,int Sequence);

    internal static string BackupDirectory(string dataDirectory) =>
        Path.Combine(Path.GetFullPath(dataDirectory),"startup-backups");

    // An absent KDBX is legitimate when joining a topology on a new computer.
    // Call under EngineHost.Gate and the active database reservation, before V2 runs.
    internal static string? CreateIfPresent(string localFile,string dataDirectory,int keep,
        DateTime? localNow=null)
    {
        if(keep is <1 or >999)throw new ArgumentOutOfRangeException(nameof(keep));
        localFile=Path.GetFullPath(localFile);
        // File.Exists() hides many access failures by returning false. A missing
        // database on a new device is normal; an inaccessible one must warn.
        FileAttributes sourceAttributes;
        try { sourceAttributes=File.GetAttributes(localFile); }
        catch(FileNotFoundException) { return null; }
        catch(DirectoryNotFoundException) { return null; }
        if((sourceAttributes&FileAttributes.Directory)!=0)
            throw new IOException("Путь рабочей KDBX указывает на каталог: "+localFile);
        var directory=BackupDirectory(dataDirectory);
        Directory.CreateDirectory(directory);
        if((File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)
            throw new IOException("Каталог локальных копий не должен быть symlink/junction: "+directory);

        var stem=Path.GetFileNameWithoutExtension(localFile);
        var today=DateOnly.FromDateTime(localNow??DateTime.Now);
        var existing=Enumerate(directory,stem);
        var last=existing.Where(x=>x.Day==today).Select(x=>x.Sequence).DefaultIfEmpty(0).Max();
        if(last>=99)
            throw new IOException("Исчерпаны номера резервных копий за "+today.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)+
                " (01–99). Номер 100 не поддерживается; следующая копия будет возможна на следующий день. " +
                "Ранее созданные копии не перезаписываются.");
        var destination=Path.Combine(directory,stem+"-"+
            today.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)+"-"+
            (last+1).ToString("D2",CultureInfo.InvariantCulture)+".kdbx");
        var temporary=Path.Combine(directory,".startup-backup-"+Guid.NewGuid().ToString("N")+".tmp");
        try
        {
            // Verify the source is a KDBX, then insist on an identical snapshot.
            // This detects a concurrent KeePassXC save rather than storing a torn copy.
            KdbxInspector.VerifyPublicHeader(localFile);
            var before=Hashes.Sha256(localFile);
            File.Copy(localFile,temporary,false);
            KdbxInspector.VerifyPublicHeader(temporary);
            var snapshotHash=Hashes.Sha256(temporary);
            var after=Hashes.Sha256(localFile);
            if(!StringComparer.Ordinal.Equals(before,snapshotHash) ||
               !StringComparer.Ordinal.Equals(snapshotHash,after))
                throw new IOException("Рабочая KDBX изменилась во время копирования. Резервная копия отклонена.");

            // Atomic, no-overwrite publish. Only verified copies get a .kdbx name.
            File.Move(temporary,destination);
        }
        finally
        {
            if(File.Exists(temporary))File.Delete(temporary);
        }

        // Retention is deliberately AFTER successful publish: an unsuccessful
        // backup must never delete the last known-good local snapshot.
        var ordered=Enumerate(directory,stem)
            .OrderByDescending(x=>x.Day).ThenByDescending(x=>x.Sequence).ToArray();
        try
        {
            foreach(var old in ordered.Skip(keep))
            {
                if((File.GetAttributes(old.Path)&FileAttributes.ReparsePoint)!=0)
                    throw new IOException("Обнаружена ссылка: "+old.Path);
                File.Delete(old.Path);
            }
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException("Резервная копия успешно создана: "+destination+
                ". Но не удалось удалить старые копии: "+ex.Message,ex);
        }
        return destination;
    }

    private static List<Entry> Enumerate(string directory,string stem)
    {
        var result=new List<Entry>();
        var prefix=stem+"-";
        foreach(var path in Directory.EnumerateFiles(directory,"*.kdbx",SearchOption.TopDirectoryOnly))
        {
            var filename=Path.GetFileName(path);
            // Only files owned by this database and this backup naming scheme.
            if(!filename.StartsWith(prefix,StringComparison.Ordinal) ||
               !filename.EndsWith(".kdbx",StringComparison.OrdinalIgnoreCase))continue;
            var suffix=filename.Substring(prefix.Length,filename.Length-prefix.Length-5);
            if(suffix.Length!=13 || suffix[10]!='-' ||
               !DateOnly.TryParseExact(suffix[..10],"yyyy-MM-dd",CultureInfo.InvariantCulture,
                   DateTimeStyles.None,out var day) ||
               !int.TryParse(suffix[11..],NumberStyles.None,CultureInfo.InvariantCulture,out var number) ||
               number is <1 or >99 ||
               suffix[11..]!=number.ToString("D2",CultureInfo.InvariantCulture))continue;
            result.Add(new Entry(path,day,number));
        }
        return result;
    }
}
