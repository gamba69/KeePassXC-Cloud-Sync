using System.Security.Cryptography;

namespace KeyPassXCCloudSync;

// Verifies the public structure of a KDBX 3/4 header. KDBX 4 exposes a
// header SHA-256, so a corrupt header can be rejected without a master key.
// This CANNOT authenticate/decrypt the encrypted body or prove that KeePassXC
// will open the database. A user-verified KeePassXC backup remains essential.
public static class KdbxInspector
{
    private const uint KeePassSignature=0x9AA2D903;
    private const uint KdbxSignature=0xB54BFB67;
    private const int MaxHeaderSize=16*1024*1024;

    public static void VerifyPublicHeader(string path)
    {
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(file.Length<96)throw new SyncSafetyException("KDBX too short to contain a valid database header");
        using var reader=new BinaryReader(file);
        if(reader.ReadUInt32()!=KeePassSignature || reader.ReadUInt32()!=KdbxSignature)
            throw new SyncSafetyException("Not a supported KeePass KDBX database (invalid signature)");
        var version=reader.ReadUInt32();
        var major=(int)(version>>16);
        if(major is not (3 or 4))throw new SyncSafetyException("Unsupported KDBX file version");
        bool terminated=false;
        for(int i=0;i<256;i++)
        {
            if(file.Position+5>file.Length || file.Position>MaxHeaderSize)
                throw new SyncSafetyException("KDBX public header is incomplete or too large");
            var field=reader.ReadByte();
            var size=major>=4?reader.ReadInt32():reader.ReadUInt16();
            if(size<0 || size>MaxHeaderSize || file.Position+size>file.Length ||
               file.Position+size>MaxHeaderSize)
                throw new SyncSafetyException("KDBX header contains an invalid field length");
            var value=reader.ReadBytes(size);
            if(value.Length!=size)throw new SyncSafetyException("Truncated KDBX header field");
            if(field==0)
            {
                if(size!=4 || !value.AsSpan().SequenceEqual(new byte[]{0x0D,0x0A,0x0D,0x0A}))
                    throw new SyncSafetyException("KDBX header terminator is invalid");
                terminated=true;
                break;
            }
        }
        if(!terminated)throw new SyncSafetyException("KDBX header terminator not found");
        if(major==4)
        {
            var headerEnd=checked((int)file.Position);
            if(file.Length<headerEnd+64)throw new SyncSafetyException("Truncated KDBX 4 header authentication fields");
            file.Position=0;
            var actual=SHA256.HashData(reader.ReadBytes(headerEnd));
            var expected=reader.ReadBytes(32);
            if(!CryptographicOperations.FixedTimeEquals(actual,expected))
                throw new SyncSafetyException("KDBX 4 public header SHA-256 mismatch");
        }
    }
}
