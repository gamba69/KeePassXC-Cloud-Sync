namespace KeyPassXCCloudSync;

// Minimal protocol-only fixture. These bytes have a valid public KDBX header
// but do NOT form a decryptable password database. Never use as real credentials.
internal static class V2TestFixtures
{
    internal static void FileBytes(string path,string label)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var header=new MemoryStream();
        using(var writer=new BinaryWriter(header,System.Text.Encoding.UTF8,leaveOpen:true))
        {
            writer.Write(0x9AA2D903u);
            writer.Write(0xB54BFB67u);
            writer.Write(0x00040000u);
            writer.Write((byte)0);
            writer.Write(4);
            writer.Write(new byte[]{13,10,13,10});
        }
        using(var file=new FileStream(path,FileMode.Create,FileAccess.Write,FileShare.None))
        {
            var publicHeader=header.ToArray();
            file.Write(publicHeader);
            file.Write(System.Security.Cryptography.SHA256.HashData(publicHeader));
            file.Write(new byte[32]);
            file.Write(System.Text.Encoding.UTF8.GetBytes(label));
            const int minLength=160;
            if(file.Position<minLength)
                file.Write(new byte[checked((int)(minLength-file.Position))]);
            file.Flush(true);
        }
        KdbxInspector.VerifyPublicHeader(path);
    }
}
