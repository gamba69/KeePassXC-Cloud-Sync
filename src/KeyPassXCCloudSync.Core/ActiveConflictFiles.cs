using System.Text.Json;

namespace KeyPassXCCloudSync;

/// <summary>
/// The *current* unresolved conflict is defined by AppState.ConflictVersionIds,
/// never by enumerating conflicts/ (which also contains historical KDBX files).
/// Reads only local version metadata and file attributes, never KDBX contents.
/// </summary>
internal static class ActiveConflictFiles
{
    internal sealed record Entry(string VersionId, string FileName, DateTimeOffset? CreatedUtc,
        long? SizeBytes, string? Source, string? Error)
    {
        internal bool Ready => Error is null;
    }

    internal static Entry[] Read(string dataDirectory, AppState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        if (!state.Conflict) return [];
        return state.ConflictVersionIds.Distinct(StringComparer.Ordinal)
            .Select(id => ReadOne(dataDirectory, state.VaultId, id)).ToArray();
    }

    private static Entry ReadOne(string root, string? vaultId, string id)
    {
        // An id originating from cloud metadata must never become a path segment
        // before it has passed the protocol's fixed-width GUID validation.
        if (!Guid.TryParseExact(id, "N", out _))
            return new Entry(id, "—", null, null, null, "Некорректный идентификатор версии");
        var name = id + ".kdbx";
        try
        {
            var path = Path.Combine(root, "conflicts", name);
            var metaPath = Path.Combine(root, "history", id + ".json");
            if (!File.Exists(path))
                return new Entry(id, name, null, null, null, "Отсутствует конфликтная копия KDBX");
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return new Entry(id, name, null, null, null, "Конфликтная копия является ссылкой");
            if (!File.Exists(metaPath))
                return new Entry(id, name, null, null, null, "Не найдены метаданные версии");
            if ((File.GetAttributes(metaPath) & FileAttributes.ReparsePoint) != 0)
                return new Entry(id, name, null, null, null, "Метаданные являются ссылкой");
            var info = JsonSerializer.Deserialize(File.ReadAllText(metaPath), SyncJson.Default.VersionInfo);
            if (info is null || info.VersionId != id || info.VaultId != vaultId ||
                !Hashes.Valid(info.Sha256) || info.Size < 1 || info.BackupName != name)
                return new Entry(id, name, null, null, null, "Метаданные версии не совпадают с конфликтом");
            var length = new FileInfo(path).Length;
            if (length != info.Size)
                return new Entry(id, name, info.CreatedUtc, length, info.Source,
                    "Размер конфликтной копии отличается от метаданных");
            return new Entry(id, name, info.CreatedUtc, length, info.Source, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or JsonException or ArgumentException or NotSupportedException)
        {
            return new Entry(id, name, null, null, null, "Ошибка чтения: " + ex.Message);
        }
    }
}
