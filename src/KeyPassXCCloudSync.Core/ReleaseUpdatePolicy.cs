using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace KeyPassXCCloudSync;

// Pure update policy. No cloud state, KDBX or Windows-specific operations here.
internal static class ReleaseUpdatePolicy
{
    private const long MaxArchiveBytes = 256L * 1024 * 1024;
    private const long MaxExtractedBytes = 512L * 1024 * 1024;
    internal const string Executable = "KeyPassXCCloudSync.exe";
    internal const string Repository = "gamba69/KeePassXC-Cloud-Sync";
    internal const string RepositoryId = "1454413194";

    internal static Version ParseVersion(string text)
    {
        if (!Regex.IsMatch(text, @"^v?[0-9]+\.[0-9]+\.[0-9]+$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Unsupported release version.");
        var parts = text.TrimStart('v').Split('.');
        if (!int.TryParse(parts[0], out var major) ||
            !int.TryParse(parts[1], out var minor) ||
            !int.TryParse(parts[2], out var patch))
            throw new InvalidDataException("Release version exceeds the supported range.");
        return new Version(major, minor, patch);
    }

    internal static string ArchiveName(string tag) =>
        $"KeyPassXCCloudSync-{ParseVersion(tag).ToString(3)}-win-x64.zip";

    internal static string ChecksumName(string tag) =>
        $"KeyPassXCCloudSync-{ParseVersion(tag).ToString(3)}-win-x64.sha256";

    internal static bool IsNewer(string candidate, string installed) =>
        ParseVersion(candidate).CompareTo(ParseVersion(installed)) > 0;

    // Equal versions are eligible only for an explicit, one-shot reinstall.
    // An older version is never eligible, regardless of the command line.
    internal static bool IsEligible(string candidate, string installed, bool reinstall)
    {
        var comparison=ParseVersion(candidate).CompareTo(ParseVersion(installed));
        return comparison>0 || (reinstall && comparison==0);
    }

    internal static string ParseChecksum(string text, string expectedFileName)
    {
        var match = Regex.Match(text, @"\A([a-fA-F0-9]{64})  ([^\r\n]+)\r?\n?\z", RegexOptions.CultureInvariant);
        if (!match.Success || !string.Equals(match.Groups[2].Value, expectedFileName, StringComparison.Ordinal))
            throw new InvalidDataException("Release checksum format or filename is invalid.");
        return match.Groups[1].Value.ToLowerInvariant();
    }

    internal static string HashFile(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    internal static void VerifyChecksum(string archive, string sha256Text, string name, string? expectedApiDigest)
    {
        if (new FileInfo(archive).Length is <= 0 or > MaxArchiveBytes)
            throw new InvalidDataException("Release archive size is invalid.");
        var actual = HashFile(archive);
        if (!string.Equals(actual, ParseChecksum(sha256Text, name), StringComparison.Ordinal) ||
            (expectedApiDigest is not null && !string.Equals(expectedApiDigest,
                "sha256:" + actual, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Release archive digest mismatch.");
    }

    internal static bool IsOwnedFile(string name)
    {
        if (name.Contains('/') || name.Contains('\\') || name is "." or "..") return false;
        if (string.Equals(name, Executable, StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("KeyPassXCCloudSync.ini.example", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("KeyPassXCCloudSync.deps.json", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("KeyPassXCCloudSync.runtimeconfig.json", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("USER_GUIDE.md", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("LICENSE", StringComparison.OrdinalIgnoreCase)) return true;
        // Only native/runtime DLLs can be updated, never arbitrary tools.
        return name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
            Regex.IsMatch(name, @"\A[A-Za-z0-9_.+-]+\.dll\z", RegexOptions.CultureInvariant);
    }

    internal static IReadOnlyList<string> ExtractVerifiedArchive(string archive, string destination)
    {
        using var zip = ZipFile.OpenRead(archive);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<ZipArchiveEntry>();
        long extracted = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            if (name.EndsWith('/')) throw new InvalidDataException("Unexpected directory in release archive.");
            if (!IsOwnedFile(name) || !names.Add(name) ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("Unsafe or duplicate release entry: " + name);
            extracted = checked(extracted + entry.Length);
            if (entry.Length < 0 || extracted > MaxExtractedBytes)
                throw new InvalidDataException("Release extracted size exceeds the limit.");
            files.Add(entry);
        }
        if (files.Count == 0 || !names.Contains(Executable) ||
            !names.Contains("KeyPassXCCloudSync.ini.example") ||
            !names.Contains("USER_GUIDE.md") || !names.Contains("LICENSE"))
            throw new InvalidDataException("Release is missing required files.");
        Directory.CreateDirectory(destination);
        foreach (var entry in files)
        {
            var output = Path.Combine(destination, entry.FullName);
            using var input = entry.Open();
            using var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(target);
            if (target.Length != entry.Length)
                throw new InvalidDataException("Truncated release entry.");
        }
        return files.Select(e => e.FullName).ToArray();
    }

    // JSON fields for the tag subject and archive subject differ: a tag has
    // a URI, whereas an archive has a name. Neither is required on the other.
    private static string? JsonString(System.Text.Json.JsonElement element, string key) =>
        element.ValueKind == System.Text.Json.JsonValueKind.Object &&
        element.TryGetProperty(key, out var value) &&
        value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() : null;

    // GitHub PURLs canonically lowercase both owner and repository. Repository
    // identity is case-insensitive, but the release tag is case-sensitive.
    // Reject qualifiers, subpaths, other PURL types and additional characters.
    private static bool IsExpectedGitHubPurl(string? value, string tag)
    {
        const string prefix="pkg:github/";
        if (value is null || !value.StartsWith(prefix,StringComparison.Ordinal)) return false;
        var at=value.LastIndexOf('@');
        return at>prefix.Length &&
            string.Equals(value[prefix.Length..at],Repository,StringComparison.OrdinalIgnoreCase) &&
            string.Equals(value[(at+1)..],tag,StringComparison.Ordinal);
    }

    internal static void VerifySignedStatement(string statement, string tag, string tagSha1,
        string archiveName, string archiveSha256)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(statement);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !root.TryGetProperty("predicate", out var predicate) ||
                predicate.ValueKind != System.Text.Json.JsonValueKind.Object)
                throw new InvalidDataException("Signed release predicate is missing or invalid.");

            if (JsonString(root,"_type") != "https://in-toto.io/Statement/v1")
                throw new InvalidDataException("Signed release statement type does not match.");
            if (JsonString(root,"predicateType") != "https://in-toto.io/attestation/release/v0.1")
                throw new InvalidDataException("Signed release predicate type does not match.");
            if (!string.Equals(JsonString(predicate,"repository"),Repository,
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Signed release repository name does not match.");
            if (JsonString(predicate,"repositoryId") != RepositoryId)
                throw new InvalidDataException("Signed release repository ID does not match.");
            if (JsonString(predicate,"tag") != tag)
                throw new InvalidDataException("Signed release tag does not match.");
            if (!IsExpectedGitHubPurl(JsonString(predicate,"purl"),tag))
                throw new InvalidDataException("Signed release package URL does not match.");

            if (!root.TryGetProperty("subject", out var subjects) ||
                subjects.ValueKind != System.Text.Json.JsonValueKind.Array)
                throw new InvalidDataException("Signed release subjects are missing.");

            bool foundZip = false, foundTag = false;
            foreach (var subject in subjects.EnumerateArray())
            {
                if (subject.ValueKind != System.Text.Json.JsonValueKind.Object ||
                    !subject.TryGetProperty("digest", out var digest) ||
                    digest.ValueKind != System.Text.Json.JsonValueKind.Object)
                    throw new InvalidDataException("Signed release subject digest is invalid.");

                if (JsonString(subject,"name") == archiveName &&
                    JsonString(digest,"sha256") == archiveSha256)
                    foundZip = true;

                if (IsExpectedGitHubPurl(JsonString(subject,"uri"),tag) &&
                    string.Equals(JsonString(digest,"sha1"),tagSha1,StringComparison.OrdinalIgnoreCase))
                    foundTag = true;
            }
            if (!foundZip || !foundTag)
                throw new InvalidDataException("Signed release does not bind the tag and archive.");
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidDataException("Signed release statement is not valid JSON.",ex);
        }
    }
}
