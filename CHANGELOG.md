# Changelog

All notable application and repository changes are recorded here, newest first.
This is the only release-history document. All other documentation describes
only the latest application behavior.

## [9.1.1] - 2026-11-22

### Changed

- Add Today as the initial period in the About performance selector; retain
  Week, Month and All time in their existing order.
- Add Today as the first row of all cloud and computer statistics summaries,
  including the Status page and cloud/computer management views.
- Calculate Today from the local calendar date using existing per-day usage
  counters; preserve weekly/monthly and all-time totals and remote schema 2.
- Localize the new period label in Russian, Ukrainian and English.

### Tests

- Check day, week, month and all-time boundaries (including future and invalid
  date keys), first-row presentation and the default performance selection.

## [9.1.0] - 2026-11-22

### Changed

- Upgrade KeePassXC.CloudTransport from 1.0.2 to 1.0.4 and use its host-owned
  Google OAuth callback page customization instead of an unused local HTTP writer.
- Match KeePassXC Card Print's self-contained result page design with distinct
  success/failure states, RU/UK/EN text, and the application's selected theme.
- Keep OAuth listeners, credentials and token exchange within CloudTransport;
  render no account details, tokens, remote resources or scripts in result HTML.

### Tests

- Replace obsolete HTTP-writer assertions with callback HTML localization,
  theme, status, safe-markup and package callback-API contract coverage.
- Preserve the existing Google Desktop OAuth identity check. Windows CI and
  real-provider OAuth acceptance remain required before release qualification.

### Interface

- Place the full-width application update status and check/install/repair actions
  first on About, above cloud performance, as in KeePassXC Card Print.
- Keep automatic checks, automatic download and installation mode in Settings;
  keep Proton Drive CLI maintenance and its preferences there as well.
- Reflow update actions below the status on narrow windows. Refresh application
  and Proton statuses independently when the user switches pages.

### UI tests

- Check the About update panel layout, action availability and section order
  with headless UI tests, while preserving performance and credits assertions.

## [9.0.1] - 2026-11-18

### Fixed

- Allow the privacy audit to recognize exactly the five embedded provider source
  files intentionally deleted by the 9.0.0 NuGet transport migration, even before
  the Git working-tree deletions are staged.
- Continue rejecting all other missing tracked sources; only skip a provider
  file when Git confirms its deletion and the replacement adapter exists.
- Preserve secret scanning, forbidden-path checks, and fail-closed audit errors.

## [9.0.0] - 2026-11-18

### Changed

- Replace the embedded Google Drive, Proton Drive, S3/R2 and HTTPS WebDAV
  transport implementations with the fixed KeePassXC.CloudTransport 1.0.2
  NuGet package. Remove the bundled provider and Google OAuth HTTP clients.
- Introduce a single host adapter that preserves provider identifiers, INI
  credentials, Google Desktop OAuth identity, the existing remote path layout,
  schema 2, conditional-write capabilities and error classifications.
- Move Google OAuth exchange and Proton interactive login verification to the
  reusable package. Share Proton CLI update-exclusion leases with the package.
- Keep sync scheduling, immutable version history, conflict decisions, vault
  preflight, backup retention and application UX in KeePassXC Cloud Sync.

### Tests and constraints

- Adapt isolated Google OAuth refresh-token and Proton CLI login regression
  tests to exercise the packaged implementations; verify error classifications.
- Require isolated cloud-test directory cleanup to be confirmed. Proton Drive
  diagnostics cannot delete the isolated folder through package 1.0.2 and
  therefore fail closed rather than claiming cleanup succeeded.
- Preserve mandatory source audit checks and add a guard against reintroducing
  embedded provider implementations. Windows CI and real-provider acceptance
  remain required before release qualification.

## [8.2.4] - 2026-11-17

### Fixed

- Align the headless CloudNameTests process launcher with the asynchronous,
  cancellation-aware Proton CLI login contract introduced in 8.2.3.
- Restore the Windows CloudNameTests compilation gate (CS0535) without changing
  runtime authentication, cloud synchronization or remote protocol behavior.

## [8.2.3] - 2026-11-17

### Fixed

- Recheck previously auth-paused Google Drive and other credential-backed
  connections when their saved credentials are edited, without requiring an
  application restart. Resume as unverified until a real cloud scan succeeds;
  preserve immutable revision history and remote cursors.
- Wait for the external Proton Drive CLI browser-login process to exit, then
  verify its actual session with a read-only `/my-files` listing. Only a
  successful probe clears the obsolete authorization warning and queues the
  independent cloud scan. Failed or canceled login never implies success.
- Keep Proton login and its validation protected from concurrent CLI binary
  replacement, and cancel the UI wait when its window closes.

### Tests

- Add credential-edit retry policy cases covering missing credentials,
  paused authentication, Proton exclusion and preservation of vault history.
- Add isolated Proton CLI login/verification cases for successful login,
  interrupted login, authentication rejection and unrelated CLI errors.

## [8.2.2] - 2026-11-16

### Fixed

- Isolate the Windows UI smoke tests for Proton CLI discovery from the
  developer machine's actual managed CLI under LocalAppData. Use an absent
  test-owned managed path and explicitly check the portable / managed / PATH
  / configured-file precedence, including a missing configured file.
- Prevent a correctly installed managed Proton CLI from causing an unrelated
  PATH-precedence assertion to fail during the Windows build.

### Tests

- Add UI smoke coverage for the managed CLI taking precedence over PATH,
  portable tools taking precedence over the managed CLI, explicit paths
  staying authoritative, and the empty-discovery case.

## [8.2.1] - 2026-11-16

### Fixed

- Persist the user-approved switch from a missing explicit Proton CLI path to
  automatic managed CLI discovery immediately for an existing connection.
  Closing the editor without pressing Save no longer resurrects the old path.
- When the managed CLI is already installed, offer to select it instead of
  displaying a misleading download prompt. Keep explicit paths fail-closed
  until the user approves the switch.
- Preserve cloud identity across in-dialog settings persistence, and cover
  automatic-versus-explicit executable precedence with isolated tests.

## [8.2.0] - 2026-11-16

### Added

- Offer to download the official Proton Drive CLI when a Proton connection is
  created or edited without an available executable. Store it in the current
  user's local tools directory; never silently override an explicit custom path.
- Check Proton CLI updates separately from application updates, with an
  independent automatic-check checkbox and manual actions in Settings.
- Show the existing compact Avalonia progress dialog during download,
  SHA-512 verification, idle wait, replacement and installation validation.
- Use the official proton.me release index, architecture-specific binaries,
  bounded streaming downloads, version validation and rollback on failure.
- Block executable replacement while Proton store sessions are in progress;
  never terminate a synchronization or restart the application for a CLI update.

### Tests

- Validate Proton release index parsing, architecture/checksum binding,
  trusted download URL checks and exclusive synchronization/update leases.

## [8.1.3] - 2026-11-16

### Fixed

- Use the existing Microsoft Fluent System Icons for Cancel and Close in
  update, recovery and post-restart dialogs, matching other application buttons.
- Size the Avalonia progress and result windows to their actual content rather
  than reserving empty vertical space. Hide the optional detail row when empty;
  retain sufficient room when an operation reports a file name or error.
- Preserve the foreground handoff and verified installation/recovery behavior.

## [8.1.2] - 2026-11-16

### Fixed

- Bring the temporary Avalonia update/recovery window to the foreground when it
  opens and again when the original application exits and file replacement or
  rollback starts. Foreground elevation is temporary, never permanently topmost.
- Grant the temporary helper Windows foreground-activation permission during
  process handoff; keep native calls in the Windows platform adapter.
- Preserve the existing verified update transaction and automatic rollback.

### Tests

- Cover the parent-exit foreground transition for installation and rollback,
  without activating the window during download, waiting or routine progress.

## [8.1.1] - 2026-11-16

### Fixed

- Keep the temporary Avalonia updater bootstrap behind a shared, OS-neutral
  delegate instead of referring to the Windows `Program` entrypoint in UI files.
  The Windows adapter exclusively provides filesystem update/recovery actions.
- Maintain a single UI helper state without starting the cloud engine or
  duplicating installation work when Avalonia initialization fails.
- Synchronize current-version metadata in README, security policy, and protocol
  reference with the centralized project version so the source audit passes.

### Tests

- Check that updater bootstrap remains unset in normal execution and that
  worker invocation is provided through the adapter-owned delegate.

## [8.1.0] - 2026-11-15

### Added

- Avalonia update/recovery operation windows, adapted from KeePassXC Card Print 7.1.0–7.1.1:
  visible preparation and download progress, temporary update-helper status,
  and a one-time result dialog after the application restarts.
- Per-installation persistent result journal with success, rollback and failure
  acknowledgement; safely migrate the completion marker from older helpers.
- Localized Ukrainian, Russian and English operation stages; foreground the
  restarted window before displaying the modal result without permanent topmost.

### Safety and tests

- Preserve verified GitHub release authentication, rollback, synchronization
  quiescence and same-version recovery confirmation. The update helper never
  starts the cloud engine or opens a KDBX file.
- Exercise result path isolation, result validation, acknowledgement, legacy
  completion and initial/cancelled update progress via update regression tests.

## [8.0.9] - 2026-11-15

### Fixed

- Accept GitHub's signed immutable-release predicate `https://in-toto.io/attestation/release/v0.2`
  alongside the previously supported `v0.1` format. Neither an unknown predicate
  nor an unrelated attestation can authorize an update.
- Keep mandatory Sigstore/TUF signature and timestamp verification, signed GitHub
  release identity, repository numeric ID, exact tag, tag-ref SHA-1 and ZIP SHA-256
  checks unchanged. No change to cloud synchronization or wire schema.

### Tests

- Test both permitted release predicate types with the same signed repository,
  tag and archive identity checks; reject unsupported versions, unrelated types,
  missing predicates, modified tag and changed ZIP hash for v0.2.

## [8.0.8] - 2026-11-15

### Fixed

- Accept canonical, case-insensitive GitHub repository names in signed release
  predicates and package URLs, as required by the GitHub Package-URL definition.
  The release tag, repository numeric ID, signed Git ref, ZIP asset name and
  SHA-256 digest remain strictly bound to the authenticated attestation.
- Report which signed release identity field mismatched instead of a single
  ambiguous repository/ID/tag error. No update verification is bypassed.

### Tests

- Add signed release regression fixtures for lowercase GitHub PURLs and mixed
  repository-name casing; reject changed package identity, tag, repository ID,
  and modified artifact digests.

## [8.0.7] - 2026-11-15

### Fixed

- Route the one-shot `--reinstall` launch request through the Windows desktop
  adapter instead of accessing the Windows entrypoint from shared Avalonia UI.
- Restore the mandatory SourceAudit platform-boundary check without relaxing
  it; preserve the same-version repair, confirmation and verification policy.

### Tests

- Add a regression for consuming the Windows-only maintenance request once,
  including confirmation that the next read is false.

## [8.0.6] - 2026-11-15

### Added

- Add a one-shot `--reinstall` maintenance option that permits the same published
  version while retaining the normal ban on downgrades.
- Reuse the verified GitHub immutable-release download and transactional
  installer, with an explicit confirmation before reinstalling.
- Distinguish repair from upgrading in update notifications, Settings,
  tray actions and RU/UK/EN confirmation dialogs.
- Turn the existing Install now button into Repair when the current version is
  installed (or after a failed check); request consent before downloading and
  verifying the published release.

### Safety

- Require GitHub Release Immutability and full attestation verification when
  reinstalling the current version; never silently use a mutable release.
- Never auto-reinstall on exit, even when normal updates install on exit.
- Do not persist the `--reinstall` command in startup shortcuts or installer
  restart arguments; reject a second-instance invocation explicitly.

### Tests

- Cover CLI parsing, allowed version ordering, verification invocation and
  rejection, persistent-stage isolation, repair button policy and confirmation
  gating using isolated updater fixtures.

## [8.0.5] - 2026-11-14

### Fixed

- Align the protocol reference and security policy with the centralized 8.0.5
  application version. The previous 8.0.4 update missed both files and caused
  the mandatory Windows SourceAudit to fail before compilation.
- Preserve the immutable-release verification changes and regression tests from
  8.0.4 without modifying synchronization or updater runtime behavior.

### Validation

- Keep the existing mandatory SourceAudit version checks for README, protocol,
  security policy and changelog; review all current-version documentation.

## [8.0.4] - 2026-11-14

### Fixed

- Correct immutable GitHub release verification with Sigstore 1.1.1: GitHub's
  signed trusted-root authority identifiers are hostnames, whereas the library
  requires absolute URIs. Canonicalize only the two known GitHub authority
  identifiers after authenticated TUF target retrieval.
- Retain mandatory TUF metadata and target verification, certificate identity,
  signed timestamps, release attestation and ZIP digest checks. Verification
  failures still block installation; errors now identify the attestation stage.

### Tests

- Add positive and negative regression cases for GitHub's hostname-only Fulcio
  and timestamp authority identifiers, unexpected hosts, insecure schemes,
  malformed trust roots and preserved certificate key material.

## [8.0.3] - 2026-11-14

### Fixed

- Update the LocalizationTests UI-source guard to match the typed
  UiErrorDisplay.PresentCloud(status) path introduced in 8.0.0, while continuing
  to require localized presentation for cloud action guidance.
- Split the guard into separately reported assertions for errors and actions,
  so a future failure identifies the broken presentation path precisely.

### Tests

- Add runtime RU/UK/EN assertions for Proton and generic authentication errors,
  including suppression of provider diagnostic payloads in visible messages.
- Keep the localization test suite in the mandatory build gate; no runtime
  synchronization behavior or remote protocol schema changes.

## [8.0.2] - 2026-11-14

### Fixed

- Restore the Core project reference in the standalone LocalizationTests build:
  UiErrorDisplay now consumes ReplicaStatus and CloudTransportException.
- Grant the localization test assembly internal access to the Core provider error
  contract without making its implementation types public.

### Tests

- Verify localized OAuth exceptions through the production presentation adapter,
  including rejection of sensitive raw error text in RU, UK and EN.
- Keep LocalizationTests as a mandatory Windows build gate.

## [8.0.1] - 2026-11-14

### Fixed

- Preserve partial multi-cloud synchronization when an isolated test filesystem
  backend fails with IOException. Only the synthetic filesystem providers use
  this classification; unknown production I/O failures still fail closed.
- Repeat failing protocol-test case identifiers next to the final test summary
  so a long console log cannot obscure the cause of a failed Windows build.

### Tests

- Add classification regressions for CAS and non-CAS test transports and a
  negative assertion that unclassified production I/O cannot bypass safety.
- Retain the full end-to-end ProtocolTests partial-publication/recovery scenario.

## [8.0.0] - 2026-11-14

### Changed

- Distinguish provider authentication, configuration, rate limiting, temporary
  outages and genuine replication safety failures across Google Drive, Proton
  Drive CLI, WebDAV, S3 and Cloudflare R2.
- Isolate a provider authentication failure from the publication safety gate;
  incompatible vault identities, conflicting CAS writes and corrupt version
  metadata remain hard safety failures.
- Keep local-only exponential retry counters, stop polling when operator action
  is required, and allow explicit manual requests to override paused retries.
- Parse Google OAuth error codes without writing provider response bodies,
  access tokens or refresh tokens to state and logs.
- After a successful Google login, retain cursors/history and require a full
  provider safety sweep before publishing again.
- Localize actionable cloud error messages and restore cloud login prompts.

### Tests

- Add CloudResilienceTests with provider/status classification matrices,
  OAuth sanitization, backoff bounds, state persistence compatibility,
  paused-worker wakeups, isolation, safety-gate and recovery assertions.
- Include CloudResilienceTests in mandatory Windows build and source audit.

## [7.2.4] - 2026-11-14

### Changed

- Track SHA-256 ownership of files installed by each verified portable update,
  scoped to the specific application directory, without writing into that directory.
- Before replacing a verified release, remove obsolete runtime files only if they
  were recorded by an earlier successful install and their bytes are unchanged.
  Never guess ownership for existing manually installed DLLs or modified files.
- Include removed runtime files and the previous ownership record in transaction
  backups, power-interruption recovery and automatic rollback.

### Tests

- Added regression coverage for first-install safety, obsolete DLL cleanup,
  per-install isolation, modified DLL preservation, malformed manifests,
  deletion failures, recovery and ownership rollback.

## [7.2.3] - 2026-11-14

### Fixed

- Made updater HTTP test fixtures derive a newer release tag from the compiled
  application version, instead of hard-coding 7.2.1, which became outdated.
- Added a regression test confirming that an already-installed release is
  ignored and strengthened the warning assertion for mutable GitHub releases.
- Applied the dynamic tag to negative API/asset tests and transaction fixtures,
  so future version bumps cannot bypass those test cases.

## [7.2.2] - 2026-11-14

### Fixed

- Fixed the release-attestation verifier to accept GitHub tag subjects with
  `uri` but no `name`, while archive subjects identify files by `name`.
- Reject missing, malformed, or mistyped attestation fields as invalid data,
  rather than crashing UpdateTests with `KeyNotFoundException`.
- Extended updater regression tests for malformed JSON, missing predicates,
  invalid subject structures and incorrect field types.

## [7.2.1] - 2026-11-14

### Fixed

- Fixed both update confirmation dialogs to pass the required localized action
  button label to `MessageDialog.AskAction`, resolving CS7036 on Windows build.

## [7.2.0] - 2026-11-14

### Added

- Built-in Windows portable self-updater with automatic GitHub release checks,
  verified background downloads, safe restart/exit installation and recovery.
- GitHub Immutable Release cryptographic attestation verification using Sigstore
  and GitHub's pinned TUF bootstrap; explicit warning for mutable releases.
- Per-PC update preferences and update status in Settings and the tray menu.
- UpdateTests suite covering versions, SHA-256, malicious ZIPs, signed release
  identity, Snappy encoding, mutable releases and immutable failure handling.

### Security

- Never overwrite KDBX, local INI, synchronization state or Proton CLI.
- Validate staged file hashes before installation; preserve a prior executable
  backup and recover interrupted installations outside the running EXE.
- Require the cryptographic attestation for immutable releases, failing closed
  if verification is unavailable or invalid.

## [7.1.17] - 2026-11-14

### Changed

- Centralized release history in this root `CHANGELOG.md`.
- Removed release-specific narratives and outdated version references from
  README, architecture, build, privacy and security documentation.
- Updated the source audit to require exactly one root changelog, verify that
  it includes the current application version, and reject release-history
  sections elsewhere in project documentation.
- Updated current-version references to 7.1.17.

### Unchanged

- Cloud synchronization, on-disk protocol schema 2, and release packaging
  behavior are unchanged.

## [7.1.16] - 2026-11-13

### Changed

- Replaced the fixed version badge in the README and GitHub Pages hero collage
  with a cloud synchronization illustration, retaining the application captures.
- Aligned protocol and README source-version references with the application.

## [7.1.15]

### Changed

- Ignored root-level `.patch` and `.zip` files to prevent accidental commits
  of local patches and downloaded release archives.

## [7.1.14]

### Fixed

- Corrected the manual release-assets workflow to find the authenticated draft
  by its exact tag and attach verified assets using the numeric release ID.

Earlier changes have not been reconstructed from release history.
