# Contributing to NAI Files

NAI Files is an independent fork of [Files](https://github.com/files-community/Files).
Contributions belong in **[cyberluke/NAI-Files](https://github.com/cyberluke/NAI-Files)**,
not in the upstream repository. Upstream Files does not track this fork's
changes, so a pull request opened there will not reach NAI Files.

Community interactions must follow the [Code of Conduct](./CODE_OF_CONDUCT.md).

## Ways to help

- **Report a bug** with the bug report form. Include the NAI Files version,
  Windows version and a log when possible.
- **Request a feature** through the feature request link on the new-issue page.
- **Send a pull request** for a bug or an approved change.
- **Test a build** on your hardware, especially Pulse, Media Lab and the AI
  settings, and report what you observe.

Search the [issues](https://github.com/cyberluke/NAI-Files/issues) before
opening a new one. Add to an existing report instead of duplicating it.

## Before you open a pull request

1. Open or find an issue that describes the change. Non-trivial work should be
   discussed first so nobody invests time in a direction that will not land.
2. Keep one pull request per issue and link the issue in the description.
3. Keep the change scoped. Avoid opportunistic refactors, formatting churn,
   dependency updates and generated-file edits.
4. Follow [`.editorconfig`](../.editorconfig) and the rules in
   [`AGENTS.md`](../AGENTS.md). Keep changed text files in CRLF line endings.

## Build and verify

Use the SDK pinned in `global.json`:

```powershell
dotnet build src/Files.App/Files.App.csproj -c Debug -p:Platform=x64 -v:quiet -clp:ErrorsOnly
```

For focused work, build the affected project first:

```powershell
dotnet build src/Files.Shared/Files.Shared.csproj -c Debug -p:Platform=x64 -v:quiet -clp:ErrorsOnly
dotnet build src/Files.App/Files.App.csproj -c Debug -p:Platform=x64 -v:quiet -clp:ErrorsOnly
```

There is no agent-ready automated test suite. A pull request must at least
build cleanly, and the description must list the manual steps you used to test
the change. If the change touches Pulse, Media Lab, AI or file operations, say
which folder types and files you exercised.

The inherited interaction tests in `tests/` are optional and use Appium and
WinAppDriver; they are not required for a normal contribution.

## Pull request titles and bodies

Use a short title that names the behavior and prepend the type:

- `Fix:` — a bug fix
- `Feature:` — a feature or enhancement
- `Code Quality:` — anything else

For the body, follow [PULL_REQUEST_TEMPLATE.md](./PULL_REQUEST_TEMPLATE.md).
Describe why the change is needed, what you chose, and what you verified.

## Translations

NAI Files does not use Crowdin. The app follows the NAI OS language by default
and stores a per-app override in Settings → General. Translation changes go
through the normal resource files and the upstream Files localization
infrastructure; do not add Crowdin project links.

## Review

The maintainer reviews pull requests. Community members may comment, but the
maintainer approves and merges. Expect iterations; keep the branch history
readable and squash fixups when asked.

## Remotes

- `origin` — `files-community/Files` (upstream, read-only)
- `fork` — `cyberluke/NAI-Files` (the product repository)

Push work to `fork`, never to `origin`.
