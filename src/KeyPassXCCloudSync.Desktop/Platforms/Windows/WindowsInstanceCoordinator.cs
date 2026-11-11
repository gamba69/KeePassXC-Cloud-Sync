using System.Security.Cryptography;
using System.Text;

namespace KeyPassXCCloudSync;

internal sealed class WindowsInstanceCoordinator : IInstanceCoordinator
{
    public IInstanceSession Enter(string configPath,Action activate)
    {
        // Unique IPC names prevent separate processes from writing the same vault.
        var identity=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            configPath.ToUpperInvariant())))[..32];
        var signal=new EventWaitHandle(false,EventResetMode.AutoReset,
            @"Local\KeePassSync.Activate."+identity);
        try
        {
            // The acknowledgement confirms that the running process accepted activation.
            var acknowledgement=new EventWaitHandle(false,EventResetMode.AutoReset,
                @"Local\KeePassSync.ActivateAck."+identity);
            try
            {
                var singleton=new Mutex(false,@"Local\KeePassSync.Instance."+identity,out bool first);
                return new Session(signal,acknowledgement,singleton,first,activate);
            }
            catch {acknowledgement.Dispose();throw;}
        }
        catch {signal.Dispose();throw;}
    }
    internal sealed class Session : IInstanceSession
    {
        private readonly EventWaitHandle _signal;
        private readonly EventWaitHandle _ack;
        private readonly Mutex _singleton;
        private readonly CancellationTokenSource _stop=new();
        private readonly Task? _listener;
        public bool IsPrimary {get;}
        internal Session(EventWaitHandle signal,EventWaitHandle acknowledgement,Mutex singleton,
            bool primary,Action activate)
        {
            _signal=signal;_ack=acknowledgement;_singleton=singleton;IsPrimary=primary;
            if(primary)_listener=Task.Run(() =>
            {
                while(!_stop.IsCancellationRequested)
                {
                    try
                    {
                        if(_signal.WaitOne(500))
                        {
                            // Acknowledgement is sent even if the UI dispatcher is
                            // still starting; activation is queued by Program.
                            activate();
                            _ack.Set();
                        }
                    }
                    catch(ObjectDisposedException) when(_stop.IsCancellationRequested){break;}
                }
            });
        }
        public bool ActivateExisting()
        {
            if(IsPrimary)return true;
            _ack.Reset();
            _signal.Set();
            return _ack.WaitOne(TimeSpan.FromMilliseconds(1200));
        }
        public void Dispose()
        {
            _stop.Cancel();
            _listener?.Wait(TimeSpan.FromSeconds(2));
            _ack.Dispose();_signal.Dispose();_singleton.Dispose();_stop.Dispose();
        }
    }
}
