## Files Companion 1.0.0

First public release — a thin layer in front of [Files](https://github.com/files-community/Files)
that puts back what residency takes away.

### What it does

- **Launch animation** — Files opens with the icon fade/zoom Windows normally plays
- **Smart routing** — folders, drives, "This PC" and `Win+E` all open in Files
- **Correct landing page** — "This PC" and `Win+E` land on Files Home, which shows drive cards with capacity bars
- **Recycle Bin link** — optionally installs and wires up [Modern Recycle Bin](https://github.com/Dannyzzy/Modern-Recycle-Bin)
- **Reversible** — per-user registry only, no administrator rights, uninstall restores the defaults

### Why it is not a "plugin"

Files has no plugin or extension API, so this ships as a companion tool that sits
beside Files rather than inside it. It contains no Files code or assets.

### Install

Download `FilesCompanionSetup.exe` below and run it. To remove it later, run
`Uninstall.cmd` in `%LOCALAPPDATA%\FilesCompanion`.

`FilesOpen.exe` is also attached for anyone who prefers to wire up the registry
keys themselves.