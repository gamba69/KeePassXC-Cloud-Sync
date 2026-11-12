# Security policy

## Supported version

The current development source version is **7.1.13** for Windows x64. This version is not production qualified until its Windows build and safety tests pass. The remote synchronization protocol retains schema version 2; that is not an additional supported application release.

## Vulnerability disclosure

Please do not disclose credential material, KDBX data, security tokens, or private cloud paths in public GitHub issues. Contact the repository maintainer through a private GitHub security advisory (**Security → Report a vulnerability**) when private reporting is enabled. If private reporting is unavailable, do not post exploit details publicly; contact the maintainer using an explicitly published private contact method.

Include the observed impact, reproducible steps with **synthetic data**, environment details and any relevant redacted logs. No response times or remediation deadlines are guaranteed.

## Security expectations

Report anything that can cause silent remote overwrite, lost conflict revisions, accidental deletion of local KDBX, credential exposure, authentication bypass or unsafe access to topology. Never weaken TLS certificate verification or strong conditional-write requirements as a workaround for provider errors. See [Security model](docs/SECURITY_MODEL.md).
