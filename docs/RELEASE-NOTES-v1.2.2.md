## Files Companion 1.2.2 — see the result immediately

### New

- **The installer now verifies itself.** When the install finishes, the same
  window reports what was detected on your machine: which Files launcher was
  found (and by which of the six strategies), whether all seven shell redirects
  are in place, whether the Recycle Bin component landed, and whether the WebView2
  Runtime is available. Any line marked ✗ says what to do about it.
- **`--verify`** runs that check without installing anything:
  `FilesCompanionSetup.exe --verify` — useful before installing, and for
  diagnosing someone else's machine.
- A redirect that already points at a **custom** `FilesOpen.exe` is now reported
  as configured rather than missing, so a hand-made setup is not flagged as broken.

### Why

Silent failure is the worst failure mode: before 1.2.1 the layer probed two
hardcoded paths and swallowed every error, so a differently-installed Files meant
a folder double-click did nothing at all. 1.2.1 fixed the detection. 1.2.2 makes
the result **visible at the moment of installation**, so nobody has to discover a
problem later.