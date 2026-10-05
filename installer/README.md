# Chorus Mod Installer

A classic Windows `Setup.exe`, **fully self-contained**: pick your
Clone Hero folder, choose options (rescan, album art, keyboard
blocking, panel size, trophy notifications), install BepInEx + the
plugin, automatic uninstaller.

**No internet connection required on the user's side**: BepInEx is
bundled directly inside the Setup.exe, not downloaded during install.
Zero risk of dead links.

## What you need to BUILD the installer (once)

- .NET 6 SDK (to compile `ChorusMod.dll`)
- [Inno Setup 6](https://jrsoftware.org/isinfo.php) (free)
- BepInEx 6.0.0-be.755 (win-x64), already unzipped

## What the PERSON INSTALLING needs

Nothing. No .NET SDK, no BepInEx, no internet connection, no technical
knowledge.

---

## Building the installer

### 1. Build the plugin as usual

From the repo root:

```bash
dotnet build -c Release
```

### 2. Gather the three pieces next to the installer script

```
installer\
  ChorusModSetup.iss
  config-template.cfg
  ChorusMod.dll          <- copied from ..\bin\Release\net6.0\
  bepinex\                <- unzipped BepInEx (see below)
    winhttp.dll
    doorstop_config.ini
    .doorstop_version
    changelog.txt
    BepInEx\core\...
    dotnet\...
```

For the `bepinex\` folder: download
`BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.755+*.zip` from
<https://builds.bepinex.dev/projects/bepinex_be> and unzip it as-is
into `installer\bepinex\`. Alternatively, copy the same files from a
game folder where be.755 is already installed (`winhttp.dll`,
`doorstop_config.ini`, `.doorstop_version`, `changelog.txt`,
`BepInEx\core\`, `dotnet\`) -- never that game's `BepInEx\config`,
`plugins` or `interop`, which are machine-specific.

> `ChorusMod.dll`, `bepinex\`, and `Output\` aren't tracked by git (see
> `.gitignore`): they're build artifacts, not source code. Everyone
> regenerates them locally.

> This folder contains the .NET runtime bundled by BepInEx (the
> `dotnet\` folder, ~67 MB) — that's expected and intentional, it's what
> lets the final installer avoid downloading anything.

### 3. Build the installer

Open `ChorusModSetup.iss` in the Inno Setup IDE, then **Build → Compile**
(`Ctrl+F9`). The result comes out at `Output\ChorusMod-Setup.exe`
(expect somewhere around 25-35 MB, the bundled .NET runtime compresses
well).

This file, and only this file, is what you distribute or keep around
for reinstalling on a new PC.

---

## Usage (for the person installing)

1. Double-click `ChorusMod-Setup.exe`
2. Choose the Clone Hero folder (pre-filled if auto-detected, otherwise
   Browse — the one containing `Clone Hero.exe`)
3. Check the options you want
4. **Trophies & notifications** page: keep trophy notifications on,
   check the Discord name (pre-filled from Rythmania Tracker's
   `%APPDATA%\Rythmania Tracker\player.json` when it's installed; leave
   empty to let the mod detect it at each launch), and pick which toasts
   to show (trophy / record beaten / level up / duel challenge / announcements)
5. Next → Install (fast, everything is already inside the Setup.exe)
6. Optional: launch the game directly from the last page

First game launch takes longer than usual (BepInEx generates its
interop files). After that, **F9** in-game opens Chorus Mod, and the
main menu shows the player card with its **SETTINGS** button.

## Uninstalling

Windows Control Panel → *Apps* → *Chorus Mod* → Uninstall. BepInEx and
plugin files are tracked natively by Inno (declared in `[Files]`) so
they're removed automatically; the config file generated at install
time (`fr.lucas.chorus-mod.cfg`, whose content depends on the choices
made) is removed via a dedicated `[UninstallDelete]` entry since it
doesn't exist at compile time. The `Songs` folder is never touched.

## Updating the bundled BepInEx version

Replace the contents of `bepinex\` with the newly unzipped version,
adjust `#define BepInExVersion` at the top of the `.iss` (purely
informational, shown nowhere except in comments), and recompile. Since
everything is bundled, there's no URL to keep up to date.

⚠️ Bleeding-edge BepInEx builds regularly break compatibility with each
other (learned the hard way during development — see the plugin
README's technical notes). Don't update without fully retesting the
plugin.

## Trophy notification topic

The Trophies page asks for the ntfy topic the trophy site publishes
to. It is **not** shipped in this repo or in public releases: anyone
who knows it can read every player's events and publish fake ones.
Players get it from the trophy site and type it in the installer (or
later as `NtfyTopic` in the `.cfg`). For a private build only, it can
be pre-filled through `#define NtfyTopic` at the top of
`ChorusModSetup.iss`.

## Known limitations

- **Windows only.** Inno Setup doesn't produce a Linux installer. For
  Linux, the manual procedure in the plugin's main README remains the
  reference.
