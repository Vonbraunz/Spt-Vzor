# Changelog

## 0.7.12

- **The blast pool is clamped too.** `ApplyDamage` now trims damage to `Common` as well as `Head` and `Chest`. `Common` is where explosion and fall damage lands, which is why a claymore still killed you while both vitals sat at 1. The heal sweep already pinned it; the hit path did not.
- **`Kill` is refused.** Some things do not damage you, they just declare you dead, and that call walks past both other guards. A prefix on `ActiveHealthController.Kill` refuses it for the local player, remembers the `EDamageType`, and pins the vitals back to 1. Logged as `[dad] refused kill (<type>)`, rate limited to one line every 5 seconds.
- **Locality says what it thinks.** `[dad] watching controller, player=you|not you|null` once per raid, plus a single `[dad] local check hit/missed` line the first time each answer comes up. If dad mode looks asleep again, those two lines say whether the check failed or the damage never reached `ApplyDamage` at all.
- **The locality check got a fallback.** `IsYourPlayer`, and if that flag is not set, a non-AI controller (offline has exactly one human in the raid).

## 0.7.11

- **The `IsAlive` guard is gone and patch registration is isolated.** Patching that setter through `ActiveHealthController` fails, because it is declared on the generic `BaseHealthController<T>`, and the throw landed in the catch shared by every registration. Every hook after it was skipped, including the world tracker, which is why Vzor looked completely absent: no ESP, no chams, no map. Dad mode registers in its own try/catch now, and the shared catch no longer claims to be about stamina.
- **The destroy guard also refuses `Common`**, which is where blast and fall deaths land.

## 0.7.10

- **Dad mode blocks body part destruction.** Ported the hook from the original DadGamerMode mod: a destroyed head or chest is the death event, so it is refused and the part healed back to 1. Limbs still black out normally.
- **Locality uses the controller's own player.** `ActiveHealthController.Player.IsYourPlayer` instead of comparing pointers against `MainPlayer`.

## 0.7.9

- **Dad Gamer Mode is local only.** The clamp never checked whose health controller it was looking at, and offline SPT simulates bots with the same `ActiveHealthController`, so every bot's head and chest were floored at 1 HP too. That is also why the magic bullet stopped killing things. Every patch now compares the controller against the local player's.
- **Bleeds and limb damage are untouched.** An earlier attempt skipped distributed damage entirely, which stopped bleeds from hurting at all. That is reverted: damage over time still drains you and still blacks out arms and legs. Only the flip to dead is swallowed, by a prefix on the `IsAlive` setter, and only for the local player.
- **Both death paths are logged.** `[dad] clamped <part> from X to 1.0` for the damage clamp and `[dad] blocked death` for the death guard, plus `[dad] died: <EDamageType>` if something still gets through. If a death line appears without a matching clamp, the log names the damage type that caused it.

## 0.7.8

- **Dad Gamer Mode also protects `Common`.** That is the pool explosions, falls and scripted damage go through; leaving it out is how you can still be killed by a grenade with both vitals sitting at 1 HP. Head, thorax and the common pool are now all floored at 1 and healed back if something gets past the clamp.
- **Every clamp is logged.** `[dad] clamped Chest from 84.0 to 1.0 (current 2.0)` and similar. It is rare, and it turns the next "something killed me anyway" into a concrete answer: if you die with no clamp line for that part, the death did not come through `ApplyDamage`.

## 0.7.7

- **Dad Gamer Mode.** A new `QoL` toggle: the head and the thorax cannot be blacked out. Damage that would take either part below 1 HP is trimmed so it lands on exactly 1, so the hit still staggers and bleeds you but cannot kill you through the two death parts. Everything else still breaks normally. A per-frame sweep heals either part back to 1 if something gets past the damage clamp (a bleed tick, distributed damage, a wound you loaded in with), because that cannot resurrect you if death already fired in the same frame.
- The patch sits on `ActiveHealthController.ApplyDamage`, which 5.0 still exposes with the same `(EBodyPart, float, DamageInfo)` signature, and `EBodyPart` came from `PlayerEnums.dll`, now referenced.

## 0.7.6

- **No Sway, take two.** Disabling the walk and hand shake effectors and zeroing `MotionReact.SwayFactors` got most of the strafe movement but left a slight side to side, because the motion effector integrates velocity and acceleration into its own `Motion` value each frame. It now clears that whole state as well: motion, velocity, position and rotation velocity, both accelerations. The force effector's random wiggle is zeroed too, and so are the PWA's own sway strength and factor, since those are re-derived every frame.

## 0.7.5

- **No Sway also stops the gun moving when you strafe.** The old version zeroed the sway fields and the breath effector, which kills aim wobble but leaves movement handled elsewhere. The walk effector produces the step bob and its side speed multiplier is what throws the weapon around during a strafe, and the motion effector adds the inertia on top. Both are now disabled while the option is on, and restored when it is turned off, so the gun sits still in your hands instead of only being steady while standing.

## 0.7.4

- **Loot categories actually work again.** Both the in-game ESP and the web loot table classified items by the client's runtime class name, and 1.2 flattened almost everything to `Item`, so half the map fell into "other". The web feed had a second bug on top: it asked the world object (`LootItem`) for its type, which is the same string for every entry. Categories now come from walking the item template tree to the category node: Weapon, Knife and ThrowWeap to weapon, Ammo to ammo, Meds to medical, FoodDrink to food, Key to key, Mod to mods, BarterItem, SpecItem and Money to barter, and the Equipment branch (ArmoredEquipment, Backpack, TacticalVest, Helmet and friends) to gear.

## 0.7.3

- **Loot favourites.** A star on every loot table row keeps that item name on the map, drawn as a gold diamond, whether or not you have the panel open and whether or not the item is selected. Favourites are name based so they carry across raids, and they persist in browser storage.
- **Containers are searched, not just floors.** Every `LootableContainer` on the map is walked once per raid, 25 per frame so a map with hundreds of them does not stall, cached, and rebuilt every 90 seconds to catch anything looted. Served on `/containers`. A container holding a favourite draws as a gold ring with the item names beside it, which is what makes a safe or a cash register worth walking to.
- The loot feed now polls with the panel closed, every 30 seconds, because favourites are drawn from it.

## 0.7.2

- **Raid found banner.** The browser map pulses "Raid Found, Loading..." across the screen while a raid exists but no layer has reached the canvas yet, with the location id underneath. It clears as soon as the art draws, and gives up after twelve seconds so an unrenderable location cannot pulse forever. This also replaces the silent blank canvas on a location with no art folder.
- **Calibration survives a reload.** Fits are written to browser storage as well as to disk, because the server's calibration file only had room for rotation, scale and offsets and was silently dropping the anchored matrix. The disk format now carries the extra six numbers too.
- Fixed the overlay's hidden state never applying: the stylesheet has no global `.hidden`, every panel scopes it to its own id, and I had left that out.

## 0.7.1

- **Loot table keeps up with the raid.** The browser map's loot list now refreshes every 30 seconds for as long as the raid lasts, instead of taking one snapshot two minutes in and never updating. The poll stops when the raid ends.
- **Floor selection falls back to height.** The map layers are picked from building footprints first, and when nothing matches, from height alone if exactly one layer claims it. Standing on the technical level of Labs at y -5 used to keep the previous floor because the footprint box missed.
- **Calibration fits in stages.** An anchored fit from two or more pins comes first: the art rotation is taken from the map config, so only horizontal scale, vertical scale and offset are solved, which cannot shear or spin the art. Behind it, an axis fit allows independent x and y scale, and last the old rotation plus uniform scale. The most constrained model that explains the points wins, and pins that sit nearly on a line are rejected rather than producing a warped map.

## 0.7.0

- **Doors.** `Unlock Key` (numpad `.` by default) blanks the `KeyId` on every locked door within `Unlock Radius`, walks it out of the `Locked` state, and optionally pushes it open. Sealed doors are skipped, extraction doors are a separate type and never touched. Keycard and sliding doors inherit the same class and are covered.
- Four new settings under `Doors`: `Unlock Key`, `Unlock Radius`, `Open Doors`, `Debug Logging`.
- Keys are now decorative inside the radius. Stashes and keycards still work, they just stop being required.

## 0.6.0

Feature release. Everything from the single 0.5.0 dump, plus the browser map growing up.

**Browser map**

- Live map served on loopback, art for all 19 playable locations, floors picked from your height and stacked over a dimmed ground layer.
- Point and click calibration: press `P`, click the art where you are, three or more points solve rotation, scale, offset and mirroring.
- Loot table with prices, category filter, name search, price and distance sort, click to mark on the map.
- Quest markers: wanted items on the floor, containers holding them, and quest zones, each toggleable.
- Quest panel scoped to the current map, `Started` only, shortfall only, story quests behind a toggle.
- Contacts panel: players, bosses, followers, raiders, rogues and cultists, sorted by distance, click to centre.
- Transits with localised names, extracts, body X marks for anything that stopped existing.
- Follow mode, place names, contact names, height chevrons.
- Assets reload themselves: the page polls `/version` and refreshes when `app.js` changes.

**Chams**

- Outline mode is now the default: the body keeps its own materials and a cloned shell carries the faction colour.
- Faction resolution is role-first, so AI PMCs stop being painted as scavs.
- Optional tint mode that copies and recolours the game's own materials.

**Loot ESP**

- New `Quest Distance`, 300 m default, instead of quest items ignoring the distance limit.
- Role-based faction colours shared with the map.

**Fixes**

- The hideout is no longer treated as a raid.
- Quest and exfil labels resolve locale ids instead of printing keys or MongoIDs.
- Floor selection uses the right axes, so resort floors actually change.

## 0.5.0

Initial release: faction chams, loot ESP, aimbot with magic bullet, and QoL patches (stamina, recoil, sway, malfunction, instant search).
