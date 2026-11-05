# Vzor

A BepInEx 6 plugin for the **IL2CPP** build of SPT 5.0 (EFT 1.2.0.47965, Unity 2022.3.43f2). It paints players by faction, marks loot, quest items, quest zones, extracts, transits and bodies, and serves a live map of the raid to your browser. Plus a handful of quality-of-life patches.

Offline single-player only. There is no anti-cheat bypass, no injector, and nothing here is designed for live EFT.

Config-driven. Three keys exist: `Insert` opens the in-game menu, `Aim Key` (LeftControl by default) is the aimbot trigger, and `Unlock Key` (numpad `.` by default) opens locked doors around you. Everything can also be edited in `BepInEx\config\local.vzor.cfg`; changes apply about a second after saving.

## Features

### Chams

- **Outline mode by default.** The body keeps its own materials and a second skinned copy of the mesh carries the faction colour, inflated by `Outline Scale` so it reads as a rim. The body renders exactly as the game made it.
- Faction colours resolved from the spawn role first, so USEC, BEAR, scav, boss and cultist/raider are correct even for AI PMCs, whose profile side is always Savage.
- `Keep Texture` tints a copy of the game's own materials instead, for anyone who prefers a colour cast over a rim. Off switches to the flat chams shader.
- Six shaders from `chams.bundle`: solid, wireframe, forcefield, glass, lava, outline.
- Optional gear painting (helmets, rigs, masks), off by default.
- Optional occlusion check: a raycast decides whether a body gets painted at all.

### Loot ESP

- Screen-space dots with labels, straight through geometry.
- Category colours and per-category toggles: weapons, ammo, medical, food, keys, gear, barter, mods, quest, wishlist, other.
- Prices from the DrakiaXYZ flea database, falling back to the item template's `CreditsPrice`.
- `Min Value` filter and `High Value Only` mode.
- **Distance rules:** normal loot, containers and corpses are limited to `Loot Distance` (10 m default). Quest items get `Quest Distance` (300 m default, matching zones). High value and wishlist items are unlimited.
- Frustum check and optional line-of-sight check.
- Container contents: item count and total value, read within 10 m, refreshed every 10 s.
- Quest progress appended to quest item labels, e.g. `[3/5]`. The task counters this reads are unreliable in this build, so treat the numbers as advisory.
- Label stacking so overlapping text steps upward instead of piling up.
- Extracts with open/closed colouring, always labelled.
- Quest zones: place item, place beacon, stay in zone, visit a place.
- Live grenades, tracked every frame, drawn as a large red dot.

### In-game menu

`Insert` opens a generic config menu. It enumerates every entry in the config file and renders a widget by type (toggle for bools, sliders for ranged numbers, text fields for strings), so new settings appear without any menu code. The cursor is released while it is open and restored on close. The window is draggable by its title bar.

### Web map

A live map of the current raid in your browser, served by the plugin on loopback. Off by default, enable with `[Web Map] Enabled`, then open `http://127.0.0.1:6970/`.

- Your position and heading, every alive body colour coded by faction, with name and distance, and a chevron when they are above or below you.
- Bodies that stop existing (killed and removed, or extracted) leave a white X where they were last seen.
- **Raid found** banner: a pulsing "Raid Found, Loading..." across the map while a raid exists but no layer has reached the canvas yet, with the location id underneath. It clears the moment the art draws.
- Extracts as diamonds, filled when open. Transits as squares, labelled with the destination.
- Map art for every playable location. Floors are picked from building footprints first and from height when exactly one layer claims it, stacked over the ground map with the base dimmed so the floor you are on stands out.
- Place names from the map config, drawn behind the live data.
- **Loot table**: every loose item in the raid with name, price and distance, filterable by category and searchable by name, sorted by price, name or distance. Click a row to mark it on the map. Refreshes every 30 seconds while the raid lasts.
- **Favourites**: star an item in the loot table and every copy of it stays on the map as a gold diamond, loose or inside a container. Containers holding one get a gold ring with the item name beside it, because a safe with a LEDX in it is worth more than the LEDX-shaped dot it would otherwise be. Favourites are by name, so they follow you between raids.
- **Quest markers**: quest items on the floor, containers holding them, and quest zones, each behind a toggle.
- **Quest panel**: active quests for this map only, with the items you are still short of.
- **Contacts panel**: players, bosses, followers, raiders, rogues and cultists, sorted by distance, click to centre.
- Point and click calibration: stand somewhere, press `P`, then click the art where you are. Three or more spread out points solve an anchored fit first (rotation comes from the map config, so the art stretches between your pins without warping), then fall back to independent axis scaling, then to rotation plus uniform scale. The residual and the model used are shown in pixels, and fits that would shear the art are rejected. Each map's fit is saved in browser storage as well as on disk.
- `follow` keeps the view on you. Dragging releases it.
- The page and its assets live in `BepInEx\plugins\Vzor\web`, so they can be edited and reloaded without restarting the game: the plugin serves `/version` and the page reloads itself when `app.js` changes.

### Aimbot

- Aim only, no triggerbot. Hold `Aim Key` (LeftControl by default).
- Nearest target inside the FOV cone, distance limited, optional line of sight.
- Targets the head bone when available, neck otherwise.
- Configurable smoothing in degrees per second.
- Magic bullet uses the same FOV pick by default, with an optional nearest-by-distance ranking.

### Magic bullet

- Hooks `Player.FirearmController.InitiateShot` and redirects your shots to the chosen head, travelling downward into the body.
- Ignores where the gun is pointed and ignores walls. Separate toggle from the aimbot.

### QoL

| Setting | What it does | Notes |
|---|---|---|
| Infinite Stamina | Lets stamina drain normally, then restores it | Endurance XP still awarded |
| No Recoil | Zeroes recoil curve multipliers and skips the camera kick | May need a weapon swap on the first weapon of a raid |
| No Sway | Zeroes the breath effector's intensity | ADS still works |
| No Malfunction | Clears jam, misfire, slide, feed and overheat flags on the weapon template | Applies per weapon type |
| Instant Search | Marks contents as known and refreshes the container view | Ported from asler's SPT-LootInstantSearch, see credits |
| Dad Gamer Mode | Head and thorax stop at 1 HP, so neither can black out and kill you | Chest and head hits still hurt, stagger and bleed; other parts break normally |

### Doors

| Setting | What it does | Notes |
|---|---|---|
| Unlock Key | Unlocks every locked door within `Unlock Radius` | Numpad `.` by default |
| Unlock Radius | Range in metres | 15 covers a dorm floor, 50 covers most of a building |
| Open Doors | Pushes them open after unlocking | Off leaves them unlocked but shut |
| Debug Logging | Logs each door with its key and state before and after | Use this when a door refuses to move |

The key requirement on a door is a single `KeyId` string, so the unlocker blanks it and then walks the state machine out of `Locked`. Sealed doors are skipped because sealing is event state, not loot. Keycard doors and sliding doors inherit from the same class, so they are covered by the same pass. Extraction doors are a separate type and are never touched.

This makes keys decorative while the radius covers the doors you care about. Your key stashes, marked room keys and keycards still work normally, they just stop being necessary.

## Requirements

- SPT 5.0 with its bundled BepInEx 6 IL2CPP. Nothing extra to install.
- `Segoe UI` on Windows for Cyrillic nicknames. Falls back to ASCII-only labels if unavailable.
- **Windows Smart App Control must be off.** It refuses unsigned DLLs that have no reputation, which means every rebuild is blocked with `An Application Control policy has blocked this` and `Vzor.dll` never loads. There is no exception list and turning it off is one-way.

## Install

Copy the whole folder to `BepInEx\plugins\Vzor`, so you end up with:

```
BepInEx\plugins\Vzor\
  Vzor.dll
  chams.bundle
  prices-regular.json     (optional, but without it prices come from templates)
  prices-pve.json         (optional alternative table)
  web\                    (map page, art and calibration)
```

Launch SPT normally. The plugin logs `Vzor 0.6.0 loaded. config only, no hotkeys.`

## Configuration

Everything lives in `BepInEx\config\local.vzor.cfg`. The plugin watches the file and reloads on change, roughly one second after saving. There are only two keys: `Insert` for the menu and the aimbot hold key. Function keys are left alone because they collide with too much.

Sections:

- `[Chams]` colours, shader, outline mode and scale, alpha, gear, occlusion.
- `[Loot ESP]` distances (including `Quest Distance`), categories, colours, filters, exfils, zones, grenades.
- `[QoL]` stamina, recoil, sway, malfunction, instant search.
- `[Aimbot]` enabled, aim key, FOV, distance, smoothing, magic bullet and its target ranking.
- `[Menu]` the in-game menu toggle key.
- `[Web Map]` enabled, port and refresh rate for the browser map.

One trap worth knowing: if you edit the file while the game is running and then press a key that saves config, the save can overwrite your edit. There are no config-saving hotkeys any more, so in practice: edit, save, done.

## Build

Requires the .NET 10 SDK, since your install's BepInEx targets `net10.0`.

```
cd _src\Vzor
dotnet build -t:Deploy
```

`-t:Deploy` builds and copies `Vzor.dll`, `chams.bundle`, the price files and the whole `web` folder into `BepInEx\plugins\Vzor`. Change `GameDir` in `Vzor.csproj` if the install moves.

Plain `dotnet build` compiles to `dist` without deploying.

The project references `Diz.Binding` from `BepInEx\interop`, because quest conditions live in a collection derived from `UpdatableBindableList`. If you build on another machine, that assembly has to exist there.

## Known issues

- **No recoil after spawn.** The first weapon of a raid may keep its recoil until you swap weapons. Two recoil pipelines exist and the fix for the other one made things worse, so it is parked. Swap once and it behaves.
- **Quest counters are unreliable.** The `[3/5]` labels and the shortfall logic read different sources; the labels read the game's counters, which do not update properly in this build. The web map's quest panel counts your actual inventory instead, which does work.
- **Instant search is confirmed working on SPT 5.0.** The upstream mod also patches `ContainsUnknownItems`, but that method shares its native body with hundreds of others and detouring it crashes the game at boot. It turns out the three safe patches are sufficient, so that one is deliberately omitted.
- **Prices are a snapshot.** The flea table comes from a community project and will drift. Missing entries fall back to the item template price, which is lower.
- **Container contents counts** rely on `ItemExtensions.GetAllItems`. Nested grids are usually included, but treat the totals as approximate until you have compared a few against what you actually find.
- **Body X marks include extractions.** A player who extracts also looks like "stopped existing", so you may get a mark at an exfil.
- **Floor selection depends on their data.** Floors are chosen from the map's own bounding boxes first, then from height when exactly one layer claims it. Ambiguous heights still keep the last floor rather than guessing.
- **Fonts.** Cyrillic renders through Segoe UI. CJK nicknames may come out as blanks.

## Licences

Two licences, because this repo contains two different kinds of thing:

- **Code:** MIT. See `LICENSE`.
- **`chams.bundle`:** CC BY-NC-SA 4.0. Non-commercial, share alike, attribution required. See `LICENSE-ASSETS`.
- **Map art** under `web\maps` comes from the DynamicMaps data set (TarkovDev, TarkovData, Shebuka art and the zzap rework edits), CC BY-NC-SA 4.0. Personal use only here.
- **Flea price data** (`prices-regular.json`, `prices-pve.json`): from **[SPT-LiveFleaPriceDB](https://github.com/DrakiaXYZ/SPT-LiveFleaPriceDB)** by DrakiaXYZ, MIT.
- **Instant search patches:** ported from **[asler/spt-loot-instant-search](https://github.com/asler/spt-loot-instant-search)**, MIT.

The bundle is loaded from disk at runtime and is not compiled into the dll, which is why the code can be MIT while the bundle stays under the share-alike licence.

See `CREDITS.md` for the full notices.

## Disclaimer

This is a mod for an offline, single-player SPT install. It contains no BattlEye bypass, no hardware ID spoofer, no launcher injector and no module hiding. Do not point it at live EFT.
