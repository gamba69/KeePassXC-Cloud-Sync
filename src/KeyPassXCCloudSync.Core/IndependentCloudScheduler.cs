namespace KeyPassXCCloudSync;

// One asynchronous scheduling loop per cloud. The scheduling layer never
// serializes unrelated cloud work: only the caller's short coordinator commit
// is allowed to acquire the shared KDBX state lock.
internal sealed class IndependentCloudScheduler : IDisposable
{
    private sealed class Worker(string id)
    {
        internal readonly string Id=id;
        internal readonly SemaphoreSlim Signal=new(0,1);
        internal Task? Task;
    }

    private readonly Dictionary<string,Worker> _workers;
    private readonly CancellationTokenSource _stop;
    private readonly Func<string,DateTimeOffset?> _nextDue;
    private readonly Func<string,CancellationToken,Task> _run;
    private readonly Action<string,Exception>? _onFailure;
    private readonly TimeSpan _retryDelay;
    private bool _disposed;

    internal IndependentCloudScheduler(IEnumerable<string> cloudIds,
        Func<string,DateTimeOffset?> nextDue,
        Func<string,CancellationToken,Task> run,
        TimeSpan retryDelay,CancellationToken shutdown,
        Action<string,Exception>? onFailure=null)
    {
        _nextDue=nextDue;
        _run=run;
        _retryDelay=retryDelay;
        _onFailure=onFailure;
        _stop=CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        _workers=cloudIds.Distinct(StringComparer.Ordinal)
            .ToDictionary(id=>id,id=>new Worker(id),StringComparer.Ordinal);
        foreach(var worker in _workers.Values)
            worker.Task=Task.Run(()=>LoopAsync(worker));
    }

    internal bool Request(string cloudId)
    {
        if(_disposed || !_workers.TryGetValue(cloudId,out var worker))return false;
        try
        {
            if(worker.Signal.CurrentCount==0)worker.Signal.Release();
            return true;
        }
        catch(ObjectDisposedException){return false;}
        catch(SemaphoreFullException){return true;}
    }

    private async Task LoopAsync(Worker worker)
    {
        var retryUntil=DateTimeOffset.MinValue;
        while(!_stop.IsCancellationRequested)
        {
            try
            {
                var now=DateTimeOffset.UtcNow;
                var due=_nextDue(worker.Id)??now;
                if(due<retryUntil)due=retryUntil;
                var delay=due>now?due-now:TimeSpan.Zero;
                // A user request is a wakeup, never a second concurrent run.
                await worker.Signal.WaitAsync(delay,_stop.Token);
                _stop.Token.ThrowIfCancellationRequested();
                await _run(worker.Id,_stop.Token);
                // If no durable deadline was advanced (for example because
                // the installation was disabled), do not hot-spin at 00:00.
                var next=_nextDue(worker.Id);
                retryUntil=next is null || next<=DateTimeOffset.UtcNow
                    ?DateTimeOffset.UtcNow.AddSeconds(5):DateTimeOffset.MinValue;
            }
            catch(OperationCanceledException) when(_stop.IsCancellationRequested){break;}
            catch(Exception ex)
            {
                _onFailure?.Invoke(worker.Id,ex);
                retryUntil=DateTimeOffset.UtcNow+_retryDelay;
            }
        }
    }

    public void Dispose()
    {
        if(_disposed)return;
        _disposed=true;
        _stop.Cancel();
        // Worker continuations unwind asynchronously. Dispose their resources
        // only after cancellation has completed, never during WaitAsync.
        _=Task.WhenAll(_workers.Values.Select(worker=>worker.Task!)).ContinueWith(completed=>
        {
            _=completed.Exception; // Observe any unexpected worker fault.
            foreach(var worker in _workers.Values)worker.Signal.Dispose();
            _stop.Dispose();
        },TaskScheduler.Default);
    }
}
