# GrannyCoop-Setup

The one-click installer players download from the release page. One `WinExe`, no DLLs
next to it: MelonLoader 0.7.3, the .NET 6 runtime installer and `GrannyCoop.dll` are
embedded as resources.

## Build

```powershell
# mod is compiled against the MelonLoader assemblies of a test install
$env:GRANNY_TEST = 'C:\Users\Public\granny-test'
.\tools\build-setup.ps1
# -> dist\GrannyCoop-Setup-1.0.0.exe (46 MB), with size and SHA-256 printed
```

`build-setup.ps1` downloads the two payloads if `payload/` is empty, checks size and
SHA-256 against pinned values, builds the mod, then builds this project. `payload/` and
`dist/` are git-ignored.

Pinned payloads:

| File | Bytes | SHA-256 |
|---|---|---|
| `MelonLoader.x64.zip` (v0.7.3, LavaGang) | 20,155,622 | `5b2b2f3d1cd42b59ec886c5bdc2663edae87a0097a4f4a8f58c0965a99dda416` |
| `dotnet-runtime-6.0.36-win-x64.exe` (Microsoft) | 28,101,408 | `6bdad7bc4c41fe93d4ae7b0312b1d017cfe369d28e7e2e421f5b675f9feefe84` |

## Why net48 and x64

- **net48**: every Windows 10/11 install already has it. Shipping a self-contained .NET 8
  installer would double the download for no gain.
- **x64**: a 32-bit process sees `C:\Program Files (x86)` as `Program Files` and would
  look for the .NET 6 runtime in the wrong place. `Prefer32Bit` is off for the same reason.

The project uses `Microsoft.NETFramework.ReferenceAssemblies`, so it builds with the .NET
SDK alone — no Visual Studio required.

## What it does, in order

1. `SteamLocator` — uninstall key `Steam App 962400`, then `libraryfolders.vdf`, then the
   default Steam folder. The GUI's «Обзор…» covers everything else.
2. `BuildCheck` — reads the magic and version words from
   `Granny_Data/il2cpp_data/Metadata/global-metadata.dat`. Anything but `0xFAB11BAF` /
   version 31 is refused unless `--force`.
3. Refuses while `Granny.exe` is running.
4. `Runtime.Ensure` — finds a `6.*` folder under `shared\Microsoft.NETCore.App`; if there
   is none, runs the embedded runtime installer with `/quiet /norestart` (UAC: only this
   step elevates).
5. `MelonLoaderInstall` — skips when `version.dll` is 0.7.3 and `MelonLoader/net6` exists.
   Otherwise renames `version.dll` → `version.dll.bak-<stamp>` and `MelonLoader/` →
   `MelonLoader.bak-<stamp>`, then extracts the zip entry by entry (zip-slip guarded).
6. `ModInstall` — `Mods/GrannyCoop.dll`, byte-compared; the old file is kept as `.bak`.
7. `ConfigWriter` — merges only the `[GrannyCoop]` section of
   `UserData/MelonPreferences.cfg`; every other mod's section is left alone.
8. Verifies the four files it is responsible for and prints them.

It never patches `Granny.exe`, game data or saves. `UnityCrashHandler64.exe`, `version.dll`
and the loader folder are the only things it touches.

## CLI

```
GrannyCoop-Setup-1.0.0.exe --install <dir> [--role off|host|join] [--name N]
                           [--address IP] [--port P] [--skip-runtime] [--force] [--silent]
```

Exit codes: `0` ok, `1` usage, `2` not a Granny folder, `3` untested build, `4` game
running, `5` write failed, `6` runtime missing. The log goes to
`%TEMP%\grannycoop-setup-yyyyMMdd-HHmmss.log` in both modes.

## Testing

Tested on a fresh copy of the game (`granny-test3`):

- clean install: 164 MelonLoader files, mod, cfg, all checks `ok`, exit 0;
- re-run: MelonLoader skipped, mod replaced via `.bak`, cfg rewritten;
- GUI screenshot at 676×659, auto-detected the Steam install;
- first game launch: `COOP initialised v0.8.0 role=Host`, `COOP net listening on
  udp/47790`, `COOP scene -> 'SplashScreen'`.

The 0.8.1 rebuild (2026-10-24) was installed over the same copy with
`--install <dir> --role off --silent`: exit 0, and `Mods/GrannyCoop.dll` came out
byte-identical to `bin/Release/GrannyCoop.dll` (SHA-256 `432bb7f6…`). One launch showed
`COOP initialised v0.8.1 role=Off`. A 0.8.1 host+guest pair was smoke-tested separately
(`role=Host` and `role=Join`, 60 s, no errors).

The runtime install path was triggered twice on a machine that already had 6.0.36 (the
detection bug is fixed); it has **not** been run on a clean Windows without .NET 6, and
the UAC prompt has **not** been seen — the test shell had the runtime present.

MelonLoader's first launch generates the Il2Cpp assemblies and downloads Cpp2IL, so that
first run needs internet. Those assemblies come from the game's own metadata and are not
shipped.
