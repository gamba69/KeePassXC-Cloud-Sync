using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace KeyPassXCCloudSync;

/// <summary>
/// Update the genuine window icon via Avalonia first (full-color status wherever
/// Windows supports dynamic taskbar icons), then restore the white caption icon.
/// Windows 11 grouped buttons may use the immutable EXE icon; for those, show
/// only a small clean status DOT in the corner, never a second cloud/key.
/// The EXE icon and the original icon assets remain unchanged.
/// </summary>
internal sealed partial class WindowsTaskbarStatusIcons : ITaskbarStatusIcons
{
    private const uint WmSetIcon=0x0080;
    private const uint IconSmall=0;
    private static readonly Guid ClsidTaskbarList=new("56FDF344-FD6D-11D0-958A-006097C9A090");
    // IID_ITaskbarList3 (shobjidl_core.h). Do not abbreviate: an invalid GUID
    // makes the type initializer fail before any taskbar overlay can be shown.
    private static readonly Guid IidTaskbarList3=new("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF");
    // Use Avalonia's built-in Win32 window procedure hook, not a raw subclass.
    // Windows Shell sends TaskbarButtonCreated only after the button is ready.
    private readonly Win32Properties.CustomWndProcHookCallback _messageCallback;
    private readonly uint _taskbarButtonCreated;
    private Window? _hookedWindow;
    private BrandStatus _wantedStatus;
    private bool _taskbarButtonReady;
    private readonly Dictionary<(string Resource,int Size),nint> _created=new();
    private nint _window;
    private BrandStatus _applied;
    private int _appliedPixels;
    private bool _disposed;
    private bool _reportedTaskbarError;

    internal WindowsTaskbarStatusIcons()
    {
        _messageCallback=TaskbarWindowMessage;
        if(OperatingSystem.IsWindows())
            _taskbarButtonCreated=RegisterWindowMessageW("TaskbarButtonCreated");
    }

    // The callback must be registered BEFORE the window becomes visible so that
    // its very first TaskbarButtonCreated notification cannot be missed.
    public void Attach(Window window)
    {
        if(_disposed || !OperatingSystem.IsWindows() || ReferenceEquals(window,_hookedWindow))return;
        if(_hookedWindow is not null)
            Win32Properties.RemoveWndProcHookCallback(_hookedWindow,_messageCallback);
        _hookedWindow=window;
        _taskbarButtonReady=false;
        Win32Properties.AddWndProcHookCallback(window,_messageCallback);
    }

    // Do not handle the message: Avalonia's original WndProc must still run.
    private nint TaskbarWindowMessage(nint hwnd,uint message,nint wParam,nint lParam,ref bool handled)
    {
        if(message==_taskbarButtonCreated && _taskbarButtonCreated!=0)
        {
            _taskbarButtonReady=true;
            // Defer COM calls until the Windows shell has finished registering
            // the button. Re-apply after Explorer restarts as well.
            Dispatcher.UIThread.Post(() =>
            {
                if(!_disposed && _hookedWindow is { } window &&
                   window.TryGetPlatformHandle()?.Handle==hwnd)
                    Apply(window,_wantedStatus,force:true);
            },DispatcherPriority.Background);
        }
        return 0;
    }

    public void Apply(Window window,BrandStatus status,bool force=false)
    {
        _wantedStatus=status;
        // Icon customization is presentation-only. Never let an absent/disabled
        // Windows shell API terminate the sync engine or the main UI.
        if(_disposed || !OperatingSystem.IsWindows())return;
        try{ApplyCore(window,status,force);}
        catch(Exception ex) when(ex is not OutOfMemoryException)
        {
            if(!_reportedTaskbarError)
            {
                _reportedTaskbarError=true;
                WindowsStartupReporter.RecoverableTaskbarError(ex);
            }
        }
    }

    private void ApplyCore(Window window,BrandStatus status,bool force)
    {
        if(!window.IsVisible || !window.ShowInTaskbar)return;
        var handle=window.TryGetPlatformHandle()?.Handle??0;
        if(handle==0)return;
        var dpi=GetDpiForWindow(handle);
        if(dpi==0)dpi=96;
        var size=SelectNativePixels((int)Math.Round(dpi/96.0*32));
        if(!force && _window==handle && _applied==status && _appliedPixels==size)return;
        {
            // Change the Avalonia property instead of sending only WM_SETICON
            // for ICON_BIG: Avalonia additionally asks Explorer to redraw.
            // Cache WindowIcon instances so unchanged statuses do not reload ICOs.
            var desired=AppWindowIcons.ForTaskbar(status);
            if(!ReferenceEquals(window.Icon,desired))window.Icon=desired;

            // Preserve the original invariant WHITE caption icon. The rest of
            // the window remains theme-aware exactly as before this change.
            var small=SelectNativePixels((int)Math.Round(dpi/96.0*16));
            _=SendMessageW(handle,WmSetIcon,(nuint)IconSmall,NativeIcon("CloudSync_white.ico",small));

            // The Win11 grouped taskbar usually ignores a window icon change.
            // Its supported overlay is a tiny antialiased circular indicator,
            // NEVER another copy of the cloud/key pictogram.
            // Wait for TaskbarButtonCreated or the shell silently drops it.
            if(_taskbarButtonReady)
            {
                nint overlay=status switch
                {
                    BrandStatus.Warning=>NativeIcon("TaskbarDot_yellow.ico",small),
                    BrandStatus.Error=>NativeIcon("TaskbarDot_red.ico",small),
                    _=>0
                };
                TrySetOverlay(handle,overlay,status);
            }
            _window=handle;_applied=status;_appliedPixels=size;
        }
        // Failures are caught at the outer UI-safe boundary and recorded once.
    }

    private static int SelectNativePixels(int target)
    {
        int[] native=[16,20,24,32,40,48,64,96,128,256];
        var found=native[0];
        foreach(var candidate in native)
            if(Math.Abs(candidate-target)<Math.Abs(found-target) ||
               (Math.Abs(candidate-target)==Math.Abs(found-target)&&candidate<found)) found=candidate;
        return found;
    }

    private nint NativeIcon(string resource,int size)
    {
        if(_created.TryGetValue((resource,size),out var result))return result;
        using var stream=AssetLoader.Open(new Uri($"avares://KeyPassXCCloudSync/Assets/{resource}"));
        using var data=new MemoryStream();stream.CopyTo(data);
        var bytes=data.ToArray();
        var frame=FindFrame(bytes,size);
        unsafe
        {
            fixed(byte* start=frame)
                result=CreateIconFromResourceEx(start,(uint)frame.Length,true,0x00030000,size,size,0);
        }
        if(result==0)throw new InvalidOperationException("Windows could not load the native icon frame.");
        _created.Add((resource,size),result);
        return result;
    }

    // The ICO files are collections of PNG frames, with dimensions native to
    // each icon size. Pass an individual frame, not the .ico directory header.
    private static byte[] FindFrame(byte[] bytes,int size)
    {
        var data=bytes.AsSpan();
        if(data.Length<6 || BinaryPrimitives.ReadUInt16LittleEndian(data[..2])!=0 ||
           BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(2,2))!=1)
            throw new InvalidOperationException("Invalid Cloud Sync icon container.");
        var count=BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(4,2));
        for(var i=0;i<count;i++)
        {
            var offset=6+i*16;
            if(offset+16>data.Length)break;
            var width=data[offset]==0?256:data[offset];
            var height=data[offset+1]==0?256:data[offset+1];
            if(width!=size || height!=size)continue;
            var length=BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset+8,4));
            var start=BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset+12,4));
            if(start>(uint)data.Length || length>(uint)data.Length-start)
                throw new InvalidOperationException("Invalid Cloud Sync icon frame length.");
            return data.Slice((int)start,(int)length).ToArray();
        }
        throw new InvalidOperationException("Missing native icon frame: "+size);
    }

    private static unsafe void TrySetOverlay(nint hwnd,nint overlay,BrandStatus status)
    {
        var initialize=CoInitializeEx(0,2); // STA; UI thread may already be initialized.
        try
        {
            // RPC_E_CHANGED_MODE means the thread already has a COM apartment:
            // do not suppress a taskbar update merely for that reason.
            if(initialize<0 && initialize!=unchecked((int)0x80010106))
                Marshal.ThrowExceptionForHR(initialize);
            var created=CoCreateInstance(in ClsidTaskbarList,0,1,in IidTaskbarList3,out var taskbar);
            if(created<0)Marshal.ThrowExceptionForHR(created);
            if(taskbar==0)throw new InvalidOperationException("Taskbar COM instance is null.");
            try
            {
                var vtable=*(nint**)taskbar;
                var init=(delegate* unmanaged[Stdcall]<nint,int>)vtable[3];
                var initialized=init(taskbar);
                if(initialized<0)Marshal.ThrowExceptionForHR(initialized);
                var setOverlay=(delegate* unmanaged[Stdcall]<nint,nint,nint,char*,int>)vtable[18];
                var label=status switch{BrandStatus.Warning=>"Синхронизация требует внимания",BrandStatus.Error=>"Ошибка синхронизации",_=>""};
                int applied;
                fixed(char* text=label) applied=setOverlay(taskbar,hwnd,overlay,text);
                if(applied<0)Marshal.ThrowExceptionForHR(applied);
            }
            finally
            {
                var vtable=*(nint**)taskbar;
                _=((delegate* unmanaged[Stdcall]<nint,uint>)vtable[2])(taskbar);
            }
        }
        finally {if(initialize is 0 or 1)CoUninitialize();}
    }

    public void Dispose()
    {
        if(_disposed)return;
        _disposed=true;
        if(_hookedWindow is not null)
            Win32Properties.RemoveWndProcHookCallback(_hookedWindow,_messageCallback);
        _hookedWindow=null;
        foreach(var handle in _created.Values) if(handle!=0)_=DestroyIcon(handle);
        _created.Clear();_window=0;
    }

    [LibraryImport("user32.dll",EntryPoint="RegisterWindowMessageW",StringMarshalling=StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessageW(string value);
    [LibraryImport("user32.dll",EntryPoint="SendMessageW")]
    private static partial nint SendMessageW(nint hwnd,uint msg,nuint wParam,nint lParam);
    [LibraryImport("user32.dll",SetLastError=true)]
    private static unsafe partial nint CreateIconFromResourceEx(byte* bits,uint count,
        [MarshalAs(UnmanagedType.Bool)] bool isIcon,uint version,int width,int height,uint flags);
    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);
    [LibraryImport("user32.dll")]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);
    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved,uint coInit);
    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();
    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid,nint outer,uint context,in Guid iid,out nint result);
}
