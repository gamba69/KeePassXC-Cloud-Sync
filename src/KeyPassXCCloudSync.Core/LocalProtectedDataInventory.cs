namespace KeyPassXCCloudSync;

/// <summary>
/// Read-only local inventory. Never opens the KDBX contents, touches clouds,
/// follows directory links or changes retention settings.
/// </summary>
internal static class LocalProtectedDataInventory
{
    internal static readonly string[] FolderNames =
        ["startup-backups", "backup", "conflicts", "history"];

    internal sealed record FolderResult(string Name, long FileCount, long SizeBytes, string? Error)
    {
        internal bool Complete => Error is null;
    }

    internal static FolderResult[] Scan(string dataDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var directory = Path.GetFullPath(dataDirectory);
        return FolderNames.Select(name => ScanFolder(directory, name, cancellationToken)).ToArray();
    }

    private static FolderResult ScanFolder(string root, string name, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = Path.Combine(root, name);
        long count = 0, size = 0;
        try
        {
            // Absence is normal on a new installation. Do not hide access errors.
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { return new FolderResult(name, 0, 0, null); }
            catch (DirectoryNotFoundException)
            {
                // An absent data root is normal; an existing non-directory root is not.
                if (File.Exists(root)) throw new IOException("Путь каталога состояния указывает на файл: " + root);
                return new FolderResult(name, 0, 0, null);
            }
            if ((attributes & FileAttributes.Directory) == 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Небезопасная папка (не каталог или ссылка): " + path);

            var dirs = new Stack<string>();
            dirs.Push(path);
            while (dirs.Count != 0)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var entry in Directory.EnumerateFileSystemEntries(dirs.Pop()))
                {
                    ct.ThrowIfCancellationRequested();
                    var entryAttributes = File.GetAttributes(entry);
                    if ((entryAttributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Обнаружена ссылка; подсчёт неполный: " + entry);
                    if ((entryAttributes & FileAttributes.Directory) != 0)
                        dirs.Push(entry);
                    else
                    {
                        count = checked(count + 1);
                        size = checked(size + new FileInfo(entry).Length);
                    }
                }
            }
            return new FolderResult(name, count, size, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or OverflowException or ArgumentException or NotSupportedException)
        {
            // Never report partial counts as complete after an inaccessible subtree.
            return new FolderResult(name, 0, 0, ex.Message);
        }
    }
}
