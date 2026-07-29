# Releasing MandoCode Desktop

Desktop shares its `major.minor` with the engine generation it ships (the engine drives
0.14 → 0.15); the patch number is Desktop's own. Before releasing, the `MandoCode` submodule
must be pinned at a **released** engine commit (one with a tag).

## 1. Prepare

- [ ] Pin the engine: in `MandoCode/`, check out the released engine commit; commit the pin.
- [ ] Bump `<Version>` in `src/MandoCode.Desktop/MandoCode.Desktop.csproj`.
- [ ] Roll `CHANGELOG.md`: retitle `[Unreleased]` to `[X.Y.Z] — YYYY-MM-DD` with a short
      narrative opener. Entries are written in plain language with the "why" included.
- [ ] PR the above, merge to `main`. (CI `build` check is required by the branch ruleset.)

## 2. Fresh-machine test (for anything beyond string changes)

```bash
dotnet publish src/MandoCode.Desktop/MandoCode.Desktop.csproj -c Release -r win-x64 --self-contained -o publish-smoke
# Zip publish-smoke\*, drag into Windows Sandbox, extract EVERYTHING, run MandoCode.Desktop.exe
```

Verify: launches with no installed runtimes; WebView2-missing fails visibly; onboarding is
sane with no Ollama present. Delete `publish-smoke/` and the zip afterward.

## 3. Tag — the pipeline does the rest

```bash
git tag -a vX.Y.Z -m "MandoCode Desktop X.Y.Z — <tagline>"
git push origin vX.Y.Z
```

Pushing the tag triggers `.github/workflows/release.yml`: it builds the self-contained
win-x64 zip and creates the GitHub release with the zip attached (~5–10 minutes). Release
tags are immutable (repo ruleset) — a bad release means a new patch version, never a re-tag.

## 4. Curate the release

- Replace the auto-generated notes: title `vX.Y.Z — <tagline>`, body in plain language with
  an **Install** section (download zip → extract everything → run the exe; self-contained,
  WebView2 is the only system dependency). Mark as latest.
- Verify `https://api.github.com/repos/DevMando/MandoCode.Desktop/releases/latest` serves the
  new tag — that endpoint is what every installed copy's `UiUpdateCheckService` polls (≤ once
  per 24h), so users see the update notice within a day.

## 5. winget

```powershell
wingetcreate update DevMando.MandoCode.Desktop --urls https://github.com/DevMando/MandoCode.Desktop/releases/download/vX.Y.Z/MandoCode.Desktop-vX.Y.Z-win-x64.zip --version X.Y.Z --submit
```

One command: it computes the hash, updates the manifests, and opens the PR to
`microsoft/winget-pkgs` from the existing fork (first run prompts for GitHub auth). The CLA
is already signed. Watch the PR's email notifications for moderator questions — reply by
commenting on the PR. Package identity: `DevMando.MandoCode.Desktop`, moniker
`mandocode-desktop`, currently zip/portable (a real installer with Start Menu entries is a
planned upgrade).
