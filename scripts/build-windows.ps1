# KeyPassXC Cloud Sync - fail-fast Windows build; -SkipTests is opt-in.
# Run on Windows with PowerShell 5.1+ and .NET SDK 10.
param(
    [switch]$SkipPublish,
    [switch]$ValidateOnly,
    [switch]$SkipTests,
    [switch]$SkipPackage,
    [switch]$NoPause
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-Checked {
    param(
        [Parameter(Mandatory=$true)][string]$Command,
        [Parameter(Mandatory=$true)][string[]]$Arguments
    )
    Write-Host "`n>>> $Command $($Arguments -join ' ')" -ForegroundColor Cyan
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[FAIL] Build / $Command ($LASTEXITCODE)" -ForegroundColor Red
        throw "STOP: $Command failed with exit code $LASTEXITCODE. Publish was not started."
    }
    Write-Host "[PASS] Build / $Command completed" -ForegroundColor Green
}

# Execute a child process with a real numeric exit code, a deadline and live
# stdout/stderr forwarding. Do not use CreateNoWindow without redirected pipes:
# on Windows it can hide even fatal .NET runtime errors from the parent console.
# Read both streams concurrently to avoid deadlocks when either buffer fills.
function Get-TimedProcessExitCode {
    param(
        [Parameter(Mandatory=$true)][string]$Command,
        [Parameter(Mandatory=$true)][string[]]$Arguments,
        [int]$TimeoutSeconds = 60
    )
    $exe = (Get-Command -Name $Command -CommandType Application -ErrorAction Stop).Source
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $exe
    # PowerShell 5.1 uses .NET Framework and has no ArgumentList. Quote paths.
    $startInfo.Arguments = (@($Arguments | ForEach-Object {
        if ($_ -match '[\s"]') { '"' + $_.Replace('"', '\"') + '"' } else { $_ }
    }) -join ' ')
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    # .NET 10 console output is UTF-8 even when launched by Windows PowerShell 5.1.
    # Explicit decoding prevents mojibake in Russian/Ukrainian test case names.
    $utf8 = [System.Text.UTF8Encoding]::new($false)
    $startInfo.StandardOutputEncoding = $utf8
    $startInfo.StandardErrorEncoding = $utf8
    $startInfo.WorkingDirectory = (Get-Location).Path

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        if (-not $process.Start()) { throw "STOP: failed to start $Command" }
        $stdoutTask = $process.StandardOutput.ReadLineAsync()
        $stderrTask = $process.StandardError.ReadLineAsync()
        while ($true) {
            # ReadLineAsync is non-blocking. Poll both tasks, draining their
            # completed lines while the child is still running.
            if ($null -ne $stdoutTask -and $stdoutTask.IsCompleted) {
                $line = $stdoutTask.GetAwaiter().GetResult()
                if ($null -eq $line) { $stdoutTask = $null }
                else {
                    if ($line -match '^\[PASS\] ') { $script:CasePassCount++ }
                    if ($line -match '^\[FAIL\] ') { $script:CaseFailCount++ }
                    # cmd.exe ECHO includes whitespace preceding command separators.
                    # Normalize only the probe comparison; keep ordinary output unchanged.
                    $isStdoutProbe = ($line.Trim() -ceq 'KPCS-stdout-probe')
                    if ($isStdoutProbe) { $script:SawStdoutProbe = $true }
                    else { Write-Host $line }
                    $stdoutTask = $process.StandardOutput.ReadLineAsync()
                }
            }
            if ($null -ne $stderrTask -and $stderrTask.IsCompleted) {
                $line = $stderrTask.GetAwaiter().GetResult()
                if ($null -eq $line) { $stderrTask = $null }
                else {
                    if ($line -match '^\[PASS\] ') { $script:CasePassCount++ }
                    if ($line -match '^\[FAIL\] ') { $script:CaseFailCount++ }
                    $isStderrProbe = ($line.Trim() -ceq 'KPCS-stderr-probe')
                    if ($isStderrProbe) { $script:SawStderrProbe = $true }
                    else { Write-Host $line -ForegroundColor Red }
                    $stderrTask = $process.StandardError.ReadLineAsync()
                }
            }
            if ($process.HasExited -and $null -eq $stdoutTask -and $null -eq $stderrTask) { break }
            if ($stopwatch.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
                Write-Host "[FAIL] $Command exceeded ${TimeoutSeconds}s; terminating process tree." -ForegroundColor Red
                try { & taskkill.exe /PID $process.Id /T /F *> $null } catch { }
                try { if (-not $process.HasExited) { $process.Kill() } } catch { }
                throw "STOP: $Command timed out; publish was not started."
            }
            Start-Sleep -Milliseconds 5
        }
        # HasExited and both streams reached EOF: the code and all diagnostic
        # output belong to this process, not to a stale PowerShell variable.
        $process.WaitForExit()
        return [int]$process.ExitCode
    }
    finally {
        $stopwatch.Stop()
        $process.Dispose()
    }
}

function Invoke-TestSuite {
    param(
        [Parameter(Mandatory=$true)][string]$Name,
        [Parameter(Mandatory=$true)][string[]]$Arguments,
        [int]$TimeoutSeconds = 900
    )
    $script:CasePassCount = 0
    $script:CaseFailCount = 0
    Write-Host "`n>>> Test suite: $Name" -ForegroundColor Cyan
    try {
        $code = Get-TimedProcessExitCode -Command 'dotnet' -Arguments $Arguments -TimeoutSeconds $TimeoutSeconds
        if ($code -ne 0 -or $script:CaseFailCount -gt 0 -or $script:CasePassCount -eq 0) {
            throw "exit=$code; PASS=$script:CasePassCount; FAIL=$script:CaseFailCount."
        }
        Write-Host "[SUMMARY] ${Name}: $script:CasePassCount named PASS, 0 FAIL (exit 0)" -ForegroundColor Green
    }
    catch {
        Write-Host "[FAIL] $Name / suite execution: $($_.Exception.Message)" -ForegroundColor Red
        throw
    }
}

function Assert-SubprocessExitCodes {
    # A genuine zero and a genuine nonzero code must be distinguished BEFORE
    # running expensive .NET tests; prevent false FAIL and false PASS alike.
    $zero = Get-TimedProcessExitCode -Command 'cmd.exe' -Arguments @('/D','/C','exit','/b','0') -TimeoutSeconds 10
    $seven = Get-TimedProcessExitCode -Command 'cmd.exe' -Arguments @('/D','/C','exit','/b','7') -TimeoutSeconds 10
    if ($zero -ne 0) {
        Write-Host "[FAIL] BuildRunner / zero exit code: received $zero" -ForegroundColor Red
        throw "STOP: subprocess success returned $zero instead of 0."
    }
    Write-Host '[PASS] BuildRunner / zero exit code is success' -ForegroundColor Green
    if ($seven -ne 7) {
        Write-Host "[FAIL] BuildRunner / nonzero exit code: received $seven" -ForegroundColor Red
        throw "STOP: subprocess failure returned $seven instead of 7."
    }
    Write-Host '[PASS] BuildRunner / nonzero exit code is detected' -ForegroundColor Green
    # Stream BOTH output channels, including on nonzero process exits.
    $script:SawStdoutProbe = $false
    $script:SawStderrProbe = $false
    $probe = Get-TimedProcessExitCode -Command 'cmd.exe' -Arguments @('/D','/C','echo KPCS-stdout-probe & echo KPCS-stderr-probe 1>&2 & exit /b 7') -TimeoutSeconds 10
    if ($probe -ne 7) {
        Write-Host "[FAIL] BuildRunner / output probe exit code: received $probe" -ForegroundColor Red
        throw "STOP: stdout/stderr probe returned $probe instead of 7."
    }
    Write-Host '[PASS] BuildRunner / output probe exit code is 7' -ForegroundColor Green
    if (-not $script:SawStdoutProbe) {
        Write-Host '[FAIL] BuildRunner / stdout probe marker not recognized' -ForegroundColor Red
        throw 'STOP: child stdout probe marker missing or malformed.'
    }
    Write-Host '[PASS] BuildRunner / stdout forwarded correctly' -ForegroundColor Green
    if (-not $script:SawStderrProbe) {
        Write-Host '[FAIL] BuildRunner / stderr probe marker not recognized' -ForegroundColor Red
        throw 'STOP: child stderr probe marker missing or malformed.'
    }
    Write-Host '[PASS] BuildRunner / stderr forwarded correctly' -ForegroundColor Green
}

Push-Location (Split-Path -Parent $PSScriptRoot)
try {
    # Windows PowerShell 5.1 treats BOM-less UTF-8 script text as ANSI.
    # Source encoding and syntax must be validated BEFORE executing any child script.
    foreach ($scriptName in @('bootstrap-build.ps1','build-windows.ps1','verify-sources.ps1','verify-repository-privacy.ps1','validate-release.ps1','package-windows.ps1')) {
        $scriptPath = Join-Path $PSScriptRoot $scriptName
        $scriptBytes = [IO.File]::ReadAllBytes($scriptPath)
        if ($scriptBytes.Length -lt 3 -or $scriptBytes[0] -ne 239 -or
            $scriptBytes[1] -ne 187 -or $scriptBytes[2] -ne 191) {
            throw "STOP: $scriptName must be UTF-8 with BOM for Windows PowerShell 5.1."
        }
        $tokens = $null
        $syntaxErrors = $null
        [void][System.Management.Automation.Language.Parser]::ParseFile(
            $scriptPath, [ref]$tokens, [ref]$syntaxErrors)
        if ($null -ne $syntaxErrors -and $syntaxErrors.Count -gt 0) {
            $problems = @($syntaxErrors | ForEach-Object {
                "line $($_.Extent.StartLineNumber): $($_.Message)"
            }) -join '; '
            throw "STOP: PowerShell parser error in ${scriptName}: $problems"
        }
        Write-Host "[PASS] BuildChecks / PowerShell syntax and UTF-8 BOM: $scriptName" -ForegroundColor Green
    }
    $desktop = './src/KeyPassXCCloudSync.Desktop/KeyPassXCCloudSync.Desktop.csproj'
    # Validate essential release metadata using PowerShell/.NET; Python is not required.
    [xml]$props = Get-Content './Directory.Build.props' -Raw
    # StrictMode throws when an XML convenience-property is absent. Select XML nodes
    # explicitly: Directory.Build.props MUST declare Version, *.csproj MUST NOT.
    $versionNodes = $props.SelectNodes('/Project/PropertyGroup/Version')
    if ($versionNodes.Count -ne 1) {
        throw 'STOP: Directory.Build.props must contain exactly one Version element.'
    }
    $version = $versionNodes.Item(0).InnerText.Trim()
    if ($version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
        throw "STOP: invalid application version in Directory.Build.props: $version"
    }
    foreach ($name in @('Core', 'Desktop')) {
        $projectFile = "./src/KeyPassXCCloudSync.$name/KeyPassXCCloudSync.$name.csproj"
        [xml]$project = Get-Content $projectFile -Raw
        $ownVersions = $project.SelectNodes('/Project/PropertyGroup/Version')
        if ($ownVersions.Count -gt 0) {
            throw "STOP: per-project version override in $projectFile."
        }
    }
    # Validate Avalonia 12 API usage without Python or dotnet. This is deliberately
    # part of -ValidateOnly, so stale UI properties fail before a lengthy build.
    $dialogSource = Get-Content './src/KeyPassXCCloudSync.Desktop/Dialogs.cs' -Raw
    if ($dialogSource -notmatch 'PlaceholderText\s*=\s*UiLang\.T\(placeholder\)') {
        throw 'STOP: UiKit.Input must use Avalonia 12 TextBox.PlaceholderText.'
    }
    # All five managed/native rendering packages must be pinned together.
    # Avalonia.Skia has open minimum versions; runtime compatibility is
    # verified by UiSmokeTests and NativeAOT, not assumed from NuGet restore.
    [xml]$desktopProjectXml = Get-Content './src/KeyPassXCCloudSync.Desktop/KeyPassXCCloudSync.Desktop.csproj' -Raw
    $expectedRenderPackages = @{
        'SkiaSharp'='4.155.0'
        'SkiaSharp.NativeAssets.Win32'='4.155.0'
        'SkiaSharp.HarfBuzz'='4.155.0'
        'HarfBuzzSharp'='14.4.0'
        'HarfBuzzSharp.NativeAssets.Win32'='14.4.0'
    }
    foreach ($packageName in $expectedRenderPackages.Keys) {
        $matches = @($desktopProjectXml.SelectNodes('/Project/ItemGroup/PackageReference') |
            Where-Object { $_.GetAttribute('Include') -ceq $packageName })
        if ($matches.Count -ne 1 -or $matches[0].GetAttribute('Version') -cne $expectedRenderPackages[$packageName]) {
            throw "STOP: renderer dependency $packageName must be pinned to $($expectedRenderPackages[$packageName])."
        }
        Write-Host "[PASS] BuildChecks / renderer package: $packageName" -ForegroundColor Green
    }
    if (-not (Test-Path './src/KeyPassXCCloudSync.Desktop/UiTypography.cs')) {
        throw 'STOP: centralized typography source is missing.'
    }
    $obsoleteUiProperty = '\b(?:Watermark|UseFloatingWatermark|SystemDecorations)\s*='
    foreach ($source in (Get-ChildItem './src/KeyPassXCCloudSync.Desktop' -Filter '*.cs' -Recurse -File)) {
        $sourceText = Get-Content -LiteralPath $source.FullName -Raw
        if ($sourceText -match $obsoleteUiProperty) {
            throw "STOP: obsolete Avalonia UI property found in $($source.FullName)."
        }
        Write-Host "[PASS] BuildChecks / no obsolete Avalonia properties: $($source.Name)" -ForegroundColor Green
    }
    # IClipboard.SetTextAsync is an extension in Avalonia 12; every source
    # file that calls it must import Avalonia.Input.Platform (including partials).
    foreach ($source in (Get-ChildItem './src/KeyPassXCCloudSync.Desktop' -Filter '*.cs' -File -Recurse)) {
        $sourceText = Get-Content -LiteralPath $source.FullName -Raw -Encoding UTF8
        if ($sourceText -match '\.SetTextAsync\s*\(' -and
            $sourceText -notmatch '(?m)^using Avalonia\.Input\.Platform;\s*$') {
            throw "STOP: Avalonia 12 clipboard extension import missing in $($source.Name)."
        }
        Write-Host "[PASS] BuildChecks / clipboard imports: $($source.Name)" -ForegroundColor Green
    }
    # Avalonia Window.Title is string?; accessing Title.Contains() under
    # TreatWarningsAsErrors produces CS8602. Keep the headless assertion
    # nullable-safe and version-independent (no hardcoded release number).
    $uiSmokeSource = Get-Content './tests/KeyPassXCCloudSync.UiSmokeTests/Program.cs' -Raw
    if ($uiSmokeSource -match 'shell\.Title\.Contains\s*\(' -or
        $uiSmokeSource -notmatch 'string\.Equals\(shell\.Title' -or
        $uiSmokeSource -notmatch 'Assembly\.GetName\(\)\.Version\?\.ToString\(3\)') {
        throw 'STOP: UI smoke test must compare nullable Window.Title safely using assembly version metadata.'
    }
    # The fake default Headless graphics backend cannot validate decoded PNG
    # pixel sizes. Prevent accidental removal of the real Skia test backend.
    if ($uiSmokeSource -notmatch '\.UseSkia\s*\(\s*\)' -or
        $uiSmokeSource -notmatch 'UseHeadlessDrawing\s*=\s*false') {
        throw 'STOP: UiSmokeTests must enable Skia and disable fake headless drawing for native bitmap tests.'
    }
    Write-Host '[PASS] BuildChecks / native PNG tests use the real Skia headless decoder' -ForegroundColor Green
    # Headless tests must inspect the logical tree of the active page.
    # Visual descendants can be absent before Avalonia's deferred layout pass;
    # searching the full shell also gives false positives from navigation labels.
    if ($uiSmokeSource -match 'GetVisualDescendants\s*\(' -or
        $uiSmokeSource -notmatch 'ActivePage\(shell\)\.GetLogicalDescendants\s*\(' -or
        $uiSmokeSource -notmatch 'hosts\.Length\s*!=\s*1') {
        throw 'STOP: headless navigation tests must inspect the active page logical tree, not the visual shell tree.'
    }
    if ($uiSmokeSource -match 'shell\.(?:Show|Close)\s*\(' -or
        $uiSmokeSource -notmatch 'UI smoke tests completed' -or
        (Get-Content './scripts/build-windows.ps1' -Raw) -notmatch 'Invoke-TestSuite -Name ''UiSmokeTests'' -Arguments @\(\$smokeDll\)') {
        throw 'STOP: UI smoke must use unshown logical tree and execute compiled DLL directly.'
    }
    Write-Host '[PASS] BuildChecks / Avalonia 12 headless navigation inspects active page logical content.' -ForegroundColor Green
    # Each diagnostic case must print a named PASS or FAIL, not an intermediate RUN.
    foreach ($source in (Get-ChildItem './src', './tests' -Filter '*.cs' -Recurse -File)) {
        $sourceText = Get-Content -LiteralPath $source.FullName -Raw
        if ($sourceText -match '\[RUN\s*\]') {
            throw "STOP: intermediate diagnostic RUN marker found in $($source.FullName)."
        }
        Write-Host "[PASS] BuildChecks / no incomplete RUN markers: $($source.Name)" -ForegroundColor Green
    }
    Write-Host '[PASS] BuildChecks / Avalonia 12 UI smoke title assertion is nullable-safe.' -ForegroundColor Green
    Write-Host '[PASS] BuildChecks / Avalonia 12 TextBox placeholders and obsolete UI properties validated.' -ForegroundColor Green
    Write-Host "[PASS] BuildChecks / application version $version" -ForegroundColor Green
    # All source and release safety checks are mandatory locally and on GitHub.
    # They require only the Windows PowerShell installation already present.
    & './scripts/verify-repository-privacy.ps1'
    if ($LASTEXITCODE -ne 0) { throw 'STOP: repository privacy audit failed.' }
    & './scripts/verify-sources.ps1'
    & './scripts/validate-release.ps1'
    if (-not ($SkipTests -or $ValidateOnly -or $SkipPublish -or $SkipPackage)) {
        # Prevent a failed build from leaving a previous ZIP that looks current.
        if (Test-Path './dist') { Remove-Item './dist' -Recurse -Force }
        Write-Host '[PASS] Build / stale portable artifacts cleared' -ForegroundColor Green
    }
    if (-not $SkipTests) { Assert-SubprocessExitCodes }
    if ($ValidateOnly) {
        Write-Host '[SUMMARY] ValidateOnly: source/metadata checks completed; .NET compilation, regression tests and NativeAOT NOT RUN.' -ForegroundColor Yellow
        return
    }
    Invoke-Checked 'dotnet' @('restore', $desktop, '-r', 'win-x64')
    Invoke-Checked 'dotnet' @('build', $desktop, '-c', 'Release', '-r', 'win-x64', '-p:PublishAot=false', '--no-restore')

    if ($SkipTests) {
        Write-Host '[SKIP] C# regression suites and UI smoke tests (-SkipTests); output is UNTESTED.' -ForegroundColor Yellow
    }
    else {
    foreach ($test in @('BackupTests', 'PurgeTests', 'InventoryTests', 'OAuthCallbackTests', 'ProtocolTests', 'LocalizationTests', 'CloudNameTests', 'ParallelTests', 'CloudStatusTests', 'UiSmokeTests')) {
        $path = "./tests/KeyPassXCCloudSync.$test/KeyPassXCCloudSync.$test.csproj"
        if (-not (Test-Path -LiteralPath $path)) { throw "STOP: missing test project $path" }
        if ($test -eq 'UiSmokeTests') {
            # Compile first, then run the DLL in an owned .NET Process.
            # Never accept a printed PASS as a substitute for exit code 0.
            Invoke-Checked 'dotnet' @('build', $path, '-c', 'Release', '-p:PublishAot=false')
            $smokeDll = './tests/KeyPassXCCloudSync.UiSmokeTests/bin/Release/net10.0-windows/KeyPassXCCloudSync.UiSmokeTests.dll'
            if (-not (Test-Path -LiteralPath $smokeDll)) {
                throw "STOP: UI smoke DLL missing after build: $smokeDll"
            }
            Invoke-TestSuite -Name 'UiSmokeTests' -Arguments @($smokeDll) -TimeoutSeconds 120
        }
        else {
            Invoke-TestSuite -Name $test -Arguments @('run', '--project', $path, '-c', 'Release') -TimeoutSeconds 900
        }
    }

    }

    if (-not $SkipPublish) {
        Invoke-Checked 'dotnet' @('publish', $desktop, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishAot=true', '-o', './publish')
        if (-not (Test-Path './publish/KeyPassXCCloudSync.exe')) {
            throw 'STOP: NativeAOT publish did not produce KeyPassXCCloudSync.exe.'
        }
        if ($SkipTests) {
            Write-Host '[SKIP] Windows NativeAOT published without C# tests (-SkipTests); package NOT created.' -ForegroundColor Yellow
        }
        elseif ($SkipPackage) {
            Write-Host '[SKIP] Portable package omitted by explicit -SkipPackage.' -ForegroundColor Yellow
        }
        else {
            # Packaging is mandatory for the default build; package and checksum errors are fatal.
            & (Join-Path $PSScriptRoot 'package-windows.ps1')
            if (-not $?) { throw 'STOP: portable package creation failed.' }
        }
        if (-not $SkipTests) { Write-Host "`n[SUMMARY] All test suites passed; Windows NativeAOT published." -ForegroundColor Green }
    }
    else {
        if ($SkipTests) { Write-Host '[SKIP] C# tests and NativeAOT publish explicitly skipped.' -ForegroundColor Yellow }
        else { Write-Host "`n[SUMMARY] All test suites passed; NativeAOT publish skipped by request." -ForegroundColor Green }
    }
}
catch {
    Write-Host "[FAIL] Build / fatal: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
finally {
    Pop-Location
}
