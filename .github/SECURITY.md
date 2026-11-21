# Security Policy

## Supported versions

Security fixes target the latest `nai` build. Older builds may not receive
backports; update to the newest release or source build before reporting an
issue that may already be fixed.

## Reporting a vulnerability

Do **not** open a public issue for a security problem.

Use GitHub's private vulnerability reporting on this repository:

1. Open the **Security** tab of
   [cyberluke/NAI-Files](https://github.com/cyberluke/NAI-Files).
2. Choose **Report a vulnerability**.
3. Include the NAI Files version, Windows version, reproduction steps and the
   impact you believe the issue has.

If private reporting is unavailable, contact the maintainer
[@cyberluke](https://github.com/cyberluke) through GitHub and ask for a private
channel before sharing details.

## Scope

In scope:

- The WinUI 3 app, including Pulse, Media Lab, AI settings and local services.
- Packaging and signing of the MSIX.
- Native interop, file operations, shell integration and drag/drop.
- Data handling in local storage and logs.

Out of scope:

- Vulnerabilities in third-party dependencies that are already fixed upstream
  (report them upstream and mention the dependency here).
- Issues that require an already-compromised machine or administrator access.
- Social engineering and physical access.

## What to expect

The maintainer will acknowledge a report, ask for clarification if needed, and
coordinate a fix and disclosure. There is no bug bounty.
