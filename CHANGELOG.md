# Changelog

All notable application and repository changes are recorded here, newest first.
This is the only release-history document. All other documentation describes
only the latest application behavior.

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
