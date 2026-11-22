# PaldiumBridge (prototype, T27)

A small UE4SS Lua mod. When you play as a client in someone else's co-op world, your own Palbox is not in any save file
on this PC. The mod reads it while the game is running and writes it to
`%LOCALAPPDATA%\Paldium\bridge\palbox.json` (written to `palbox.json.tmp` first, then renamed).

This is a **prototype to find out what the game lets a client read**. It is original code; the idea of reading the
replicated player storage through `PalUtility` is also used by other mods, but no code was copied.

## How UE4SS loads a hand-installed Lua mod

UE4SS loads a Lua mod in only two cases: the mod is listed in `Mods\mods.txt` (`Name : 1`; the Workshop mod manager keeps that file,
so do not edit it), or the mod folder contains an **`enabled.txt`**. This mod ships an empty `enabled.txt`, so copying the whole
folder is enough. (A mod without it is silently ignored: the first test showed exactly that.)

The Palworld 1.0 mod manager itself installs a Lua mod as `Mods\NativeMods\UE4SS\Mods\<PackageName>\Scripts\main.lua` plus
`Mods\ManagedMods\<PackageName>\Info.json`; this mod is not managed by it.

## Automatic export

No key press is needed. The mod reads what the game has replicated to your client (Pal Box pages and Dimensional Pal Storage
pages you looked at, plus the party and the base Pals when they can be read) and rewrites `palbox.json` when something changed.
The reading never holds the game for long: the work is cut into small jobs, at most one per 0.25 s tick, so there is no hitch every
few seconds. The whole Pal Box is read (one page per tick) when the game starts, after a cache clear or a character change, once after you
close the box menu, and about every 5 s while a box menu is open; with the menu closed only a cheap check runs (one page per 0.5 s, it counts the
occupied slots and reads the page only when the count changed, for example after a capture). The party is read every 10 s (2 s while a box
menu is open) and each base every 30 s (10 s with a menu open), one base per tick. The app watches the file and updates My Pals within a few seconds. Browse all pages of the Pal Box and the Dimensional Pal Storage
once and the list is complete (the mod remembers the pages for the whole session). It only reads: nothing is sent to the server and no game state is changed.
(`bIsForceSyncAllSlot` / `SyncPageIndex` are not used.)

The app can install the mod for you: Settings > Sources > "Install mod". The mod reports scan progress in `bridge\status.json`; the app shows it next to the mod source (there is no scan button in the app: the scan is started with the shortcut in the game).

## Scan all

Only the pages you have looked at are sent to a client. "Scan all" turns the pages through the open UI's own page control, which is what the page buttons do; nothing else in the game is changed. It stops (`cancelled`) when the UI is closed or you change the page by hand. If the Dimensional Pal Storage page control is not found it ends with `failed`; press F10 with the storage open and send the log (it lists the widgets that were considered).

## Keys

The shortcuts are set in the app (Settings > Mod shortcuts) and written to `%LOCALAPPDATA%\Paldium\bridge\settings.json`; the mod reads
that file when the game starts, so a change needs a game restart. Defaults:

| Key | Action |
|---|---|
| F9 | scan all: with the Pal Box or Dimensional Pal Storage open, the mod turns the pages itself, reads each one and returns to your page (smart mode stops after 5 empty pages; full mode goes through every page) |
| F10 | probe: read-only discovery written to `UE4SS.log` (at most 300 lines): class/property/function names and property values, no game function is called except the page-control lookup the scan also uses; every section starts with a "probe step" line |
| F11 | clear the mod's session cache |

A shortcut is a key (F1-F12, A-Z) with optional Ctrl / Shift / Alt, for example `Ctrl+F9`. An invalid name falls back to the default (a line in
`UE4SS.log` says so). A plain key ignores the press while Ctrl/Shift/Alt is held, so `Ctrl+F9` does not also run the action on `F9`.
Other mods use F5 to F8 (Pal Insight Breeding Link uses F8 and opens a website).

## Why there is no "trial" key

A page request without the menu (`RequestChangeDimensionStoragePage_ToServer`) was tried: with the menu closed the server only re-sent page 0 of the last opened storage, never pages 1-5. It does not send pages without an open storage session, so the idea was dropped and the key removed.

## Pages across game sessions

The mod's own cache is lost when the game closes, so the first export of a new session holds only the pages seen so far. The app therefore keeps the pages it has read per source in `%LOCALAPPDATA%\Paldium\cache\bridge-pages\<sourceId>.json` and merges every new file into them page by page (a page the new file read replaces the stored one, an empty page too; pages it did not read stay; a Pal found on several pages is kept once, newest read). Pages are kept per world (`worldId` in the file, else the player uid). The clear-cache key of the mod (default F11) only clears the mod's session cache; the app's page store is cleared with "Clear page cache" in Settings > Sources.

## Safety rules

- Only live objects are used: class default objects (`Default__...`) and archetypes are never touched (`FindAllOf` also returns them).
- A game function is only called when the class really has it; weak pointers (`WeakLockerControl`) are resolved with `Get()` and checked with `IsValid`.
- The owner of a Dimensional Pal Storage comes from `OwnerPlayerUId`, the resolved locker control, the storage structure the player stands at or the storage menu title. Only the nearest storage structure within interaction distance (6 m) is used, and when it has no uid the owner stays unknown (never the builder of a neighbouring storage). Which storage an opening belongs to is decided by its CONTENT: the first filled page of the opening is compared with the page of the same number of every storage read earlier in the session. Two pages are "the same" when the Pals that are on both (same instance id) are at least half of the Pals of each page (one shared Pal is not enough: Pals are moved between players' storages). The storage with the same page (the most matched Pals when several) is the same storage and its key and owner are used ("opening N: same storage as ..."), otherwise it is a new storage ("opening N: new storage ..."). Empty pages wait until the first filled page decides. For a new storage the owner is looked for in this order: (a) the resolved `WeakLockerControl` of the storage object: the storage structure or model on its outer chain (`GetOuter`) and the uid of the player who built it; (b) the interaction target of the player (the objects held by the `Interact...` properties of the controller and the pawn) when it is such a structure; (c) the nearest storage structure within 6 m, but only when no storage of another owner is within 15 m (otherwise the owner stays unknown). Structures are searched by their class, `BP_BuildObject_DimensionPalStorage_C`. An "opening" starts when the storage menu goes from not activated (`IsActivated`) to activated; the game reuses the same menu widget for every opening, so the widget name is not used. A different locker object inside the activated menu also starts a new opening. The box key is the owner. Safety net: when the first page of an opening shares no Pal (instance id) with the stored page of the same box, the owner is treated as uncertain, the log says "owner uncertain: page content does not match the stored box" and that opening is written under its own `unknown-<session>-<n>` key. Storage pages are only captured while a storage menu is active (never with the menu closed). When no owner is known every opening gets its own `unknown-<session>-<n>` key, so two boxes never overwrite each other (the Pals are de-duplicated by instance id).
- Empty pages: a Pal Box page is recorded as empty only when the game delivered it (every slot handle is `IsEmpty`) and it is the page on screen or was read earlier in the session; a page that never delivered data is never stored as empty, so unopened pages are not erased. A base whose worker container is readable and empty loses its old Pals; a base that is not loaded is left alone.
- Bases: the game's Japanese template name for a base the player never named is not exported. Each base logs once its positions ("base ... positions: director SpawnTransform ..., model ... Transform ... AreaRange ...") so a failed position match is explained in the log.
- `players`: the JSON has a `players` array (`{ uid, name }` from the player states, refreshed every 10 s); the app offers these names when the owner of a storage is unknown.
- F10 owner discovery (read only, log only): the object properties of the storage menu widgets (class, name, uid/owner fields), the `PalInteractComponent` objects of the local controller or pawn and their object properties, and the distribution of `SaveParameter.OwnerPlayerUId` of the Pals on the open page. Nothing of it is used yet.
- Bases: the director's `SpawnTransform` is (0, 0, 0) on a client and counts as unknown; the base is then matched to its model when the player stands inside exactly one model's `AreaRange` and exactly one director has a filled container, otherwise by order.
- Scan speed: a Pal Box page that is confirmed empty is passed in one loop period (about 0.25 s).
- Threads: key presses (scan, probe, clear) only set flags that the game-thread loop acts on; if `ExecuteInGameThread` is unavailable nothing runs directly. The quick page polling runs only while a box menu is open or a scan runs. With the menu closed a menu that is already known (its widget stays in memory) is seen opening with a plain `IsActivated` check each tick; the `FindAllOf` searches for the menu widgets run every 30 s (every 10 s while a menu class has no widget yet) and the searches for bases at most every 30 s.
- Timing: every game-thread job is measured with `os.clock`. `UE4SS.log` gets one summary line per minute (`timing (last minute ...): box-page n=... avg ... max ... ms; ...`, and `over N` when a job took more than the 2 ms budget). To log every single job add `"debugTiming": "on"` to `bridge\settings.json` by hand (the app does not write this key and removes it when it saves the shortcuts) and restart the game.
- Cached game objects are short-lived: the menu and UI objects are let go when the menu closes, and every cache is emptied when the controller or the character changes.

## If the game crashes

The mod only reads. If the game crashes after installing it, turn the mod off by deleting `...\Mods\NativeMods\UE4SS\Mods\PaldiumBridge\enabled.txt`
(or the whole folder), start the game again and tell us what you were doing, with `UE4SS.log` and the newest folder in `%LOCALAPPDATA%\Pal\Saved\Crashes`.
## Troubleshooting

- F9/F10 do not react: search `UE4SS.log` for "Error loading script". If the line is there, the script did not load (for example Lua 5.4 allows at most 200 local variables in the main chunk of `main.lua`); no key, scan or export works until it is fixed. When the script loads, the log has a line starting "loaded. F9 = scan all pages".
- `ModScriptLoadTests` (tests/Paldium.Import.Tests) compiles `main.lua` with a real Lua 5.4 and keeps the main chunk under 150 local variables. Sections of `main.lua` live in `do -- section` blocks; a section exports only the names other sections use (pre-declared with `local` right before the block).

## Steps for you

1. In the Paldium app: Settings > Sources > "Install mod" (or copy the whole `mod\PaldiumBridge` folder, `enabled.txt` included, to
   `...\Palworld\Mods\NativeMods\UE4SS\Mods\PaldiumBridge`).
2. Start Palworld and join the world as a client. Wait until you can move. In `...\Mods\NativeMods\UE4SS\UE4SS.log` look for
   `[PaldiumBridge] loaded. F9 = scan all pages ...`; if it is missing the mod did not load.
3. Open the Pal Box (or the Dimensional Pal Storage) and press the scan key (F9): the pages turn by themselves and you land on your page again.
   Or just browse the pages yourself; every page that is shown is read within a fraction of a second.
4. The app (Settings > Sources) shows what has been read per box and which pages are still missing.
5. If something is wrong, press F10 with the Pal Box or storage open and send `UE4SS.log` and `%LOCALAPPDATA%\Paldium\bridge\palbox.json`.

The mod never writes into the game folder; it only writes the JSON and status files in `%LOCALAPPDATA%\Paldium\bridge`.
Remove it by deleting the `PaldiumBridge` folder.
## JSON (schema version 1)

```jsonc
{ "schemaVersion": 1, "exportedAtUtc": "2026-11-12T20:00:00Z", "gameBuild": null, "playerName": "...", "playerUid": "...",
  "pals": [ { "instanceId": "...", "characterId": "BOSS_CowPal", "gender": "Female", "level": 23, "rank": 1,
              "passives": ["CraftSpeed_up2"], "talents": {"hp": 70, "shot": 55, "defense": 80},
              "equippedAttacks": ["..."], "learnedAttacks": ["..."], "nickname": null,
              "location": {"kind": "Palbox", "page": 2, "slot": 14} } ] }
```

Capture progress: `"capture": { "bonus": { "Sheepball": 5, "Kitsunebi": 4 }, "count": { "Sheepball": 7 } }` is read from the own player's record data
(`PlayerState.RecordData.PalCaptureBonusCount` and `PalCaptureCount`, the same names the save file uses) once a minute by one small job (`capture` in the
timing line); when the game does not give it (a client may not have the record) the log says so once and the field is left out. Not verified in the game yet.

Fields the game does not give (or the mod could not read) are left out; the log line `field access: ...` after a probe says which field was read
as a property, a getter or not at all.
