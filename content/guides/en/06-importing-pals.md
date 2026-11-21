---
group: uygulama
summary: The save file and the mod bridge; what owner and scope mean.
tools:
  - { label: "Settings · Sources", href: "/settings#kaynak" }
art: import
---
# Importing your Pals in multiplayer

Paldium reads your Pals from Palworld save files, or, in a world you only joined, from the assistant mod. Which one you need depends on how you play.

## Where the saves are
`%LOCALAPPDATA%\Pal\Saved\SaveGames\<SteamID64>\<WorldId>\`

- **Worlds you host:** the folder holds `Level.sav`, `LevelMeta.sav` and `Players\*.sav`. Add it as a source and your Pals are read. The host's `Level.sav` contains the Pals of **all** players, so the host can import everyone's.
- **Worlds you joined** (a friend's co-op or a server): the folder only has `LocalData.sav`, so there is nothing to read. Use the assistant mod below, or ask the host to share the world folder (next section).
- **Dimensional Pal Storage:** `Players\<uid>_dps.sav` in the host's world folder.
- **Dedicated server:** `Pal\Saved\SaveGames\0\<WorldId>\`. On a rented server download the folder with SFTP or the control panel.

## Adding a source
Open **My Pals, Sources** (not Settings), add a save source and pick the world folder. Then sync. The app copies the files first and never writes to the game folder. Your Pals appear in My Pals with their location (party, Pal Box with page and slot, base, cage).

## Joined worlds, option 1: the host shares the world folder
The host puts a copy of the world folder into a synced folder (a cloud drive, for example) and you add that folder as a source. Everything the host's save holds is then readable, including every player's Pals. The copy is only as new as the host's last save.

## Joined worlds, option 2: the assistant mod
The mod reads what the game has sent to your own client while you play.
1. In **My Pals, Sources** press **Install mod** (or copy the `PaldiumBridge` folder into the game's `Mods\NativeMods\UE4SS\Mods` folder) and restart Palworld.
2. Join the world and open your Pal Box or a Dimensional Pal Storage. Every page you look at is read within a moment.
3. Press **F9** with the box open to let the mod turn the pages for you. **F10** writes a diagnostic to the UE4SS log (send it when something looks wrong). **F11** clears the mod's own session cache. The keys can be changed in Settings, Mod shortcuts.
4. The sources panel shows, for every box, which pages were read and when. A page you have not opened yet is simply missing.

The app keeps the pages of earlier game sessions (per world), so the list does not shrink when you restart the game; a page that is read again replaces its old content. "Clear page cache" in My Pals, Sources forgets all of them.

### Limits
- Only what the game sends to you is readable: your own Pal Box and party, the storages you open, and bases the game has replicated to you. A friend's party and storages nobody opened are not.
- A storage's owner is not always known. The panel then says so and keeps that storage apart from the others.
- Pals that moved since the page was last read are shown where they were then.

## Global Palbox
`GlobalPalStorage.sav` sits in the account folder (`<SteamID64>\`). Pals in it are only a **genetic copy**: they cannot be used directly in this world (they can be recreated in another world), so Paldium does not count them by default and this guide does not recommend it for joined worlds. It is available as an advanced source for your own world.

## Notes
- Both save formats are supported (`PlM1`, the newer Oodle format, and the older `PlZ2`). Steam and Xbox saves work.
- Close the game or copy the folder first if the sync is blocked.
