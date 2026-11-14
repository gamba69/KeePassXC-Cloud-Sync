# KeyPassXC Cloud Sync - architecture and documentation audit.
# This is a static guard; Windows compilation and runtime tests are still required.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    function Assert-Valid([bool]$Condition, [string]$Description) {
        if (-not $Condition) {
            Write-Host "[FAIL] SourceAudit / $Description" -ForegroundColor Red
            throw "Source validation failed: $Description"
        }
        Write-Host "[PASS] SourceAudit / $Description" -ForegroundColor Green
    }
    [xml]$props = Get-Content './Directory.Build.props' -Raw -Encoding UTF8
    $versionNodes = $props.SelectNodes('/Project/PropertyGroup/Version')
    Assert-Valid ($versionNodes.Count -eq 1) 'Exactly one centralized application version'
    $version = $versionNodes.Item(0).InnerText.Trim()
    Assert-Valid ($version -match '^[0-9]+\.[0-9]+\.[0-9]+$') "Centralized semantic application version: $version"
    $changelogs = @(Get-ChildItem -Path . -Recurse -File -Filter 'CHANGELOG*')
    Assert-Valid ($changelogs.Count -eq 1 -and
                  $changelogs[0].FullName -eq (Join-Path $root 'CHANGELOG.md')) 'Only root CHANGELOG.md stores release history'
    Assert-Valid ((Get-ChildItem -Path './docs' -File -Filter '*.md' | Measure-Object).Count -ge 7) 'Complete English documentation set'
    foreach ($document in @('README.md','CHANGELOG.md','LICENSE','SECURITY.md',
      'docs/USER_GUIDE.md','docs/CONFIGURATION.md','docs/ARCHITECTURE.md',
      'docs/PROTOCOL.md','docs/BUILD.md','docs/TROUBLESHOOTING.md','docs/SECURITY_MODEL.md')) {
        Assert-Valid (Test-Path -LiteralPath $document -PathType Leaf) "Repository document: $document"
    }
    Assert-Valid ((Get-Content './LICENSE' -Raw -Encoding UTF8).StartsWith('MIT License')) 'MIT license present'
    $oldDocs = @(Get-ChildItem -Path . -Recurse -File -Include '*_RU.md','*_UA.md')
    Assert-Valid ($oldDocs.Count -eq 0) 'No localized project documentation copies'
    foreach ($document in (Get-ChildItem . -Recurse -File -Include '*.md')) {
        $body = Get-Content -LiteralPath $document.FullName -Raw -Encoding UTF8
        Assert-Valid ($body -notmatch '[\u0400-\u04ff]') "English-only documentation: $($document.Name)"
    }
    foreach ($proj in @('./src/KeyPassXCCloudSync.Core/KeyPassXCCloudSync.Core.csproj',
        './src/KeyPassXCCloudSync.Desktop/KeyPassXCCloudSync.Desktop.csproj')) {
        [xml]$project = Get-Content $proj -Raw -Encoding UTF8
        Assert-Valid ($project.SelectNodes('/Project/PropertyGroup/Version').Count -eq 0) "No project-local version: $proj"
    }
    $core = (Get-ChildItem './src/KeyPassXCCloudSync.Core' -File -Filter '*.cs' | ForEach-Object {
        Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8 }) -join "`n"
    $shared = (Get-ChildItem './src/KeyPassXCCloudSync.Desktop' -File -Filter '*.cs' | ForEach-Object {
        Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8 }) -join "`n"
    $native = (Get-ChildItem './src/KeyPassXCCloudSync.Desktop/Platforms/Windows' -File -Filter '*.cs' | ForEach-Object {
        Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8 }) -join "`n"
    $disallowed = '\b(?:DllImport|LibraryImport|Registry|FileSystemWatcher|NetworkChange|ProcessStartInfo|Win32Properties|WindowsTaskbarStatusIcons|WindowsNativeToast)\b|OperatingSystem\.IsWindows'
    Assert-Valid ($core -notmatch $disallowed) 'Core has no native, shell or direct OS event implementation'
    Assert-Valid ($shared -notmatch $disallowed -and $shared -notmatch '\bProgram\.' -and
                  $shared -notmatch 'RuntimeInformation' -and $shared -notmatch 'Process.GetCurrentProcess') 'Shared UI has no direct Windows entrypoint or process probing' 
    Assert-Valid ($native -match 'WindowsChangeMonitoring' -and
                  $native -match 'WindowsCliRunner' -and
                  $native -match 'WindowsTaskbarStatusIcons' -and
                  $native -match 'WindowsCredentialStore') 'Native services are in Windows adapter directory'
    $contracts = Get-Content './src/KeyPassXCCloudSync.Core/PlatformContracts.cs' -Raw -Encoding UTF8
    foreach ($contract in @('ISecretStore','IAutostartService','IDatabaseLockService',
       'IPlatformPaths','IExternalBrowser','IPlatformProcessLauncher','IPlatformChangeMonitoring','IPlatformCliRunner')) {
        Assert-Valid ($contracts.Contains("interface $contract")) "Platform contract $contract"
    }
    Assert-Valid ($core -match 'Changes\.WatchFile' -and $core -match 'Changes\.WatchNetwork' -and
                  $core -match 'Cli\.RunProtonAsync') 'Core uses file, network and child-process abstractions'
    Assert-Valid ($core -notmatch 'Environment\.MachineName') 'Core does not read host identity directly'
    Assert-Valid ($core -notmatch 'Environment\.GetFolderPath') 'Core has no OS-local data path lookup'
    Assert-Valid ($core -notmatch '\b(?:EffectiveDirectory|migration|legacy)\b') 'No historical migration adapters in Core'
    foreach ($source in Get-ChildItem './src','./tests' -File -Recurse -Filter '*.cs') {
        foreach ($line in Get-Content -LiteralPath $source.FullName -Encoding UTF8) {
            if ($line -match '^\s*(?://|/\*|\*|///).*?[\u0400-\u04ff]') {
                Write-Host "[FAIL] SourceAudit / non-English source comment: $($source.FullName)" -ForegroundColor Red
                throw "Non-English source comment in $($source.FullName)"
            }
        }
    }
    Assert-Valid $true 'Source code comments contain no Cyrillic'
    Assert-Valid ((Get-Content './src/KeyPassXCCloudSync.Core/SyncModels.cs' -Raw).Contains('SchemaVersion {get;set;}=2;')) 'Wire schema 2 unchanged'
    foreach ($cloud in @('CloudSync_black.ico','CloudSync_white.ico','CloudSync_yellow.ico','CloudSync_red.ico','TaskbarDot_yellow.ico','TaskbarDot_red.ico')) {
        Assert-Valid (Test-Path "./src/KeyPassXCCloudSync.Desktop/Assets/$cloud") "Bundled icon: $cloud"
    }
    Assert-Valid (Test-Path './scripts/bootstrap-build.ps1') 'PowerShell parser preflight launcher exists'
    Assert-Valid (Test-Path './scripts/verify-repository-privacy.ps1') 'Fail-fast privacy audit included in every build'
    Assert-Valid ((Get-Content './build.bat' -Raw -Encoding UTF8) -match 'bootstrap-build\.ps1') 'Build always executes parser preflight'
    Assert-Valid (Test-Path './tests/TestReporter.cs') 'Shared named C# test reporter exists'
    Assert-Valid (Test-Path './tests/Directory.Build.targets') 'Every C# suite imports common test reporter'
    Assert-Valid ((Get-Content './scripts/build-windows.ps1' -Raw -Encoding UTF8) -match 'Invoke-TestSuite -Name \$test') 'Build executes all test suites through the checked reporter'
    foreach ($suite in @('Backup','Purge','Inventory','OAuthCallback','Protocol',
                         'Localization','CloudName','Parallel','CloudStatus','UiSmoke')) {
        $program = "./tests/KeyPassXCCloudSync.${suite}Tests/Program.cs"
        Assert-Valid (Test-Path $program) "C# regression suite: ${suite}Tests"
        $source = Get-Content $program -Raw -Encoding UTF8
        Assert-Valid ($source -match 'TestReporter\.|V2Diagnostics\.Progress') "Named case reporting: ${suite}Tests"
    }
    Assert-Valid ((Get-Content './README.md' -Raw -Encoding UTF8) -match ([regex]::Escape($version))) 'User README displays current version'
    Assert-Valid ((Get-Content './docs/PROTOCOL.md' -Raw -Encoding UTF8) -match ([regex]::Escape($version))) 'Protocol reference displays current app version'
    Assert-Valid ((Get-Content './SECURITY.md' -Raw -Encoding UTF8) -match ([regex]::Escape($version))) 'Security policy displays current app version'
    $history = Get-Content './CHANGELOG.md' -Raw -Encoding UTF8
    Assert-Valid ($history -match ("(?m)^## \[" + [regex]::Escape($version) + "\]")) 'Changelog records current app version'
    $docsWithoutHistory = @((Get-Item './README.md'), (Get-Item './SECURITY.md')) +
        @(Get-ChildItem './docs' -File -Recurse -Filter '*.md')
    foreach ($doc in $docsWithoutHistory) {
        $docBody = Get-Content -LiteralPath $doc.FullName -Raw -Encoding UTF8
        Assert-Valid ($docBody -notmatch "(?m)^#{1,6}\s+(?:Changelog|Release Notes|What's New|Version [0-9]+\.[0-9]+\.[0-9]+)\b") "No release history in $($doc.Name)"
    }
    Assert-Valid (Test-Path './.github/workflows/windows.yml') 'Windows CI workflow included'
    Assert-Valid (-not (Test-Path './.github/workflows/release.yml')) 'No automatic release workflow on tag push'
    Assert-Valid (Test-Path './.github/workflows/release-assets.yml') 'GitHub manual asset build workflow included'
    $ciSource = Get-Content './.github/workflows/windows.yml' -Raw -Encoding UTF8
    Assert-Valid ($ciSource -match "branches: \['\*\*'\]" -and
                  $ciSource -notmatch '(?m)^\s+tags:' -and
                  $ciSource -match 'pull_request:' -and
                  $ciSource -match 'persist-credentials: false') 'CI covers all branches and PRs, not tag pushes, without retained credentials'
    Assert-Valid (Test-Path './scripts/package-windows.ps1') 'Shared portable packaging script included'
    Assert-Valid (-not (Test-Path './CONTRIBUTING.md')) 'Unneeded CONTRIBUTING.md is absent from repository'
    Assert-Valid (-not (Test-Path './third_party/ProtonDrive-CLI.json')) 'No pinned vendor download manifest'
    Assert-Valid (-not (Test-Path './third_party/ProtonDrive-LICENSE.txt')) 'No Proton vendor license bundled without its executable' 
    $batSource = Get-Content './build.bat' -Raw -Encoding UTF8
    Assert-Valid ($batSource -match 'BUILD_NO_PAUSE' -and $batSource -match '-NoPause') 'Manual pause and CI no-pause switch included'
    $packageSource = Get-Content './scripts/package-windows.ps1' -Raw -Encoding UTF8
    Assert-Valid ($packageSource -match 'KeyPassXCCloudSync-\$version-win-x64\.sha256' -and
                  $packageSource -match 'validate-release' -and
                  $packageSource -notmatch 'Invoke-WebRequest|Start-BitsTransfer|DownloadFile|ProtonDrive-CLI\.json') 'Packaging uses local runtime only; no vendor download'
    Assert-Valid ($packageSource -match "From='docs/USER_GUIDE.md'" -and
                  $packageSource -notmatch "From='(?:docs/(?!USER_GUIDE\.md)[^']+|CONTRIBUTING\.md)'" ) 'Only user guide copied from documentation to portable ZIP'
    $zipAudit = Get-Content './scripts/validate-release.ps1' -Raw -Encoding UTF8
    Assert-Valid ($zipAudit -match 'single bundled user guide' -and
                  $zipAudit -match 'no Proton CLI or contribution files') 'Release ZIP validator excludes vendor CLI and extra documentation' 
    Assert-Valid ($packageSource -match '-Checksum \$sumFile' -and
                  $packageSource -match 'KeyPassXCCloudSync-\$version-win-x64\.sha256') 'Portable release produces a paired .zip and .sha256 sidecar'
    $assetSource = Get-Content './.github/workflows/release-assets.yml' -Raw -Encoding UTF8
    Assert-Valid ($assetSource -match 'workflow_dispatch' -and $assetSource -match 'gh release upload' -and $assetSource -notmatch '\-\-clobber' -and $assetSource -notmatch '(?m)^\s+push:' -and $assetSource -notmatch 'gh release create' -and $assetSource -notmatch 'gh release edit') 'Assets require manual action and pre-existing draft; no publishing or overwrite'
    Assert-Valid ($assetSource -match 'KeyPassXCCloudSync-\$\{RELEASE_VERSION\}-win-x64\.sha256' -and
                  $assetSource -notmatch 'SHA256SUMS\.txt' -and
                  $ciSource -match 'KeyPassXCCloudSync-\$version-win-x64\.sha256') 'Both GitHub workflows use version-matched .sha256 files'
    Assert-Valid ($assetSource -match '(?m)^  preflight:' -and
                  $assetSource -match '(?m)^  build:' -and
                  $assetSource -match '(?m)^  attach:' -and
                  $assetSource -match 'sha256sum -c' -and
                  $assetSource -match 'EXPECTED_DRAFT_ID' -and
                  $assetSource -match 'EXPECTED_TAG_OBJECT' -and
                  $assetSource -match 'persist-credentials: false') 'Three-stage release checks draft/tag identity and verifies checksum across runners'
    Assert-Valid ($ciSource -match 'build.bat -NoPause' -and
                  $assetSource -match 'build.bat -NoPause' -and
                  $ciSource -notmatch '(?m)^\s+contents: write') 'Both workflows build and test; CI remains read-only'
    Assert-Valid $true 'Static source audit completed (not a substitute for compilation)'
}
catch {
    Write-Host "[FAIL] SourceAudit / unexpected: $($_.Exception.Message)" -ForegroundColor Red
    throw
}
finally { Pop-Location }
