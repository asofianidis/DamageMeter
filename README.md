# Nevaris Damage Meter (BepInEx 6 / IL2CPP)

Client-side damage meter with an in-game Mod Configuration Menu page, built against the supplied IL2CPP dump. It patches
FishNet's generated `Damage` deserializer, so outgoing hits do not depend on the
local player first receiving damage or on visible damage-number settings. The
display uses an ordinary Unity canvas and does not inject a metadata-stub
`MonoBehaviour` into IL2CPP.

## Build

From this directory:

```powershell
dotnet build -c Release
```

The DLL is written to `bin/Release/net6.0/NevarisDamageMeter.dll`.

## Install

1. Install the IL2CPP build of BepInEx 6 and launch the game once.
2. Copy only `NevarisDamageMeter.dll` to `<game>\BepInEx\plugins\DamageMeter\`.
3. Install Mod Configuration Menu. Damage Meter does not require a specific MCM version.
4. Start the game. Press **F8** to toggle and **F9** to reset by default. Drag the
   meter's title bar to move it, or drag its bottom-right grip to resize it.
   Position and size are saved automatically.

Click any player row to open that player's skill breakdown. Damage is grouped by
the player, each named summon type, and clones, with per-skill damage, hit count,
and share of that player's total. Use the mouse wheel when the breakdown is
longer than the panel, then click **BACK** or right-click the panel to return to
the party summary.

Open **ESC → Settings → MODS → Damage Meter** to change visibility, summon attribution, encounter timing, row count, scale, and hotkeys. Hotkeys support combinations: hold the desired keys, press the main key last, then release. The page also provides actions for resetting the current encounter and restoring the default meter layout.

Damage Meter accepts any installed MCM plugin version. Multi-key hotkey editing is enabled when that MCM build exposes chord controls. With older builds, the shortcuts remain active and the rest of the Damage Meter page remains available, but chord editing is omitted from the page.

For local updates, use `Install-DamageMeter.ps1`. Before replacing the installed DLL, it archives the prior version and current configuration under `<game>\BepInEx\mod-backups\DamageMeter\` with timestamps.

Configuration is generated at
`<game>\BepInEx\config\alex.nevaris.damagemeter.cfg`.

The plugin does not create or render its UI, process hotkeys, or collect damage
at the login/character-select screens. It waits until the owned player is
network-spawned, active, and reports that its character is loaded. The canvas is
destroyed again when that player leaves the world.

The meter reuses the live game's panel sprite, tint, and TextMesh Pro font. It
automatically hides while a full-screen menu, popup, loading screen, chat input,
party/friends/guild interface, or context menu has focus.
Its aspect ratio remains fixed while resizing so text and columns do not clip,
and it automatically scales down further when necessary to fit a smaller screen.

No component or GameObject is injected during application startup. There are no
network lifecycle hooks. The world gate begins only from the existing update of
a `PlayerController` that is already FishNet-spawned and locally owned.

Damage is scoped strictly to the local player and members of that player's
current party. Non-party players and unrelated world entities are discarded
before names or meter rows are read. Summons and clones are resolved to their
owning player and are included only when that owner is local or in the party.
All plugin-authored log messages begin with `[DamageMeter]`.

The project compiles against the interop assemblies and Mod Configuration Menu DLL installed in the SpiritVale folder. Its default game path is
`F:\SteamLibrary\steamapps\common\SpiritVale`; override the `GameDir` MSBuild
property if the game moves:

```powershell
dotnet build -c Release -p:GameDir="D:\Games\SpiritVale"
```

## Scope

The meter sees units included in the local client's observer set. It cannot
measure damage that the server never sends to that client. Summon damage is
attributed to the summoning player when ownership can be resolved.
