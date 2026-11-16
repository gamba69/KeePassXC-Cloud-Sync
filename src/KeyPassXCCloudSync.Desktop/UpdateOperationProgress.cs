using System.Security.Cryptography;
using System.Text;

namespace KeyPassXCCloudSync;

// Indeterminate percentages are intentional unless actual bytes/files were counted.
internal sealed record UpdateOperationProgress(string Phase, int? Percent = null, string Detail = "");
internal sealed record UpdateOperationResult(string Status, string Operation,
    string FromVersion, string ToVersion, string Detail = "");

// Results survive staging cleanup and belong to the exact portable installation.
internal static class UpdateOperationResults
{
    internal static string PathFor(string root, string target)
    {
        var canonical=Path.GetFullPath(target).TrimEnd(
            Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar).ToUpperInvariant();
        var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return Path.Combine(root,"results",hash+".txt");
    }

    internal static void Save(string root,string target,UpdateOperationResult value)
    {
        value=value with { Detail=Clean(value.Detail) };
        if(!Valid(value))throw new InvalidDataException("Invalid update result.");
        var path=PathFor(root,target);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            File.WriteAllLines(temporary,
                [value.Status,value.Operation,value.FromVersion,value.ToVersion,value.Detail],
                new UTF8Encoding(false));
            File.Move(temporary,path,true);
        }
        finally { if(File.Exists(temporary))File.Delete(temporary); }
    }

    internal static UpdateOperationResult? Read(string root,string target)
    {
        var path=PathFor(root,target);
        try
        {
            if(!File.Exists(path) || new FileInfo(path).Length>2048 ||
                (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)return null;
            var lines=File.ReadAllLines(path);
            if(lines.Length!=5)return null;
            var result=new UpdateOperationResult(lines[0],lines[1],lines[2],lines[3],lines[4]);
            return Valid(result)?result:null;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return null; }
    }

    // 8.0.x helpers only wrote completed.txt. Capture those successful handoffs
    // before deleting the staging directory during the first 8.1 launch.
    internal static void CaptureLegacyCompleted(string root,string target,string runningVersion)
    {
        if(Read(root,target) is not null)return;
        try
        {
            var marker=Path.Combine(root,"completed.txt");
            if(!File.Exists(marker))return;
            var rootPrefix=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            var stage=Path.GetFullPath(File.ReadAllText(marker).Trim());
            if(!stage.StartsWith(rootPrefix,StringComparison.OrdinalIgnoreCase) ||
                (File.GetAttributes(stage)&FileAttributes.ReparsePoint)!=0)return;
            var stageTarget=Path.GetFullPath(File.ReadAllText(Path.Combine(stage,"target.txt")).Trim());
            if(!string.Equals(stageTarget,Path.GetFullPath(target),StringComparison.OrdinalIgnoreCase))return;
            var installed=ReleaseUpdatePolicy.ParseVersion(
                File.ReadAllText(Path.Combine(stage,"verified.txt")).Trim()).ToString(3);
            if(installed!=runningVersion)return;
            var operation=File.Exists(Path.Combine(stage,"operation.txt")) &&
                File.ReadAllText(Path.Combine(stage,"operation.txt")).Trim()=="repair"?"repair":"update";
            Save(root,target,new("installed",operation,runningVersion,runningVersion));
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or
            ArgumentException or InvalidDataException or OverflowException)
        { /* An interrupted or unrelated installation has no success result. */ }
    }

    internal static void Consume(string root,string target)
    {
        try { File.Delete(PathFor(root,target)); }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
    }

    private static string Clean(string value)=>
        new(value.Where(c=>!char.IsControl(c) && c!='\t').Take(500).ToArray());

    private static bool Valid(UpdateOperationResult value)=>
        (value.Status is "installed" or "rolled-back" or "failed") &&
        (value.Operation is "update" or "repair") &&
        Version.TryParse(value.FromVersion,out _) && Version.TryParse(value.ToVersion,out _) &&
        value.Detail.Length<=500 && !value.Detail.Any(char.IsControl);
}
