using System.Security.Cryptography;
using System.Text;

namespace KeyPassXCCloudSync;

internal sealed class CloudDraft
{
    internal string Name="",Provider="GoogleDrive",Remote="KeePassSync",Account="",ClientId="",
        SecretTarget="",RefreshTarget="",BaseUrl="",CredentialTarget="",WebUser="",WebPassword="",
        GoogleSecret="",Executable="",Root="/my-files",S3Region="auto",S3AccessKey="",S3SecretKey="";
    internal int Timeout=120,GoogleFolderCacheSeconds=10,Poll=60;
    internal string CloudId=Guid.NewGuid().ToString("N");
    internal string Label=>$"{Name}  ·  {CloudProviderNames.Display(Provider)}"+
        (Account.Length>0?"  ·  "+Account:"");
    internal CloudDraft Clone()=>(CloudDraft)MemberwiseClone();
    internal static CloudDraft From(CloudOptions o)=>new(){Name=o.Name,Provider=o.Provider,Remote=o.RemoteDirectory,
        Account=o.Provider=="GoogleDrive"?o.AccountLabel:"",ClientId=o.ClientId,SecretTarget=o.ClientSecretCredential,
        RefreshTarget=o.RefreshCredential,BaseUrl=o.BaseUrl,CredentialTarget=o.CredentialTarget,
        Executable=o.ProtonAutoExecutable?"":o.Executable,Root=o.RootPath,Timeout=o.TimeoutSeconds,GoogleFolderCacheSeconds=o.GoogleFolderCacheSeconds,S3Region=o.S3Region,Poll=o.PollSeconds,CloudId=o.CloudId};
}
internal sealed class SettingsDraft
{
    internal bool SyncEnabled=false,RememberWindowPosition=false,BackupOnStartup=false;
    internal string LocalFile="",DataDirectory="",Theme="Системная",Language="auto";
    internal int Poll=60,StatsPublish=60,Debounce=1500,Stable=1200,Retry=60,Offline=30,
        Copies=20,Days=30,StartupBackupKeep=30,LogMb=5,LogFiles=5;
    internal string LogLevel="Info";
    internal List<CloudDraft> Clouds=new();
    internal static SettingsDraft Load(string path)
    {
        var m=new SettingsDraft{
            DataDirectory=PlatformServices.Current.Paths.DefaultDataDirectory(path)};
        if(!File.Exists(path))return m;
        var c=AppOptions.Load(path);
        m.SyncEnabled=c.SyncEnabled;m.LocalFile=c.LocalFile;m.DataDirectory=c.DataDirectory;m.Poll=c.PollSeconds;m.StatsPublish=c.StatsPublishMinutes;
        m.Debounce=c.DebounceMs;m.Stable=c.StableMs;m.Retry=c.RetrySeconds;
        m.Offline=c.NotifyAfterMinutes;m.Copies=c.BackupCopies;m.Days=c.BackupDays;
        m.BackupOnStartup=c.BackupOnStartup;m.StartupBackupKeep=c.StartupBackupKeep;
        m.LogMb=c.LogSizeMB;m.LogFiles=c.LogFiles;m.LogLevel=c.LogLevel;
        m.Clouds=c.Clouds.Select(CloudDraft.From).ToList();
        foreach(var cloud in m.Clouds.Where(x=>x.Provider=="WebDAV" && x.CredentialTarget.Length>0))
        {
            try{cloud.WebUser=PlatformServices.Current.Secrets.Read(cloud.CredentialTarget).User;}
            catch(SyncSafetyException){} // credential may need to be supplied anew
        }
        var lines=File.ReadAllLines(path);
        bool ui=false;
        foreach(var raw in lines)
        {
            var text=raw.Trim();
            if(text.StartsWith('['))ui=text.Equals("[UI]",StringComparison.OrdinalIgnoreCase);
            else if(ui && text.Contains('='))
            {
                var split=text.IndexOf('=');
                var key=text[..split].Trim();
                var value=text[(split+1)..].Trim();
                if(key.Equals("Theme",StringComparison.OrdinalIgnoreCase) &&
                   value is "Системная" or "Тёмная" or "Светлая")m.Theme=value;
                else if(key.Equals("Language",StringComparison.OrdinalIgnoreCase) &&
                        value is "auto" or "ru" or "uk" or "en")m.Language=value;
                else if(key.Equals("RememberWindowPosition",StringComparison.OrdinalIgnoreCase) &&
                        bool.TryParse(value,out var remember))m.RememberWindowPosition=remember;
            }
        }
        return m;
    }
    internal SettingsDraft Clone()=>new(){SyncEnabled=SyncEnabled,RememberWindowPosition=RememberWindowPosition,
        BackupOnStartup=BackupOnStartup,StartupBackupKeep=StartupBackupKeep,LocalFile=LocalFile,DataDirectory=DataDirectory,Theme=Theme,Language=Language,
        Poll=Poll,StatsPublish=StatsPublish,Debounce=Debounce,Stable=Stable,Retry=Retry,Offline=Offline,Copies=Copies,Days=Days,
        LogMb=LogMb,LogFiles=LogFiles,
        LogLevel=LogLevel,Clouds=Clouds.Select(c=>c.Clone()).ToList()};
}
internal static class SettingsStore
{
    private static string Safe(string s)
    {
        if(s.IndexOfAny(['\r','\n'])>=0)throw new ArgumentException("В настройках обнаружен перевод строки");
        return s.Trim();
    }
    internal static string Ini(SettingsDraft m)
    {
        if(m.LocalFile.Length==0 || !m.LocalFile.EndsWith(".kdbx",StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Выберите файл KeePass (.kdbx)");
        if(m.DataDirectory.Length==0)throw new ArgumentException("Укажите папку данных KeyPassXC Cloud Sync");
        if(m.StartupBackupKeep is <1 or >999)throw new ArgumentException("Число локальных резервных копий должно быть от 1 до 999");
        if(m.Clouds.Count==0)throw new ArgumentException("Добавьте хотя бы одно облако");
        if(m.Clouds.Any(c=>!CloudConnectionName.IsValid(c.Name)))
            throw new ArgumentException("Недопустимое имя облачного подключения (1–80 символов; буквы, цифры, пробелы и безопасные разделители).");
        if(m.Clouds.GroupBy(c=>c.Name,StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1))
            throw new ArgumentException("Названия подключений должны быть уникальными");
        var b=new StringBuilder("; KeyPassXC Cloud Sync "+BuildInfo.Version+" - settings generated by the application\n");
        b.AppendLine("[General]").AppendLine("LocalFile = "+Safe(Path.GetFullPath(m.LocalFile)))
            .AppendLine("StateFile = "+Safe(Path.Combine(Path.GetFullPath(m.DataDirectory),"state.json")))
            .AppendLine("TrayEnabled = true")
            .AppendLine("SyncEnabled = "+(m.SyncEnabled?"true":"false")).AppendLine()
            .AppendLine("[Sync]").AppendLine("PollSeconds = "+m.Poll).AppendLine("DebounceMs = "+m.Debounce)
            .AppendLine("StableFileCheckMs = "+m.Stable).AppendLine("RetrySeconds = "+m.Retry)
            .AppendLine("StatsPublishMinutes = "+m.StatsPublish).AppendLine()
            .AppendLine("[Notifications]").AppendLine("OfflineAfterMinutes = "+m.Offline).AppendLine()
            .AppendLine("[Backup]").AppendLine("MinCopies = "+m.Copies).AppendLine("MinDays = "+m.Days)
            .AppendLine("OnStartup = "+(m.BackupOnStartup?"true":"false"))
            .AppendLine("KeepStartupCopies = "+m.StartupBackupKeep).AppendLine()
            .AppendLine("[Logging]").AppendLine("Level = "+Safe(m.LogLevel))
            .AppendLine("MaxSizeMB = "+m.LogMb).AppendLine("MaxFiles = "+m.LogFiles).AppendLine()
            .AppendLine("[UI]").AppendLine("Theme = "+Safe(m.Theme))
            .AppendLine("Language = "+Safe(m.Language))
            .AppendLine("RememberWindowPosition = "+(m.RememberWindowPosition?"true":"false"));
        foreach(var c in m.Clouds)
        {
            b.AppendLine().AppendLine("[Cloud."+Safe(c.Name)+"]")
                .AppendLine("Enabled = true").AppendLine("Provider = "+Safe(c.Provider))
                .AppendLine("RemoteDirectory = "+Safe(c.Remote))
                .AppendLine("CloudId = "+Safe(c.CloudId))
                .AppendLine("PollSeconds = "+c.Poll);
            if(c.Provider=="GoogleDrive")
            {
                if(c.ClientId.Length>0)b.AppendLine("ClientId = "+Safe(c.ClientId));
                if(c.SecretTarget.Length>0)b.AppendLine("ClientSecretCredential = "+Safe(c.SecretTarget));
                b.AppendLine("RefreshCredential = "+Safe(c.RefreshTarget.Length>0?c.RefreshTarget:
                    "KeePassSync/Google/"+Guid.NewGuid().ToString("N")+"/refresh-token"));
                if(c.Account.Length>0)b.AppendLine("AccountLabel = "+Safe(c.Account));
                b.AppendLine("GoogleFolderCacheSeconds = "+c.GoogleFolderCacheSeconds);
            }
            if(c.Provider=="WebDAV")
            {
                b.AppendLine("BaseUrl = "+Safe(c.BaseUrl));
                b.AppendLine("CredentialTarget = "+Safe(c.CredentialTarget));
            }
            if(c.Provider is "S3" or "CloudflareR2")
            {
                b.AppendLine("BaseUrl = "+Safe(c.BaseUrl)).AppendLine("RootPath = "+Safe(c.Root))
                    .AppendLine("CredentialTarget = "+Safe(c.CredentialTarget))
                    .AppendLine("S3Region = "+Safe(c.S3Region));
            }
            if(c.Provider=="FileSystemTest" || c.Provider=="FileSystemTestNoCas")
                b.AppendLine("BaseUrl = "+Safe(c.BaseUrl));
            if(c.Provider=="ProtonDrive")
            {
                // Empty Executable means automatic discovery beside the app.
                if(c.Executable.Length>0)b.AppendLine("Executable = "+Safe(c.Executable));
            }
            b.AppendLine("TimeoutSeconds = "+c.Timeout);
        }
        return b.ToString();
    }
    // Save only UI Language without rewriting cloud credentials or pending form edits.
    // If no INI exists yet, the language is written with the first full Save.
    internal static void SaveUiLanguage(string configPath,string language)
    {
        if(language is not ("auto" or "ru" or "uk" or "en"))
            throw new ArgumentException("Unsupported interface language",nameof(language));
        if(!File.Exists(configPath))return;
        var lines=File.ReadAllLines(configPath).ToList();
        var uiStart=-1;
        var uiEnd=lines.Count;
        for(var i=0;i<lines.Count;i++)
        {
            var row=lines[i].Trim();
            if(row.Equals("[UI]",StringComparison.OrdinalIgnoreCase))
            {uiStart=i;continue;}
            if(uiStart>=0 && i>uiStart && row.StartsWith('['))
            {uiEnd=i;break;}
        }
        if(uiStart>=0)
        {
            var found=false;
            for(var i=uiStart+1;i<uiEnd;i++)
            {
                var row=lines[i].Trim();
                var equals=row.IndexOf('=');
                if(equals<0 || !row[..equals].Trim().Equals("Language",StringComparison.OrdinalIgnoreCase))continue;
                lines[i]="Language = "+language;
                found=true;
                break;
            }
            if(!found)lines.Insert(uiEnd,"Language = "+language);
        }
        else
        {
            lines.Add("");lines.Add("[UI]");lines.Add("Language = "+language);
        }
        var tmp=Path.Combine(Path.GetDirectoryName(configPath)!,".kps-ui-"+Guid.NewGuid().ToString("N")+".tmp");
        try
        {
            File.WriteAllLines(tmp,lines,new UTF8Encoding(false));
            File.Move(tmp,configPath,true);
        }
        finally{if(File.Exists(tmp))File.Delete(tmp);}
    }

    internal static AppOptions Preview(SettingsDraft m,string configPath)
    {
        var dir=Path.GetDirectoryName(configPath)!;
        Directory.CreateDirectory(dir);
        var temp=Path.Combine(dir,".kps-"+Guid.NewGuid().ToString("N")+".preview");
        try
        {
            File.WriteAllText(temp,Ini(m),new UTF8Encoding(false));
            return AppOptions.Load(temp);
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    // Change only the local run switch. Never rewrite cloud secrets or unsaved form changes.
    internal static void SetSyncEnabled(string configPath,bool enabled)
    {
        if(!File.Exists(configPath))throw new FileNotFoundException("Сначала сохраните настройки подключения.");
        var rows=File.ReadAllLines(configPath).ToList();
        var section=-1;var end=rows.Count;
        for(var i=0;i<rows.Count;i++)
        {
            var item=rows[i].Trim();
            if(item.Equals("[General]",StringComparison.OrdinalIgnoreCase)){section=i;continue;}
            if(section>=0 && i>section && item.StartsWith('[')){end=i;break;}
        }
        if(section<0)throw new InvalidOperationException("Секция [General] отсутствует.");
        var newLine="SyncEnabled = "+(enabled?"true":"false");
        var replaced=false;
        for(var i=section+1;i<end;i++)
        {
            var entry=rows[i].Trim();var eq=entry.IndexOf('=');
            if(eq<=0 || !entry[..eq].Trim().Equals("SyncEnabled",StringComparison.OrdinalIgnoreCase))continue;
            rows[i]=newLine;replaced=true;break;
        }
        if(!replaced)rows.Insert(end,newLine);
        var temp=Path.Combine(Path.GetDirectoryName(configPath)!,".kps-toggle-"+Guid.NewGuid().ToString("N")+".tmp");
        try
        {
            File.WriteAllLines(temp,rows,new UTF8Encoding(false));
            var candidate=AppOptions.Load(temp);
            if(candidate.SyncEnabled!=enabled)throw new SyncSafetyException("Проверка состояния переключателя не прошла.");
            File.Move(temp,configPath,true);
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    internal static bool SamePath(string x,string y)=>string.Equals(Path.GetFullPath(x),Path.GetFullPath(y),PlatformServices.Current.Paths.PathComparison);
    // A remote topology application is NOT a local user edit. Reusing this
    // persistence method must not create a feedback edit in topology-pending.json.
    internal static AppOptions Save(SettingsDraft m,string configPath,string? oldDataDirectory,
        bool queueSharedTopologyUpdate=true)
    {
        // These paths define the synchronization identity and must never move live.
        if(File.Exists(configPath))
        {
            var old=AppOptions.Load(configPath);
            if(old.SyncEnabled && (!SamePath(old.LocalFile,m.LocalFile) || !SamePath(old.DataDirectory,m.DataDirectory)))
                throw new SyncSafetyException("Перед изменением KDBX или каталога данных выключите синхронизацию.");
            m.SyncEnabled=old.SyncEnabled; // settings cannot implicitly turn synchronization on/off
        }
        var text=Ini(m);
        var parent=Path.GetDirectoryName(configPath)!;
        Directory.CreateDirectory(parent);
        var temp=Path.Combine(parent,".kps-"+Guid.NewGuid().ToString("N")+".tmp");
        try
        {
            File.WriteAllText(temp,text,new UTF8Encoding(false));
            _=AppOptions.Load(temp); // validate before touching permanent configuration
            if(oldDataDirectory is {Length:>0} &&
                !Path.GetFullPath(oldDataDirectory).Equals(Path.GetFullPath(m.DataDirectory),PlatformServices.Current.Paths.PathComparison))
                CopyDataSafely(oldDataDirectory,m.DataDirectory);
            foreach(var c in m.Clouds)
            {
                if(c.Provider=="WebDAV" && c.WebPassword.Length>0)
                    PlatformServices.Current.Secrets.Write(c.CredentialTarget,c.WebUser,c.WebPassword);
                if(c.Provider=="GoogleDrive" && c.GoogleSecret.Length>0)
                    PlatformServices.Current.Secrets.Write(c.SecretTarget,c.ClientId,c.GoogleSecret);
                if((c.Provider is "S3" or "CloudflareR2") && c.S3SecretKey.Length>0)
                    PlatformServices.Current.Secrets.Write(c.CredentialTarget,c.S3AccessKey,c.S3SecretKey);
            }
            File.Move(temp,configPath,true);
            var saved=AppOptions.Load(configPath);
            // Only KeePassSync-owned namespaced entries are eligible for cleanup.
            if(saved.Clouds.All(c=>c.Provider is not ("FileSystemTest" or "FileSystemTestNoCas")))
                PlatformServices.Current.Secrets.RemoveUnused(ActiveCredentialTargets(saved.Clouds));
            if(queueSharedTopologyUpdate)
                TopologyCoordinator.QueueFromSettings(saved);
            return saved;
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    internal static HashSet<string> ActiveCredentialTargets(IEnumerable<CloudOptions> clouds)
    {
        var targets=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var cloud in clouds)
            foreach(var target in new[]{cloud.CredentialTarget,cloud.ClientSecretCredential,cloud.RefreshCredential})
                if(!string.IsNullOrWhiteSpace(target))targets.Add(target);
        return targets;
    }
    private static void CopyDataSafely(string source,string dest)
    {
        source=Path.GetFullPath(source);dest=Path.GetFullPath(dest);
        if(!Directory.Exists(source))return;
        if(Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any())
            throw new IOException("Новая папка данных уже содержит файлы. Выберите пустую папку.");
        var src=source.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        var dst=dest.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        if(src.StartsWith(dst,PlatformServices.Current.Paths.PathComparison) || dst.StartsWith(src,PlatformServices.Current.Paths.PathComparison))
            throw new IOException("Папки данных не могут быть вложенными друг в друга");
        Directory.CreateDirectory(dest);
        foreach(var entry in Directory.EnumerateFileSystemEntries(source,"*",SearchOption.AllDirectories))
        {
            var attr=File.GetAttributes(entry);
            if((attr & FileAttributes.ReparsePoint)!=0)throw new IOException("Нельзя переносить symlink/junction: "+entry);
            var target=Path.Combine(dest,Path.GetRelativePath(source,entry));
            if((attr & FileAttributes.Directory)!=0)Directory.CreateDirectory(target);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(entry,target,false);
                if(!Hashes.Sha256(entry).Equals(Hashes.Sha256(target),StringComparison.Ordinal))
                    throw new IOException("Ошибка проверки перенесённого файла: "+entry);
            }
        }
        // Previous directory deliberately retained as an emergency copy.
    }
    internal static bool AutostartEnabled()=>PlatformServices.Current.Autostart.IsEnabled();
    internal static void SetAutostart(bool enabled,string configPath)=>
        PlatformServices.Current.Autostart.SetEnabled(enabled,configPath);
}
