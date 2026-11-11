# Testing and diagnostics

The default Windows source build runs every automated regression test. The headless UI suite explicitly uses the real Skia rendering backend (not the default fake drawing backend), so PNG decoding and native pixel-size assertions are meaningful. Tests use synthetic KDBX fixtures, isolated local folders and headless UI components. They do not require a real KeePassXC password, a key file, or real cloud credentials.

## Running the automated tests

From a Windows terminal in the source root:

```powershell
.\build.bat
```

The entry-point preflight validates the syntax of every PowerShell build script before executing it. This runs all mandatory source and package checks, compiles the application, executes all ten C# test executables, and publishes the Windows NativeAOT program. The build stops when any stage fails. The executable is published only after the automated tests succeed.

To run the complete test suite without creating a NativeAOT executable:

```powershell
.\build.bat -SkipPublish
```

Other switches:

- `-ValidateOnly`: check source code, platform boundaries, documentation and version metadata; do not compile or run C# tests.
- `-SkipTests`: skip C# regression and UI tests, **but keep mandatory source checks**. Builds produced with this option are untested and unsuitable for release qualification.
- `-SkipPublish`: keep automated testing enabled; skip only NativeAOT publication.

The default is **no skips**. You do not need to enable testing manually.

## Version 7 independent-worker regressions

`ParallelTests` includes independently scheduled cloud workers (a blocked slow
provider must not stall a different cloud), a manual wakeup while another
provider is busy, and coalescing of repeated requests during an active cycle.
The isolated `ProtocolTests` also exercise committing a network scan staged
outside the coordinator, refusing a superseded cloud cursor, and maintaining
the existing CAS and non-CAS safety checks. These are headless tests only: a
Windows .NET 10 build and live multi-computer trials are required for release.

## Individual results

Each test case is printed separately. Successful assertions use `[PASS]`, failed assertions use `[FAIL]`, and both include a suite/case name. Subsystem-specific writers, including native PowerShell validation and application diagnostics, follow the same two result markers. Nonterminal progress is tagged separately (`[INFO]`, `[STEP]`, `[WAIT]`, `[CLEANUP]`). `[SUMMARY]` totals are informational; they do not replace individual results. Explicitly omitted stages use `[SKIP]`, never `[PASS]`.

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

A nonzero process exit code, any `[FAIL]` line, or a suite that reports **zero** individual `[PASS]` cases makes the build fail. Unhandled exceptions receive an explicit failure record; a printed success line without successful process exit cannot pass the build.

## Automated suites

| Suite | What it validates |
| --- | --- |
| BackupTests | Offline startup backups, integrity and retention |
| PurgeTests | Safe, permanent removal of obsolete device/cloud records |
| InventoryTests | Local protected-data accounting and safe conflict copies |
| OAuthCallbackTests | Secure browser responses, localized callback pages, and presence of the published Desktop OAuth client |
| ProtocolTests | Isolated replication, concurrency, conflict preservation and safety invariants |
| LocalizationTests | Every translation catalog entry, generated labels and UI fallback guards |
| CloudNameTests | Display-name validation, configuration paths and Proton executable discovery |
| ParallelTests | Concurrency limits, cancellation and stable result ordering |
| CloudStatusTests | Live connection selection versus stale cached status |
| UiSmokeTests | Headless window hierarchy, theming, artwork, fonts and navigation |

## Tests using real providers

Cloud connection tests require an **explicit user action** under **Diagnostics** in the running application, with a configured and authenticated account. They do **not** run automatically during a build because the build must not access a user's real cloud or credentials.

The cloud test runs inside a separate randomly named remote directory. It prints separate `[PASS]` or `[FAIL]` results for bootstrap, idle polling, second-device bootstrap, upload/download or conflict handling, and test-directory cleanup. Do not interpret a successful connection probe as proof that cloud synchronization is safe; execute the isolated provider checks and verify the subsequent safety preflight.

Do not upload KDBX files, key files, tokens, live configuration files or unredacted log contents when reporting a failure.

## Windows PowerShell and multilingual output

The Windows test runner explicitly decodes child process standard output and standard error as UTF-8. This preserves Russian and Ukrainian UI test identifiers in PowerShell 5.1 consoles. A test failure must remain visible as a distinct `[FAIL]` line, and the build stops before NativeAOT publication.

## Partial-availability regression coverage

The V2 protocol suite exercises activation preflight with a reachable fake cloud and an unauthenticated replica, stable double-check snapshots, the required failure when no cloud can be verified, actual `PARTIAL` engine publication, and rejection of an independently initialized foreign vault on reconnection. The transport classifier also ensures metadata, vault identity and conflict errors cannot be hidden as network outages. All tests operate on isolated temporary local files; they do not contact real cloud accounts.

## Manual merge acceptance regression coverage

The V2 protocol suite checks three additional isolated scenarios: saving a merged
KDBX directly over a prepared working copy in `data/merge/`; requiring an explicit,
version-specific confirmation before keeping a byte-identical original branch; and
rejecting a merge when any immutable original under `data/conflicts/` changes.
The same path classification is shared by the desktop file picker and the engine.
The selected result is copied to a verified private snapshot before both the local
working database and immutable history backup are written. Original conflicting
versions remain available for recovery. These automated tests do not prove that
KeePassXC entries were semantically merged: that check requires the user to reopen
and inspect the resolved vault in KeePassXC.

## Version 7 regression focus

The ParallelTests suite now checks that all configured independent I/O
operations can start together, that each completed scan is committed before
its slowest peer finishes, and that live activity timestamps are ephemeral.

Before any production deployment, the Windows CI build and synthetic
multi-replica integration tests must additionally validate: simultaneous
remote edits, one provider becoming unavailable during a publish, manual
requests while a slow peer is active, restarting with partially staged
versions, and the final independent per-cloud scheduler. No real KDBX
secrets or real cloud credentials should be used as test fixtures.

## GitHub workflow safeguards

Windows CI runs on every pushed branch and pull request (but not on tag pushes) and executes the full test/Native AOT/package gate. Manual release automation has separate preflight, read-only build and attachment jobs. The attachment job rechecks the tag and draft identity and verifies SHA-256 on its own runner; it cannot publish the release. A fail-fast repository privacy audit runs before .NET restore.
