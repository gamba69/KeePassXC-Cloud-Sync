namespace KeyPassXCCloudSync;

// Logical cloud-store operations, not underlying HTTP packets or CLI subprocesses.
// Exact millisecond duration frequencies support the median and nearest-rank P95.
public sealed class V2OperationTotals
{
    public long Get {get;set;}
    public long Put {get;set;}
    public long Probe {get;set;}
    public long List {get;set;}
    public long Mkdir {get;set;}
    public long Delete {get;set;}
    public long DownloadBytes {get;set;}
    public long UploadBytes {get;set;}
    public long ElapsedMilliseconds {get;set;}
    public long Failed {get;set;}
    public long SuccessfulMilliseconds {get;set;}
    public long DownloadMilliseconds {get;set;}
    public long UploadMilliseconds {get;set;}
    public Dictionary<long,long> LatencyMilliseconds {get;set;}=new();
    public long Operations=>Get+Put+Probe+List+Mkdir+Delete;
    public long SuccessfulSamples
    {
        get
        {
            var samples=LatencyMilliseconds;
            return samples is null?0:samples.Values.Sum(count=>Math.Max(0L,count));
        }
    }
    // Rank is one-based, so the percentile follows the nearest-rank definition.
    private long? DurationAtRank(long rank)
    {
        var samples=LatencyMilliseconds;
        if(samples is null || rank<=0)return null;
        long seen=0;
        foreach(var (duration,frequency) in samples.OrderBy(item=>item.Key))
        {
            seen+=Math.Max(0,frequency);
            if(seen>=rank)return duration;
        }
        return null;
    }
    // For an even number of observations, average the two central values.
    public double? MedianMilliseconds
    {
        get
        {
            var count=SuccessfulSamples;
            if(count==0)return null;
            var left=DurationAtRank((count+1)/2);
            var right=DurationAtRank(count/2+1);
            if(left is null || right is null)return null;
            return left.Value/2d+right.Value/2d;
        }
    }
    // The 95th percentile is the actual observation at rank ceil(0.95*n).
    // n - floor(n/20) avoids floating-point rounding of large sample counts.
    public long? P95Milliseconds
    {
        get
        {
            var count=SuccessfulSamples;
            return count==0?null:DurationAtRank(count-count/20);
        }
    }
    // Reset only duration measurements; preserve operation and traffic counters.
    public void ResetLatency()
    {
        SuccessfulMilliseconds=0;
        LatencyMilliseconds=new Dictionary<long,long>();
    }
    // Values reflect payload bytes divided by elapsed logical transfer operation time;
    // they are not a benchmark of the provider's raw network connection.
    public double? DownloadBytesPerSecond=>DownloadBytes>0 && DownloadMilliseconds>0
        ?DownloadBytes*1000d/DownloadMilliseconds:null;
    public double? UploadBytesPerSecond=>UploadBytes>0 && UploadMilliseconds>0
        ?UploadBytes*1000d/UploadMilliseconds:null;
    public void Observe(long elapsedMilliseconds,bool succeeded)
    {
        elapsedMilliseconds=Math.Max(0,elapsedMilliseconds);
        ElapsedMilliseconds+=elapsedMilliseconds;
        if(!succeeded){Failed++;return;}
        SuccessfulMilliseconds+=elapsedMilliseconds;
        var samples=LatencyMilliseconds??=new Dictionary<long,long>();
        samples.TryGetValue(elapsedMilliseconds,out var frequency);
        samples[elapsedMilliseconds]=frequency+1;
    }
    public V2OperationTotals Copy()=>new(){Get=Get,Put=Put,Probe=Probe,List=List,
        Mkdir=Mkdir,Delete=Delete,DownloadBytes=DownloadBytes,UploadBytes=UploadBytes,
        ElapsedMilliseconds=ElapsedMilliseconds,Failed=Failed,
        SuccessfulMilliseconds=SuccessfulMilliseconds,
        DownloadMilliseconds=DownloadMilliseconds,UploadMilliseconds=UploadMilliseconds,
        LatencyMilliseconds=new Dictionary<long,long>(LatencyMilliseconds??new())};
    public void Add(V2OperationTotals next)
    {
        Get+=next.Get;Put+=next.Put;Probe+=next.Probe;List+=next.List;
        Mkdir+=next.Mkdir;Delete+=next.Delete;
        DownloadBytes+=next.DownloadBytes;UploadBytes+=next.UploadBytes;
        ElapsedMilliseconds+=next.ElapsedMilliseconds;Failed+=next.Failed;
        SuccessfulMilliseconds+=next.SuccessfulMilliseconds;
        DownloadMilliseconds+=next.DownloadMilliseconds;UploadMilliseconds+=next.UploadMilliseconds;
        var samples=LatencyMilliseconds??=new Dictionary<long,long>();
        if(next.LatencyMilliseconds is { } newSamples)
            foreach(var (duration,frequency) in newSamples)
            {
                samples.TryGetValue(duration,out var previous);
                samples[duration]=previous+frequency;
            }
    }
}

internal class V2MeteredStore(IRemoteStore inner,Action<string,bool,bool>? activity=null) : IRemoteStore
{
    protected readonly IRemoteStore Inner=inner;
    internal V2OperationTotals Stats {get;}=new();
    protected async Task<T> Measure<T>(Func<Task<T>> action,Action<long,T>? onSuccess=null,string stage="Проверка изменений…")
    {
        activity?.Invoke(stage,false,false);
        var start=System.Diagnostics.Stopwatch.GetTimestamp();
        bool success=false;
        try
        {
            var result=await action();
            success=true;
            onSuccess?.Invoke(Math.Max(0,(long)Math.Ceiling(
                System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds)),result);
            return result;
        }
        finally
        {
            Stats.Observe((long)Math.Ceiling(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds),success);
            activity?.Invoke(stage,true,success);
        }
    }
    protected async Task MeasureVoid(Func<Task> action,Action<long>? onSuccess=null,string stage="Отправка данных…")
    {
        activity?.Invoke(stage,false,false);
        var start=System.Diagnostics.Stopwatch.GetTimestamp();
        bool success=false;
        try
        {
            await action();success=true;
            onSuccess?.Invoke(Math.Max(0,(long)Math.Ceiling(
                System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds)));
        }
        finally
        {
            Stats.Observe((long)Math.Ceiling(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds),success);
            activity?.Invoke(stage,true,success);
        }
    }
    internal Task<T> Probe<T>(Func<Task<T>> action){Stats.Probe++;return Measure(action);}
    internal Task Probe(Func<Task> action){Stats.Probe++;return MeasureVoid(action);}
    public Task<bool> DownloadAsync(string path,string destination,CancellationToken ct)
    {
        Stats.Get++;
        return Measure(()=>Inner.DownloadAsync(path,destination,ct),(ms,result)=>
        {
            if(!result)return;
            Stats.DownloadBytes+=new FileInfo(destination).Length;
            Stats.DownloadMilliseconds+=ms;
        },path.EndsWith(".kdbx",StringComparison.OrdinalIgnoreCase)
            ?"Загрузка KDBX…":"Получение метаданных…");
    }
    public Task UploadAsync(string source,string path,WriteMode mode,CancellationToken ct)
    {
        Stats.Put++;
        var bytes=new FileInfo(source).Length;
        return MeasureVoid(()=>Inner.UploadAsync(source,path,mode,ct),ms=>
        {Stats.UploadBytes+=bytes;Stats.UploadMilliseconds+=ms;},
            path.EndsWith(".kdbx",StringComparison.OrdinalIgnoreCase)
                ?"Публикация KDBX…":"Публикация метаданных…");
    }
    public Task<IReadOnlyList<RemoteFile>> ListAsync(string directory,CancellationToken ct)
    {Stats.List++;return Measure(()=>Inner.ListAsync(directory,ct),stage:"Получение списка файлов…");}
    public Task DeleteAsync(string path,CancellationToken ct)
    {Stats.Delete++;return MeasureVoid(()=>Inner.DeleteAsync(path,ct),stage:"Очистка старых версий…");}
    public Task EnsureDirectoryAsync(string directory,CancellationToken ct)
    {Stats.Mkdir++;return MeasureVoid(()=>Inner.EnsureDirectoryAsync(directory,ct),stage:"Подготовка каталога…");}
    public Task DeleteTestDirectoryAsync(string directory,CancellationToken ct)
    {Stats.Delete++;return MeasureVoid(()=>Inner.DeleteTestDirectoryAsync(directory,ct),stage:"Очистка каталога…");}
    public void Dispose()=>Inner.Dispose();
}
internal sealed class V2MeteredCasStore(IRemoteStore inner,Action<string,bool,bool>? activity=null) : V2MeteredStore(inner,activity),IConditionalWriteStore
{
    public Task<string?> ReadTokenAsync(string path,CancellationToken ct)
        =>Probe(() =>((IConditionalWriteStore)Inner).ReadTokenAsync(path,ct));
    public Task UploadConditionalAsync(string source,string path,string? observedToken,CancellationToken ct)
    {
        Stats.Put++;
        var bytes=new FileInfo(source).Length;
        return MeasureVoid(()=>((IConditionalWriteStore)Inner).UploadConditionalAsync(source,path,observedToken,ct),ms=>
        {Stats.UploadBytes+=bytes;Stats.UploadMilliseconds+=ms;},
            path.EndsWith(".kdbx",StringComparison.OrdinalIgnoreCase)
                ?"Публикация KDBX…":"Публикация метаданных…");
    }
}
