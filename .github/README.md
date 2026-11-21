# NAI Files

NAI Files is a personal WinUI 3 file manager for **NAI OS**, with a media-first
Pulse view, a built-in FFmpeg media lab, and local AI and semantic search.

> NAI Files is a personal project. It is not affiliated with Microsoft.

## Why NAI Files

### Pulse view

A chronological, time-railed feed for folders that contain images, videos and
audio. Cards show real thumbnails, inline video playback, waveform strips,
hover previews and contact sheets, and the layout adapts to portrait and
landscape media.

### Pulse is the default for media folders

- A manual per-folder layout choice always wins and is remembered.
- Pictures and Videos open in Pulse.
- Any folder that contains an image, video or audio file opens in Pulse.
- A folder with no media stays in the default details view.

The decision is re-evaluated on navigation while the folder still follows the
adaptive default; changing the layout from the toolbar pins that folder.

### NAI Media Lab

46 FFmpeg 9 operations for video, audio and images: convert, trim, split,
compress, HDR-to-SDR tonemap, extract frames and GIFs, contact sheets, audio
normalize and denoise, and more. Operations run locally and write sibling
outputs, with cancellable progress.

### AI and semantic search

- Pluggable AI providers: local LUSI, Kelvin and OpenAI-compatible gateways.
- Per-capability routing for chat and vision, with local NPU/GPU lanes preferred.
- File search backed by NAI Lattice vectors and LUSI embeddings, all local.

### NAI OS integration

- Follows the NAI OS language by default; a per-app override lives in
  Settings → General.
- NAI theme, SAP 72 font and rainbow folder icons.

### The file manager underneath

Tabs, panes, tags, archives, FTP/SFTP, Git status, the preview pane, batch
rename, and the rest of the file manager feature set remain available.

## Install

NAI Files is distributed as a signed MSIX and built from source. It is not on
the Microsoft Store and does not run an automatic updater; updates are manual.

1. Build the package (below), or install a signed MSIX from the NAI Files
   download domain (Cloudflare R2; the public link will be added here).
2. Trust the certificate used to sign the package.
3. Install it:

```powershell
Add-AppxPackage -Path .\Files.App_<version>_x64_Debug.msix
```

## Build from source

### Prerequisites

- Windows 10 2004 (10.0.19041) or newer; Windows 11 recommended.
- .NET SDK **11.0.100-rc.3** (pinned in `global.json`); NuGet restore brings the
  Windows App SDK and WinUI 3 packages.
- Visual Studio 2022 with the WinUI/Windows App SDK components is optional when
  using the `dotnet` CLI.
- For packaging: Windows SDK 10.0.26100 (`makeappx`, `signtool`) and a trusted
  code-signing certificate.

### Build the app

```powershell
dotnet build src/Files.App/Files.App.csproj -c Debug -p:Platform=x64 -v:quiet -clp:ErrorsOnly
```

Use `-p:Platform=arm64` for ARM64.

### Build an MSIX

```powershell
dotnet build src/Files.App/Files.App.csproj -c Debug -p:Platform=x64 `
  -p:AppxBundle=Never -p:GenerateAppxPackageOnBuild=true `
  -p:AppxPackageDir=artifacts/AppxPackages/
```

Sign the produced `.msix` with your certificate, then `Add-AppxPackage` it.

## Repository layout

- `src/Files.App` — WinUI 3 app: Pulse, Media Lab, AI, shell and settings.
- `src/Files.App.Controls`, `Files.App.Storage`, `Files.App.CsWin32` — shared
  controls, storage abstractions and native interop.
- `tests/` — inherited upstream test projects.
- `.github/` — workflows and community documents.
- `AGENTS.md` — development and agent guidelines.

## Contributing

- Bugs and feature requests:
  [issues](https://github.com/cyberluke/NAI-Files/issues)
- Guidelines: [CONTRIBUTING.md](CONTRIBUTING.md)
- Security: [SECURITY.md](SECURITY.md)
- Code of conduct: [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)

## License

NAI Files is distributed under the MIT license (`LICENSE-MIT`); some components
are under the MPL (`LICENSE-MPL`). Third-party notices are in
[NOTICE.md](NOTICE.md).
