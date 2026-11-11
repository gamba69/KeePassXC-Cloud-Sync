# Windows x64 portable package: successful default builds only.
# External Proton Drive CLI is intentionally not downloaded or bundled.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
$stage = Join-Path $root 'release-stage'
$version = 'unknown'
try {
    [xml]$props = Get-Content './Directory.Build.props' -Raw
    $version = $props.SelectSingleNode('/Project/PropertyGroup/Version').InnerText.Trim()
    if ($version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') { throw 'Invalid app version' }
    $publish = Join-Path $root 'publish'
    $exe = Join-Path $publish 'KeyPassXCCloudSync.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'NativeAOT EXE missing; package aborted.' }

    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    # Only known runtime outputs. Never copy user INI, state, logs, databases or debug symbols.
    $runtimeFiles = @(Get-ChildItem -LiteralPath $publish -File | Where-Object {
        $_.Name -ceq 'KeyPassXCCloudSync.exe' -or
        $_.Extension -eq '.dll' -or
        $_.Name -match '\.(?:deps|runtimeconfig)\.json$'
    })
    if ($runtimeFiles.Count -lt 1) { throw 'No release runtime files.' }
    foreach ($file in $runtimeFiles) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $stage $file.Name) -Force
        Write-Host "[PASS] Package / runtime file: $($file.Name)" -ForegroundColor Green
    }
    $documents = @(
        @{ From='KeyPassXCCloudSync.ini.example'; To='KeyPassXCCloudSync.ini.example' },
        @{ From='docs/USER_GUIDE.md'; To='USER_GUIDE.md' },
        @{ From='LICENSE'; To='LICENSE' }
    )
    foreach ($item in $documents) {
        $dest = Join-Path $stage $item.To
        New-Item -ItemType Directory -Path (Split-Path -Parent $dest) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $root $item.From) -Destination $dest -Force
        Write-Host "[PASS] Package / document: $($item.To)" -ForegroundColor Green
    }


    Write-Host '[PASS] Package / Proton Drive CLI is external and not bundled' -ForegroundColor Green

    $dist = Join-Path $root 'dist'
    New-Item -ItemType Directory -Path $dist -Force | Out-Null
    # dist is build-owned. Remove only our previously generated release assets.
    foreach ($old in @(Get-ChildItem -LiteralPath $dist -File | Where-Object {
        $_.Name -match '^KeyPassXCCloudSync-[0-9]+\.[0-9]+\.[0-9]+-win-x64\.(?:zip|sha256)$' -or
        $_.Name -ceq 'SHA256SUMS.txt'
    })) {
        Remove-Item -LiteralPath $old.FullName -Force
    }
    if (@(Get-ChildItem -LiteralPath $dist -Force).Count -ne 0) {
        throw 'dist contains unrelated files; refusing to mix them with release assets.'
    }
    $zipName = "KeyPassXCCloudSync-$version-win-x64.zip"
    $zip = Join-Path $dist $zipName
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
    Write-Host "[PASS] Package / portable ZIP created: $zipName" -ForegroundColor Green
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    $sumName = "KeyPassXCCloudSync-$version-win-x64.sha256"
    $sumFile = Join-Path $dist $sumName
    [IO.File]::WriteAllText($sumFile, "$hash  $zipName`n", [Text.Encoding]::ASCII)
    Write-Host "[PASS] Package / SHA-256 checksum created: $sumName" -ForegroundColor Green
    & (Join-Path $PSScriptRoot 'validate-release.ps1') -Zip $zip -Checksum $sumFile
    if (-not $?) { throw 'Portable ZIP or checksum validation failed.' }
    if (@(Get-ChildItem -LiteralPath $dist -Force).Count -ne 2) {
        throw 'Expected exactly two portable output files in dist.'
    }
    Write-Host '[PASS] Package / exactly two matching release files' -ForegroundColor Green
    Write-Host "[SUMMARY] Portable package ready in $dist" -ForegroundColor Green
}
catch {
    Write-Host "[FAIL] Package / $($_.Exception.Message)" -ForegroundColor Red
    # Do not leave partial or stale release candidates after any failure.
    $zipName = "KeyPassXCCloudSync-$version-win-x64.zip"
    $badZip = Join-Path (Join-Path $root 'dist') $zipName
    if (Test-Path -LiteralPath $badZip) { Remove-Item -LiteralPath $badZip -Force }
    $badChecksum = Join-Path (Join-Path $root 'dist') "KeyPassXCCloudSync-$version-win-x64.sha256"
    if (Test-Path -LiteralPath $badChecksum) { Remove-Item -LiteralPath $badChecksum -Force }
    throw
}
finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    Pop-Location
}
