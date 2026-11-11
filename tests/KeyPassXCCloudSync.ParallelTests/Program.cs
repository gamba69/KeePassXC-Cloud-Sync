using KeyPassXCCloudSync;

// No external cloud, no KDBX credentials. Exercise coordinator concurrency
// and deterministic reconciliation independently of platform services.
internal static class Program
{
    private static int _passes;
    private static void Check(bool condition,string name)
    {
        TestReporter.Check("ParallelTests",name,condition);_passes++;
    }
    private static async Task Main()
    {
        // Optional exact-duration maps may deserialize as null. Aggregation
        // must retain good observations without dereferencing null values.
        var totals=new V2OperationTotals{LatencyMilliseconds=null!};
        var observed=new V2OperationTotals{LatencyMilliseconds=null!};
        observed.Observe(125,true);
        totals.Add(observed);
        Check(totals.SuccessfulSamples==1 && totals.P95Milliseconds==125,
            "nullable exact-duration maps merge without losing percentiles");
        totals.Add(new V2OperationTotals{LatencyMilliseconds=null!});
        Check(totals.SuccessfulSamples==1 && totals.MedianMilliseconds==125,
            "null source map does not corrupt existing samples");

        // Repeated manual clicks are coalesced, but a click arriving during
        // an active cycle must survive its already-drained request snapshot.
        var mailbox=new CloudRequestMailbox();
        Check(mailbox.Enqueue("google") && !mailbox.Enqueue("google") &&
            mailbox.HasPending,"duplicate manual requests are coalesced");
        var firstBatch=mailbox.Drain();
        Check(firstBatch.SetEquals(new[]{"google"}) && !mailbox.HasPending,
            "manual request snapshot is removed atomically");
        mailbox.Enqueue("google"); // Received while previous scan is active.
        mailbox.Enqueue("proton");
        Check(mailbox.HasPending &&
            mailbox.Drain().SetEquals(new[]{"google","proton"}),
            "requests received during a scan survive for the next cycle");
        mailbox.Enqueue("google");
        mailbox.Clear();
        Check(!mailbox.HasPending,"reconfiguration drops stale manual requests");

        // Manual checks select one cloud unless a safety recheck is required.
        var now=DateTimeOffset.UtcNow;
        IReadOnlySet<string> forced=new HashSet<string>(StringComparer.Ordinal){"google"};
        Check(CloudScanSchedule.IsDue("google",now.AddHours(1),now,false,forced),
            "manual check bypasses only the selected cloud deadline");
        Check(!CloudScanSchedule.IsDue("proton",now.AddHours(1),now,false,forced),
            "manual check does not wake unrelated providers");
        Check(CloudScanSchedule.IsDue("proton",now.AddHours(1),now,true,forced),
            "safety recheck can override all provider deadlines");

        // Independent long-lived cloud clocks: a slow provider cannot delay
        // another provider's scheduled scan or an explicit manual request.
        var schedulerDue=new System.Collections.Concurrent.ConcurrentDictionary<string,DateTimeOffset>();
        schedulerDue["proton"]=DateTimeOffset.UtcNow.AddSeconds(-1);
        schedulerDue["google"]=DateTimeOffset.UtcNow.AddSeconds(-1);
        var slowEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlow=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var quickFinished=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var googleRuns=0;
        using(var scheduler=new IndependentCloudScheduler(new[]{"proton","google"},
            id=>schedulerDue[id], async (id,token)=>
            {
                if(id=="proton")
                {
                    schedulerDue[id]=DateTimeOffset.UtcNow.AddHours(1);
                    slowEntered.TrySetResult();
                    await releaseSlow.Task.WaitAsync(token);
                }
                else
                {
                    Interlocked.Increment(ref googleRuns);
                    schedulerDue[id]=DateTimeOffset.UtcNow.AddHours(1);
                    quickFinished.TrySetResult();
                }
            },TimeSpan.FromSeconds(1),CancellationToken.None))
        {
            await slowEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await quickFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(googleRuns==1,"independent Google scan completes during blocked Proton scan");
            Check(scheduler.Request("google"),"manual cloud scan wakes only its worker");
            var deadline=DateTimeOffset.UtcNow.AddSeconds(5);
            while(Volatile.Read(ref googleRuns)<2 && DateTimeOffset.UtcNow<deadline)
                await Task.Delay(20);
            Check(googleRuns==2,"manually requested Google rescan bypasses its long deadline");
            releaseSlow.SetResult();
        }

        // A stalled first provider MUST NOT prevent another provider's I/O.
        var blocked=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fastDone=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run=ParallelCloudRunner.RunAsync(new[]{0,1},async (id,token)=>
        {
            if(id==0)await blocked.Task.WaitAsync(token);
            else fastDone.TrySetResult();
            return id;
        },CancellationToken.None);
        await fastDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!run.IsCompleted,"slow cloud does not block fast cloud I/O");
        blocked.SetResult();
        Check((await run).SequenceEqual(new[]{0,1}),"results maintain stable cloud order");

        // Configured providers begin independently, without a four-cloud queue.
        int running=0,maxObserved=0;
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var maxTask=ParallelCloudRunner.RunAsync(Enumerable.Range(0,9).ToArray(),
            async (id,ct)=>
            {
                var concurrent=Interlocked.Increment(ref running);
                int seen;
                do {seen=Volatile.Read(ref maxObserved);if(seen>=concurrent)break;}
                while(Interlocked.CompareExchange(ref maxObserved,concurrent,seen)!=seen);
                if(concurrent==9)started.TrySetResult();
                await release.Task.WaitAsync(ct);
                Interlocked.Decrement(ref running);
                return id;
            },CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(maxObserved==9,
            "all configured cloud I/O workers can start together");
        release.SetResult();
        Check((await maxTask).SequenceEqual(Enumerable.Range(0,9)),
            "fully parallel scan does not lose or reorder clouds");

        // Completion callbacks are serialized, but do not wait for the slowest cloud.
        var slow=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var quick=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var committed=new List<int>();
        var independent=ParallelCloudRunner.ForEachCompletedAsync(new[]{0,1},
            async (id,token)=>
            {
                if(id==0)await slow.Task.WaitAsync(token);
                return id;
            },id=>
            {
                committed.Add(id);
                if(id==1)quick.TrySetResult();
            },CancellationToken.None);
        await quick.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(committed.SequenceEqual(new[]{1}) && !independent.IsCompleted,
            "fast cloud completion is committed while slow cloud still runs");
        slow.SetResult();
        await independent;
        Check(committed.SequenceEqual(new[]{1,0}),"completion callbacks keep arrival order");

        // Live status is entirely in memory and cannot rewrite durable state.
        var activity=new CloudActivityTracker();
        activity.Requested("google");
        Check(activity.Get("google") is {Active:false,Stage:"Запрошена проверка…"},
            "manual request is visible without inventing active I/O");
        activity.Begin("google","Проверка изменений…");
        activity.OperationStarted("google","Получение метаданных…");
        Check(activity.Get("google") is {Active:true,WaitingForResponse:true,
            OperationStartedUtc:not null},"per-cloud request and elapsed clock start");
        activity.OperationFinished("google","Получение метаданных…",true);
        Check(activity.Get("google") is {CompletedRequests:1,LastResponseUtc:not null},
            "a transport response updates the last-response timestamp");
        activity.Begin("proton","Проверка изменений…");
        activity.OperationStarted("proton","Получение метаданных…");
        activity.End("google",true);
        Check(activity.Get("google") is {Active:false,Stage:"Проверено"} &&
              activity.Get("proton") is {Active:true},
            "fast cloud stops independently while slow peer is still active");
        activity.OperationFinished("proton","Получение метаданных…",false);
        Check(activity.Get("proton") is {LastResponseUtc:null,Stage:"Ошибка запроса"},
            "failed request never fabricates a successful server response");
        activity.Clear();
        Check(activity.Get("google") is null,"reconfiguration clears stale activity");

        // A cloud failure is an individual outcome: the scheduler still
        // collects every other result and never mislabels an inaccessible cloud.
        var failures=await ParallelCloudRunner.RunAsync(new[]{0,1,2},
            async (id,ct)=>
            {
                await Task.Yield();ct.ThrowIfCancellationRequested();
                try {if(id==1)throw new IOException("offline");return "ok";}
                catch(IOException){return "error";}
            },CancellationToken.None);
        Check(failures.SequenceEqual(new[]{"ok","error","ok"}),
            "cloud errors are isolated without fabricating successful reads");

        using var cancel=new CancellationTokenSource();cancel.Cancel();
        try
        {
            await ParallelCloudRunner.RunAsync(new[]{0},async (id,ct)=>
            {await Task.Delay(100,ct);return id;},cancel.Token);
            throw new Exception("Cancellation was ignored");
        }
        catch(OperationCanceledException){Check(true,"cancellation propagates without partial commit");}
        Console.WriteLine($"[SUMMARY] ParallelTests: {_passes}/{_passes} passed.");
    }
}
