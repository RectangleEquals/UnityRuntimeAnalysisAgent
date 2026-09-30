# Overlay assets

The in-game overlay is data-driven: its screens (`assets/overlay/views/`), looks (`assets/overlay/themes/`) and icons
(`assets/overlay/icons/`) are plain files the agent reads at runtime. Only what a game can't load from plain files goes
into a small Unity asset bundle: the fonts (as Unity fonts), the UI Toolkit theme and the effect shaders. Everything here
builds those files. Git holds only text: the binaries (bundles, the icon atlas) are published as a GitHub release of this
repository and locked by `assets/overlay/release.json` (tag, SHA-256 and size of each file). When they're missing, the
build downloads them from that release and checks their hashes, so building the agent needs neither Unity nor Python
(only network access the first time).

## Bundle families

| Bundle | Built with | Used by games made with |
|---|---|---|
| `2021.3.bundle` | Unity 2021.3.x | 2021.3 up to 6000.2 |
| `6000.3.bundle` | Unity 6000.3.x | 6000.3 and newer |
| `legacy.bundle` (optional) | Unity 2018.4.x | 2018.1–2021.2 (uGUI only: fonts and shaders) |

A bundle loads in its own Unity version and newer ones. UI Toolkit's style-sheet format changed in 6000.3, so the theme
needs one family on each side of that change. Unity doesn't report a theme it can't read, so the overlay checks that its
theme really applies (the `.ov-probe` rule in `OverlayBase.uss`) and otherwise uses its uGUI renderer.

## Rebuilding and releasing

Only needed when something in `Source/Overlay/` changes (fonts, the theme, the shaders) or the icon list, never for new
views or themes.

1. **Fonts** (not in git): `python fonts.py` fetches the pinned releases (Ark Pixel, Rubik, JetBrains Mono), trims Ark
   Pixel to the scripts the overlay needs and writes the fonts to `Source/Overlay/Fonts/` and their licences to
   `assets/overlay/licenses/`. Needs `fonttools`; the output is the same bytes on every run.
2. **Bundles:** `./build.ps1` (or `./build.ps1 -Family 2021.3`) builds every family whose editor is installed through
   Unity Hub, in batch mode, into `assets/overlay/bundles/`, and writes `manifest.json` (each bundle's SHA-256 and the hash
   of the sources it was built from). All families must be rebuilt together after a source change; the packager refuses
   bundles that don't match the manifest or were built from different sources. By hand: open a project with the
   family's editor, copy `Source/Overlay` into its `Assets`, and use the menu **Overlay → Build Asset Bundles**.
3. **Icons:** `python icons.py` rasterises the listed Phosphor icons (regular and fill) into `assets/overlay/icons/`.
   Needs `pillow`.
4. **Release:** `./release.ps1` locks the changed files in `assets/overlay/release.json` (a new revision) and prints the
   `gh release create overlay-assets-r<N> …` command. Commit and push the lock, then run that command; until the
   release exists, other checkouts can't download the new files.

When a change in `Source/Overlay/` must be required by the agent, raise `$assetsVersion` in `build.ps1` and
`OverlayAssets.RequiredAssetsVersion` together.

## Licences

Ark Pixel, Rubik and JetBrains Mono are under the SIL Open Font License 1.1; Phosphor Icons under the MIT licence. Their
licence texts ship with the agent in `overlay/licenses/`.
