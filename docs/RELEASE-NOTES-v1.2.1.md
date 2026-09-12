## Files Companion 1.2.1 — reliability

**This release exists because of a real failure mode.** The enhancement layer had
to locate Files, and it probed two hardcoded paths while swallowing every error.
On a machine where Files was installed differently — the classic GitHub installer,
winget, scoop, or a preview build with a different execution alias — double-clicking
a folder did **nothing at all**. Silent failure is the worst kind.

### Fixed

- **Six-way launcher discovery**: the local Files folder, *any* `files*.exe`
  execution alias in `WindowsApps` (so `files-stable`, `files-preview` and future
  names all work), `Program Files`, the AppX package root recorded by the Windows
  package repository, winget, and scoop.
- **Explorer fallback**: if no launcher is found, the folder opens in Explorer
  instead. A working window beats a dead double-click.
- **Launch failures are logged and also fall back**, rather than disappearing into
  an empty catch block.
- URI targets (`files-stable:`, `files-preview:`, `files:`) are recognised, so a
  non-store install's scheme still routes correctly.

### New: a self check

```
"%LOCALAPPDATA%\FilesCompanion\FilesOpen.exe" --doctor
```

Writes a report and opens it in Notepad: which launcher was found and by which
strategy, the state of all eight shell redirects, whether the WebView2 Runtime is
present, and a plain verdict. If something is wrong on your machine, that report
says what.

### Install

Download `FilesCompanionSetup.exe` below and run it — no administrator rights, both
components embedded, no network access.