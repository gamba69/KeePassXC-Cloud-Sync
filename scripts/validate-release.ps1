# Release metadata and asset safety checks. Runs under Windows PowerShell 5.1+.
param(
    [string]$Tag = '',
    [string]$Zip = '',
    [string]$Checksum = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    [xml]$props = Get-Content './Directory.Build.props' -Raw
    $versions = $props.SelectNodes('/Project/PropertyGroup/Version')
    if ($versions.Count -ne 1) { throw 'Expected exactly one Version in Directory.Build.props.' }
    $version = $versions.Item(0).InnerText.Trim()
    if ($version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') { throw "Invalid version: $version" }
    Write-Host "[PASS] Metadata / semantic version: $version" -ForegroundColor Green
    foreach ($name in @('Core','Desktop')) {
        $file = "./src/KeyPassXCCloudSync.$name/KeyPassXCCloudSync.$name.csproj"
        [xml]$project = Get-Content $file -Raw
        if ($project.SelectNodes('/Project/PropertyGroup/Version').Count -ne 0) {
            throw "Unexpected project-local Version in $file"
        }
        Write-Host "[PASS] Metadata / no project-local version: $name" -ForegroundColor Green
    }
    if ($Tag) {
        if ($Tag -cnotmatch '^(?:v)?[0-9]+\.[0-9]+\.[0-9]+$') {
            throw "Invalid tag format: $Tag (expected vN.N.N or N.N.N)"
        }
        $tagVersion = $Tag -creplace '^v', ''
        if ($tagVersion -cne $version) { throw "Tag $Tag does not match source version $version" }
    }
    if ($Tag) { Write-Host "[PASS] Metadata / matching tag: $Tag" -ForegroundColor Green }

    if ($Checksum -and -not $Zip) {
        throw 'Checksum validation requires the matching -Zip argument.'
    }
    if ($Zip) {
        $file = (Resolve-Path $Zip).Path
        if ([IO.Path]::GetFileName($file) -cne "KeyPassXCCloudSync-$version-win-x64.zip") {
            throw "Incorrect release archive name: $file"
        }
        Add-Type -AssemblyName System.IO.Compression
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($file)
        try {
            $names = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\','/') })
            if ($names.Count -eq 0 -or $names -cnotcontains 'KeyPassXCCloudSync.exe' -or
                $names -cnotcontains 'KeyPassXCCloudSync.ini.example' -or
                $names -cnotcontains 'LICENSE' -or
                $names -cnotcontains 'USER_GUIDE.md') {
                throw 'Portable ZIP lacks the EXE, license, user guide or example configuration.'
            }
            # Only one Markdown document belongs to the user-facing package.
            $markdown = @($names | Where-Object { $_ -match '(?i)\.md$' })
            if ($markdown.Count -ne 1 -or $markdown[0] -cne 'USER_GUIDE.md') {
                throw 'Portable ZIP must contain only USER_GUIDE.md from repository documentation.'
            }
            Write-Host '[PASS] ArtifactAudit / single bundled user guide' -ForegroundColor Green
            $disallowed = @($names | Where-Object {
                $_ -match '(?i)(^|/)(?:proton-drive\.exe|CONTRIBUTING\.md)$' -or
                $_ -match '(?i)^THIRD_PARTY/'
            })
            if ($disallowed.Count -ne 0) { throw "Unwanted vendor or contribution file in ZIP: $($disallowed -join ', ')" }
            Write-Host '[PASS] ArtifactAudit / no Proton CLI or contribution files' -ForegroundColor Green
            foreach ($entry in $archive.Entries) {
                $name = $entry.FullName.Replace('\','/')
                if ($name.StartsWith('/') -or $name -match '(^|/)\.\.(/|$)' -or
                    $name -match '^[A-Za-z]:') { throw "Unsafe path in ZIP: $name" }
                if ($name -match '(?i)(\.kdbx(\.sha256)?|\.pdb|\.log|\.key|\.pem|\.p12|\.pfx|\.secret|\.env|(?:^|/)(?:state|usage|topology|topology-pending)\.json)$' -or
                    ($name -match '(?i)\.ini$' -and $name -notmatch '(?i)\.ini\.example$')) {
                    throw "Private/debug file in release ZIP: $name"
                }
                # Reading every entry detects truncated and corrupt compressed data.
                if ($name.EndsWith('/')) { continue }
                $stream = $entry.Open()
                try { $stream.CopyTo([IO.Stream]::Null) }
                finally { $stream.Dispose() }
                Write-Host "[PASS] ArtifactAudit / ZIP entry: $name" -ForegroundColor Green
            }
        }
        finally { $archive.Dispose() }
        Write-Host "[PASS] ArtifactAudit / ZIP contents and integrity: $file" -ForegroundColor Green
        if ($Checksum) {
            $checksumPath = (Resolve-Path -LiteralPath $Checksum).Path
            $checksumName = "KeyPassXCCloudSync-$version-win-x64.sha256"
            if ([IO.Path]::GetFileName($checksumPath) -cne $checksumName) {
                throw "Incorrect SHA-256 filename: $checksumPath"
            }
            $expectedHash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
            $expectedLine = "$expectedHash  $([IO.Path]::GetFileName($file))`n"
            $actualLine = [IO.File]::ReadAllText($checksumPath, [Text.Encoding]::ASCII)
            if ($actualLine -cne $expectedLine) {
                throw 'SHA-256 content must match the ZIP and contain one standard checksum line.'
            }
            Write-Host "[PASS] ArtifactAudit / SHA-256 filename and digest verified: $checksumName" -ForegroundColor Green
        }
    }

    if (Test-Path './.git') {
        $tracked = @(& git ls-files)
        if ($LASTEXITCODE -ne 0) { throw 'Could not inspect Git tracked files.' }
        foreach ($name in $tracked) {
            $name = $name.Replace('\','/')
            if ($name -match '(?i)(\.kdbx(\.sha256)?|\.pfx|\.p12|\.key|\.pem|\.secret)$' -or
                $name -match '(?i)(^|/)(state|usage|topology|topology-pending)\.json$' -or
                $name -match '(?i)(^|/)(keypassxccloudsync|keepasssync)\.ini$') {
                throw "Sensitive file tracked by Git: $name"
            }
            Write-Host "[PASS] ArtifactAudit / Git tracked path: $name" -ForegroundColor Green
        }
    }
    Write-Host "[PASS] Metadata / version and safe packaging rules: v$version" -ForegroundColor Green
}
catch {
    Write-Host "[FAIL] Metadata / unexpected: $($_.Exception.Message)" -ForegroundColor Red
    throw
}
finally { Pop-Location }
