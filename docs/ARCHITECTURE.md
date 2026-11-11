# Architecture and platform boundary

## Design goals

The synchronization engine must remain independent of Avalonia, P/Invoke, Win32 APIs, the Windows registry, platform secret storage and shell UI. The desktop is currently shipped only on Windows, but the code is structured so a Linux/macOS host can be added without rewriting the synchronization algorithm. **A directory separation is a boundary; it does not claim that a non-Windows host is already implemented or tested.**

## Source tree and dependency direction

```text
KeyPassXCCloudSync.Core (net10.0)
  Domain models, protocol, cloud transports, configuration, safety, synchronization
  PlatformContracts.cs defines host-facing interfaces
        ^
        |   references the Core types; Core never references Desktop
        |
KeyPassXCCloudSync.Desktop (net10.0-windows)
  Avalonia UI, view models, app lifecycle, shared desktop interfaces
  Platforms/Windows/
    WindowsHost.cs                 application startup and process activation
    WindowsPlatform.cs             composition root, appearance, registry, paths, browser
    WindowsChangeMonitoring.cs     local file watcher and network notifications
    WindowsCliRunner.cs            official Proton CLI process execution
    WindowsCredentials.cs          Windows Credential Manager
    WindowsDatabaseLockService.cs  named mutex and thread ownership
    WindowsInstanceCoordinator.cs  one running instance per configuration
    WindowsNativeToast.cs and others
    WindowsTaskbarStatusIcons.cs   Win32 taskbar and overlay
```

No `DllImport`, `LibraryImport`, registry access, Win32 COM, or direct OS event subscription belongs in `Core`. Shared Avalonia views obtain desktop features through `IDesktopHost`, never by instantiating `WindowsTaskbarStatusIcons` or other native classes. Resource selection and branding are shared; platform-specific rendering of the taskbar icon is delegated to an adapter. Configuration parses cloud identity but asks `IPlatformPaths` to discover the external Proton CLI executable.

## OS-neutral contracts

Defined in `PlatformContracts.cs`: `IPlatformServices` exposes `ISecretStore`, `IAutostartService`, `IDatabaseLockService`, `IPlatformPaths`, `IExternalBrowser`, `IPlatformProcessLauncher`, `IPlatformChangeMonitoring` and `IPlatformCliRunner`. `EngineHost` uses `IPlatformChangeMonitoring.WatchFile`/`WatchNetwork` to wake the worker without importing OS-specific events. `ProtonDriveStore` constructs commands and parses responses; `IPlatformCliRunner` launches the process, drains stdout/stderr, enforces a timeout and terminates failed children. Test doubles implement the same contracts without touching real secrets or a real cloud.

Desktop-specific contracts in `DesktopPlatform.cs`: `IDesktopHost`, `ISystemAppearance`, `IInstanceCoordinator`, `IInstanceSession`, `ITaskbarStatusIcons`, `IDesktopNotifier`. The Windows platform composition root instantiates the concrete implementations. Both platform-service registration and per-config process ownership happen before the Avalonia main window starts.

## Workflows

**Local change:** platform watcher -> `EngineHost.Wake()` -> debounce/stable-file check -> read-only preflight -> synchronized operation gate -> immutable revision publication -> device pointer and working-file updates -> status/usage refresh.

**Remote change:** per-cloud metadata polling -> discover changed version/pointers/topology -> verify provenance/identity -> download only necessary content -> preserve independent revisions -> apply safe local update or raise conflict.

**Settings:** GUI draft -> validation -> atomic config write -> refresh platform notifications and engine configuration; protected KDBX/state paths cannot be moved while synchronization is active.

**Diagnostics:** temporary local vault and isolated cloud directory -> per-step PASS/FAIL -> safe cleanup; never mutate production KDBX.

## Cross-platform porting checklist

1. Add another host adapter implementing every `IPlatformServices` contract; secure secret storage must provide reliable read/write/delete semantics.
2. Implement local file-change detection, network connectivity events, per-vault single-writer locking and process-level singleton behavior.
3. Implement browser/OS shell, Proton CLI discovery and launch for that OS, autostart, local data paths, desktop notifications and taskbar status behavior (or a clearly documented fallback).
4. Bind the new adapter in that OS's bootstrap; choose its runtime identifier and native rendering packages without affecting the Core project.
5. Run all isolated Core tests plus GUI and provider tests on the actual platform before advertising support. Ensure no native Windows source is included in the new target.
6. Revisit path and case sensitivity, INI storage, filename normalization, secure removal semantics and platform-specific permission errors. Do not share `state.json` between machines.

## Security invariants

Never log raw credentials, passwords or vault contents. Do not accept cloud IDs as mere display labels. Require a verifiable common ancestry before automatic fast-forward. Preserve conflicting KDBX bytes and prevent remote deletion from deleting the local vault. Do not weaken CAS or TLS handling merely to get a green provider test. The platform boundary must not bypass these invariants.

## Reviewed platform-dependency inventory

| Platform concern | Contract in portable Core/shared UI | Windows implementation |
| --- | --- | --- |
| Machine identity and case-sensitive path comparisons | `IPlatformPaths` | `WindowsPlatformPaths` |
| Application config, local UI data and startup log locations | `IPlatformPaths` | `WindowsPlatformPaths` |
| Local database change and network availability events | `IPlatformChangeMonitoring` | `WindowsChangeMonitoring` |
| Official Proton CLI discovery and subprocess execution | `IPlatformPaths`, `IPlatformCliRunner` | `WindowsPlatformPaths`, `WindowsCliRunner` |
| Account credential storage | `ISecretStore` | `WindowsCredentialStore` / `WindowsCredentials` |
| KDBX single-writer lock | `IDatabaseLockService` | `WindowsDatabaseLockService` |
| Autostart and shell/browser launch | `IAutostartService`, `IExternalBrowser`, `IPlatformProcessLauncher` | `WindowsAutostartService`, `WindowsBrowser`, `WindowsProcessLauncher` |
| Per-config process singleton and external window activation | `IInstanceCoordinator`, `IDesktopHost` | `WindowsInstanceCoordinator`, `WindowsHost`, `WindowsProgram` |
| Light/dark caption preferences and taskbar status | `ISystemAppearance`, `ITaskbarStatusIcons` | `WindowsSystemAppearance`, `WindowsTaskbarStatusIcons` |
| Native toast notifications | `IDesktopNotifier` | `WindowsNativeToast`, `WindowsToastNotifier` |
| Process architecture and memory reporting | `IDesktopHost` | `WindowsDesktopPlatform` |

The Core project targets the portable `net10.0` TFM. Its file and HTTP operations use standard cross-platform .NET APIs, not operating-system-specific functions. All explicit native Windows operations reside in `Platforms/Windows/`. The current **Windows-targeted Desktop project compiles those adapters into the application executable**; they are isolated by source and interface boundaries rather than by a third native-adapter assembly. A future non-Windows host must exclude `Platforms/Windows/` from compilation and supply its own implementation. This has not been built or tested yet.

The source guard checks these boundaries and rejects direct native calls, shell/process probing, and Windows entrypoint coupling in shared code. This review does **not** replace compilation, headless regression tests, or an actual Windows integration run.

## Version 7.1.11 development transition (not yet release qualified)

The Status subtitle distinguishes independent live cloud activity from the
host's periodic maintenance timer. While idle it uses the current configured
clouds, replica statuses and `NextCloudPollUtc` deadlines to report the next
actual cloud poll, retry, authorization requirement or pending reconciliation.
The countdown is computed on the UI timer without changing remote scheduling
or synchronizer state. An overdue deadline is shown as waiting to start; a
missing deadline is not replaced with a fabricated value.

The global `SYNCED` result is derived from positive acknowledgements of the
local immutable KDBX version across all currently configured cloud replicas.
A prior `PARTIAL` state is retired as soon as all current acknowledgements
match, while a healthy transport with an older working version is not treated
as fully synchronized. Background progress is reset after the last active
cloud operation. The Status banner stops growing at the visible width of
four 350-DIP cloud cards plus three 10-DIP gaps (1430 DIP); the trailing card
margin does not count. History data-inventory rows reuse the application
zebra-stripe theme brush. For the protected-data inventory, the four-column
row grid itself paints the stripe across every column, without a separate
rounded background wrapper.

Each configured KDBX cloud has its own `IndependentCloudScheduler` worker and
poll deadline. A worker snapshots the current local graph briefly under
`EngineHost.Gate`, performs its network scan in an isolated staging directory
**without** holding the global gate, then commits through the single V2
coordinator. This prevents an idle slow provider from occupying the KDBX gate
for its entire scheduled scan. The coordinator ignores a staged scan whose
cloud cursor was superseded by a newer committed scan.

Remote changes (including a new local KDBX) still trigger an intentionally
conservative *fresh* safety sweep of other replicas before selecting a common
head, installing a local copy, or publishing a KDBX version. The sweep can
wait for a slow peer; this is a data-integrity dependency, not a polling queue.
The snapshot/commit boundaries prevent background observations from mutating
the shared history tree or state file outside the coordinator gate.

Manual per-cloud requests wake the corresponding worker, with duplicates
coalesced. Requests arriving before the worker starts or during the old host
cycle remain in a durable-in-process mailbox for the next opportunity.
`UsageExchange.CreateBackgroundJob` performs network I/O independently of
KDBX reconciliation; its final merge reloads the latest state before adding
counter deltas and peer snapshots. The paused-mode control plane still uses
its existing explicit topology polling path.

**Remaining limitations:** topology changes and new-version safety sweeps still
require coordinated peer results; independent background scans may contend
with those uncommon shared operations. The UI activity record represents the
primary KDBX operation, not simultaneous statistics requests. Windows .NET 10
compilation and tests, NativeAOT publishing and multi-machine acceptance are
mandatory before calling this source production-ready.
