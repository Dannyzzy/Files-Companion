## Files Companion 1.2.0

Installer redesign.

### Changed

- **The installer is now DPI aware**, so text stays sharp at 125% and 150%
  display scaling instead of being bitmap-scaled by Windows.
- **The whole window is drawn with GDI+**: borderless rounded frame, the two
  components as cards with custom checkboxes, a custom progress bar and a single
  call-to-action button.
- Clicking anywhere on a card toggles that component; the window drags from any
  empty area and closes with Escape.
- Behaviour is unchanged: still one file, both components embedded, no network
  access, per-user registry only.