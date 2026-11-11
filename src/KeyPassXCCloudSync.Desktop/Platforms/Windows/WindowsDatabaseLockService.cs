using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace KeyPassXCCloudSync;

// All Mutex operations execute on ONE dedicated OS thread. A mutex is owned by
// the acquiring thread, not by an async task; the UI and worker may both change
// configuration and prevents cross-thread ReleaseMutex failures.
internal sealed class WindowsDatabaseLockService : IDatabaseLockService, IDisposable
{
    private readonly BlockingCollection<Action> _jobs=new();
    private readonly Thread _thread;
    private bool _disposed;
    private readonly HashSet<string> _held=new(StringComparer.Ordinal);
    internal WindowsDatabaseLockService()
    {
        _thread=new Thread(()=>{foreach(var job in _jobs.GetConsumingEnumerable())job();})
        {IsBackground=true,Name="KeyPassXC database-lock owner"};
        _thread.Start();
    }
    private T Run<T>(Func<T> work)
    {
        var completion=new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock(_jobs)
        {
            if(_disposed)throw new ObjectDisposedException(nameof(WindowsDatabaseLockService));
            _jobs.Add(()=>
            {
                try{completion.SetResult(work());}
                catch(Exception ex){completion.SetException(ex);}
            });
        }
        return completion.Task.GetAwaiter().GetResult();
    }
    public IDatabaseReservation Acquire(string fullFilePath)=>Run(() =>
    {
        var canonical=Path.GetFullPath(fullFilePath).ToUpperInvariant();
        var fingerprint=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..20];
        var lockName=@"Local\KeePassSync1_"+fingerprint;
        // Mutex is reentrant on its owning thread: reject duplicate leases
        // locally instead of silently allowing a second writer in this process.
        if(_held.Contains(lockName))
            throw new SyncSafetyException("Этот файл KDBX уже зарезервирован другой операцией.");
        var mutex=new Mutex(false,lockName);
        bool acquired;
        try{acquired=mutex.WaitOne(0);}
        catch(AbandonedMutexException){acquired=true;}
        catch{mutex.Dispose();throw;}
        if(!acquired)
        {
            mutex.Dispose();
            throw new SyncSafetyException("Другая копия KeePassSync уже обслуживает эту KDBX. Одновременная запись запрещена.");
        }
        _held.Add(lockName);
        return (IDatabaseReservation)new Reservation(this,mutex,lockName);
    });
    private sealed class Reservation(WindowsDatabaseLockService service,Mutex mutex,string lockName) : IDatabaseReservation
    {
        private int _closed;
        public void Dispose()
        {
            if(Interlocked.Exchange(ref _closed,1)!=0)return;
            service.Run(() =>
            {
                try{mutex.ReleaseMutex();}
                finally{mutex.Dispose();service._held.Remove(lockName);}
                return true;
            });
        }
    }
    public void Dispose()
    {
        lock(_jobs)
        {
            if(_disposed)return;
            _disposed=true;
            _jobs.CompleteAdding();
        }
        _thread.Join(TimeSpan.FromSeconds(3));
        _jobs.Dispose();
    }
}
