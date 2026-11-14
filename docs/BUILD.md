# Build and tests

## Prerequisites

- Windows x64 build machine with Windows SDK toolchain suitable for .NET 10 NativeAOT.
- .NET 10 SDK installed for building (the published EXE does not require it).
- PowerShell 5.1+; the scripts also support PowerShell 7 on Windows.
- Internet access to restore NuGet dependencies in a clean checkout, unless a validated offline NuGet cache is available.

The application projects are `net10.0` (Core) and `net10.0-windows` (Desktop). Avalonia and SkiaSharp/HarfBuzz package versions are pinned in the project to avoid unreviewed native-binary combinations.

## Local build

Open a PowerShell terminal in the repository root:

```powershell
.\build.bat
# Or call the underlying script:
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\bootstrap-build.ps1
```

The launcher first parses all six PowerShell scripts and checks their UTF-8 BOM, printing an individual `[PASS]` or `[FAIL]` for each script before the main build begins. The build then performs fail-fast repository privacy, source and metadata checks, validates subprocess exit-code handling, restores dependencies, compiles the application, runs every test suite and publishes self-contained NativeAOT output to `publish/`. On a normal build, it also assembles `dist/KeyPassXCCloudSync-<version>-win-x64.zip` plus `dist/KeyPassXCCloudSync-<version>-win-x64.sha256`. The ZIP does not include Proton Drive CLI; users obtain it separately when needed. Every individual test prints one `[PASS]` or `[FAIL]` result with its name (in English). A failed test stops the build before publication. On failure it must not produce or claim a successful build.

By default `build.bat` pauses at the end, whether the build passes or fails. In CI or other noninteractive commands use `build.bat -NoPause` to suppress the pause while preserving the exit code. Use `-SkipPackage` to build and test without creating the ZIP. `-SkipTests` produces no distributable ZIP.

For fast metadata inspection without compilation:

```powershell
.\build.bat -ValidateOnly
```

To explicitly skip C# tests during local experimentation, use `-SkipTests` and treat any output as **untested**. Mandatory source and metadata checks still execute. `-SkipPublish` compiles and runs the complete suite without producing the NativeAOT executable. Continuous integration uses the same default test gate.

## Test output and build behavior

All checks have a named terminal result. Examples:

```text
[PASS] SourceAudit / MIT license present
[PASS] BackupTests / missing KDBX is skipped
[PASS] CloudStatusTests / three configured clouds, not five cached clouds
[PASS] UiSmokeTests / new brand Image starts with Auto dimensions
[PASS] UiSmokeTests / native 48px artwork decoded
[PASS] UiSmokeTests / new Auto-size Image receives 48 DIP dimensions
[PASS] UiSmokeTests / 125 percent DPI adjusts DIP size without raster scaling
[SUMMARY] UiSmokeTests: N named PASS, 0 FAIL (exit 0)
```

If a case fails, `[FAIL] Suite / case: reason` is printed and the overall command exits nonzero. NativeAOT publication does **not** run when tests fail. A zero exit code without any named `[PASS]` result is also rejected. `[SUMMARY]` lines describe completed suites; `[SKIP]` means a user-requested stage was not executed and is never considered proof that tests passed.

`build.bat` (no arguments) always runs source checks, all .NET regression suites, UI smoke, NativeAOT publication and portable ZIP/SHA256 generation. `-SkipTests` explicitly skips the C# suites but not source/metadata checks. `-ValidateOnly` runs only source/metadata checks; `-SkipPublish` still runs all tests unless `-SkipTests` is also present. Both options skip packaging because no new published executable is produced.

## Test suites

`BackupTests`, `PurgeTests`, `InventoryTests`, `OAuthCallbackTests`, `ProtocolTests`, `LocalizationTests`, `CloudNameTests`, `ParallelTests`, `CloudStatusTests`, `UiSmokeTests`. The headless Avalonia smoke test is compiled and executed as a DLL in an owned child process with timeout, live output and a checked exit code. It uses Skia-backed headless rendering to validate native PNG dimensions; Avalonia’s fake headless backend cannot verify actual pixels. UI tests do not access a real KDBX or genuine cloud credentials. Protocol tests use isolated fake filesystems and explicitly named test roots.

## Platform boundary checks

`./scripts/verify-repository-privacy.ps1` rejects Git-tracked private files and common secret patterns before compilation. `./scripts/verify-sources.ps1` rejects native API or registry imports outside `Platforms/Windows`, direct `FileSystemWatcher` and `NetworkChange` use in Core, platform process launches from cloud transports, missing platform interfaces, non-English source comments, migration code and misplaced changelogs. It also validates icon resources, protocol schema, English documentation structure and GitHub CI assets. This is **static validation** only; it does not prove runtime compatibility. A clean Windows build is mandatory for automated qualification. Tests using real provider credentials are run explicitly from the application Diagnostics tab; see [Testing and diagnostics](TESTING.md).

### Version policy

The current application version is defined exclusively in `Directory.Build.props`.
Release history is maintained only in the root [CHANGELOG.md](../CHANGELOG.md).
All other documentation describes the latest source behavior, without
version-by-version notes. The remote protocol's schema identifier does not
change when the application version changes. Any change to that remote schema
requires explicit specification, security review and tests; it is not hidden
inside a routine packaging change.
