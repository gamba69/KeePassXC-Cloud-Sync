using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace KeyPassXCCloudSync;

/// <summary>
/// Same native-pixel artwork selection as Card Print 5.9.44. Use the actual PNG
/// payload of the original 24-256 px ICO frame, never a scaled 256 px bitmap.
/// In-window brandmarks follow the Avalonia content theme; OS icons do not.
/// </summary>
internal static class BrandArtwork
{
    private const string AssetRoot="avares://KeyPassXCCloudSync/Assets/";
    private static readonly int[] NativeSizes=[24,32,40,48,64,96,128,256];
    private static readonly Dictionary<(bool White,int Pixels),Bitmap> CachedFrames=new();

    internal static void Apply(Image image,bool white,double renderScaling,int preferredDip)
    {
        var scale=double.IsFinite(renderScaling)&&renderScaling>0?renderScaling:1.0;
        var target=preferredDip*scale;
        var pixels=NativeSizes[0];
        foreach(var candidate in NativeSizes)
        {
            var distance=Math.Abs(candidate-target);
            var current=Math.Abs(pixels-target);
            if(distance<current || (distance==current && candidate<pixels))pixels=candidate;
        }
        var key=(white,pixels);
        if(!CachedFrames.TryGetValue(key,out var frame))
        {
            using var stream=AssetLoader.Open(new Uri($"{AssetRoot}CloudSync_{(white?"white":"black")}_{pixels}.png"));
            frame=new Bitmap(stream);
            CachedFrames.Add(key,frame);
        }
        var matchedDip=pixels/scale;
        if(!ReferenceEquals(image.Source,frame))image.Source=frame;
        // Avalonia defaults Width/Height to NaN (Auto). A comparison with NaN
        // always evaluates false, so explicit native-frame dimensions must be
        // applied on the first use as well as after a DPI change.
        if(!double.IsFinite(image.Width) || Math.Abs(image.Width-matchedDip)>0.001)
            image.Width=matchedDip;
        if(!double.IsFinite(image.Height) || Math.Abs(image.Height-matchedDip)>0.001)
            image.Height=matchedDip;
    }
}

internal enum BrandStatus { Normal, Warning, Error }

/// <summary>Identical titlebar branding for the main window and secondary dialogs.</summary>
internal static class AppWindowIcons
{
    private static WindowIcon? _white;
    private static WindowIcon? _yellow;
    private static WindowIcon? _red;
    internal static WindowIcon White => _white??=Load("CloudSync_white.ico");
    // Avalonia's Window.Icon setter notifies the native window/Windows Shell.
    // The platform taskbar adapter restores the invariant white titlebar icon.
    internal static WindowIcon ForTaskbar(BrandStatus status) => status switch
    {
        BrandStatus.Warning => _yellow??=Load("CloudSync_yellow.ico"),
        BrandStatus.Error => _red??=Load("CloudSync_red.ico"),
        _ => White
    };
    internal static WindowIcon Load(string filename)
    {
        using var stream=AssetLoader.Open(new Uri("avares://KeyPassXCCloudSync/Assets/"+filename));
        return new WindowIcon(stream);
    }
    internal static void Attach(Window window)
    {
        void Apply()=>window.Icon=White;
        window.Opened+=(_,_)=>Apply();
        Apply();
    }
}
