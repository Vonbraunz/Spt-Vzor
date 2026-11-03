# Changelog

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
