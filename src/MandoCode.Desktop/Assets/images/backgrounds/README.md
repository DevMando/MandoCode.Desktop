# Bundled chat backgrounds

Images in this folder ship with the release and appear as tiles in **Settings → Appearance → Chat
background**, above "Choose image…". Users can still pick any file of their own; these are just the
ones that come in the box.

Nothing here is registered in code. `Services/BuiltInBackgrounds.cs` reads the folder at startup, so
**adding an image is a file drop** — copy it in, and it's in the gallery on the next run. Removing one
is a delete; anyone already using it keeps their copy (see "How selection works" below).

## Naming

    NN-kebab-case-name.jpg

- `NN-` orders the gallery and is stripped from the label, so `01-` sorts first without showing.
- **`01-` is also the first-run default** — the image a brand-new install opens on, at the standard
  30% opacity (see `ThemeManager.ApplyFirstRunBackground`). Change the default by renumbering, not by
  editing code. It applies on first launch only; existing users are never re-skinned.
- The rest becomes the tile's caption: `02-violet-nebula.jpg` → **"Violet Nebula"**.
- Extensions offered: `.jpg`, `.jpeg`, `.png`, `.webp`. Anything else in this folder is ignored.
- The file name is the durable identity — it's what's saved in `ui-settings.json` to mark the active
  tile. **Renaming an image in a later release un-marks it** for anyone who had it selected (their
  background keeps working; the tile just stops showing as active). Prefer adding over renaming.

## Sizing

These go into the installer, so every megabyte here is a megabyte every user downloads.

- **1920×1080 is plenty** — the image is a backdrop behind text, drawn at whatever the window is, and
  it renders at 30% opacity by default.
- **Aim for ≤600 KB each.** JPEG at quality ~80, or WebP, gets a 1920-wide render there comfortably.
- Favor **low-contrast, low-detail** images. Busy or bright ones fight the text, which is the whole
  reason the opacity slider exists — an image that only works at 10% opacity isn't a good default.
- Thumbnails are generated at runtime (`DecodePixelWidth`), so don't add separate thumbnail files.

## How selection works

Picking a tile **copies** the image into the user's data folder (`%LOCALAPPDATA%\MandoCode.Desktop`)
as `chat-bg.<ext>`, exactly like picking your own file — the per-tab WebView serves it from there over
the `mandocode.userdata` host.

That copy is why a user's background survives the app updating or being reinstalled underneath it, and
why an image dropped from a future release doesn't break anyone still using it. It also means an
updated image with the same file name will **not** replace the copy someone already has; ship it under
a new name if you want existing users to see the new version.
