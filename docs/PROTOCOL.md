# Synchronization protocol (wire schema 2)

The **application source version** is `7.1.16` (development snapshot). The persistent remote protocol has **schema version 2**. They serve different purposes; the on-disk and remote schema is retained intentionally for safe access to vault data, not as application upgrade code. There is no automatic migration from unrelated remote layouts. The client refuses incompatible remote directories.

## Remote layout

```text
<RemoteDirectory>/
  database.kdbx               current working encrypted database
  versions/<version-id>.kdbx   immutable snapshots
  versions/<version-id>.json   metadata, parents and content hash
  devices/<installation-id>.json
  topology.json               non-secret shared device/replica registry
  stats/<installation-id>.json  optionally published usage totals
```

Each client has its own persistent `InstallationId`. Each cloud connection has a stable `CloudId` distinct from its display name; moving an endpoint or bucket changes the storage identity. Remote metadata stores topology, version graph and opaque identifiers, **not** local passwords, tokens, key files or KDBX master keys. The vault remains encrypted from the perspective of this utility.

## Discovery and publication

The normal poll reads small metadata to learn revision changes and device pointers. It should not fetch an unchanged KDBX body or issue unnecessary writes. For a new local change, the engine waits for debounce and file stability. Publication creates immutable revision objects first, updates device pointers and maintains the current working `database.kdbx` while preserving ancestry. It uses conditional operations where supported, with conservative checks on stores without strong CAS. Such checks cannot make an inherently non-CAS provider transactional; the application must be transparent about that limitation.

## Conflict handling

Two independently written revisions form separate tips of a directed acyclic graph. The engine detects diverging histories and stops unsafe automatic overwrite. The user resolves the copies in KeePassXC and publishes the resulting deliberate revision. Reverting to identical bytes from an older snapshot is still a new event when the recorded ancestry changes. The absence of a remote working file must never propagate as deletion of the user's local KDBX.

A manually accepted resolution records all active conflicting heads as parent
revisions, even when the user explicitly chooses to retain the unchanged bytes of
one branch. Immutable source snapshots are protected from selection as merge
results; prepared workspace files may be edited in place and then selected.

## Topology and shared settings

`topology.json` uses `TopologyId`, monotonically increasing `Revision`, random `ChangeId`, participating computers and cloud descriptors. Shared options include polling intervals, timeouts, debounce, retry, backup retention and usage publication cadence. Local paths, actual authorization tokens and OS credential targets stay private. Cloud suggestions on another PC require explicit local authorization. A newly discovered cloud may report `NEEDS_AUTH` without blocking other independent replicas.

Topology updates can be queued and published later. A pending edit is not the same as a published edit; archive display must not report permanent deletion until the change is acknowledged. Removing a cloud or computer from shared topology does not physically erase every remote snapshot or revoke independent provider credentials.

## Retention and statistics

Keep a minimum of 20 backup copies and all backups younger than 30 days by default. Preserve any required ancestors or conflict copies even if nominal retention has elapsed. Each computer maintains local per-cloud counters and optionally publishes aggregated non-secret usage snapshots. Usage exchange is independent of the vault revision graph and must not be allowed to alter KDBX bytes.

## Safety limitations

Preflight reads a snapshot of the current state; it is not a distributed lock on all cloud APIs. Network isolation and non-CAS backends may still have external races. The program uses a local per-vault lock to prevent two running copies on the same Windows machine from writing one file concurrently. All irreversible actions, remote identity mismatch and inconsistent ancestry must fail safely rather than assume an arbitrary winner.
