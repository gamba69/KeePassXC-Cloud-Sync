---
layout: default
title: KeyPassXC Cloud Sync
permalink: /
---

# KeyPassXC Cloud Sync

Secure KeePassXC database synchronization across independent cloud connections.

![KeyPassXC Cloud Sync header collage]({{ '/images/hero-collage.png' | relative_url }})

**KeyPassXC Cloud Sync** is a portable Windows desktop application that keeps an
encrypted KeePassXC `.kdbx` database synchronized across multiple cloud
providers without decrypting the vault contents. The application never asks for
your KeePassXC master password and does not upload your key file as part of its
own protocol.

- **Platforms:** Windows x64
- **Framework:** .NET 10 + Avalonia 12
- **License:** MIT
- **Source repository:** [github.com/gamba69/KeePassXC-Cloud-Sync](https://github.com/gamba69/KeePassXC-Cloud-Sync)
- **Privacy policy:** [Privacy Policy]({{ '/privacy/' | relative_url }})

The promotional header above is an illustration. The screenshots below are
actual captures of the Windows desktop application. All images are published
from the `docs/` folder used by this GitHub Pages site.

## What the app does

- Synchronizes encrypted KeePassXC databases between multiple computers.
- Supports independent cloud connections rather than a single primary server.
- Tracks vault history, local backups and manual conflict resolution.
- Provides per-cloud status, statistics, diagnostics and topology management.

## Supported cloud providers

- Google Drive (OAuth desktop flow)
- Proton Drive (via the official Proton Drive CLI)
- S3 and S3-compatible services, including Cloudflare R2
- HTTPS WebDAV

## Screenshots

### Status page

![Status page]({{ '/images/status-page.png' | relative_url }})

The Status page shows the current synchronization result, local vault path,
latest check time, latest change time and direct action buttons.

### Status dashboard sections

![Status page with cloud and computer sections]({{ '/images/status-sections.png' | relative_url }})

Further down, the Status page continues with cloud and computer cards so that
global and per-entity state stay visible together.

### Clouds page

![Clouds page]({{ '/images/clouds-page.png' | relative_url }})

The Clouds page is used to add, edit and monitor provider connections.

### Computers page

![Computers page]({{ '/images/computers-page.png' | relative_url }})

The Computers page shows the participating devices and allows local rename,
removal and leave-synchronization actions.

### Settings page

![Settings page top]({{ '/images/settings-page-top.png' | relative_url }})

![Settings page bottom]({{ '/images/settings-page-bottom.png' | relative_url }})

The Settings page separates local computer settings from shared topology-wide
settings.

### Diagnostics page

![Diagnostics page]({{ '/images/diagnostics-page.png' | relative_url }})

The Diagnostics page contains isolated local and cloud tests together with the
execution log.

### History page

![History page]({{ '/images/history-page.png' | relative_url }})

The History page contains conflict-resolution actions and the local protected
data inventory.

### About page

![About page]({{ '/images/about-page.png' | relative_url }})

The About page contains the cloud performance table and runtime diagnostics.

## Security and privacy summary

- The application synchronizes encrypted `.kdbx` files; it is **not** a password
  database reader or a cloud password manager service.
- The application does **not** run a hosted backend and does not collect user
  data on a project server.
- OAuth tokens and cloud credentials are stored locally on the user’s computer.
- Diagnostic logs may contain environment details and should be reviewed before
  being shared.

Read the full [Privacy Policy]({{ '/privacy/' | relative_url }}).

## Support

- Documentation: [User Guide](https://github.com/gamba69/KeePassXC-Cloud-Sync/blob/main/docs/USER_GUIDE.md)
- Troubleshooting: [Troubleshooting](https://github.com/gamba69/KeePassXC-Cloud-Sync/blob/main/docs/TROUBLESHOOTING.md)
- Source and issue tracker: [GitHub repository](https://github.com/gamba69/KeePassXC-Cloud-Sync)

## Legal

KeyPassXC Cloud Sync is an independent project. It is **not** affiliated with
or endorsed by KeePassXC, Google, Proton, Cloudflare, Amazon Web Services or
any other cloud provider.
