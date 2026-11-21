# Privacy

NAI Files is a local-first desktop app. It does not include analytics,
advertising, Crowdin or Discord integrations, and it does not run a background
update service.

## What the app collects

**Nothing by default.** NAI Files does not send usage analytics or telemetry.

Crash reporting is compiled in for non-Dev builds only, and activates only when
a Sentry DSN is injected at build time. Local and fork builds run in the `Dev`
environment and do not initialize Sentry; the fork's build pipeline does not
inject a DSN. If a future release build enables it, crash reports would contain
app and Windows versions, a stack trace and a local user ID, with usernames in
paths redacted.

The **User ID** shown under Settings → About is a GUID stored on this machine.
It is not sent automatically. The in-app bug report link adds only the app
version and Windows version to the GitHub URL; the optional User ID field in
the report form exists only so you can correlate a report with log excerpts you
choose to share.

## Data stored on this machine

Settings, layout preferences, the per-folder layout database, thumbnails and
logs are stored in the app's package data under `%LOCALAPPDATA%\Packages\`.
Use **Settings → About → Open log location** to open the exact log folder.

Logs can contain file paths, folder names and usernames. Review and redact them
before posting a log in a public issue.

## Network access

NAI Files contacts a remote service only when you use a feature that needs it:

- **NAI Lattice** and **LUSI** run on this machine (loopback) and provide
  semantic indexing and embeddings.
- **AI providers** you configure — local LUSI, Kelvin, or an OpenAI-compatible
  gateway — receive the prompts, text or images you submit to them. Their own
  privacy policies apply.
- **Remote storage** such as SMB, WebDAV, FTP/SFTP or cloud locations is
  contacted only when you open or operate on it.

There is no Microsoft Store integration, no automatic updater and no Crowdin or
Discord connection.

## Contact

Questions about this policy: open an issue at
[cyberluke/NAI-Files](https://github.com/cyberluke/NAI-Files/issues).
