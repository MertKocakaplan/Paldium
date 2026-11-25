# Paldium.GameExtractor

Reads raw data tables from the Palworld game files with [CUE4Parse](https://github.com/FabianFG/CUE4Parse) and writes
them as JSON. It is a developer tool: the app never needs the game files at run time.

## Safety rules

- The game files are opened **read-only**. Nothing is written to the game folder or to `Mods\`.
- Only `Pal-Windows.pak` is loaded. The folders `Paks\~mods\` and `Paks\LogicMods\` hold mod pak files that can
  override assets and corrupt the data, so the extractor scans the top level of the Paks folder only and refuses to
  continue if any other pak file was mounted. The list of mounted pak files is printed at start.
- Output goes to `tools/Paldium.GameExtractor/out/` (ignored by git). Do not commit game data or the `.usmap` file.

## Requirements

- Palworld installed (tested with build 25246127, UE 5.1). Example Paks folder:
  `<your Steam library>\steamapps\common\Palworld\Pal\Content\Paks`
- A `.usmap` mappings file made for the **same game build** (Palworld stores properties without names, so the assets
  cannot be read without it). See "Creating the .usmap file".
- Internet access on the first run: CUE4Parse downloads the Oodle decompression library (`oodle-data-shared.dll`) into
  the tool's own output folder.

## Usage

Paths come from the options or from the environment variables `PALDIUM_PALWORLD_PAKS` and `PALDIUM_USMAP`.

```
dotnet run --project tools/Paldium.GameExtractor -c Release -- list --prefix Pal/Content/Pal/DataTable --paks <PaksFolder> --usmap <file.usmap>
dotnet run --project tools/Paldium.GameExtractor -c Release -- list --contains Spawner --contains DT_
dotnet run --project tools/Paldium.GameExtractor -c Release -- dump DT_PalMonsterParameter Pal/Content/Pal/DataTable/Spawner/DT_PalWildSpawner --out out
```

- `list`: prints the game file paths (`--prefix` filters by path start, `--contains` filters by text and can be repeated).
  `list` without filters prints all ~185,000 files.
- `dump <asset>...`: writes every export of the asset(s) as JSON to `--out` (default: `out` next to the executable).
  An asset is a full path (`Pal/Content/...`, extension optional) or a bare, unique name such as `DT_PalWildSpawner`.
  File names are the asset path with `/` replaced by `_`. Data tables have a `Rows` object (row name to properties);
  Blueprint defaults (`BP_PalGameSetting`) are under the `Default__...` export's `Properties`.

## Creating the .usmap file (again after a game update)

Palworld ships with the UE4SS mod loader in this setup (Steam Workshop "UE4SS Experimental (Palworld)", folder
`<Palworld>\Mods\NativeMods\UE4SS`). Steps that worked:

1. In `UE4SS-settings.ini`, section `[Debug]`, set `ConsoleEnabled`, `GuiConsoleEnabled` and `GuiConsoleVisible` to `1`.
2. Start the game and wait in the main menu.
3. In the UE4SS window open "UE4SS Debugging Tools", then "Dumpers", then "Generate .usmap file...".
4. The log shows "Mappings Generation Completed Successfully"; the file appears in the UE4SS folder.
5. Set the three options back to `0`.
6. Copy the file to `%LOCALAPPDATA%\Paldium\extractor\` (any place outside the repository works) and pass it with
   `--usmap` or `PALDIUM_USMAP`.

The current file is `Pal-5.1.1-0+++UE5+Release-5.1-2281fa31.usmap` (usmap version 4, UE 5.1.1, build 25246127).

## scan-world

`scan-world --out <dir> [--grid MainGrid,oilrig,CloseRange]` reads the persistent map `PL_MainWorld5` and the World Partition cells
under `PL_MainWorld5/_Generated_/` (about 7,500 packages, about an hour and 4 GB of memory) and writes `world-actors.json` (class -> actors with
world position and an allow list of properties) and `world-classes.txt` (inventory). At the end it compares known table positions
(`DT_BossSpawnerLoactionData`, `DT_PalQuestLocationData`) with the nearest scanned actors: a difference above 1,000 units means the
transform is wrong. Run it once per game update, then give the folder to `export --world <dir>` (shop places) and to
`Paldium.DataBuilder --world <dir>` (map markers).

## export

`export --out <dir>` writes normalized JSON for the DataBuilder (`species.json`, `passive-effects.json`, `texts.json`, `meta.json`); it reads only `Pal-Windows.pak`. Feed the folder to `Paldium.DataBuilder --extractor <dir>`.

With `--world <scan folder>` the export also fills what depends on placed actors: shop places, the level spawners, and for the map `quests.json` (main and side quests, their givers, places and rewards) and `pickups.json` (the contents of the ancient towers).

`dump ... --script` also reads Blueprint bytecode, e.g. to see which text keys a function uses.

`types <typeName | ~propertyPart>...` prints the properties of the game's own types from the mappings file (e.g. `types PalPlayerRecordData` for
the field names a save and the mod read), or which types have a property whose name contains the part.

`export` also writes `world-map.png` (the main world map, `Pal/Content/Pal/Texture/UI/Map/T_WorldMap`, a DXT1 8192x8192 texture, decoded and averaged down to 2048x2048 by the tool's own code, no imaging package) and `world-map.json` (its size and the world bounds of `DT_WorldMapUIData` row `MainMap`). The DataBuilder copies both to `data/map/` when they are in the `--extractor` folder.

`export` also writes `ranch.json` (what a Pal drops in the Ranch, by rank 1 to 10: `DT_PalBPClass` -> the Pal Blueprint's `SpawnItem.FieldLotteryNameByRank` -> `DT_ItemLotteryDataTable` rows `CharacterSpawnItem_*`) and the item icons `icons/items/*.png` (64 px, decoded by the tool's own DXT code). The DataBuilder merges `ranch.json` into `data/gamedata.json` (`pals[].ranch`) and copies the icons to `data/icons/items/`.
