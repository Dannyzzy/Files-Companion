## Files Companion 1.2.3 — the self-check tells the truth on any setup

### Fixed

- **`--verify` no longer calls a hand-made Recycle Bin "missing".** The check
  looked only at the folder the installer itself would have used. If the shell
  keys already pointed at your own `RecycleBin.exe`, the report said
  `✗ 回收站组件缺失` even though the bin worked fine. It now reads the redirect
  keys and reports `✓ 回收站组件已就位（自定义位置：…）` instead. This is the same
  mistake 1.2.2 fixed for the `FilesOpen.exe` shim — the Recycle Bin half had
  simply been left behind.
- **`--doctor` prints the real display DPI.** It used to report `96 (100%)` on a
  125% display: the shim deliberately stays DPI-unaware (see `PlayAnimation`),
  and the desktop device context then hands back a virtualised 96. The doctor run
  happens before any window exists and exits immediately, so it becomes aware for
  that run only — and now reports `120 (125%)`, which is what you actually see.
- **`--doctor` also accepts a Recycle Bin outside the installed folder**, and
  prints its path with a `(custom location)` marker.

### Why

Both wrong numbers came out of the very report you would hand to someone whose
setup does not work. A self-check that is wrong about a *working* component sends
people hunting for a problem they do not have — and a DPI that reads 100% on
every machine hides the one display question worth knowing about.

Verified on Windows 11 at 125% scaling, with both a standard install and a
hand-made setup: `--verify` shows four green checks, `--doctor` reports
`120 (125%)`.
