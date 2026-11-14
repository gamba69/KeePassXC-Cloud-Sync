# Changelog

All notable application and repository changes are recorded here, newest first.
This is the only release-history document. All other documentation describes
only the latest application behavior.

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
