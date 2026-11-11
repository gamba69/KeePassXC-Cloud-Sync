namespace KeyPassXCCloudSync;

// Individual providers have their own retry/timeout limits. Independent
// operations are launched together; serialization is reserved for committing
// results to the shared KDBX version graph, never for remote I/O.
internal static class ParallelCloudRunner
{
    // No artificial four-provider cap. A caller may explicitly set a limit
    // for other kinds of workloads, such as rate-limited cleanup.
    internal const int MaximumConcurrency = int.MaxValue;

    internal static async Task<T[]> RunAsync<TItem,T>(
        IReadOnlyList<TItem> items, Func<TItem,CancellationToken,Task<T>> work,
        CancellationToken ct, int maxConcurrency=MaximumConcurrency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrency);
        using var slots=new SemaphoreSlim(maxConcurrency,maxConcurrency);
        async Task<T> Execute(TItem item)
        {
            await slots.WaitAsync(ct).ConfigureAwait(false);
            try{return await work(item,ct).ConfigureAwait(false);}
            finally{slots.Release();}
        }
        return await Task.WhenAll(items.Select(Execute)).ConfigureAwait(false);
    }

    // Completion callbacks are serialized but invoked as each cloud finishes,
    // rather than after the slowest one. Worker errors must be represented in
    // T (an outcome) so every completed worker can always be disposed/committed.
    internal static Task ForEachCompletedAsync<TItem,T>(
        IReadOnlyList<TItem> items,Func<TItem,CancellationToken,Task<T>> work,
        Action<T> completed,CancellationToken ct)
    {
        var commitGate=new object();
        // Start all scans and let each clone the baseline DAG before any
        // completion may mutate the coordinator's shared version dictionary.
        var pending=items.Select(item=>work(item,ct)).ToArray();
        // Each completed outcome is committed/disposed without a group barrier.
        return Task.WhenAll(pending.Select(async task=>
        {
            var outcome=await task.ConfigureAwait(false);
            lock(commitGate)completed(outcome);
        }));
    }
}
