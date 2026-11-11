# Cloud Sync repository privacy audit; Windows PowerShell 5.1 compatible.
# Inspect tracked files in CI, or the known source directories in ZIP builds.
# Never output sensitive matches; report only file names and finding types.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

try {
    $root = (Resolve-Path -LiteralPath $root).Path
    $blockedExtensions = '(?i)\.(?:kdbx|keyx|keyfile|pfx|p12|pem|ppk|secret|secrets|sqlite3?|db3?|bak|backup|dmp|log|zip|7z|rar)$'
    $blockedExactNames = @('.env', 'credentials.json', 'secrets.json', 'secrets.yml',
                           'secrets.yaml', 'id_rsa', 'id_ed25519',
                           'KeyPassXCCloudSync.ini', 'KeePassSync.ini', 'state.json',
                           'usage.json', 'topology.json', 'topology-pending.json')
    $blockedDirectories = @('publish', 'dist', 'data', 'logs', 'backup', 'backups',
                            'conflicts', 'temp', 'bin', 'obj', '.vs', 'TestResults')
    $textExtensions = @('.cs', '.csproj', '.axaml', '.ps1', '.bat', '.md', '.txt',
                        '.yml', '.yaml', '.json', '.xml', '.props', '.targets',
                        '.gitattributes', '.gitignore')
    $patterns = @(
        @{ Name='private key block'; Regex='-----BEGIN (?:RSA |EC |DSA |OPENSSH |PGP )?PRIVATE KEY-----' },
        @{ Name='GitHub token'; Regex='(?:gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{40,})' },
        @{ Name='AWS access key'; Regex='(?:AKIA|ASIA)[0-9A-Z]{16}' },
        @{ Name='Google API key'; Regex='AIza[0-9A-Za-z_-]{25,}' },
        @{ Name='OpenAI secret'; Regex='sk-(?:proj-)?[A-Za-z0-9_-]{20,}' },
        @{ Name='JWT'; Regex='eyJ[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}' },
        @{ Name='URL with embedded credentials'; Regex='(?i)\b(?:https?|ftp|webdav)://[^\s"''<>:@/]+:[^\s"''<>@/]+@' },
        @{ Name='email address'; Regex='(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b' }
    )
    $findings = New-Object 'System.Collections.Generic.List[string]'
    $files = New-Object 'System.Collections.Generic.List[string]'
    if (Test-Path -LiteralPath (Join-Path $root '.git')) {
        $gitTool = Get-Command git -ErrorAction Stop
        $tracked = @(& $gitTool.Source -C $root -c core.quotePath=false ls-files --cached)
        if ($LASTEXITCODE -ne 0) { throw 'Git could not enumerate tracked files.' }
        foreach ($file in $tracked) { if ($file) { $files.Add($file) } }
    }
    else {
        foreach ($directory in @('src','scripts','tests','docs','.github')) {
            foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root $directory) -Recurse -File) {
                $files.Add($file.FullName.Substring($root.Length).TrimStart([char[]]@('\','/')).Replace('\','/'))
            }
        }
        foreach ($file in @('README.md','SECURITY.md','Directory.Build.props',
                           'KeyPassXCCloudSync.ini.example','LICENSE','build.bat','run.bat',
                           '.gitignore','.gitattributes','FLUENT_ICONS_NOTICE.txt')) {
            if (Test-Path -LiteralPath (Join-Path $root $file)) { $files.Add($file) }
        }
    }
    foreach ($relativeName in $files) {
        $normalized = $relativeName.Replace('\','/')
        $segments = @($normalized -split '/')
        $name = $segments[$segments.Count - 1]
        $blockedDirectory = @($segments | Where-Object { $blockedDirectories -icontains $_ }).Count -gt 0
        if ($blockedDirectory -or $blockedExactNames -icontains $name -or
            $name -match $blockedExtensions -or $name -match '(?i)^\.env(?:\..*)?$') {
            $findings.Add('Tracked private/generated path: ' + $normalized)
            continue
        }
        $full = Join-Path $root $normalized
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
            $findings.Add('Tracked source is missing: ' + $normalized)
            continue
        }
        if ($textExtensions -notcontains [IO.Path]::GetExtension($full)) { continue }
        $content = [IO.File]::ReadAllText($full)
        foreach ($pattern in $patterns) {
            foreach ($hit in [regex]::Matches($content, $pattern.Regex)) {
                if ($pattern.Name -eq 'email address' -and
                    $hit.Value -match '(?i)@(?:[A-Za-z0-9-]+\.)*(?:invalid|test|example)$') { continue }
                $findings.Add('Possible ' + $pattern.Name + ' in ' + $normalized)
            }
        }
    }
    if ($findings.Count -gt 0) {
        foreach ($finding in $findings) { Write-Host "[FAIL] Privacy / $finding" -ForegroundColor Red }
        throw 'Repository privacy audit failed. Remove sensitive files before building.'
    }
    Write-Host "[PASS] Privacy / $($files.Count) source files checked; no obvious secrets or private files" -ForegroundColor Green
    exit 0
}
catch {
    Write-Host "[FAIL] Privacy / audit could not finish safely: $($_.Exception.GetType().Name)" -ForegroundColor Red
    exit 1
}
