# Security model

## Scope

The application synchronizes an already encrypted KeePassXC KDBX database. It never attempts to decrypt the database or read password entries. The cryptographic protection and choice of master password/key file belong to KeePassXC and the user. The application can still observe and publish **encrypted vault bytes**, metadata, paths, remote objects and synchronization topology. Treat those as sensitive.

## Assets and trust boundaries

- Working vault: chosen local `.kdbx`; never delete it merely because a remote replica is missing.
- Local private state: `state.json`, version metadata, history, conflicts, backup files and diagnostic logs; not shared across computers.
- Remote replicas: each cloud can read encrypted database objects and metadata under its authorized directory. They are not necessarily trusted with availability or strong concurrency guarantees.
- Authentication: Google OAuth refresh token and S3/WebDAV secrets in Windows Credential Manager; Proton CLI session managed by Proton CLI. The INI contains credential target names and non-secret configuration. The Google Desktop OAuth client ID/secret identify this public installed application; they are embedded in the binary and do **not** authorize access to any account without user consent and a token.
- External processes and UI: the Windows platform adapter launches CLI and mediates native taskbar, tray, registry and system API calls. These should not be invoked directly by Core.

## Protections

The engine writes immutable versions with parent relationships, checks data hashes and identity, detects multiple tips, preserves conflicts and requires explicit manual merge. It performs a safe read-only preflight before first enablement, debounces local file writes, checks file stability and prevents duplicate local writers. The remote CAS implementation is used where available. Credential deletion only targets application-owned entries; external Proton CLI sessions are not unconditionally removed.

## Manual conflict resolution boundaries

`data/conflicts/` contains immutable copies of the conflicting encrypted versions;
`data/merge/` contains disposable, editable working copies. The user may save a
merged KDBX over a working copy. Before accepting it, the engine verifies the
original copies against their recorded content hashes and rejects direct selection
of those immutable originals. A byte-identical branch is accepted only after a
separate explicit choice acknowledging that the other branch's changes may be
omitted. Every resolution records the conflicting revisions as parents, retains
the originals and installs the exact bytes of one private verified snapshot into
both local history and the working database. The tool cannot decrypt the KDBX
and therefore cannot certify the semantic completeness of a KeePassXC merge.

## Known limitations

A malicious or broken server can withhold, delay, replay, drop or misreport objects. Not all providers can enforce atomic competing writes. The local host may still expose encrypted bytes, sensitive metadata or provider tokens to processes with sufficient user privileges. NativeAOT and portable packaging do not replace OS disk encryption and backups. Removal of a client from topology does not revoke cloud-provider tokens; revoke them at the provider when a device is lost. Secure SSD/remote physical erasure is not guaranteed.

## Responsible reporting

Do not open a public GitHub issue containing live credentials or personal vault content. Use the private channel described in [SECURITY.md](../SECURITY.md). Include a minimal reproduction against a synthetic KDBX and an isolated test cloud wherever possible.

## Partial availability during activation

Preflight verifies the local file and metadata, then independently inspects every reachable configured cloud. Authentication failures and known network/time-budget failures are recorded as unverified replicas rather than being treated as empty remote directories. At least one verified replica is required. Malformed metadata, incompatible vault identities, mismatched topology, unknown revision tokens and other safety violations remain fail-closed. The user receives an explicit partial-coverage warning, and a second identical preflight snapshot is required before enabling. An offline replica cannot be declared synchronized; upon reconnection the engine rescans, checks vault identity and ancestry, and stops unsafe publication rather than force-overwriting a divergent head. This is not a distributed lock and cannot prove that an inaccessible replica has no independent edits.
