namespace KeyPassXCCloudSync;

// Read-only options adapter for the provider implementations.
public sealed record Config(
    string Provider, string LocalFile, string RemoteDirectory, string StateFile,
    int DebounceMs, int StableFileCheckMs, int RemoteShaPollSec, int FullAuditHours,
    int MissingRemoteRecheckSec, int RetryCount, int RetryDelaySec, int OperationTimeoutSec,
    int LocalMinCopies, int CloudMinCopies, int LocalMinDays, int CloudMinDays,
    string WebDavUrl, string WebDavCredentialTarget, string ProtonExecutable,
    string GoogleClientId, string GoogleClientSecretCredential, string GoogleRefreshCredential,
    string GoogleAccountLabel, string ProtonRootPath,
    string LoggingLevel, int MaxLogSizeMB, int MaxLogFiles,
    bool NotifyPlatform, int GoogleFolderCacheSeconds=10, string S3Region="auto");
