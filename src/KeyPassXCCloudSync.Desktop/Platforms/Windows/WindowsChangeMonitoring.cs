using System.Net.NetworkInformation;

namespace KeyPassXCCloudSync;

// Owns operating-system file and network notifications; Core sees only a callback.
internal sealed class WindowsChangeMonitoring : IPlatformChangeMonitoring
{
    internal sealed class Subscription(Action dispose) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if(Interlocked.Exchange(ref _disposed,1)==0)dispose();
        }
    }
    public IDisposable WatchFile(string fullFilePath,Action changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        var folder=Path.GetDirectoryName(fullFilePath)!;
        Directory.CreateDirectory(folder);
        var watcher=new FileSystemWatcher(folder)
        {
            Filter=Path.GetFileName(fullFilePath),
            NotifyFilter=NotifyFilters.FileName|NotifyFilters.LastWrite|
                NotifyFilters.Size|NotifyFilters.CreationTime
        };
        FileSystemEventHandler fileHandler=(_,_)=>changed();
        RenamedEventHandler renameHandler=(_,_)=>changed();
        ErrorEventHandler errorHandler=(_,_)=>changed();
        watcher.Changed+=fileHandler;
        watcher.Created+=fileHandler;
        watcher.Deleted+=fileHandler;
        watcher.Renamed+=renameHandler;
        watcher.Error+=errorHandler;
        try{watcher.EnableRaisingEvents=true;}
        catch{watcher.Dispose();throw;}
        return new Subscription(watcher.Dispose);
    }
    public IDisposable WatchNetwork(Action changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        NetworkAvailabilityChangedEventHandler available=(_,_)=>changed();
        NetworkAddressChangedEventHandler address=(_,_)=>changed();
        NetworkChange.NetworkAvailabilityChanged+=available;
        try{NetworkChange.NetworkAddressChanged+=address;}
        catch{NetworkChange.NetworkAvailabilityChanged-=available;throw;}
        return new Subscription(()=>
        {
            NetworkChange.NetworkAvailabilityChanged-=available;
            NetworkChange.NetworkAddressChanged-=address;
        });
    }
}
