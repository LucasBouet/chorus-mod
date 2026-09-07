# Chorus Mod

BepInEx plugin for Clone Hero: search charts on enchor.us, download,
install, and auto-rescan your library — all from an in-game window.
**Single DLL**, no external app, no dependency to copy around.

Works on **Windows and Linux** (native build or Proton). No path is
hardcoded: everything goes through configuration.

> For a Windows install with no command line, see the all-in-one
> installer in [`installer/`](installer/README.md).

---

## Summary

- [Requirements](#requirements)
- [Fresh install — Linux](#fresh-install--linux)
- [Fresh install — Windows](#fresh-install--windows)
- [Building the plugin](#building-the-plugin)
- [Usage](#usage)
- [Configuration](#configuration)
- [Troubleshooting](#troubleshooting)
- [Technical notes: IL2CPP limitations of this build](#technical-notes-il2cpp-limitations-of-this-build)

---

## Requirements

- Clone Hero **v1.1.0.6142** (IL2CPP, Unity 2022.3.62f2) — other
  versions may work, see the technical notes
- BepInEx **6.0.0-be.755**, *Unity.IL2CPP* variant
- **.NET 6** SDK (only needed to build)

> The BepInEx version matters. "Bleeding edge" builds regularly break
> compatibility with each other: `be.785`, for example, doesn't load the
> same plugins as `be.755`. The plugin is pinned to `be.755` in the
> `.csproj`.

---

## Fresh install — Linux

### 1. Locate the game folder

```bash
find ~ -maxdepth 8 -iname "Clone Hero" -type d 2>/dev/null
```

Common locations:

| Context | Path |
| --- | --- |
| Steam native | `~/.local/share/Steam/steamapps/common/Clone Hero` |
| Steam Flatpak | `~/.var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/common/Clone Hero` |
| Manual install | varies |

Note this path, it'll be used everywhere below:

```bash
export CLONEHERO_DIR="$HOME/.local/share/Steam/steamapps/common/Clone Hero"
```

### 2. Identify the game variant

```bash
ls "$CLONEHERO_DIR"
```

- `Clone Hero.x86_64` present → **native Linux build**
- `Clone Hero.exe` present → **Windows build under Proton**

This determines which BepInEx build to install.

### 3a. Native Linux build → BepInEx Linux

Download `BepInEx-Unity.IL2CPP-linux-x64-6.0.0-be.755+*.zip` from
<https://builds.bepinex.dev/projects/bepinex_be>, then:

```bash
cd "$CLONEHERO_DIR"
unzip ~/Downloads/BepInEx-Unity.IL2CPP-linux-x64-6.0.0-be.755*.zip
chmod +x run_bepinex.sh
```

Launching from Steam: *Properties* → *Launch Options*:

```
"/full/path/to/Clone Hero/run_bepinex.sh" %command%
```

Or outside Steam:

```bash
./run_bepinex.sh
```

> ⚠️ The `run_bepinex.sh` script has known issues with Steam's launch
> wrapper. If BepInEx doesn't load (see §5), launch the game directly
> from the command line to check, then fall back to the Proton method
> below.

### 3b. Windows build under Proton → BepInEx Windows

Download `BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.755+*.zip`, then:

```bash
cd "$CLONEHERO_DIR"
unzip ~/Downloads/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.755*.zip
```

Steam launch options:

```
WINEDLLOVERRIDES="winhttp=n,b" %command%
```

This is the most reliable method: it reproduces the exact Windows
environment, so the same IL2CPP methods are available.

### 4. First launch (required)

Launch the game **once** and let it reach the main menu. This startup
takes longer than usual: BepInEx generates the *interop assemblies*
from the game binary.

### 5. Verify BepInEx loaded correctly

```bash
grep -m3 "BepInEx\|Chainloader" "$CLONEHERO_DIR/BepInEx/LogOutput.log"
ls "$CLONEHERO_DIR/BepInEx/interop" | head
```

You should see the BepInEx version in the log, and about a hundred DLLs
in `interop/` (including `CloneHero.dll`). Without that, there's no
point going further: the plugin won't load, and it won't even build.

### 6. Install the plugin

```bash
mkdir -p "$CLONEHERO_DIR/BepInEx/plugins/chorus"
cp ChorusMod.dll "$CLONEHERO_DIR/BepInEx/plugins/chorus/"
```

Relaunch the game, then check:

```bash
grep "Chorus Mod" "$CLONEHERO_DIR/BepInEx/LogOutput.log"
```

Expected: `Chorus Mod loaded. Songs folder: '...'` then
`Press F9 in-game to open Chorus Mod.`

### 7. Verify the Songs folder

Auto-detection covers the usual locations (next to the game,
`~/Documents/Clone Hero/Songs`, `~/.local/share/Clone Hero/Songs`, and
Proton prefixes). If the log shows an empty or wrong path, fix it:

```bash
nano "$CLONEHERO_DIR/BepInEx/config/fr.lucas.chorus-mod.cfg"
```

```ini
SongsFolder = /home/you/Music/CloneHeroSongs
```

The folder must be **one that Clone Hero actually scans** (check the
game's settings), otherwise charts will install but never show up.

---

## Fresh install — Windows

1. Locate the game folder (Steam → right-click → *Manage* → *Browse
   Local Files*).
2. Download `BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.755+*.zip` from
   <https://builds.bepinex.dev/projects/bepinex_be> and extract **the
   whole content** to the game folder's root (next to `Clone Hero.exe`).
3. If the zip came from a browser, unblock the files, otherwise Windows
   may silently refuse to load them:
   ```powershell
   Get-ChildItem -Path "C:\Games\Clone Hero" -Recurse -Filter *.dll | Unblock-File
   ```
4. Launch the game once (long startup: interop generation).
5. Copy `ChorusMod.dll` to `<game>\BepInEx\plugins\chorus\`.
6. Relaunch and look for `Chorus Mod` in `BepInEx\LogOutput.log`.

---

## Building the plugin

The game path is **never** hardcoded. Three ways to provide it, in
priority order:

**1. Local file (recommended)**

```bash
cp ChorusMod.user.props.example ChorusMod.user.props
# then edit the path inside
```

This file is ignored by git: everyone keeps their own path without
conflicts.

**2. Environment variable**

```bash
export CLONEHERO_DIR="$HOME/.local/share/Steam/steamapps/common/Clone Hero"
dotnet build -c Release
```

**3. Command line**

```bash
dotnet build -c Release -p:CloneHeroDir="/path/to/Clone Hero"
```

Then:

```bash
dotnet restore
dotnet build -c Release
```

The DLL comes out at `bin/Release/net6.0/ChorusMod.dll`. No dependency
to copy alongside it: every reference is `Private=false` (they already
exist on the game's side) and `System.Text.Json` is part of the .NET 6
runtime that BepInEx bundles.

If the path is wrong, the build stops with an explicit message instead
of a wall of missing-type errors.

---

## Usage

| Action | Command |
| --- | --- |
| Open / close | **F9** (configurable) or **Esc** |
| Run search | **Enter** or the *Search* button |
| Scroll | mouse wheel |
| Paste from clipboard | *Paste* button |

**Filters.** Instrument, difficulty, and target field (title, artist,
album, genre, charter). The collapsible *Advanced filters* add 11
tri-state criteria: one click to **require**, two to **exclude**,
three to go back to don't-care.

With no search text but active advanced filters, you can browse every
chart matching a given criterion.

**Install.** The *Install* button downloads, extracts to the Songs
folder, then triggers the game's rescan. Charts hosted on Google Drive
folders can't be fetched automatically: the button becomes *Open* and
hands off to your browser.

The rescan requires the `SongScan` object to exist in the scene: stay
on the main menu or the song-selection screen during downloads.

---

## Configuration

`<game>/BepInEx/config/fr.lucas.chorus-mod.cfg`, generated on first
launch.

### `[General]`

| Key | Default | Role |
| --- | --- | --- |
| `SongsFolder` | auto-detected | Install folder. **Must be scanned by Clone Hero.** |
| `ToggleKey` | `F9` | Key to open the overlay. |
| `BlockGameInput` | `true` | Prevents typed keys from triggering game shortcuts. |
| `FullScan` | `false` | Set to `true` if new songs don't show up after install. |
| `PanelWidth` | `1180` | Overlay width in pixels. |
| `PanelHeight` | `780` | Overlay height. |
| `ShowAlbumArt` | `true` | Album art. Turn off if image display doesn't work on your build. |
| `PanelOpacityLayers` | `8` | Background opacity when tinting isn't available. |

### `[API]`

| Key | Default | Role |
| --- | --- | --- |
| `BaseUrl` | `https://api.enchor.us` | API host. |
| `SearchEndpoint` | `/search` | Free-text search (POST). |
| `FilesBaseUrl` | `https://files.enchor.us` | File host (charts and album art). |
| `Instrument` | `guitar` | Instrument selected on open. |
| `LogRawResponse` | `true` | Dumps the first result's JSON. **Set to `false`** once everything works. |

> These settings let you follow an API URL change without recompiling.

---

## Troubleshooting

**The plugin doesn't load.** Look for `Chorus Mod` in
`BepInEx/LogOutput.log`. Missing → BepInEx doesn't see the DLL: check
the `BepInEx/plugins/chorus/` path, the BepInEx version (`be.755`), and
on Windows, whether the files are unblocked.

**For more detail**, set verbose logging in `BepInEx/config/BepInEx.cfg`,
**in both sections**:

```ini
[Logging.Console]
LogLevels = All

[Logging.Disk]
LogLevels = All
```

**Search fails (HTTP 405).** The `.cfg` has a stale URL. BepInEx never
overwrites an existing value when defaults change: fix `BaseUrl` /
`SearchEndpoint` by hand, or delete the file to regenerate it.

**Songs don't show up after install.** Set `FullScan = true`. If it
persists, check that `SongsFolder` is actually one of the folders
scanned in the game's settings.

**Rewired is broken (looping "Rewired is not initialized").** Restart
the game. This used to come from older versions that disabled the
Input Manager; the current approach doesn't touch it anymore.

---

## Technical notes: IL2CPP limitations of this build

Unity's managed stripping removed a number of methods from the binary
that the game doesn't use. They exist in the reference DLLs but throw
`NotSupportedException: Method unstripping failed` when called — there's
no working around it via injection, the native code simply isn't there.

Mapped by testing on `v1.1.0.6142`:

| Works | Doesn't work |
| --- | --- |
| `GUI.Box`, `GUI.Label`, `GUI.Button` | `GUI.TextField`, `GUILayout.TextField` |
| `GUI.BeginGroup` / `EndGroup` | `GUILayout.Window` (`DoWindow`) |
| `GUI.color` (tint) | `GUILayout.Space` |
| `Texture2D.whiteTexture` | `GUI.DrawTexture`, `Graphics.DrawTexture` |
| `GUIStyle` background (`normal.background`) | `GUI.Label`/`Box` taking a `Texture` |
| `UnityWebRequestTexture` | `Texture2D(int,int)` ctor |
| `Object.FindObjectOfType` | `RectOffset(int,int,int,int)` ctor |
| | `Resources.FindObjectsOfTypeAll` |

Consequences in the code:

- **Custom text input**: keystrokes are read via `Event.current` and
  the text rendered in a `GUI.Label` (see `ChorusUI.HandleTextInput`).
- **No draggable window**: fixed, centered panel.
- **Manual scrolling**: offset driven by the mouse wheel + `GUI.BeginGroup`
  for clipping.
- **Images via `GUIStyle` background**: the texture is assigned to
  `normal.background` then drawn by an empty `GUI.Box`. It's the only
  surviving image-rendering path (see `Theme.DrawImage`, which tests
  six methods and keeps whichever one works).
- **No texture creation**: album art comes from `UnityWebRequestTexture`,
  which builds it natively.

`Theme.DrawImage` logs the chosen mode on first display. On a different
game version, availability may vary: the code tests and falls back
automatically, so at worst a cosmetic feature disappears with a warning
in the log, without crashing.

### Input blocking

Three approaches were tried before the current one:

1. `SetActive(false)` on the *Rewired Input Manager* → deinitializes
   Rewired **permanently**. Avoid.
2. `enabled = false` on its components → same consequence.
3. `ReInput.controllers.Keyboard.enabled = false` → applies correctly
   (verified by reading the flag back) but isn't enough: the game reads
   some keys through Unity's legacy `Input`.

The chosen solution is a **Harmony prefix** on `Input.GetKey*`, which
returns "key not pressed" while the overlay is open. No state is
modified, and the patch removes itself cleanly.

### enchor.us API

Endpoints verified by network capture then tested:

- `POST /search` — body
  `{search, page, instrument, difficulty, drumType, drumsReviewed, source}`.
  `difficulty` accepts `null`, `easy`, `medium`, `hard`, `expert`.
- `POST /search/advanced` — text fields as
  `{value, exact, exclude}` (`name`, `artist`, `album`, `genre`, `year`,
  `charter`), `min*`/`max*` bounds, and 11 tri-state booleans
  (`null` = don't care, `true` = require, `false` = exclude).
  Response: `{found, out_of, page, data, search_time_ms}` — `found`
  gives the total, absent from the simple endpoint.
  **Doesn't support free-text search**: a `search` key is ignored there.
- Files: `https://files.enchor.us/{md5}.sng` for charts,
  `{albumArtMd5}.jpg` for album art.
