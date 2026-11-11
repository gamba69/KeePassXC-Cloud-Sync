---
layout: default
title: Privacy Policy
permalink: /privacy/
---

# Privacy Policy

[Home]({{ '/' | relative_url }}) · [Project repository](https://github.com/gamba69/KeePassXC-Cloud-Sync)

_Last updated for source version 7.1.11._

This Privacy Policy describes how **KeyPassXC Cloud Sync** handles user data.
The project is an independent desktop application distributed from the GitHub
repository at [github.com/gamba69/KeePassXC-Cloud-Sync](https://github.com/gamba69/KeePassXC-Cloud-Sync).

## Summary

KeyPassXC Cloud Sync is a **local desktop application**. It does not operate a
project-owned hosted synchronization service. The application works directly
between the user’s computer and the cloud providers configured by the user.

## Data the application accesses

The application may access the following data on the user’s computer and in the
user’s chosen cloud providers:

- The user-selected encrypted KeePassXC `.kdbx` file
- Provider-specific synchronization metadata required by the application
- Local backup files, conflict copies and version-history metadata
- Cloud account access tokens or credentials needed to authenticate to the
  selected provider
- Application logs and diagnostics generated locally

## Data the application does not collect for the project owner

The project does **not** collect or receive user vault contents on a central
project server. In particular, the project does not operate a backend that
stores:

- KeePassXC master passwords
- Decrypted password entries
- Key files
- Google Drive file contents copied to a project server
- Central analytics about a user’s vault contents

## Google Drive access

When a user enables Google Drive, the application uses Google OAuth to obtain
access to the user’s Google Drive account for synchronization of the encrypted
KeePassXC database and protocol metadata chosen by the user. The application
uses that access only to perform the requested synchronization tasks.

The application does not use Google Drive access to sell data, to run ads, or
to analyze personal content for unrelated purposes.

## Local storage of credentials

Cloud credentials and tokens are stored locally on the user’s own computer:

- Google Drive OAuth credentials are stored locally by the application
- S3 and WebDAV credentials are stored in Windows Credential Manager
- Proton Drive authentication is handled by the official Proton Drive CLI and
  its own local session storage

Users are responsible for securing their own device, Windows account and local
backup locations.

## Logs and diagnostics

The application can generate local logs and diagnostic reports to help diagnose
configuration or synchronization issues. These diagnostics may contain technical
metadata such as provider names, file paths, timing data or transport errors.
Users should review and redact diagnostics before sharing them publicly.

## Data sharing

The project does not sell personal data and does not share user data with a
central project-operated service, because no such service is part of the
application architecture. Data may of course be transmitted to the cloud
providers selected by the user as part of the requested synchronization flow.
Those providers operate under their own privacy policies and terms.

## User control

Users can stop using a provider at any time by removing the provider from the
application and/or revoking the provider’s credentials or OAuth grant using the
provider’s own account tools. Users can also delete local application state and
protected data copies from their own machine.

## Children’s privacy

The application is not directed to children.

## Changes to this policy

This policy may be updated when the application’s behavior or documentation
changes. The current source version updates the repository files and GitHub
Pages content together.

## Contact

For project information or source code, see the
[GitHub repository](https://github.com/gamba69/KeePassXC-Cloud-Sync).
