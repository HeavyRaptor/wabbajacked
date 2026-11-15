# Wabbajack Browser Automation

This branch adds automated manual downloads for Nexus Mods by driving your own Microsoft Edge browser over the Chrome DevTools Protocol (CDP). Because it uses a real browser with your real profile, it passes Cloudflare/Nexus bot checks that headless Playwright Chromium could not.

## What it does

- During preflight, Nexus Mods files marked as manual downloads are opened in Microsoft Edge.
- The automation clicks **Slow download**, then **Standard download** if it appears, and falls back to **Start download manually** if the file does not start automatically.
- Common Nexus popups (endorsement reminders, etc.) are dismissed automatically.
- Tabs are closed as soon as a download starts, so they do not pile up.
- Up to **5** Nexus download pages are handled at once. This can be tuned in `BrowserAutomationOptions.cs` if Nexus starts throttling or CAPTCHAs appear.

## Requirements

- Windows 10/11
- Microsoft Edge (any channel)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) — only needed for building from source
- A Nexus Mods account that is logged in inside Edge

## How to run

Pre-built binaries are attached to each GitHub Release. Look for `Wabbajack.exe` and `Wabbajack.CLI.exe` under Releases — binaries are **not** stored in git.

1. Log in to [Nexus Mods](https://www.nexusmods.com) in Edge.
2. Close all Edge windows, then start Edge with remote debugging enabled:
   ```powershell
   start msedge --remote-debugging-port=9222
   ```
   If you skip this step, Wabbajacked launches a separate Edge profile and asks you to log in there once.
3. Run `Wabbajack.exe`, start a modlist install, and check **Automate Nexus downloads** on the manual-download card.

## How to build

```bash
# Debug build (about 60 seconds clean)
dotnet build Wabbajack.sln -c Debug

# Release build
dotnet build Wabbajack.sln -c Release
```

Run the offline test suite:

```bash
dotnet test --filter "Category!=RequiresOAuth&Category!=RequiresNetwork"
```

For full project conventions, CI, and contribution guidelines, see the main [README](README.md) and [CONTRIBUTING.md](CONTRIBUTING.md).

## Notes

- **Watch folder must match Edge's download folder.** The manual-download acquirer watches one folder for new files. If Edge downloads somewhere else, Wabbajack either misses the file or has to copy it, which costs extra disk writes. Set both to the same folder (for example on a drive with plenty of space).
- **Verified files are moved into the install folder.** Once an archive is matched by size and hash, it is moved into the modlist's `Downloads` folder (e.g. `D:\Modlists\<list>\downloads`). A small `<archive>.meta` file is written next to it — this is normal and is how Wabbajack tracks successfully installed archives.
