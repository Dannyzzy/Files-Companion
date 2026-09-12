## Files Companion 1.1.0 — the all-in-one set

Both components now ship **inside one installer**. Nothing is downloaded at
install time, so it works on a machine with no GitHub access at all.

### What is in the box

- **Files enhancement layer** — restores the launch animation Files loses while it
  stays resident, and routes folders, drives, "This PC" and `Win+E` through Files
- **Modern Recycle Bin** — image previews, restore anywhere, copy files out,
  filter by type, Windows 11 styling

### Changed in 1.1.0

- The installer embeds both components: **262 KB, one click, no network**
- Each component can still be unticked before installing
- The bundled Recycle Bin is removed on uninstall **only when this installer
  placed it** — a copy you installed separately is left alone
- `vendor/RecycleBin` carries the component with `scripts/refresh-vendor.ps1`

### Install

Download `FilesCompanionSetup.exe` below and run it — **no administrator
rights**. To remove it later, run `Uninstall.cmd` in
`%LOCALAPPDATA%\FilesCompanion`.

`FilesOpen.exe` is also attached for anyone who prefers to wire up the registry
keys themselves.

### Requirements

Windows 10 or 11 (64-bit), Files installed, and the WebView2 Runtime for the
Recycle Bin component (preinstalled on Windows 11).