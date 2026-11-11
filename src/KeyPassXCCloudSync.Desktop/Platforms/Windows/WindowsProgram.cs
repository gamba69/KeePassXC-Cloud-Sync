using Avalonia;
using Avalonia.Threading;

namespace KeyPassXCCloudSync;

internal static class Program
{
    internal static bool ShowRequested {get;set;}
    internal static bool BackgroundRequested {get;set;}
    internal static string ShortcutArguments {get;set;}="--show";
    private static Action? _activateWindow;
    private static int _activationPending;

    [STAThread]
    public static int Main(string[] args)=>WindowsHost.Start(args);

    internal static void UnbindActivation()=>Volatile.Write(ref _activateWindow,null);
    internal static void BindActivation(Action showWindow)
    {
        Volatile.Write(ref _activateWindow,showWindow);
        DispatchPendingActivation();
    }
    internal static void SignalActivation()
    {
        Interlocked.Exchange(ref _activationPending,1);
        DispatchPendingActivation();
    }
    private static void DispatchPendingActivation()
    {
        var callback=Volatile.Read(ref _activateWindow);
        if(callback is null)return;
        if(Interlocked.Exchange(ref _activationPending,0)==1)
            Dispatcher.UIThread.Post(callback);
    }
    internal static string QuoteArgument(string value)=>"\""+value.Replace("\"","\\\"")+"\"";
}
