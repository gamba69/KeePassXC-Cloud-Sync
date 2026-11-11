# Troubleshooting and recovery

## Application starts and immediately exits

Look in the notification area: an instance with the same configuration may already be running. Use **Exit** from its tray menu and restart the application. A second instance intentionally refuses to start a second synchronization engine for the same vault. If an actual startup failure occurred, check `%LOCALAPPDATA%\KeyPassXCCloudSync\startup-errors.log`. Never delete a state directory just to clear an error.

## Taskbar icon does not reflect status

Windows 11 grouped taskbar buttons may continue displaying the white executable icon even when the app changes the window icon. The Windows adapter applies a small warning/error overlay for those cases. It waits for `TaskbarButtonCreated` and reapplies the indicator after Explorer restarts. Taskbar API failures should be nonfatal and logged; they must never stop sync.

## Proton Drive CLI connection

Verify that the official `proton-drive.exe` is on disk and discoverable beside the application, in `tools/`, or via the Windows `PATH`. Alternatively use the Browse button in the Proton editor to select the executable explicitly. Run it manually **as the same Windows user** to confirm that the CLI session is authorized and can list `/my-files`. The application's cloud directory is relative to `/my-files`. Do not set `RootPath` or a custom cache directory for Proton in a new configuration. The CLI owns its session; installing the graphical Proton Drive desktop client is not a substitute. A CLI HTTP 200 confirms only that one request succeeded; also test isolated create/download/upload/delete before enabling sync. When sharing logs, redact account identifiers, tokens, URLs containing secrets and internal opaque IDs.

## Google Drive OAuth

If authentication is missing, use the account button and perform OAuth again. Check the intended Google account, OAuth application configuration, allowed scopes and permissions. Testing-mode Google OAuth apps may have short-lived refresh-token authorization; use a properly configured production OAuth application as appropriate. Do not place a refresh token or app secret into the INI, GitHub issue or screenshot.

## WebDAV or S3

For WebDAV verify the exact HTTPS WebDAV endpoint, TLS certificate, rights to create/rename/write/delete and strong ETag/conditional-write behavior. `401`/`403` usually indicate missing credentials or permissions, `404` may indicate a wrong endpoint/path. For S3 verify bucket, region, endpoint, credential scopes and write permissions. A successful list operation alone does not prove correct conditional-write behavior. Providers without CAS remain subject to unavoidable concurrent-writer limitations.

## Partial or conflicting replica state

`PARTIAL` indicates at least one cloud did not confirm the current revision. Examine each card's error, authentication and last-check timestamps. `CONFLICT` means independent revisions exist: copy the full encrypted revision files to an independent safe directory, merge them manually in KeePassXC, then resume publication only once preflight confirms the intended result. Do not force-push one branch on top of another or delete all ancestry in an attempt to suppress the warning.

## Diagnostic tests and reports

Run tests through the **Diagnostics** tab. The test provider uses an isolated remote test folder and temporary local files. Inspect the report status rather than assuming network access means the client is ready for production. Logs are rotated according to `[Logging]`; debug logs may expose paths, account labels, endpoint hostnames and opaque cloud identifiers. Redact before sharing.

## Safe recovery checklist

1. Stop editing the vault on all devices until the situation is understood.
2. Make new independent encrypted copies of the local KDBX and available remote conflicting revisions.
3. Verify local vault integrity with KeePassXC; keep any key file separately secured.
4. Check each cloud's identity, permissions, read-only preflight and revision ancestry.
5. Fix authorization/network issues; do not erase `state.json` unless performing a deliberately isolated fresh installation.
6. Resolve conflicts in KeePassXC and validate data before resuming writes.
7. Verify the status on each computer separately. A green status on one PC does not prove that every other machine is correctly configured.

## Unsupported provider configuration keys

The application deliberately rejects unknown keys in enabled `[Cloud.NAME]` sections. This is not an automatic migration. Check the reported key, confirm the intended remote directory and provider account, and use the current configuration form to create a supported connection. Never remove unknown fields blindly if doing so could redirect the vault to another cloud location. Keep an offline backup until the first synchronization passes an isolated test and preflight.
