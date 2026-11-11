namespace KeyPassXCCloudSync;

public static class Hashes
{
    public static string Sha256(string path)
    {
        using var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(f)).ToLowerInvariant();
    }
    public static bool Valid(string? sha) => sha is {Length:64} && sha.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static string ReadMarker(string file)
    {
        var bytes=File.ReadAllBytes(file);
        if(bytes.Length!=65 || bytes[64]!=10) throw new SyncSafetyException("Remote marker has an invalid format");
        var sha=System.Text.Encoding.ASCII.GetString(bytes,0,64);
        if(!Valid(sha)) throw new SyncSafetyException("Remote marker has an invalid SHA-256");
        return sha;
    }
    public static void WriteMarker(string file,string sha) => File.WriteAllText(file,sha+"\n",new System.Text.UTF8Encoding(false));
}
