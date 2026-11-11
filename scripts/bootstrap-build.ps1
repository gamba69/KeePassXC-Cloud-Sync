# Entry-point preflight: validate all PowerShell scripts before executing the build.
# PowerShell parses the entire target script before running any of its statements.
# Keep this launcher minimal so that a syntax error in build-windows.ps1 is
# reported as a named [FAIL] instead of an unhandled command-line parser error.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
try {
    foreach ($name in @('bootstrap-build.ps1','build-windows.ps1','verify-sources.ps1','verify-repository-privacy.ps1','validate-release.ps1','package-windows.ps1')) {
        $path = Join-Path $PSScriptRoot $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Missing PowerShell script: $name"
        }
        $bytes = [System.IO.File]::ReadAllBytes($path)
        if ($bytes.Length -lt 3 -or $bytes[0] -ne 239 -or $bytes[1] -ne 187 -or $bytes[2] -ne 191) {
            throw "PowerShell script must be UTF-8 with BOM: $name"
        }
        $tokens = $null
        $parseErrors = $null
        [void][System.Management.Automation.Language.Parser]::ParseFile(
            $path, [ref]$tokens, [ref]$parseErrors)
        if ($null -ne $parseErrors -and $parseErrors.Count -gt 0) {
            $details = @($parseErrors | ForEach-Object {
                "line $($_.Extent.StartLineNumber): $($_.Message)"
            }) -join '; '
            throw "PowerShell syntax error in ${name}: $details"
        }
        Write-Host "[PASS] ScriptParser / $name" -ForegroundColor Green
    }
}
catch {
    Write-Host "[FAIL] ScriptParser / $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

# Forward all build options, such as -SkipTests, unchanged.
try {
    & (Join-Path $PSScriptRoot 'build-windows.ps1') @args
    if (-not $?) { throw 'Main build script failed.' }
}
catch {
    Write-Host "[FAIL] Build / $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
