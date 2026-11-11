namespace KeyPassXCCloudSync;

// Live, process-local telemetry. Never written to state.json or diagnostic logs.
// Distinguishes elapsed work from a countdown to the next scheduled poll.
internal sealed record CloudActivitySnapshot(
    string Stage, DateTimeOffset? StartedUtc, DateTimeOffset? OperationStartedUtc,
    DateTimeOffset? LastResponseUtc, bool Active, bool WaitingForResponse,
    int CompletedRequests);

internal sealed class CloudActivityTracker
{
    private readonly object _gate=new();
    private readonly Dictionary<string,CloudActivitySnapshot> _clouds=new(StringComparer.Ordinal);

    internal CloudActivitySnapshot? Get(string id)
    {
        lock(_gate)return _clouds.TryGetValue(id,out var snapshot)?snapshot:null;
    }

    internal bool AnyActive
    {
        get {lock(_gate)return _clouds.Values.Any(snapshot=>snapshot.Active);}
    }

    internal void Clear()
    {
        lock(_gate)_clouds.Clear();
    }

    internal void Requested(string id)
    {
        lock(_gate)
        {
            if(_clouds.TryGetValue(id,out var old) && old.Active)return;
            _clouds[id]=new("Запрошена проверка…",null,null,null,false,false,0);
        }
    }

    internal void Begin(string id,string stage)
    {
        var now=DateTimeOffset.UtcNow;
        lock(_gate)
            _clouds[id]=new(stage,now,null,null,true,false,0);
    }

    internal void OperationStarted(string id,string stage)
    {
        var now=DateTimeOffset.UtcNow;
        lock(_gate)
        {
            _clouds.TryGetValue(id,out var old);
            _clouds[id]=new(stage,old?.Active==true?old.StartedUtc:now,now,
                old?.LastResponseUtc,true,true,old?.CompletedRequests??0);
        }
    }

    internal void OperationFinished(string id,string stage,bool success)
    {
        var now=DateTimeOffset.UtcNow;
        lock(_gate)
        {
            _clouds.TryGetValue(id,out var old);
            _clouds[id]=new(success?"Обработка результатов…": "Ошибка запроса",
                old?.StartedUtc??now,null,success?now:old?.LastResponseUtc,
                true,false,(old?.CompletedRequests??0)+1);
        }
    }

    internal void End(string id,bool success)
    {
        var now=DateTimeOffset.UtcNow;
        lock(_gate)
        {
            _clouds.TryGetValue(id,out var old);
            _clouds[id]=new(success?"Проверено":"Ошибка проверки",null,null,
                old?.LastResponseUtc,false,false,old?.CompletedRequests??0);
        }
    }
}
