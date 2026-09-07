# Chorus Mod Installer

A classic Windows `Setup.exe`, **fully self-contained**: pick your
Clone Hero folder, choose options (rescan, album art, keyboard
blocking, panel size), install BepInEx + the plugin, automatic
uninstaller.

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
into `installer\bepinex\`.

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
4. Next → Install (fast, everything is already inside the Setup.exe)
5. Optional: launch the game directly from the last page

First game launch takes longer than usual (BepInEx generates its
interop files). After that, **F9** in-game opens Chorus Mod.

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

## Known limitations

- **Windows only.** Inno Setup doesn't produce a Linux installer. For
  Linux, the manual procedure in the plugin's main README remains the
  reference.
