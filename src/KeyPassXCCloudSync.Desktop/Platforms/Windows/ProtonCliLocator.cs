namespace KeyPassXCCloudSync;

// Windows-only executable discovery. An explicitly selected path never falls
// back silently to another binary, even if the selected file is missing.
internal static class ProtonCliLocator
{
    private const string CliFileName="proton-drive.exe";

    internal static string Resolve(string? configured,string applicationDirectory,
        string? configPath,string? pathEnvironment)
    {
        if(!string.IsNullOrWhiteSpace(configured))
        {
            var value=configured.Trim().Trim('"');
            var baseDirectory=string.IsNullOrWhiteSpace(configPath)?applicationDirectory:
                Path.GetDirectoryName(Path.GetFullPath(configPath))!;
            return Path.GetFullPath(Path.IsPathFullyQualified(value)
                ?value:Path.Combine(baseDirectory,value));
        }

        // First: next to the running application (and its portable tools folder).
        // A separate configuration directory can also be a portable install root.
        var directories=new List<string>{applicationDirectory};
        if(!string.IsNullOrWhiteSpace(configPath))
        {
            var configDirectory=Path.GetDirectoryName(Path.GetFullPath(configPath))!;
            if(!directories.Contains(configDirectory,StringComparer.OrdinalIgnoreCase))
                directories.Add(configDirectory);
        }
        foreach(var directory in directories)
            foreach(var relative in new[]{CliFileName,Path.Combine("tools",CliFileName)})
            {
                var candidate=Path.GetFullPath(Path.Combine(directory,relative));
                if(File.Exists(candidate))return candidate;
            }

        // Second: the executable must be available as proton-drive.exe via PATH.
        // Ignore empty, relative and malformed entries to prevent accidental
        // execution of a binary from the current working directory.
        foreach(var raw in (pathEnvironment??string.Empty).Split(Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries))
        {
            var directory=Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
            if(string.IsNullOrWhiteSpace(directory))continue;
            try
            {
                if(!Path.IsPathFullyQualified(directory))continue;
                var candidate=Path.GetFullPath(Path.Combine(directory,CliFileName));
                if(File.Exists(candidate))return candidate;
            }
            catch(ArgumentException) { /* Ignore invalid PATH entries. */ }
            catch(NotSupportedException) { /* Ignore unsupported PATH entries. */ }
        }
        return string.Empty;
    }
}
