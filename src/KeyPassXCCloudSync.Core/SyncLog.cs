namespace KeyPassXCCloudSync;

public sealed class SyncLog(AppOptions config)
{
    private readonly object _lock=new();
    public string Path=>System.IO.Path.Combine(config.DataDirectory,"logs","KeePassSync.log");
    public void Info(string text)=>Write("INFO",text);
    public void Warn(string text)=>Write("WARN",text);
    public void Error(string text)=>Write("ERROR",text);
    public void Debug(string text) { if(config.LogLevel=="Debug")Write("DEBUG",text); }
    private void Write(string level,string text)
    {
        lock(_lock)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            if(File.Exists(Path) && new FileInfo(Path).Length>config.LogSizeMB*1024L*1024L)
            {
                for(int i=config.LogFiles;i>=1;i--)
                {
                    var dest=Path+"."+i;
                    var src=i==1?Path:Path+"."+(i-1);
                    if(File.Exists(dest))File.Delete(dest);
                    if(File.Exists(src))File.Move(src,dest);
                }
            }
            File.AppendAllText(Path,$"{DateTimeOffset.UtcNow:O} [{level}] {text.Replace('\n',' ').Replace('\r',' ')}\n");
        }
    }
}
