namespace KeyPassXCCloudSync;

// A single convention enforced both by the test runner and all destructive cleanup methods.
// Only a UUID-owned child of the reserved integration test namespace can be removed as a tree.
public static class IntegrationIsolation
{
    public const string Namespace = "__KeePassSync_IntegrationTests__";

    public static string NewRunRoot(string baseDirectory)
        => baseDirectory.TrimEnd('/') + "/" + Namespace + "/run-" + Guid.NewGuid().ToString("N");

    public static bool IsTestRunRoot(string path)
    {
        var parts=path.Split('/');
        if(parts.Length < 3) return false;
        var owner=parts[^2];
        var leaf=parts[^1];
        return owner == Namespace && leaf.Length==36 &&
               leaf.StartsWith("run-",StringComparison.Ordinal) &&
               Guid.TryParseExact(leaf[4..],"N",out _);
    }
}
