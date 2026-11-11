using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace KeyPassXCCloudSync;

// Window geometry belongs to the local desktop profile and configuration path.
// Keep it out of topology.json and the KDBX state directory, which can be moved.
internal sealed record WindowPlacement(int X,int Y,double Width,double Height,bool Maximized=false);

internal static class WindowPlacementStore
{
    internal static string FileFor(string configPath)
    {
        var full=PlatformServices.Current.Paths.PathComparison==StringComparison.OrdinalIgnoreCase
            ?Path.GetFullPath(configPath).ToUpperInvariant():Path.GetFullPath(configPath);
        var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)).AsSpan(0,8));
        return PlatformServices.Current.Paths.LocalUiFile(configPath,"window-"+hash+".txt");
    }

    internal static WindowPlacement? Load(string configPath)
    {
        try
        {
            var file=FileFor(configPath);
            if(!File.Exists(file))return null;
            var items=File.ReadAllText(file).Trim().Split('|');
            if(items.Length!=5 ||
                !int.TryParse(items[0],NumberStyles.Integer,CultureInfo.InvariantCulture,out var x) ||
                !int.TryParse(items[1],NumberStyles.Integer,CultureInfo.InvariantCulture,out var y) ||
                !double.TryParse(items[2],NumberStyles.Float,CultureInfo.InvariantCulture,out var width) ||
                !double.TryParse(items[3],NumberStyles.Float,CultureInfo.InvariantCulture,out var height) ||
                !double.IsFinite(width) || !double.IsFinite(height) ||
                width<ShellWindow.MinimumWindowWidth || height<640 || width>16000 || height>16000)return null;
            if(!bool.TryParse(items[4],out var maximized))return null;
            return new WindowPlacement(x,y,width,height,maximized);
        }
        catch(IOException){return null;}
        catch(UnauthorizedAccessException){return null;}
        catch(ArgumentException){return null;}
    }

    internal static void Save(string configPath,WindowPlacement p)
    {
        var file=FileFor(configPath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var text=string.Join("|",p.X.ToString(CultureInfo.InvariantCulture),
            p.Y.ToString(CultureInfo.InvariantCulture),
            p.Width.ToString("R",CultureInfo.InvariantCulture),
            p.Height.ToString("R",CultureInfo.InvariantCulture),
            p.Maximized?"true":"false");
        var temp=file+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            File.WriteAllText(temp,text,new UTF8Encoding(false));
            File.Move(temp,file,true);
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
}
