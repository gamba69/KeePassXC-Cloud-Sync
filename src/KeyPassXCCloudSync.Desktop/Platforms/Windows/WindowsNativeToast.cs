using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;

namespace KeyPassXCCloudSync;

// Native Windows.UI.Notifications (WinRT) toast implementation. No Windows App SDK,
// third-party notification NuGet packages, or secondary notification-area icons.
// A per-user Start Menu link with System.AppUserModel.ID is required for unpackaged
// desktop toasts: https://learn.microsoft.com/windows/win32/shell/enable-desktop-toast-with-appusermodelid
// The shortcut is installed/updated only when the user explicitly requests a
// notification. Its stable name is owned by KeePassSync; it is not an autorun entry.
internal static unsafe class WindowsNativeToast
{
    private const string AppId="KeyPassXCCloudSync.Portable";
    private const string ShortcutName="KeyPassXC Cloud Sync.lnk";
    // Windows may need time to discover a newly created Start-menu AUMID.
    // Delay only the first notification after a new/changed shortcut.
    private static long _shortcutWriteTimeMs=-1;
    private const uint ClsCtxInprocServer=1;
    private const uint CoInitApartmentThreaded=2;
    private const uint RoInitSingleThreaded=0, RoInitMultiThreaded=1;
    private const int RpcEChangedMode=unchecked((int)0x80010106);
    private static readonly Guid ClsidShellLink=new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid IidShellLinkW=new("000214F9-0000-0000-C000-000000000046");
    private static readonly Guid IidPropertyStore=new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly Guid IidPersistFile=new("0000010B-0000-0000-C000-000000000046");
    private static readonly Guid AppModelFormatId=new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private static readonly Guid IidXmlDocumentIo=new("6CD0E74E-EE65-4489-9EBF-CA43E87BA637");
    private static readonly Guid IidToastManagerStatics=new("50AC103F-D235-4598-BBEF-98FE4D1A3AD4");
    private static readonly Guid IidToastFactory=new("04124B20-82C6-4229-B109-FD9ED4662B53");

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid FormatId; public uint PropertyId; }
    // PROPVARIANT: VARTYPE + reserved fields + 16-byte union on 64-bit Windows.
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Type, Reserved1, Reserved2, Reserved3;
        public nint Value;
        public nint Reserved4;
    }

    [DllImport("ole32.dll",ExactSpelling=true)]
    private static extern int CoInitializeEx(nint reserved,uint mode);
    [DllImport("ole32.dll",ExactSpelling=true)]
    private static extern void CoUninitialize();
    [DllImport("ole32.dll",ExactSpelling=true)]
    private static extern int CoCreateInstance(in Guid clsid,nint outer,uint clsctx,in Guid iid,out nint value);
    [DllImport("ole32.dll",ExactSpelling=true)]
    private static extern int PropVariantClear(ref PropVariant value);
    [DllImport("shell32.dll",EntryPoint="SetCurrentProcessExplicitAppUserModelID",CharSet=CharSet.Unicode,ExactSpelling=true)]
    private static extern int SetCurrentProcessExplicitAppUserModelId([MarshalAs(UnmanagedType.LPWStr)] string appId);

    [DllImport("combase.dll",ExactSpelling=true)]
    private static extern int RoInitialize(uint mode);
    [DllImport("combase.dll",ExactSpelling=true)]
    private static extern void RoUninitialize();
    [DllImport("combase.dll",ExactSpelling=true)]
    private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string value,uint length,out nint handle);
    [DllImport("combase.dll",ExactSpelling=true)]
    private static extern int WindowsDeleteString(nint handle);
    [DllImport("combase.dll",ExactSpelling=true)]
    private static extern int RoActivateInstance(nint className,out nint instance);
    [DllImport("combase.dll",ExactSpelling=true)]
    private static extern int RoGetActivationFactory(nint className,in Guid iid,out nint instance);

    private static nint Slot(nint obj,int index)
    {
        if(obj==nint.Zero)throw new InvalidOperationException("Windows returned a null COM/WinRT object.");
        return ((nint*)(*(nint*)obj))[index];
    }
    private static void Check(int hr,string step)
    {
        if(hr<0)throw new Win32Exception(hr,$"Windows toast: {step} failed (HRESULT 0x{unchecked((uint)hr):X8}).");
    }
    private static nint Query(nint obj,Guid iid)
    {
        nint result=0;
        Check(((delegate* unmanaged[Stdcall]<nint,Guid*,nint*,int>)Slot(obj,0))(obj,&iid,&result),"QueryInterface");
        return result;
    }
    private static void Release(nint obj)
    {
        if(obj!=nint.Zero)((delegate* unmanaged[Stdcall]<nint,uint>)Slot(obj,2))(obj);
    }

    private static nint NewString(string value)
    {
        Check(WindowsCreateString(value,(uint)value.Length,out var handle),"WindowsCreateString");
        return handle;
    }
    private static nint Factory(string className,Guid iid)
    {
        var h=NewString(className);
        try
        {
            Check(RoGetActivationFactory(h,in iid,out var instance),"RoGetActivationFactory("+className+")");
            return instance;
        }
        finally { WindowsDeleteString(h); }
    }

    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),"Programs",ShortcutName);

    // The Start Menu shortcut doubles as the regular user-facing launcher. Its
    // AppUserModelID registers KeePassSync as the native toast sender. We do not
    // overwrite an unrelated user shortcut with the same name.
    private static bool IsOurShortcut(string path)
    {
        if(!File.Exists(path))return false;
        nint link=0,store=0,persist=0;
        var variant=new PropVariant();
        try
        {
            Check(CoCreateInstance(in ClsidShellLink,0,ClsCtxInprocServer,in IidShellLinkW,out link),"Load shortcut");
            persist=Query(link,IidPersistFile);
            fixed(char* pathPointer=path)
                Check(((delegate* unmanaged[Stdcall]<nint,char*,uint,int>)Slot(persist,5))(persist,pathPointer,0),"IPersistFile.Load");
            store=Query(link,IidPropertyStore);
            var key=new PropertyKey{FormatId=AppModelFormatId,PropertyId=5};
            Check(((delegate* unmanaged[Stdcall]<nint,PropertyKey*,PropVariant*,int>)Slot(store,5))(store,&key,&variant),"IPropertyStore.GetValue");
            return variant.Type==31 && string.Equals(Marshal.PtrToStringUni(variant.Value),AppId,StringComparison.Ordinal);
        }
        finally
        {
            PropVariantClear(ref variant);
            Release(persist);Release(store);Release(link);
        }
    }

    // Only recreate our shortcut when its executable or launch arguments changed.
    // Rewriting the .lnk for every notification makes the first toast unreliable
    // because the Windows shell may still be updating its AUMID index.
    private static bool ShortcutTargetMatches(string path,string exe,string args)
    {
        nint link=0,persist=0;
        try
        {
            Check(CoCreateInstance(in ClsidShellLink,0,ClsCtxInprocServer,in IidShellLinkW,out link),"Open shortcut target");
            persist=Query(link,IidPersistFile);
            fixed(char* pathPointer=path)
                Check(((delegate* unmanaged[Stdcall]<nint,char*,uint,int>)Slot(persist,5))(persist,pathPointer,0),"IPersistFile.Load");
            var targetBuffer=new char[32768];
            var argumentsBuffer=new char[8192];
            fixed(char* target=targetBuffer)
                Check(((delegate* unmanaged[Stdcall]<nint,char*,int,nint,uint,int>)Slot(link,3))(link,target,targetBuffer.Length,0,4),"IShellLink.GetPath");
            fixed(char* arguments=argumentsBuffer)
                Check(((delegate* unmanaged[Stdcall]<nint,char*,int,int>)Slot(link,10))(link,arguments,argumentsBuffer.Length),"IShellLink.GetArguments");
            var targetPath=new string(targetBuffer,0,Math.Max(0,Array.IndexOf(targetBuffer,'\0')));
            var currentArgs=new string(argumentsBuffer,0,Math.Max(0,Array.IndexOf(argumentsBuffer,'\0')));
            if(string.IsNullOrWhiteSpace(targetPath))return false;
            return string.Equals(Path.GetFullPath(targetPath),Path.GetFullPath(exe),StringComparison.OrdinalIgnoreCase) &&
                string.Equals(currentArgs,args,StringComparison.Ordinal);
        }
        finally {Release(persist);Release(link);}
    }

    private static void EnsureAppShortcut(string exe)
    {
        var dir=Path.GetDirectoryName(ShortcutPath)!;
        Directory.CreateDirectory(dir);
        var shortcutExists=File.Exists(ShortcutPath);
        if(shortcutExists && !IsOurShortcut(ShortcutPath))
            throw new InvalidOperationException("KeyPassXC Cloud Sync.lnk already exists in Start Menu and does not belong to KeePassSync. Rename it manually before registering notifications; no user shortcut was overwritten.");
        if(shortcutExists && ShortcutTargetMatches(ShortcutPath,exe,Program.ShortcutArguments))return;
        nint link=0,store=0,persist=0;
        try
        {
            Check(CoCreateInstance(in ClsidShellLink,0,ClsCtxInprocServer,in IidShellLinkW,out link),"CoCreateInstance(ShellLink)");
            fixed(char* exePointer=exe)
                Check(((delegate* unmanaged[Stdcall]<nint,char*,int>)Slot(link,20))(link,exePointer),"IShellLink.SetPath");
            var args=Program.ShortcutArguments;
            fixed(char* argsPointer=args)
                Check(((delegate* unmanaged[Stdcall]<nint,char*,int>)Slot(link,11))(link,argsPointer),"IShellLink.SetArguments");
            var workingDir=Path.GetDirectoryName(exe)!;
            fixed(char* dirPointer=workingDir)
                Check(((delegate* unmanaged[Stdcall]<nint,char*,int>)Slot(link,9))(link,dirPointer),"IShellLink.SetWorkingDirectory");
            // The icon is extracted by Windows from the KeePassSync executable.
            fixed(char* iconPath=exe)
                Check(((delegate* unmanaged[Stdcall]<nint,char*,int,int>)Slot(link,17))(link,iconPath,0),"IShellLink.SetIconLocation");
            store=Query(link,IidPropertyStore);
            var key=new PropertyKey{FormatId=AppModelFormatId,PropertyId=5}; // PKEY_AppUserModel_ID
            var variant=new PropVariant{Type=31,Value=Marshal.StringToCoTaskMemUni(AppId)}; // VT_LPWSTR
            try
            {
                Check(((delegate* unmanaged[Stdcall]<nint,PropertyKey*,PropVariant*,int>)Slot(store,6))(store,&key,&variant),"IPropertyStore.SetValue(AppUserModelID)");
                Check(((delegate* unmanaged[Stdcall]<nint,int>)Slot(store,7))(store),"IPropertyStore.Commit");
            }
            finally { PropVariantClear(ref variant); }
            persist=Query(link,IidPersistFile);
            var shortcutPath=ShortcutPath;
            fixed(char* pathPointer=shortcutPath)
                Check(((delegate* unmanaged[Stdcall]<nint,char*,int,int>)Slot(persist,6))(persist,pathPointer,1),"IPersistFile.Save");
            Volatile.Write(ref _shortcutWriteTimeMs,Environment.TickCount64);
        }
        finally
        {
            Release(persist);Release(store);Release(link);
        }
    }

    // IInspectable methods occupy vtable entries 0-5. All WinRT interface
    // functions below use the ABI signatures from Windows SDK IDL.
    private static void Show(string title,string message)
    {
        nint document=0,xmlIo=0,toastManager=0,notifier=0,toastFactory=0,toast=0;
        nint appIdHandle=0,xmlHandle=0;
        try
        {
            var documentClass=NewString("Windows.Data.Xml.Dom.XmlDocument");
            try { Check(RoActivateInstance(documentClass,out document),"XmlDocument activation"); }
            finally { WindowsDeleteString(documentClass); }
            xmlIo=Query(document,IidXmlDocumentIo);
            // Escape both fields: never treat vault names or user messages as XML.
            var xml="<toast><visual><binding template=\"ToastGeneric\"><text>"+
                SecurityElement.Escape(title)+"</text><text>"+
                SecurityElement.Escape(message)+"</text></binding></visual></toast>";
            xmlHandle=NewString(xml);
            Check(((delegate* unmanaged[Stdcall]<nint,nint,int>)Slot(xmlIo,6))(xmlIo,xmlHandle),"IXmlDocumentIO.LoadXml");
            toastFactory=Factory("Windows.UI.Notifications.ToastNotification",IidToastFactory);
            Check(((delegate* unmanaged[Stdcall]<nint,nint,nint*,int>)Slot(toastFactory,6))(toastFactory,document,&toast),"IToastNotificationFactory.CreateToastNotification");
            toastManager=Factory("Windows.UI.Notifications.ToastNotificationManager",IidToastManagerStatics);
            appIdHandle=NewString(AppId);
            Check(((delegate* unmanaged[Stdcall]<nint,nint,nint*,int>)Slot(toastManager,7))(toastManager,appIdHandle,&notifier),"IToastNotificationManagerStatics.CreateToastNotifierWithId");
            Check(((delegate* unmanaged[Stdcall]<nint,nint,int>)Slot(notifier,6))(notifier,toast),"IToastNotifier.Show");
        }
        finally
        {
            if(appIdHandle!=0)WindowsDeleteString(appIdHandle);
            if(xmlHandle!=0)WindowsDeleteString(xmlHandle);
            Release(toast);Release(notifier);Release(toastManager);Release(toastFactory);
            Release(xmlIo);Release(document);
        }
    }

    // Prepare at application startup, not at the moment of the first toast.
    // If startup registration fails, the normal app still works and the next
    // notification reports a clear, actionable error to Diagnostics.
    internal static void Prepare()
    {
        if(!OperatingSystem.IsWindows())return;
        var exe=Environment.ProcessPath;
        if(string.IsNullOrWhiteSpace(exe)||!File.Exists(exe))
            throw new FileNotFoundException("Cannot register Windows toast sender: KeyPassXC Cloud Sync executable not found.");
        var comHr=CoInitializeEx(0,CoInitApartmentThreaded);
        if(comHr<0 && comHr!=RpcEChangedMode)Check(comHr,"CoInitializeEx");
        try
        {
            EnsureAppShortcut(exe);
            Check(SetCurrentProcessExplicitAppUserModelId(AppId),"SetCurrentProcessExplicitAppUserModelID");
        }
        finally {if(comHr>=0)CoUninitialize();}
    }

    internal static void Send(string title,string message)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        if(string.IsNullOrWhiteSpace(title)||string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("Toast title and message must be nonempty.");
        Prepare();
        // The shortcut should normally already exist from application startup.
        // Do not block the UI thread: callers submit notifications on a worker.
        var writtenAt=Volatile.Read(ref _shortcutWriteTimeMs);
        if(writtenAt>=0)
        {
            var remaining=4500-(Environment.TickCount64-writtenAt);
            if(remaining>0)Thread.Sleep((int)Math.Min(remaining,4500));
        }
        var roHr=RoInitialize(RoInitMultiThreaded);
        if(roHr==RpcEChangedMode)roHr=RoInitialize(RoInitSingleThreaded);
        Check(roHr,"RoInitialize");
        try { Show(title,message); }
        finally {RoUninitialize();}
    }
}
