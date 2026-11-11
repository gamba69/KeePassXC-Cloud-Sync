using System.Runtime.InteropServices;
using System.Text;

namespace KeyPassXCCloudSync;

// Read Generic Credential only. Provision it through Windows Credential Manager.
internal static class WindowsCredentials
{
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags,Type;
        public IntPtr TargetName,Comment;
        public long LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist,AttributeCount;
        public IntPtr Attributes,TargetAlias,UserName;
    }
    [DllImport("advapi32.dll", EntryPoint="CredReadW", CharSet=CharSet.Unicode, SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target,uint type,uint reserved,out IntPtr result);
    [DllImport("advapi32.dll",EntryPoint="CredWriteW",CharSet=CharSet.Unicode, SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential,uint flags);
    [DllImport("advapi32.dll",EntryPoint="CredFree")]
    private static extern void CredFree(IntPtr pointer);

    [DllImport("advapi32.dll",EntryPoint="CredDeleteW",CharSet=CharSet.Unicode,SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target,uint type,uint flags);
    [DllImport("advapi32.dll",EntryPoint="CredEnumerateW",CharSet=CharSet.Unicode,SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredEnumerate(string? filter,uint flags,out uint count,out IntPtr records);

    internal static void Delete(string target)
    {
        if(string.IsNullOrWhiteSpace(target))return;
        if(!target.StartsWith("KeePassSync/",StringComparison.OrdinalIgnoreCase))
            throw new SyncSafetyException("Refusing to delete a credential not owned by KeePassSync");
        if(!CredDelete(target,1,0) && Marshal.GetLastWin32Error()!=1168)
            throw new SyncSafetyException("Could not delete credential: "+target);
    }
    // Normal saves prune targets recorded by this application. A full leave
    // also enumerates credentials in the application's own namespace.
    internal static void RemoveUnused(IReadOnlyCollection<string> active,bool fullLeave=false)
    {
        if(!OperatingSystem.IsWindows())return;
        if(fullLeave)
        {
            if(!CredEnumerate("KeePassSync/*",0,out var count,out var records))
            {
                if(Marshal.GetLastWin32Error()!=1168)
                    throw new SyncSafetyException("Cannot enumerate KeePassSync credentials");
                count=0;records=IntPtr.Zero;
            }
            try
            {
                for(int i=0;i<count;i++)
                {
                    var ptr=Marshal.ReadIntPtr(records,i*IntPtr.Size);
                    var credential=Marshal.PtrToStructure<Credential>(ptr);
                    var target=Marshal.PtrToStringUni(credential.TargetName)??"";
                    if(target.StartsWith("KeePassSync/",StringComparison.OrdinalIgnoreCase) &&
                       !active.Contains(target,StringComparer.OrdinalIgnoreCase))Delete(target);
                }
            }
            finally{if(records!=IntPtr.Zero)CredFree(records);}
        }
        var owned=Owned();
        foreach(var target in owned)
            if(!active.Contains(target,StringComparer.OrdinalIgnoreCase))
            {
                if(target.StartsWith("KeePassSync/",StringComparison.OrdinalIgnoreCase))Delete(target);
                else if(!CredDelete(target,1,0) && Marshal.GetLastWin32Error()!=1168)
                    throw new SyncSafetyException("Cannot delete owned credential: "+target);
            }
        var retained=owned.Where(x=>active.Contains(x,StringComparer.OrdinalIgnoreCase)).ToArray();
        SyncStorage.SaveJsonAtomic(OwnedListPath,retained,StateJsonContext.Default.StringArray);
    }
    internal static bool Exists(string target)
    {
        if(string.IsNullOrEmpty(target))return false;
        if(!CredRead(target,1,0,out var ptr))return false;
        CredFree(ptr);return true;
    }
    private static string OwnedListPath=>Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData),"KeePassSync","owned-credential-targets.json");
    private static string[] Owned()
    {
        var path=OwnedListPath;
        return File.Exists(path)?System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(path),
            StateJsonContext.Default.StringArray)??[]:[];
    }
    private static void Remember(string target)
    {
        var all=Owned().ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(all.Add(target))SyncStorage.SaveJsonAtomic(OwnedListPath,all.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            StateJsonContext.Default.StringArray);
    }
    public static void Write(string target,string user,string secret)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Windows Credential Manager required");
        var encoded=Encoding.Unicode.GetBytes(secret);
        if(encoded.Length>2560)throw new SyncSafetyException("Credential exceeds Windows Credential Manager limit");
        IntPtr name=IntPtr.Zero, username=IntPtr.Zero, blob=IntPtr.Zero;
        try
        {
            name=Marshal.StringToHGlobalUni(target);
            username=Marshal.StringToHGlobalUni(user);
            blob=Marshal.AllocHGlobal(encoded.Length);
            Marshal.Copy(encoded,0,blob,encoded.Length);
            var value=new Credential { Type=1, TargetName=name, UserName=username,
                BlobSize=(uint)encoded.Length, Blob=blob, Persist=2 };
            if(!CredWrite(ref value,0)) throw new SyncSafetyException("Cannot save credential to Windows Credential Manager ("+Marshal.GetLastWin32Error()+")");
            Remember(target);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(encoded);
            if(blob!=IntPtr.Zero)
            {
                // Erase before releasing unmanaged memory.
                Marshal.Copy(new byte[Encoding.Unicode.GetByteCount(secret)],0,blob,Encoding.Unicode.GetByteCount(secret));
                Marshal.FreeHGlobal(blob);
            }
            if(username!=IntPtr.Zero)Marshal.FreeHGlobal(username);
            if(name!=IntPtr.Zero)Marshal.FreeHGlobal(name);
        }
    }
    public static (string User,string Password) Read(string target)
    {
        if(!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows Credential Manager is required");
        if(!CredRead(target,1,0,out var pointer))
            throw new SyncSafetyException($"Missing Windows Generic Credential '{target}'");
        try
        {
            var c=Marshal.PtrToStructure<Credential>(pointer);
            var user=Marshal.PtrToStringUni(c.UserName) ?? "";
            if(c.BlobSize == 0 || c.BlobSize>8192 || (c.BlobSize&1)!=0)
                throw new SyncSafetyException("Invalid Windows credential blob");
            var bytes=new byte[checked((int)c.BlobSize)];
            Marshal.Copy(c.Blob,bytes,0,bytes.Length);
            try { return (user,Encoding.Unicode.GetString(bytes)); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { CredFree(pointer); }
    }
}
