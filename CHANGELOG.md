# Changelog

All notable application and repository changes are recorded here, newest first.
This is the only release-history document. All other documentation describes
only the latest application behavior.

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
