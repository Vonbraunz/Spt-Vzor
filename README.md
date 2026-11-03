# Vzor

A BepInEx 6 plugin for the **IL2CPP** build of SPT 5.0 (EFT 1.2.0.47965, Unity 2022.3.43f2). It paints players by faction, marks loot, quest zones, extracts and grenades, and includes a handful of quality-of-life patches.

Offline single-player only. There is no anti-cheat bypass, no injector, and nothing here is designed for live EFT.

Config-driven. Two keys exist: `Insert` opens the in-game menu, and `Aim Key` (LeftControl by default) is the aimbot trigger. Everything can also be edited in `BepInEx\config\local.vzor.cfg`; changes apply about a second after saving.

## Features

### Chams

- Material swap on player body skins, restored on toggle and on despawn.
- Faction colours: USEC, BEAR, scav, boss, cultist/raider (followers included).
- Per-type materials with separate visible and occluded alpha.
- Six shaders from `chams.bundle`: solid, wireframe, forcefield, glass, lava, outline.
- Optional gear painting (helmets, rigs, masks), off by default.
- Optional occlusion check: a raycast decides whether a body gets painted at all.

### Loot ESP

- Screen-space dots with labels, straight through geometry.
- Category colours and per-category toggles: weapons, ammo, medical, food, keys, gear, barter, mods, quest, wishlist, other.
- Prices from the DrakiaXYZ flea database, falling back to the item template's `CreditsPrice`.
- `Min Value` filter and `High Value Only` mode.
- **Distance rules:** normal loot, containers and corpses are limited to `Loot Distance` (10 m default). High value, quest and wishlist items ignore the limit entirely.
- Frustum check and optional line-of-sight check.
- Container contents: item count and total value, read within 10 m, refreshed every 10 s.
- Quest progress appended to quest item labels, e.g. `[3/5]`, from your active quest counters.
- Label stacking so overlapping text steps upward instead of piling up.
- Extracts with open/closed colouring, always labelled.
- Quest zones: place item, place beacon, stay in zone, visit a place.
- Live grenades, tracked every frame, drawn as a large red dot.

### In-game menu

`Insert` opens a generic config menu. It enumerates every entry in the config file and renders a widget by type (toggle for bools, sliders for ranged numbers, text fields for strings), so new settings appear without any menu code. The cursor is released while it is open and restored on close. The window is draggable by its title bar.

### Web map

A live map of the current raid in your browser, served by the plugin on loopback.

- Open `http://127.0.0.1:6970/` while in a raid. Off by default, enable with `[Web Map] Enabled`.
- Shows your position and heading, plus every alive body colour coded by faction.
- Map art for every playable location, with the floor picked from your height.
- Point and click calibration: stand somewhere, press `P`, then click the art where you are. Three or more spread out points solve rotation, scale, offset and mirroring, and the residual is shown in pixels. Each map's fit is saved.
- Sliders, `shift`+drag and `shift`+wheel adjust the art by hand afterwards. Only the art moves, the dots are truth.
- The page and its assets live in `BepInEx\plugins\Vzor\web`, so they can be edited and reloaded without restarting the game.

### Aimbot

- Aim only, no triggerbot. Hold `Aim Key` (LeftControl by default).
- Nearest target inside the FOV cone, distance limited, optional line of sight.
- Targets the head bone when available, neck otherwise.
- Configurable smoothing in degrees per second.

### Magic bullet

- Hooks `Player.FirearmController.InitiateShot` and redirects your shots to the closest on-screen head, travelling downward into the body.
- Ignores where the gun is pointed and ignores walls. Separate toggle from the aimbot.

### QoL

| Setting | What it does | Notes |
|---|---|---|
| Infinite Stamina | Lets stamina drain normally, then restores it | Endurance XP still awarded |
| No Recoil | Zeroes recoil curve multipliers and skips the camera kick | May need a weapon swap on the first weapon of a raid |
| No Sway | Zeroes the breath effector's intensity | ADS still works |
| No Malfunction | Clears jam, misfire, slide, feed and overheat flags on the weapon template | Applies per weapon type |
| Instant Search | Marks contents as known and refreshes the container view | Ported from asler's SPT-LootInstantSearch, see credits |

## Requirements

- SPT 5.0 with its bundled BepInEx 6 IL2CPP. Nothing extra to install.
- `Segoe UI` on Windows for Cyrillic nicknames. Falls back to ASCII-only labels if unavailable.

## Install

Copy the whole folder to `BepInEx\plugins\Vzor`, so you end up with:

```
BepInEx\plugins\Vzor\
  Vzor.dll
  chams.bundle
  prices-regular.json     (optional, but without it prices come from templates)
  prices-pve.json         (optional alternative table)
```

Launch SPT normally. The plugin logs `Vzor 0.5.0 loaded. config only, no hotkeys.`

## Configuration

Everything lives in `BepInEx\config\local.vzor.cfg`. The plugin watches the file and reloads on change, roughly one second after saving. There are only two keys: `Insert` for the menu and the aimbot hold key. Function keys are left alone because they collide with too much.

Sections:

- `[Chams]` colours, shader, alpha, gear, occlusion.
- `[Loot ESP]` distances, categories, colours, filters, exfils, zones, grenades.
- `[QoL]` stamina, recoil, sway, malfunction, instant search.
- `[Aimbot]` enabled, aim key, FOV, distance, smoothing, magic bullet.
- `[Menu]` the in-game menu toggle key.
- `[Web Map]` enabled, port and refresh rate for the browser map.

One trap worth knowing: if you edit the file while the game is running and then press a key that saves config, the save can overwrite your edit. There are no config-saving hotkeys any more, so in practice: edit, save, done.

## Build

Requires the .NET 10 SDK, since your install's BepInEx targets `net10.0`.

```
cd _src\Vzor
dotnet build -t:Deploy
```

`-t:Deploy` builds and copies `Vzor.dll` plus `chams.bundle` into `BepInEx\plugins\Vzor`. Change `GameDir` in `Vzor.csproj` if the install moves.

Plain `dotnet build` compiles to `dist` without deploying.

## Known issues

- **No recoil after spawn.** The first weapon of a raid may keep its recoil until you swap weapons. Two recoil pipelines exist and the fix for the other one made things worse, so it is parked. Swap once and it behaves.
- **Instant search is confirmed working on SPT 5.0.** The upstream mod also patches `ContainsUnknownItems`, but that method shares its native body with hundreds of others and detouring it crashes the game at boot. It turns out the three safe patches are sufficient, so that one is deliberately omitted.
- **Prices are a snapshot.** The flea table comes from a community project and will drift. Missing entries fall back to the item template price, which is lower.
- **Container contents counts** rely on `ItemExtensions.GetAllItems`. Nested grids are usually included, but treat the totals as approximate until you have compared a few against what you actually find.
- **Fonts.** Cyrillic renders through Segoe UI. CJK nicknames may come out as blanks.

## Licences

Two licences, because this repo contains two different kinds of thing:

- **Code:** MIT. See `LICENSE`.
- **`chams.bundle`:** CC BY-NC-SA 4.0. Non-commercial, share alike, attribution required. See `LICENSE-ASSETS`.
- **Flea price data** (`prices-regular.json`, `prices-pve.json`): from **[SPT-LiveFleaPriceDB](https://github.com/DrakiaXYZ/SPT-LiveFleaPriceDB)** by DrakiaXYZ, MIT.
- **Instant search patches:** ported from **[asler/spt-loot-instant-search](https://github.com/asler/spt-loot-instant-search)**, MIT.

The bundle is loaded from disk at runtime and is not compiled into the dll, which is why the code can be MIT while the bundle stays under the share-alike licence.

See `CREDITS.md` for the full notices.

## Disclaimer

This is a mod for an offline, single-player SPT install. It contains no BattlEye bypass, no hardware ID spoofer, no launcher injector and no module hiding. Do not point it at live EFT.
