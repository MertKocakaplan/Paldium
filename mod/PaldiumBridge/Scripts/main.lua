-- PaldiumBridge (T27 prototype): exports the local player's Palbox to
-- %LOCALAPPDATA%\Paldium\bridge\palbox.json so Paldium can read it while the player is a client in a
-- friend's co-op world (their save is not on this PC).
--
-- Automatic: the replicated Pal Box / Dimensional Pal Storage pages (and the party and bases, best effort) are read in small jobs (one per
--            tick, T118) and palbox.json is rewritten when the content changed. Only reads: nothing is sent to the server.
-- scan key (F9)  scan all: turn the pages of the open Pal Box / Dimensional Pal Storage UI and read every one (T38)
-- clear key (F11)  clear the session cache (start over)
-- probe key (F10)  probe: log the members of the Pal storage objects (what can be read) to UE4SS.log (at most 300 lines)
--
-- Original code. The approach (replicated player storage through PalUtility) is the one used by other mods;
-- no code was copied. Everything is wrapped in pcall: a failed read never crashes the game.

local MOD = "PaldiumBridge"
local SCHEMA_VERSION = 1
local MAX_PAGES, log, unwrap, valid, text, number, member, length
local items, jsonValue, array, outputFolder, ensureFolder, writeAtomically, controllerCache, utilityCache
local lastControllerLookup, localController, palStorage, guidText, parameterOf, readPal
do -- section: helpers
local SLOTS_PER_PAGE = 30
MAX_PAGES = 64

function log(fmt, ...)
    local ok, text = pcall(string.format, fmt, ...)
    print(string.format("[%s] %s\n", MOD, ok and text or tostring(fmt)))
end

---------------------------------------------------------------------------------------------------------------
-- small helpers around UE4SS values
---------------------------------------------------------------------------------------------------------------

-- Values from UE4SS come as Lua values, UObjects, structs, or "param" wrappers (RemoteUnrealParam / LocalUnrealParam, which have get()).
-- get() is only called on the wrappers: calling it on anything else raised errors in the first T38 test, right before the game crashed.
local unwrapKindsSeen, unwrapKindsLogged = {}, 0

function unwrap(value)
    if type(value) ~= "userdata" then return value end
    local okName, name = pcall(tostring, value)
    local kind = okName and tostring(name):match("^[%w_]+") or "?"
    if not unwrapKindsSeen[kind] and unwrapKindsLogged < 8 then
        unwrapKindsSeen[kind] = true
        unwrapKindsLogged = unwrapKindsLogged + 1
        log("value kind seen: %s", kind)
    end
    if kind == "RemoteUnrealParam" or kind == "LocalUnrealParam" then
        local ok, inner = pcall(function() return value:get() end)
        if ok and inner ~= nil then return inner end
    end
    return value
end

function valid(object)
    if object == nil then return false end
    local ok, result = pcall(function() return object:IsValid() end)
    if not ok then return true end -- a struct value has no IsValid(); it is still readable
    return result == true
end

function text(value)
    value = unwrap(value)
    if value == nil then return nil end
    if type(value) == "string" then return value end
    local ok, s = pcall(function() return value:ToString() end)
    if ok and type(s) == "string" then return s end
    return nil
end

function number(value)
    value = unwrap(value)
    if type(value) == "number" then return value end
    return tonumber(text(value))
end

-- Reads a member as a property first, then as Get<Name>(); the first one that works wins. Returns value, how.
function member(object, name)
    if not valid(object) then return nil, nil end
    local ok, value = pcall(function() return object[name] end)
    value = ok and unwrap(value) or nil
    if value ~= nil and type(value) ~= "function" then return value, "property" end
    ok, value = pcall(function() return object["Get" .. name](object) end)
    value = ok and unwrap(value) or nil
    if value ~= nil then return value, "getter" end
    return nil, nil
end

function length(array)
    array = unwrap(array)
    if array == nil then return nil end
    local ok, n = pcall(function() return #array end)
    if ok and type(n) == "number" then return n end
    ok, n = pcall(function() return array:GetArrayNum() end)
    if ok and type(n) == "number" then return n end
    return nil
end

function items(array)
    local result = {}
    local n = length(array)
    if n == nil then
        -- Some arrays only support ForEach(function(index, element) ... end).
        local okEach = pcall(function() unwrap(array):ForEach(function(_, element) result[#result + 1] = unwrap(element) end) end)
        if okEach and #result > 0 then return result end
        return nil
    end
    for i = 1, n do
        local ok, value = pcall(function() return array[i] end)
        if not ok then return nil end
        result[#result + 1] = unwrap(value)
    end
    return result
end

local function nameList(array)
    local list = items(array)
    if list == nil then return nil end
    local result = {}
    for _, value in ipairs(list) do
        local s = text(value)
        if s ~= nil and s ~= "None" and s ~= "" then result[#result + 1] = s end
    end
    return result
end

---------------------------------------------------------------------------------------------------------------
-- minimal JSON writer (UE4SS Lua has no JSON library)
---------------------------------------------------------------------------------------------------------------

local function jsonString(s)
    s = tostring(s):gsub('[%c"\\]', function(c)
        local map = { ['"'] = '\\"', ['\\'] = '\\\\', ['\n'] = '\\n', ['\r'] = '\\r', ['\t'] = '\\t' }
        return map[c] or string.format("\\u%04x", c:byte())
    end)
    return '"' .. s .. '"'
end

function jsonValue(v)
    local t = type(v)
    if v == nil then return "null" end
    if t == "boolean" then return v and "true" or "false" end
    if t == "number" then return tostring(math.floor(v) == v and math.floor(v) or v) end
    if t == "string" then return jsonString(v) end
    if t == "table" then
        if v.__array then
            local parts = {}
            for _, item in ipairs(v) do parts[#parts + 1] = jsonValue(item) end
            return "[" .. table.concat(parts, ",") .. "]"
        end
        local keys = {}
        for k in pairs(v) do keys[#keys + 1] = k end
        table.sort(keys)
        local parts = {}
        for _, k in ipairs(keys) do parts[#parts + 1] = jsonString(k) .. ":" .. jsonValue(v[k]) end
        return "{" .. table.concat(parts, ",") .. "}"
    end
    return "null"
end

function array(list)
    list = list or {}
    list.__array = true
    return list
end

---------------------------------------------------------------------------------------------------------------
-- output
---------------------------------------------------------------------------------------------------------------

function outputFolder()
    local base = os.getenv("LOCALAPPDATA")
    if base == nil or base == "" then return nil end
    return base .. "\\Paldium\\bridge"
end

function ensureFolder(folder)
    local probe = io.open(folder .. "\\.probe", "w")
    if probe then probe:close() os.remove(folder .. "\\.probe") return true end
    os.execute('mkdir "' .. folder .. '" >nul 2>&1')
    probe = io.open(folder .. "\\.probe", "w")
    if probe then probe:close() os.remove(folder .. "\\.probe") return true end
    return false
end

-- Writes <file>.tmp first and then renames it over <file> (Windows cannot rename over an existing file).
function writeAtomically(path, content)
    local temp = path .. ".tmp"
    local file, err = io.open(temp, "wb")
    if not file then return false, "cannot open " .. temp .. ": " .. tostring(err) end
    file:write(content)
    file:close()
    os.remove(path)
    local ok, renameError = os.rename(temp, path)
    if not ok then return false, "cannot rename: " .. tostring(renameError) end
    return true
end

---------------------------------------------------------------------------------------------------------------
-- reading the Palbox
---------------------------------------------------------------------------------------------------------------

local fieldReport = {} -- field -> how it was read (for the log: what is readable)

local function noteField(name, how)
    if fieldReport[name] == nil then fieldReport[name] = how or "unreadable" end
end

-- The controller and the utility object are kept and checked with IsValid on each use; they are only searched again when they are gone
-- (and then at most once a second).
controllerCache, utilityCache, lastControllerLookup = nil, nil, -100

function localController()
    if controllerCache ~= nil and valid(controllerCache) then return controllerCache end
    controllerCache = nil
    local now = os.clock()
    if now - lastControllerLookup < 1 then return nil end
    lastControllerLookup = now
    local ok, controller = pcall(FindFirstOf, "PalPlayerController")
    if ok and valid(controller) then controllerCache = unwrap(controller) end
    return controllerCache
end

function palStorage(controller)
    if utilityCache == nil or not valid(utilityCache) then
        utilityCache = nil
        pcall(function() utilityCache = StaticFindObject("/Script/Pal.Default__PalUtility") end)
    end
    local utility = utilityCache
    if not valid(utility) then return nil, "PalUtility not found" end
    local ok, data = pcall(function() return unwrap(utility:GetPlayerDataStorage(controller)) end)
    if not ok or not valid(data) then return nil, "player data storage not available" end
    local okStorage, storage = pcall(function() return unwrap(data:GetPalStorage()) end)
    if not okStorage or not valid(storage) then return nil, "Pal storage not available", data end
    return storage, nil, data
end

local function genderName(value)
    local s = text(value)
    if s ~= nil then
        if s:find("Female", 1, true) then return "Female" end
        if s:find("Male", 1, true) then return "Male" end
    end
    local n = number(value)
    if n == 1 then return "Male" elseif n == 2 then return "Female" end
    return nil
end

function guidText(struct)
    struct = unwrap(struct)
    if struct == nil then return nil end
    local parts = {}
    for _, name in ipairs({ "A", "B", "C", "D" }) do
        local n = number(member(struct, name))
        if n == nil then return text(struct) end
        parts[#parts + 1] = string.format("%08X", n % 4294967296)
    end
    return table.concat(parts)
end

-- The parameter object of a slot. Different game versions expose it differently, so a few routes are tried.
local parameterClassLogged = false

local function noteParameterClass(parameter)
    if parameterClassLogged then return end
    parameterClassLogged = true
    local ok, name = pcall(function() return parameter:GetClass():GetFullName() end)
    if not ok or not tostring(name):find("PalIndividualCharacterParameter", 1, true) then
        log("warning: the parameter object is %s, expected PalIndividualCharacterParameter", ok and tostring(name) or "unknown")
    end
end

-- Returns the parameter object of a slot, or nil (second value true when the slot is simply empty).
-- Route 1: the replicated parameter on the slot. Route 2: the handle's TryGetIndividualParameter().
function parameterOf(slot)
    slot = unwrap(slot)
    local okEmpty, empty = pcall(function() return slot:IsEmpty() end)
    if okEmpty and unwrap(empty) == true then return nil, true end

    local value = member(slot, "ReplicateIndividualParameter")
    if value ~= nil and valid(value) then
        noteField("slot.ReplicateIndividualParameter", "property")
        noteParameterClass(value)
        return value
    end

    local handle = member(slot, "Handle")
    if handle ~= nil and valid(handle) then
        local out = {}
        local ok, result = pcall(function() return handle:TryGetIndividualParameter(out) end)
        local parameter = ok and unwrap(result) or nil
        if parameter == nil or type(parameter) == "boolean" then parameter = unwrap(out.Parameter or out.OutParameter or out.ReturnValue) end
        if parameter ~= nil and type(parameter) ~= "boolean" and valid(parameter) then
            noteField("slot.Handle.TryGetIndividualParameter", "call")
            noteParameterClass(parameter)
            return parameter
        end
    end
    return nil
end

-- EPalWazaID values come as numbers: resolve them to the enum names that the game data uses ("AirBlade", "Unique_Alpaca_Tackle").
local wazaEnum
local wazaEnumTried = false

local function wazaName(value)
    value = unwrap(value)
    if value == nil then return nil end
    if type(value) == "number" then
        if not wazaEnumTried then
            wazaEnumTried = true
            local ok, found = pcall(StaticFindObject, "/Script/Pal.EPalWazaID")
            if ok and valid(found) then wazaEnum = found else log("EPalWazaID enum not found: attack ids stay unresolved") end
        end
        if wazaEnum == nil then return nil end
        local ok, name = pcall(function() return wazaEnum:GetNameByValue(value):ToString() end)
        if not ok or type(name) ~= "string" then return nil end
        return (name:gsub("^.*::", ""))
    end
    local s = text(value)
    if s == nil then return nil end
    return (s:gsub("^.*::", ""))
end

local function attackList(array)
    local list = items(array)
    if list == nil then return nil end
    local result = {}
    for _, value in ipairs(list) do
        local name = wazaName(value)
        if name ~= nil and name ~= "None" and name ~= "" then result[#result + 1] = name end
    end
    return result
end

function readPal(parameter)
    local save = member(parameter, "SaveParameter") or parameter
    local function read(name)
        local value, how = member(save, name)
        if value == nil and save ~= parameter then value, how = member(parameter, name) end
        noteField(name, how)
        return value
    end

    local characterId = text(read("CharacterID"))
    if characterId == nil then return nil, "species unreadable" end
    -- An empty slot has CharacterID None (and a zero instance id): it is not a Pal.
    if characterId == "None" or characterId == "" then return nil, "empty" end
    local talents = {
        hp = number(read("Talent_HP")), shot = number(read("Talent_Shot")), defense = number(read("Talent_Defense")),
    }
    local instance = member(parameter, "IndividualId") or member(save, "IndividualId") or member(parameter, "InstanceId")
    local instanceId = instance and guidText(member(instance, "InstanceId")) or nil
    noteField("InstanceId", instanceId and "IndividualId" or nil)
    if instanceId ~= nil and instanceId:match("^0+$") then return nil, "empty" end

    return {
        instanceId = instanceId,
        characterId = characterId,
        gender = genderName(read("Gender")),
        level = number(read("Level")),
        rank = number(read("Rank")),
        passives = array(nameList(read("PassiveSkillList")) or {}),
        talents = (talents.hp or talents.shot or talents.defense) and talents or nil,
        equippedAttacks = array(attackList(read("EquipWaza")) or {}),
        learnedAttacks = array(attackList(read("MasteredWaza")) or {}),
        nickname = text(read("NickName")),
    }
end
end -- section: helpers

-- Slots of one page; the array comes back either as the return value or through an out parameter.
local partyPage, basePages, baseNames, dpsBoxes, resetCache, nowUtc, dirty, lastActivity
local storePage, readPalboxPage, beginPagesSweep, sweepPagesStep, fingerprintPagesStep, isZeroGuid, isDefaultObject, dimensionObjects, pageNumValue, dpsPageCount
local cacheEpoch = 0
local objectName, resolveWeak, uiMenus, storageTexts, structureOwner, activeMenuName, menuTitleOwnerName, captureDimension
local dimensionStorage, lastFingerprint, pollDimension, worldIdCache, worldId, lastContent, export, AUTO_SECONDS
local POLL_MS, currentOpening, trackOpening, playersList
do -- section: page cache and export
local function slotsOfPage(storage, page)
    local out = {}
    local ok, first = pcall(function() return storage:GetSlotsInPage(page, out) end)
    local list = items(ok and first or nil) or items(out.Slots) or items(out)
    if list == nil then
        local ok2, second = pcall(function() return storage:GetSlotsInPage(page) end)
        list = ok2 and items(second) or nil
    end
    return list
end

-- Everything read so far this session (only visited pages are replicated, so pages pile up). Each stored page remembers when it was read
-- (seq, readAt) and its content; a Pal that is seen again in a newer page replaces the older copy (it was moved).
local seqCounter = 0
local palboxPages = {}        -- page -> { pals, seq, readAt, content }
local palboxPageCount = nil
partyPage = nil
basePages = {}          -- baseId -> { [0] = page entry } (Pals that work at a base)
baseNames = {}          -- baseId -> name (BaseCampName), for the coverage report
dpsBoxes = {}           -- boxOwnerUid -> { pageCount, pages = { page -> { pals, seq, readAt, content } } }

-- T118: the number of occupied slots (not IsEmpty) each Pal Box page had when it was last read: the cheap fingerprint of an idle box.
local pageCounts = {}

function resetCache()
    seqCounter, palboxPages, palboxPageCount, partyPage, dpsBoxes, basePages = 0, {}, nil, nil, {}, {}
    pageCounts = {}
    cacheEpoch = cacheEpoch + 1   -- the loop sees this and reads the whole box again (budgeted, over the next ticks)
end

function nowUtc() return os.date("!%Y-%m-%dT%H:%M:%SZ") end

dirty = true         -- something changed since palbox.json was written
lastActivity = -100  -- os.clock() of the last change (decides how often the quick polling runs)
relicsData = nil     -- T133: { Type = { have = n, collected = n } } of the own player's effigies, written to palbox.json
captureData = nil    -- T121: { bonus = { species = 0..5 }, count = { species = n } } read from the player's record data; written to palbox.json when it changed

-- Keeps a page; returns true when it is new or its content changed.
function storePage(store, page, pals)
    local content = jsonValue(array(pals))
    local old = store[page]
    if old ~= nil and old.content == content then return false end
    seqCounter = seqCounter + 1
    store[page] = { pals = pals, seq = seqCounter, readAt = nowUtc(), content = content }
    dirty = true
    lastActivity = os.clock()
    return true
end

-- Reads one Pal Box page into the cache; returns the number of Pals on it and whether the page is confirmed empty.
-- The client also sees pages it has never opened as empty (nothing was replicated), so an empty result is only recorded when it is certain:
-- the page has slot handles and every one of them is IsEmpty AND the page is the one the player is looking at (shown) or was read before in this
-- session (it held Pals; they were taken out). A page that never delivered data is never stored as empty and nothing is deleted for it (T49-1).
function readPalboxPage(storage, page, shown)
    local slots = slotsOfPage(storage, page)
    local pals, emptySlots, plainEmpty = {}, 0, 0
    if slots ~= nil then
        for index, slot in ipairs(slots) do
            local parameter, isEmpty = parameterOf(slot)
            if isEmpty then
                emptySlots = emptySlots + 1
                plainEmpty = plainEmpty + 1
            elseif parameter ~= nil then
                local pal, problem = readPal(parameter)
                if pal ~= nil then
                    pal.location = { kind = "Palbox", page = page, slot = index - 1 }
                    pals[#pals + 1] = pal
                elseif problem == "empty" then
                    emptySlots = emptySlots + 1
                else
                    log("Palbox page %d slot %d: %s", page, index - 1, tostring(problem))
                end
            end
        end
    end
    local confirmedEmpty = #pals == 0 and slots ~= nil and #slots > 0 and emptySlots == #slots
    if slots ~= nil then pageCounts[page] = #slots - plainEmpty end
    if #pals > 0 then
        storePage(palboxPages, page, pals)
    elseif confirmedEmpty and (shown or palboxPages[page] ~= nil) then
        if storePage(palboxPages, page, {}) then log("Palbox page %d: empty (read and confirmed)", page) end
    end
    return #pals, confirmedEmpty
end

-- T118: the whole box is read in steps of one page (the loop gives each step its own tick), not in one game-thread job. The order and the stop
-- rule are those of the old single-pass read: pages from 0 until three empty ones in a row; then the pages read earlier beyond that gap (a page
-- that was emptied since must disappear too).
function beginPagesSweep(storage)
    palboxPageCount = number(member(storage, "PageNum")) or palboxPageCount
    return { page = 0, emptyRun = 0, reached = -1, later = nil }
end

-- Reads the next page of the sweep; returns true when the sweep is finished.
function sweepPagesStep(storage, sweep)
    if sweep.later == nil then
        local page = sweep.page
        sweep.reached = page
        if readPalboxPage(storage, page, false) > 0 then sweep.emptyRun = 0 else sweep.emptyRun = sweep.emptyRun + 1 end
        sweep.page = page + 1
        if sweep.emptyRun < 3 and sweep.page < MAX_PAGES then return false end
        sweep.later = {}
        for known in pairs(palboxPages) do
            if known > sweep.reached then sweep.later[#sweep.later + 1] = known end
        end
        table.sort(sweep.later)
        return #sweep.later == 0
    end
    readPalboxPage(storage, table.remove(sweep.later, 1), false)
    return #sweep.later == 0
end

-- The idle check of the box (menu closed): one page per call, no Pal is read, only whether each slot IsEmpty. Pals can only appear or leave an
-- unopened box (a capture, a release, a Pal that died), and that changes the count of occupied slots. Returns "sweep" (the page count changed:
-- read the box), a page number (that page changed: read it) or nil (nothing changed). `state.page` is the rotation.
function fingerprintPagesStep(storage, state)
    local pageNum = number(member(storage, "PageNum"))
    if pageNum ~= nil and palboxPageCount ~= nil and pageNum ~= palboxPageCount then return "sweep" end
    local limit = pageNum or palboxPageCount
    if limit == nil then
        limit = 0
        for known in pairs(palboxPages) do
            if known + 1 > limit then limit = known + 1 end
        end
    end
    if limit < 1 then limit = 1 end   -- an empty box: page 0 is still checked (a first Pal)
    local page = state.page % limit
    state.page = page + 1
    local slots = slotsOfPage(storage, page)
    if slots == nil then return nil end
    local occupied = 0
    for _, slot in ipairs(slots) do
        local ok, empty = pcall(function() return unwrap(slot):IsEmpty() end)
        if not (ok and unwrap(empty) == true) then occupied = occupied + 1 end
    end
    local known = pageCounts[page]
    if known == nil then
        -- Never read: a page with Pals on it must be read; an empty one is just remembered as empty.
        if occupied > 0 then return page end
        pageCounts[page] = 0
        return nil
    end
    if known ~= occupied then return page end
    return nil
end

---------------------------------------------------------------------------------------------------------------
-- Dimensional Pal Storage: one object per box (PalPlayerDataPalDimensionStorage, owner in OwnerPlayerUId), a single replicated page at
-- a time in PageReplicationData { PageData = array of { SaveParameter, InstanceId }, pageIndex, bDirtyToggle, TriggerType }.
-- Pages are captured by polling the box objects (a cheap fingerprint check, a full read only when it changed). No native hooks are used.
---------------------------------------------------------------------------------------------------------------

function isZeroGuid(uid)
    return uid == nil or tostring(uid):match("^0+$") ~= nil
end

local function shortUid(uid) return tostring(uid):sub(-8) end

-- Live objects only: FindAllOf also returns class default objects ("Default__...") and archetypes. A game function called on one of them
-- reads null fields and crashed the game (F10 in the third test). Every FindAllOf result goes through this.
function isDefaultObject(object)
    local ok, name = pcall(function() return object:GetFullName() end)
    if not ok or type(name) ~= "string" then return true end
    if name:find("Default__", 1, true) or name:find("Archetype", 1, true) or name:find("/Script/", 1, true) and name:find(":", 1, true) == nil then return true end
    local okFlags, flagged = pcall(function()
        return object:HasAnyFlags(EObjectFlags.RF_ClassDefaultObject | EObjectFlags.RF_ArchetypeObject)
    end)
    return okFlags and flagged == true
end

function dimensionObjects()
    local list = {}
    local ok, found = pcall(FindAllOf, "PalPlayerDataPalDimensionStorage")
    if ok and type(found) == "table" then
        for _, object in pairs(found) do
            local unwrapped = unwrap(object)
            if valid(unwrapped) and not isDefaultObject(unwrapped) then list[#list + 1] = unwrapped end
        end
    end
    return list
end

-- GetDimensionStoragePageNum is a static function that takes a world context object ("expected 1 parameters" without it): the storage
-- object, then the player controller, are tried. The answer is logged once; it is probably the capacity of the storage (e.g. 320 pages).
local pageNumLogged
function dpsPageCount(dimension)
    if pageNumValue ~= nil then return pageNumValue end
    local tries = { { "storage object", dimension }, { "player controller", localController() } }
    local notes = {}
    for _, attempt in ipairs(tries) do
        local label, context = attempt[1], attempt[2]
        if context ~= nil then
            local ok, result = pcall(function() return dimension:GetDimensionStoragePageNum(context) end)
            local value = ok and number(result) or nil
            notes[#notes + 1] = string.format("%s: ok=%s result=%s (%s)", label, tostring(ok), tostring(ok and result or result), type(result))
            if value ~= nil then
                pageNumValue = value
                log("GetDimensionStoragePageNum(%s) = %s", label, tostring(value))
                return value
            end
        end
    end
    if not pageNumLogged then
        pageNumLogged = true
        log("GetDimensionStoragePageNum gave no number. %s", table.concat(notes, "; "))
    end
    return nil
end

-- The owner of a box. The client's OwnerPlayerUId came back as all zeros, and InstanceId.PlayerUId of the Pals is the host for every box
-- (it is not the owner), so the owner comes only from: 1. OwnerPlayerUId, 2. the resolved WeakLockerControl (the structure's builder uid),
-- 3. the title of the storage menu ("<Player>'s Dimensional Pal Storage"). When none of them is known, every opening of the storage UI gets
-- its own "unknown-<n>" key (the Pals are de-duplicated by instance id), so two different boxes never overwrite each other.
-- T58: a box is keyed by its owner. An "opening" is the period in which a storage menu is activated (see currentOpening); the owner is decided
-- once per opening, and a different storage object (locker control) inside the open menu starts a new opening. Pages are only captured while an
-- opening exists.
local unknownCounter = 0
-- The key of a box with an unknown owner belongs to THIS game session ("unknown-<start of the session>-<n>"): the app keeps pages across sessions,
-- and unknown-1 of today must not be taken for unknown-1 of yesterday (a different storage).
local sessionStamp = os.date("!%Y%m%d%H%M%S")

function objectName(object)
    local ok, name = pcall(function() return object:GetFullName() end)
    return ok and tostring(name) or tostring(object)
end

-- A weak pointer is resolved with Get() and checked with IsValid; nil when it cannot be resolved.
function resolveWeak(weak)
    if weak == nil then return nil end
    local ok, object = pcall(function() return weak:Get() end)
    object = ok and unwrap(object) or nil
    if object ~= nil and valid(object) and not isDefaultObject(object) then return object end
    return nil
end

local function lockerOwner(dimension)
    local control = resolveWeak(member(dimension, "WeakLockerControl"))
    if control == nil then return nil end
    for _, name in ipairs({ "BuildPlayerUId", "OwnerPlayerUId", "PlayerUId", "TargetPlayerUId", "LockerOwnerPlayerUId" }) do
        local uid = guidText(member(control, name))
        if not isZeroGuid(uid) then return uid end
    end
    return nil
end

-- "<Player>'s Dimensional Pal Storage" / "<Oyuncu>'nın Boyutsal Pal Deposu": the name before the apostrophe.
local function ownerNameFromTitle(title)
    if type(title) ~= "string" then return nil end
    local lower = title:lower()
    if not (lower:find("dimension", 1, true) or lower:find("boyutsal", 1, true)) then return nil end
    local name = title:match("^%s*(.-)%s*['\226\128\153]")
    return name ~= nil and name ~= "" and name or nil
end

uiMenus = {}   -- the storage menu widgets found by the UI search (refreshUi)
-- storageTexts (declared above) -- set below: the text widgets under the storage menus
-- structureOwner (declared above) -- set below: uid, name of the player who built the storage structure the player stands at
-- activeMenuName (declared above) -- set below: the name of the storage menu instance that is open now (nil: none)

function menuTitleOwnerName()
    if storageTexts ~= nil then
        for _, item in ipairs(storageTexts()) do
            local found = ownerNameFromTitle(item.text)
            if found then return found end
        end
    end
    return nil
end

-- Older route: the text properties of the menu itself (kept as a fallback).
local function menuPropertyTitleOwnerName()
    for _, menu in ipairs(uiMenus) do
        if valid(menu) then
            local names = {}
            pcall(function()
                menu:GetClass():ForEachProperty(function(property)
                    local ok, n = pcall(function() return property:GetFName():ToString() end)
                    if ok and n and #names < 80 then names[#names + 1] = n end
                end)
            end)
            for _, name in ipairs(names) do
                local value = member(menu, name)
                local found = ownerNameFromTitle(text(value)) or (type(value) == "userdata" and valid(value) and ownerNameFromTitle(text(member(value, "Text"))))
                if found then return found end
            end
        end
    end
    return nil
end

-- The same page (ARCHITECTURE 6.2): the Pals that are on both pages (same instance id; the pages have the same number) are at least half of the
-- Pals of each page. One shared Pal is not enough: Pals are moved between the storages of the players. Returns the matched count, or nil.
local function samePage(storedPals, ids, count)
    local matched, stored = 0, 0
    for _, pal in ipairs(storedPals) do
        stored = stored + 1
        if pal.instanceId ~= nil and ids[pal.instanceId] then matched = matched + 1 end
    end
    if stored == 0 or count == 0 then return nil end
    if matched * 2 >= stored and matched * 2 >= count then return matched end
    return nil
end

-- The owner by the T58 order (OwnerPlayerUId, locker control, structure/interaction/nearest), as uid, source, name; the key is built by the caller.
local function ownerBySources(dimension)
    local uid, source = guidText(member(dimension, "OwnerPlayerUId")), "storage"
    if isZeroGuid(uid) then uid, source = lockerOwner(dimension), "locker" end
    local structureName
    if isZeroGuid(uid) and structureOwner ~= nil then
        local structureUidFound, nameFound, via = structureOwner(dimension)
        if structureUidFound ~= nil then
            uid, source, structureName = structureUidFound, "structure", nameFound
            log("storage owner found by %s", tostring(via))
        end
    end
    local name = structureName or menuTitleOwnerName() or menuPropertyTitleOwnerName()
    if isZeroGuid(uid) and name ~= nil then source = "title" end
    if uid ~= nil and source == "structure" and name ~= nil then log("storage owner from the structure: %s (%s)", name, uid:sub(-8)) end
    if isZeroGuid(uid) then uid = nil end
    if uid == nil and name ~= nil then source = "title" end
    if uid == nil and name == nil then source = nil end
    return uid, source, name
end

-- T59: which storage an opening belongs to is decided by the CONTENT of its first filled page (ARCHITECTURE 6.2: the client does not show the owner
-- reliably). That page is compared with the page of the same number of every storage read earlier in the session; a storage whose page is "the same"
-- (see samePage) is the same storage (the one with the most matched Pals when several are) and its key and owner are used. Otherwise it is a new
-- storage: the owner is looked for (T58 order), and when that owner's key already holds a comparable page that is NOT the same, the owner search was
-- wrong and the storage gets its own unknown key. Sets op.choice.
local function decideBox(dimension, op, pals, page)
    local ids, count = {}, 0
    for _, pal in ipairs(pals) do
        count = count + 1
        if pal.instanceId ~= nil then ids[pal.instanceId] = true end
    end
    local bestKey, bestCount
    for key, box in pairs(dpsBoxes) do
        local entry = box.pages[page]
        local matched = entry ~= nil and samePage(entry.pals, ids, count) or nil
        if matched ~= nil and (bestCount == nil or matched > bestCount) then bestKey, bestCount = key, matched end
    end
    if bestKey ~= nil then
        local box = dpsBoxes[bestKey]
        op.choice = { uid = box.ownerUid, source = box.ownerSource, name = box.ownerName, boxKey = bestKey }
        log("opening %d: same storage as %s (page %d: %d matched Pals)", op.seq, shortUid(bestKey), page, bestCount)
        return
    end

    local uid, source, name = ownerBySources(dimension)
    local boxKey = uid or (name and ("name:" .. name))
    if boxKey ~= nil then
        local known = dpsBoxes[boxKey]
        local entry = known ~= nil and known.pages[page] or nil
        if entry ~= nil and #entry.pals > 0 then
            -- The owner's storage has this page filled and the page is not the same: another storage (the owner search was wrong).
            log("owner uncertain: page %d does not match the stored box %s (opening %d); this storage is written to its own unknown key", page, shortUid(boxKey), op.seq)
            uid, source, name, boxKey = nil, nil, nil, nil
        end
    end
    if boxKey == nil then
        unknownCounter = unknownCounter + 1
        boxKey = "unknown-" .. sessionStamp .. "-" .. unknownCounter
    end
    op.choice = { uid = uid, source = source, name = name, boxKey = boxKey }
    log("opening %d: new storage %s (owner %s)", op.seq, shortUid(boxKey), source or "unknown")
end

-- Returns changed, boxKey, refused. A page is only captured while a storage menu is active (an opening exists): the storage object also holds
-- pages when the menu is closed, and which box they belong to is then unknown (8th test: a page read 3 s after joining went to the wrong box).
function captureDimension(dimension, via)
    local data = member(dimension, "PageReplicationData")
    if data == nil then return false end

    local pageList = items(member(data, "PageData"))
    if pageList == nil or #pageList == 0 then return false end
    local op = currentOpening ~= nil and currentOpening(dimension) or nil
    if op == nil then return false, nil, true end
    local page = number(member(data, "pageIndex")) or number(member(data, "PageIndex")) or number(member(dimension, "CurrentPage")) or 0

    -- The slot is the Pal's place in PageData (0-29); SaveParameter.SlotId is the slot in some other container and is not used.
    local pals, empty = {}, 0
    for position, element in ipairs(pageList) do
        local pal, problem = readPal(element)
        if pal ~= nil then
            local info = member(element, "InstanceId")
            local palOwner = info and guidText(member(info, "PlayerUId")) or nil
            pal.ownerUid = not isZeroGuid(palOwner) and palOwner or nil
            pal.slotIndex = position - 1
            pals[#pals + 1] = pal
        elseif problem == "empty" then
            empty = empty + 1
        else
            log("DPS[%s] page %d element %d: %s", op.choice and shortUid(op.choice.boxKey) or "?", page, position, tostring(problem))
        end
    end

    -- The storage is decided by the first FILLED page of the opening; empty pages before it wait (and are stored once it is known).
    local waitingEmpty = false
    if op.choice == nil then
        if #pals == 0 then
            op.emptyPages = op.emptyPages or {}
            op.emptyPages[page] = true
            return false
        end
        decideBox(dimension, op, pals, page)
        waitingEmpty = true
    end
    local owner, source, ownerName, boxKey = op.choice.uid, op.choice.source, op.choice.name, op.choice.boxKey
    for _, pal in ipairs(pals) do
        pal.location = { kind = "DimensionalStorage", page = page, slot = pal.slotIndex, boxOwnerUid = owner, boxId = boxKey }
        pal.slotIndex = nil
    end

    local box = dpsBoxes[boxKey]
    if box == nil then box = { pages = {} } dpsBoxes[boxKey] = box end
    box.ownerUid, box.ownerSource, box.ownerName = owner, source, ownerName or box.ownerName
    box.pageCount = dpsPageCount(dimension) or box.pageCount

    if waitingEmpty and op.emptyPages ~= nil then
        for emptyPage in pairs(op.emptyPages) do
            if emptyPage ~= page then storePage(box.pages, emptyPage, {}) end
        end
        op.emptyPages = nil
    end
    local changed = storePage(box.pages, page, pals)
    if changed then
        log("DPS[%s] page %d: %d Pals (%d empty) via %s%s", shortUid(boxKey), page, #pals, empty, via, source and (", owner from " .. source) or ", owner unknown")
    end
    return changed, boxKey
end

-- The player's own Dimensional Pal Storage object hangs off the Pal storage (property PalDimensionStorage or GetDimensionStorage()).
function dimensionStorage(storage, data)
    local candidates = {}
    if storage ~= nil then
        candidates[#candidates + 1] = member(storage, "PalDimensionStorage")
        local okDim, dim = pcall(function() return unwrap(storage:GetDimensionStorage()) end)
        candidates[#candidates + 1] = okDim and dim or nil
    end
    for _, candidate in ipairs(candidates) do
        if candidate ~= nil and valid(candidate) then return candidate end
    end
    return nil
end

lastFingerprint = {}
local refusedSince = {}  -- box object -> { fp, at }: the data (fingerprint) whose capture was refused, and since when

-- A cheap check of one box object; a full capture only when the page, the toggle or the size changed. Returns true when something was stored,
-- and a second value true when the capture was refused because no storage menu is active (it is retried for 5 s: the menu may be found a moment later).
function pollDimension(dimension)
    local data = member(dimension, "PageReplicationData")
    if data == nil then return false end
    local owner = objectName(dimension)
    local fingerprint = table.concat({
        tostring(number(member(data, "pageIndex"))), tostring(unwrap(member(data, "bDirtyToggle"))), tostring(length(member(data, "PageData"))),
    }, "|")
    if lastFingerprint[owner] == fingerprint then return false end
    lastFingerprint[owner] = fingerprint
    local changed, _, refused = captureDimension(dimension, "poll")
    if refused then
        local now = os.clock()
        local r = refusedSince[owner]
        if r == nil or r.fp ~= fingerprint then r = { fp = fingerprint, at = now } refusedSince[owner] = r end
        if now - r.at < 5 then lastFingerprint[owner] = nil end
        return false, true
    end
    refusedSince[owner] = nil
    return changed
end

local function playerInfo(controller)
    local name, uid
    local state = member(controller, "PlayerState")
    if valid(state) then
        name = text(member(state, "PlayerNamePrivate")) or text(member(state, "PlayerName"))
        uid = guidText(member(state, "PlayerUId"))
    end
    return name, uid
end

local KIND_ORDER = { Party = 1, Palbox = 2, DimensionalStorage = 3, Base = 4 }

local function sortedPages(store)
    local pages = {}
    for page in pairs(store) do pages[#pages + 1] = page end
    table.sort(pages)
    return pages
end

-- Merges every stored page into the Pal list (a Pal seen in several places keeps only its newest copy) and builds the coverage report.
-- Works from the cache only: nothing is read from the game here.
local function collect(controller)
    local _, playerUid = playerInfo(controller)
    local stored, coverage = {}, {}
    local function addStore(kind, owner, store, pageCount, ownerSource, boxId, ownerName)
        local pages, latest = sortedPages(store), nil
        for _, page in ipairs(pages) do
            local entry = store[page]
            stored[#stored + 1] = entry
            if latest == nil or entry.readAt > latest then latest = entry.readAt end
        end
        if #pages > 0 then
            coverage[#coverage + 1] = {
                kind = kind, boxOwnerUid = owner, boxId = boxId, ownerName = ownerName, ownerSource = ownerSource, pageCount = pageCount,
                pagesRead = array(pages), lastReadUtc = latest,
            }
        end
    end
    addStore("Palbox", playerUid, palboxPages, palboxPageCount)
    if partyPage ~= nil then addStore("Party", playerUid, partyPage, nil) end
    local baseIds = {}
    for baseId in pairs(basePages) do baseIds[#baseIds + 1] = baseId end
    table.sort(baseIds)
    for _, baseId in ipairs(baseIds) do addStore("Base", nil, basePages[baseId], nil, nil, baseId, baseNames[baseId]) end
    local owners = {}
    for owner in pairs(dpsBoxes) do owners[#owners + 1] = owner end
    table.sort(owners)
    for _, owner in ipairs(owners) do
        local box = dpsBoxes[owner]
        addStore("DimensionalStorage", box.ownerUid, box.pages, box.pageCount, box.ownerSource, owner, box.ownerName)
    end

    -- Oldest first: a later copy of the same Pal replaces the earlier one.
    table.sort(stored, function(a, b) return a.seq < b.seq end)
    local byInstance, anonymous = {}, {}
    for _, entry in ipairs(stored) do
        for _, pal in ipairs(entry.pals) do
            if pal.instanceId ~= nil then byInstance[pal.instanceId] = pal else anonymous[#anonymous + 1] = pal end
        end
    end
    local pals = {}
    for _, pal in pairs(byInstance) do pals[#pals + 1] = pal end
    for _, pal in ipairs(anonymous) do pals[#pals + 1] = pal end
    table.sort(pals, function(a, b)
        local la, lb = a.location, b.location
        local ka, kb = KIND_ORDER[la.kind] or 9, KIND_ORDER[lb.kind] or 9
        if ka ~= kb then return ka < kb end
        local oa, ob = la.boxOwnerUid or "", lb.boxOwnerUid or ""
        if oa ~= ob then return oa < ob end
        if la.page ~= lb.page then return (la.page or 0) < (lb.page or 0) end
        return (la.slot or 0) < (lb.slot or 0)
    end)
    return pals, coverage
end

-- The world the file was read in (T41: the app keeps pages of earlier sessions per world). Looked for in the game state; the properties tried are
-- guesses, the probe lists the game state's scalar values so the right one can be named. nil: the app uses the player uid.
local lastWorldLookup
worldIdCache, lastWorldLookup = nil, -100

function worldId()
    if worldIdCache ~= nil then return worldIdCache end
    local now = os.clock()
    if now - lastWorldLookup < 5 then return nil end
    lastWorldLookup = now
    for _, className in ipairs({ "PalGameStateInGame", "PalGameState", "GameStateBase" }) do
        local ok, state = pcall(FindFirstOf, className)
        state = ok and unwrap(state) or nil
        if state ~= nil and valid(state) and not isDefaultObject(state) then
            for _, name in ipairs({ "WorldSaveDirectoryName", "WorldId", "WorldUId", "WorldUID", "SaveWorldName", "WorldName" }) do
                local value = member(state, name)
                local s = type(value) == "string" and value or text(value)
                if s ~= nil and s ~= "" and s ~= "None" then
                    worldIdCache = s
                    log("world id: %s (from %s.%s)", s, className, name)
                    return s
                end
                local guid = guidText(value)
                if guid ~= nil and #guid == 32 and not isZeroGuid(guid) then
                    worldIdCache = guid
                    log("world id: %s (from %s.%s)", guid, className, name)
                    return guid
                end
            end
        end
    end
    return nil
end

-- lastContent (declared above) -- the JSON of the last written export without the timestamp (nothing is rewritten when it is unchanged)

local function write(controller, pals, coverage)
    local name, uid = playerInfo(controller)
    local document = {
        schemaVersion = SCHEMA_VERSION,
        exportedAtUtc = "@@TIME@@",
        gameBuild = nil,
        playerName = name,
        playerUid = uid,
        worldId = worldId(),
        players = array(playersList ~= nil and playersList() or {}),
        pals = array(pals),
        coverage = array(coverage),
        capture = captureData,
        relics = relicsData,
    }
    local content = jsonValue(document)
    if content == lastContent then return false, "unchanged" end

    local folder = outputFolder()
    if folder == nil or not ensureFolder(folder) then return false, "cannot create the output folder " .. tostring(folder) end
    local stamped = content:gsub("@@TIME@@", os.date("!%Y-%m-%dT%H:%M:%SZ"), 1)
    local ok, err = writeAtomically(folder .. "\\palbox.json", stamped .. "\n")
    if not ok then return false, "write failed: " .. tostring(err) end
    lastContent = content
    return true
end

-- Writes palbox.json from the cache when something changed.
function export()
    local controller = localController()
    if controller == nil then return false end
    local pals, coverage = collect(controller)
    if #pals == 0 then return false end

    local written, why = write(controller, pals, coverage)
    if not written then
        if why ~= "unchanged" then log("%s", tostring(why)) end
        return false
    end
    log("exported %d Pals from %d box(es) to %s\\palbox.json", #pals, #coverage, tostring(outputFolder()))
    return true
end

AUTO_SECONDS = 2        -- how often the player's own storage is read while the game is idle (10 s after 30 s without changes)
POLL_MS = 250           -- loop period; the quick work (page polling) only runs while a box menu is open or a scan runs
end -- section: page cache and export

---------------------------------------------------------------------------------------------------------------
-- "Scan all" (T38): with the Pal Box or Dimensional Pal Storage UI open the mod turns the pages itself through the UI's own page control
-- (the same thing the player does with the page buttons), reads each page when the game has sent it and returns to the page the player was on.
-- Nothing else is done: no game state is changed and no request is made beyond the page request that a manual page change makes.
-- Trigger: the scan shortcut in the game (F9 by default, see bridge\settings.json). Progress goes to bridge\status.json.
---------------------------------------------------------------------------------------------------------------

local FIND_INTERVAL, scanMode, scan, bridgeFile, classNameOf, functionNamesOf, hasFunction, currentPageOf
local allWidgets, isShown, findControl, uiCache, refreshUi, startScan, scanTick
do -- section: scanner
local PAGE_TIMEOUT = 1.5      -- seconds to wait for the data of one page
local EMPTY_SETTLE = 0.2      -- seconds after which a page whose slots are all empty counts as read (it did arrive)
local SMART_EMPTY_STOP = 5    -- smart mode: stop after this many empty pages in a row
FIND_INTERVAL = 5       -- FindAllOf is never called more often than this (seconds)
scanMode = "smart"      -- from bridge\settings.json

-- scan (declared above) -- the running scan, or nil
local lastStatusKey

function bridgeFile(name)
    local folder = outputFolder()
    return folder and (folder .. "\\" .. name) or nil
end

local function writeStatus(state, extra)
    extra = extra or {}
    local document = {
        state = state, kind = extra.kind, boxOwnerUid = extra.boxOwnerUid,
        page = extra.page, pageCount = extra.pageCount, message = extra.message, updatedAtUtc = "@@TIME@@",
    }
    local content = jsonValue(document)
    if content == lastStatusKey then return end
    local folder = outputFolder()
    local path = bridgeFile("status.json")
    if path == nil or not ensureFolder(folder) then return end
    lastStatusKey = content
    writeAtomically(path, (content:gsub("@@TIME@@", nowUtc(), 1)) .. "\n")
end

function classNameOf(object)
    local ok, name = pcall(function() return object:GetClass():GetFullName() end)
    return ok and type(name) == "string" and name or ""
end

-- The functions a class really has (its own and its parents'), by name. Indexing a UObject with any name returns a non-nil value in UE4SS, so
-- "object[name] ~= nil" proves nothing; the class's function list does. Cached per class.
local functionCache = {}

function functionNamesOf(object)
    local className = classNameOf(object)
    if functionCache[className] ~= nil then return functionCache[className] end
    local names = {}
    pcall(function()
        local class = object:GetClass()
        for _ = 1, 30 do
            if class == nil then break end
            class:ForEachFunction(function(fn)
                local ok, name = pcall(function() return fn:GetFName():ToString() end)
                if ok and name then names[name] = true end
            end)
            local okSuper, super = pcall(function() return class:GetSuperStruct() end)
            class = okSuper and unwrap(super) or nil
            if class ~= nil and not valid(class) then break end
        end
    end)
    functionCache[className] = names
    return names
end

function hasFunction(object, name)
    return functionNamesOf(object)[name] == true
end

function currentPageOf(control)
    -- GetCurrentPage has an output parameter that UE4SS takes for an input ("expected 1 parameters"); the LastSelectedPageNum property of the
    -- list is read instead (method of the EsInventoryHotKeys notes, RESEARCH 11).
    local property = number(member(control, "LastSelectedPageNum"))
    if property ~= nil then return math.max(0, math.floor(property)) end
    local out = {}
    local ok, returned = pcall(function() return control:GetCurrentPage(out) end)
    local value = ok and (number(out.CurrentPageNum) or number(returned)) or nil
    return value ~= nil and math.max(0, math.floor(value)) or nil
end

local function requestPage(control, page)
    return pcall(function() control:SetCurrentPage(page) end)
end

-- The page control of a page list widget: its root widget first (the route that works in Breeding Link), then the widget itself. A candidate
-- must really have both functions and GetCurrentPage must answer with a number.
local function pageControlOf(widget)
    local root
    pcall(function() root = unwrap(widget.WidgetTree.RootWidget) end)
    for _, candidate in ipairs({ root or false, widget }) do
        if candidate and valid(candidate) and hasFunction(candidate, "SetCurrentPage") and hasFunction(candidate, "GetCurrentPage")
            and currentPageOf(candidate) ~= nil then
            return candidate
        end
    end
    return nil
end

function allWidgets(className)
    local ok, found = pcall(FindAllOf, className)
    local list = {}
    if ok and type(found) == "table" then
        for _, widget in pairs(found) do
            local unwrapped = unwrap(widget)
            if valid(unwrapped) and not isDefaultObject(unwrapped) then list[#list + 1] = unwrapped end
        end
    end
    return list
end

-- Is a menu open? IsActivated is called directly (the way Breeding Link does it). IsVisible alone is not enough: closed menus stay in memory
-- with Visibility 4. Returns shown, how.
local function shownInfo(menu)
    local ok, active = pcall(function() return menu:IsActivated() end)
    if ok and type(unwrap(active)) == "boolean" then return unwrap(active), "IsActivated" end
    local okVisible, visible = pcall(function() return menu:IsVisible() end)
    if okVisible and type(unwrap(visible)) == "boolean" then return unwrap(visible), "IsVisible" end
    return false, "none"
end

function isShown(menu)
    return (shownInfo(menu))
end

-- The main menus: the Pal Box and the Dimensional Pal Storage ("locker"). The page list widget sits somewhere below them (its outer chain).
-- Exact class names: a substring match took WBP_IngameMenu_PalBoxLockerList_C for the menu.
local MENU_TOKENS = { Palbox = "WBP_PalStorageMenu_C", DimensionalStorage = "WBP_IngameMenu_PalBoxLocker_C" }

local function classIs(object, token)
    local name = classNameOf(object)
    return name == token or name:sub(-(#token + 1)) == "." .. token
end

local function menuOf(widget, token)
    local current = widget
    for _ = 1, 8 do
        local ok, outer = pcall(function() return unwrap(current:GetOuter()) end)
        if not ok or not valid(outer) then return nil end
        if classIs(outer, token) then return outer end
        current = outer
    end
    return nil
end

local function listWidgets(broad)
    local list = allWidgets("WBP_BoxPalList_C")
    if broad then
        -- Other list widget classes of the storage menus (searches every user widget: only for a scan or the probe).
        for _, widget in ipairs(allWidgets("UserWidget")) do
            local name = classNameOf(widget)
            if (name:find("BoxPalList", 1, true) or name:find("PalBoxLocker", 1, true)) and not name:find("WBP_BoxPalList_C", 1, true) then
                list[#list + 1] = widget
            end
        end
    end
    return list
end

local function chainClasses(widget)
    local names, current = {}, widget
    for _ = 1, 8 do
        local ok, outer = pcall(function() return unwrap(current:GetOuter()) end)
        if not ok or not valid(outer) then break end
        names[#names + 1] = classNameOf(outer)
        current = outer
    end
    return names
end

-- Every page list of an open box UI that has a working page control, best candidate first. The storage menu holds TWO lists (the player's
-- Pal Box on the left, the opened storage on the right): a list whose outer chain has a storage panel (locker list / dimension) is preferred and one
-- inside the normal Pal Box menu is demoted. The scan still checks that the list really turns the storage's page (see "verify").
local function controlCandidates(kind, broad)
    local token = MENU_TOKENS[kind]
    local found = {}
    if kind == "DimensionalStorage" then
        -- The storage menu has two lists: WBP_BoxPalList (the player's Pal Box, left) and WBP_IngameMenu_PalBoxLockerList (the storage, right).
        -- The right one holds a WBP_BoxPalListBase_C with SetCurrentPage / GetCurrentPage / ToNextPage: that is the storage's page control.
        local lockerList = "WBP_IngameMenu_PalBoxLockerList_C"
        for _, inner in ipairs(allWidgets("WBP_BoxPalListBase_C")) do
            local inLockerList = false
            for _, name in ipairs(chainClasses(inner)) do
                if name:sub(-#lockerList) == lockerList then inLockerList = true break end
            end
            local menu = inLockerList and menuOf(inner, token) or nil
            if menu ~= nil and isShown(menu) then
                local control = pageControlOf(inner)
                if control ~= nil then found[#found + 1] = { control = control, menu = menu, list = inner, score = 10, order = #found } end
            end
        end
    end
    for _, list in ipairs(listWidgets(broad)) do
        local menu = menuOf(list, token)
        if menu ~= nil and isShown(menu) then
            local control = pageControlOf(list)
            if control ~= nil then
                local score = 0
                if kind == "DimensionalStorage" then
                    for _, name in ipairs(chainClasses(list)) do
                        local lower = name:lower()
                        if lower:find("lockerlist", 1, true) or lower:find("dimension", 1, true) then score = score + 2 end
                        if lower:find("palstoragemenu", 1, true) then score = score - 2 end
                    end
                end
                found[#found + 1] = { control = control, menu = menu, list = list, score = score, order = #found }
            end
        end
    end
    if broad and #found == 0 then
        -- Last resort for the storage: the menu widget itself may be the page control.
        for _, widget in ipairs(allWidgets("UserWidget")) do
            if classIs(widget, token) and isShown(widget) then
                local control = pageControlOf(widget)
                if control ~= nil then found[#found + 1] = { control = control, menu = widget, list = widget, score = 0, order = #found } end
            end
        end
    end
    table.sort(found, function(a, b)
        if a.score ~= b.score then return a.score > b.score end
        return a.order < b.order
    end)
    return found
end

-- The name of the open storage menu instance; the cached list is looked at first, a fresh search is made at most once a second.
local lastMenuForce = -100
activeMenuName = function()
    for _, menu in ipairs(uiMenus) do
        if valid(menu) and isShown(menu) then return objectName(menu) end
    end
    local now = os.clock()
    if now - lastMenuForce >= 1 then
        lastMenuForce = now
        uiMenus = allWidgets("WBP_IngameMenu_PalBoxLocker_C")
        for _, menu in ipairs(uiMenus) do
            if isShown(menu) then return objectName(menu) end
        end
    end
    return nil
end

-- The text widgets (TextBlock and relatives) under the storage menu and the storage display widget: name and Text (property reads only).
storageTexts = function()
    local result = {}
    for _, className in ipairs({ "TextBlock", "CommonTextBlock", "RichTextBlock" }) do
        for _, widget in ipairs(allWidgets(className)) do
            local under = false
            for _, name in ipairs(chainClasses(widget)) do
                if name:find("PalDetail", 1, true) then under = false break end
                if name:find("PalBoxLocker", 1, true) or name:find("DimensionPalStorage_ForDisplay", 1, true) then under = true end
            end
            if under then result[#result + 1] = { name = objectName(widget):match("[^%.:]+$") or "?", text = text(member(widget, "Text")) } end
        end
    end
    return result
end

-- T58: openings. One opening = one period in which a storage menu is activated (IsActivated false -> true). The game reuses the menu widget
-- for every opening (the 8th test: Alice's box, closed, then bob's box in the same widget instance), so the widget's name says nothing. What tells
-- two storages apart is the resolved WeakLockerControl of the storage object (its full name is the storage's identity): when it changes inside an
-- activated menu a new opening starts. The owner is decided once per opening.
local opening, openingSeq = nil, 0

local function storageMenuShown()
    for _, menu in ipairs(uiMenus) do
        if valid(menu) and isShown(menu) then return true end
    end
    return false
end

local function lockerNameOf(dimension)
    local control = resolveWeak(member(dimension, "WeakLockerControl"))
    return control ~= nil and objectName(control) or nil
end

local function beginOpening(why)
    openingSeq = openingSeq + 1
    opening = { seq = openingSeq, checked = false }
    log("storage opening %d (%s)", openingSeq, why)
end

-- Called every tick: the transition of IsActivated of the (cached) storage menus decides when an opening starts and ends.
trackOpening = function()
    local shown = storageMenuShown()
    if shown and opening == nil then
        beginOpening("menu activated")
    elseif not shown and opening ~= nil then
        log("storage menu closed (opening %d)", opening.seq)
        opening = nil
    end
end

-- The current opening for a storage object that has data, or nil when no storage menu is active. A different locker object since the opening
-- began means the game switched to another storage inside the activated menu (a quick close and open between two ticks looks the same): new opening.
currentOpening = function(dimension)
    if opening == nil then return nil end
    local locker = lockerNameOf(dimension)
    if locker ~= nil and opening.locker ~= nil and locker ~= opening.locker then
        beginOpening(string.format("storage changed in the activated menu: %s -> %s", tostring(opening.locker), tostring(locker)))
        opening.locker = locker
    else
        opening.locker = opening.locker or locker
    end
    return opening
end

-- The page control of an open box UI: returns control, menu, listWidget (the best candidate). kind = "Palbox" | "DimensionalStorage".
function findControl(kind, broad)
    local first = controlCandidates(kind, broad)[1]
    if first ~= nil then return first.control, first.menu, first.list end
    return nil
end
local function activePalboxControl()
    local control, menu = findControl("Palbox", false)
    return control, menu
end

-- The UI objects that the quick polling needs, searched at most every FIND_INTERVAL seconds and only while a box UI is in use.
uiCache = { at = -100, dims = {}, palboxControl = nil, palboxMenu = nil }

function refreshUi(force)
    local now = os.clock()
    if not force and now - uiCache.at < FIND_INTERVAL then return end
    uiCache.at = now
    uiCache.dims = dimensionObjects()
    uiMenus = allWidgets("WBP_IngameMenu_PalBoxLocker_C")
    uiCache.palboxControl, uiCache.palboxMenu = activePalboxControl()
end

local function finishScan(state, message)
    local extra = { kind = scan.kind, boxOwnerUid = scan.owner, page = scan.page, pageCount = scan.pageCount, message = message }
    log("scan %s: %s", state, message)
    writeStatus(state, extra)
    scan = nil
end

-- The replicated Dimensional Pal Storage page, when it is page p and has data: returns the object (from the cache, checked with IsValid).
local function dpsShowing(page)
    for _, dimension in ipairs(uiCache.dims) do
        if valid(dimension) then
            local data = member(dimension, "PageReplicationData")
            if data ~= nil and number(member(data, "pageIndex")) == page and (length(member(data, "PageData")) or 0) > 0 then return dimension end
        end
    end
    return nil
end

-- Pressing the scan key while a scan runs cancels it and starts again (there is no waiting: the search is done once, now).
function startScan(via)
    if scan ~= nil then
        log("scan restarted (%s): the running scan is cancelled", via)
        if scan.control ~= nil and valid(scan.control) and scan.startPage ~= nil and scan.page > 0 then
            pcall(function() scan.control:SetCurrentPage(scan.startPage) end)
        end
        writeStatus("cancelled", { kind = scan.kind, message = "Restarted" })
        scan = nil
    end
    scan = { phase = "waiting", mode = scanMode, page = 0, emptyRun = 0 }
    log("scan all requested (%s, %s)", scan.mode, via)
end

-- One page change: SetCurrentPage, or ToNextPage when SetCurrentPage does not make the storage send the page (scan.useNext).
local function goToPage(control, page)
    if scan ~= nil and scan.useNext and page > 0 then return pcall(function() control:ToNextPage() end) end
    return pcall(function() control:SetCurrentPage(page) end)
end

-- Goes back to the page the player was on (after a failure in the middle of a scan too).
local function restorePage()
    if scan ~= nil and scan.control ~= nil and scan.startPage ~= nil and scan.page > 0 then requestPage(scan.control, scan.startPage) end
end

-- The page index the storage object has replicated right now (nil: none).
local function dpsReplicatedPage()
    for _, dimension in ipairs(uiCache.dims) do
        if valid(dimension) then
            local data = member(dimension, "PageReplicationData")
            if data ~= nil and (length(member(data, "PageData")) or 0) > 0 then return number(member(data, "pageIndex")) end
        end
    end
    return nil
end

-- Takes the next candidate list for the scan. The control must answer GetCurrentPage with a number before anything is changed. For the
-- storage the first page request doubles as the check: a page other than the replicated one is requested and the storage must show it.
-- Returns false when this candidate cannot be used (the caller goes on with the next one or the scan has ended).
local function beginCandidate()
    local candidate = scan.candidates[scan.candidateIndex]
    scan.control, scan.menu = candidate.control, candidate.menu
    scan.useNext, scan.useNextTried, scan.verifyViaNext = false, false, false
    scan.startPage = currentPageOf(candidate.control)
    if scan.startPage == nil then
        scan.tried[#scan.tried + 1] = classNameOf(candidate.control) .. " (no page number)"
        if scan.candidateIndex >= #scan.candidates then
            finishScan("failed", "No page list answered GetCurrentPage. Tried: " .. table.concat(scan.tried, ", "))
        else
            scan.candidateIndex = scan.candidateIndex + 1
            return false
        end
        return false
    end

    if scan.kind == "Palbox" then
        local controller = localController()
        local storage = controller and palStorage(controller)
        if storage == nil then finishScan("failed", "Pal storage is not available") return false end
        scan.storage = storage
        scan.pageCount = number(member(storage, "PageNum"))
        scan.page, scan.phase = 0, "request"
    else
        local dimension = uiCache.dims[1]
        scan.pageCount = dimension ~= nil and valid(dimension) and dpsPageCount(dimension) or nil
        if scan.pageCount == 1 then
            scan.page, scan.phase = 0, "request"
        else
            scan.verifyPage = dpsReplicatedPage() == 0 and 1 or 0
            if not requestPage(scan.control, scan.verifyPage) then
                scan.tried[#scan.tried + 1] = classNameOf(candidate.control) .. " (SetCurrentPage failed)"
                scan.candidateIndex = scan.candidateIndex + 1
                return false
            end
            scan.stepStart, scan.phase = os.clock(), "verify"
        end
    end
    log("scan: %s via %s (candidate %d of %d), %s pages, started on page %d", scan.kind, classNameOf(scan.control), scan.candidateIndex, #scan.candidates,
        tostring(scan.pageCount), scan.startPage)
    return true
end

function scanTick()
    if scan == nil then return end
    local now = os.clock()

    if scan.phase == "waiting" then
        refreshUi(true)

        -- One search. With the storage UI open the scan is of the storage (its menu also contains the player's Pal Box list); the Pal Box is only
        -- scanned from the normal Pal Box menu. One diagnostic line says what was found.
        local storageOpen, parts = false, {}
        for index, menu in ipairs(uiMenus) do
            if valid(menu) then
                local shown, how = shownInfo(menu)
                parts[#parts + 1] = string.format("%d:%s(%s)", index, tostring(shown), how)
                if shown then storageOpen = true end
            end
        end
        local palboxShown = false
        for _, menu in ipairs(allWidgets("WBP_PalStorageMenu_C")) do
            if isShown(menu) then palboxShown = true break end
        end
        scan.kind = storageOpen and "DimensionalStorage" or "Palbox"
        scan.candidates = controlCandidates(scan.kind, storageOpen)
        scan.candidateIndex, scan.tried = 1, {}
        log("scan search: storage menus=%d [%s], Pal Box menu shown=%s, %s candidates=%d", #uiMenus, table.concat(parts, ", "), tostring(palboxShown),
            scan.kind, #scan.candidates)
        if #scan.candidates == 0 then
            finishScan("failed", "No Pal Box or Dimensional Pal Storage UI is open, or its page control was not found. Open it and press the scan key again (the probe key lists the widgets).")
            return
        end

        -- Take the candidates one by one until one can be used (beginCandidate ends the scan itself on a hard failure).
        while true do
            if beginCandidate() then break end
            if scan == nil then return end
            if scan.candidateIndex > #scan.candidates then
                finishScan("failed", "No usable page list. Tried: " .. table.concat(scan.tried, ", "))
                return
            end
        end
    end

    -- The UI closed, or the player turned the page by hand: stop.
    if not valid(scan.control) then restorePage() finishScan("cancelled", "The UI was closed") return end
    if scan.menu ~= nil and (not valid(scan.menu) or not isShown(scan.menu)) then finishScan("cancelled", "The UI was closed") return end

    if scan.phase == "verify" then
        -- The storage menu has two page lists. A list is the storage's own when its page request makes the storage replicate that page.
        if dpsShowing(scan.verifyPage) ~= nil then
            scan.useNext = scan.verifyViaNext
            log("scan: the list %s turns the storage page via %s (confirmed)", classNameOf(scan.control), scan.useNext and "ToNextPage" or "SetCurrentPage")
            scan.page, scan.phase = 0, "request"
        elseif not scan.useNextTried and now - scan.stepStart > PAGE_TIMEOUT and hasFunction(scan.control, "ToNextPage") then
            -- SetCurrentPage may only change the list, not ask the storage for the page: ToNextPage is the same as the player's "next page" button.
            scan.useNextTried = true
            requestPage(scan.control, scan.startPage)
            scan.verifyPage = (dpsReplicatedPage() or scan.startPage) + 1
            if pcall(function() scan.control:ToNextPage() end) then
                scan.verifyViaNext, scan.stepStart = true, now
            else
                scan.stepStart = now - PAGE_TIMEOUT - 1
            end
        elseif now - scan.stepStart > PAGE_TIMEOUT then
            requestPage(scan.control, scan.startPage) -- not the storage's list: back to where it was
            scan.tried[#scan.tried + 1] = classNameOf(scan.control)
            scan.candidateIndex = scan.candidateIndex + 1
            while scan.candidateIndex <= #scan.candidates do
                if beginCandidate() then return end
                if scan == nil then return end
            end
            finishScan("failed", "None of the page lists turned the storage page. Tried: " .. table.concat(scan.tried, ", "))
            return
        end
        if scan ~= nil and scan.phase == "verify" then return end
    end

    if scan.phase == "request" then
        local limit = scan.pageCount or MAX_PAGES
        if scan.page >= limit or scan.page >= MAX_PAGES then
            scan.phase = "finish"
        elseif not goToPage(scan.control, scan.page) then
            restorePage()
            finishScan("failed", "The page request failed on " .. classNameOf(scan.control))
            return
        else
            scan.stepStart, scan.phase = now, "wait"
            writeStatus("scanning", { kind = scan.kind, boxOwnerUid = scan.owner, page = scan.page, pageCount = scan.pageCount, message = "Scanning" })
        end
    end

    if scan.phase == "wait" then
        local shown = currentPageOf(scan.control)
        if now - scan.stepStart > 0.4 and shown ~= nil and shown ~= scan.page then
            finishScan("cancelled", "The page was changed by hand")
            return
        end

        local count, ready
        if scan.kind == "Palbox" then
            local confirmedEmpty
            count, confirmedEmpty = readPalboxPage(scan.storage, scan.page, true)
            -- A page with Pals is ready at once; an empty one that is confirmed (every slot IsEmpty) after a short settling time, not the full timeout.
            ready = count > 0 or (confirmedEmpty and now - scan.stepStart > EMPTY_SETTLE)
        else
            local dimension = dpsShowing(scan.page)
            if dimension ~= nil then
                local _, owner, refused = captureDimension(dimension, "scan")
                if refused then
                    trackOpening() -- no opening yet: the menu is looked at again and the page is retried on the next tick
                else
                    scan.owner = owner or scan.owner
                    local box = dpsBoxes[owner or "unknown"]
                    local stored = box and box.pages[scan.page]
                    count, ready = stored and #stored.pals or 0, true
                end
            end
        end

        if ready or now - scan.stepStart > PAGE_TIMEOUT then
            if (count or 0) == 0 then scan.emptyRun = scan.emptyRun + 1 else scan.emptyRun = 0 end
            scan.page = scan.page + 1
            if scan.mode == "smart" and scan.emptyRun >= SMART_EMPTY_STOP then scan.phase = "finish" else scan.phase = "request" end
            -- The next page is requested in this tick (a confirmed empty page costs one loop period, not two).
            if scan.phase == "request" and (scan.page < (scan.pageCount or MAX_PAGES)) then return scanTick() end
        end
    end

    if scan.phase == "finish" then
        requestPage(scan.control, scan.startPage)
        scan.phase, scan.stepStart, scan.restoreTries = "restore", now, 0
        return
    end

    if scan.phase == "restore" then
        if now - scan.stepStart < 0.4 then return end
        local shown = currentPageOf(scan.control)
        if shown ~= nil and shown ~= scan.startPage and scan.restoreTries < 1 then
            scan.restoreTries = scan.restoreTries + 1
            requestPage(scan.control, scan.startPage)
            scan.stepStart = now
            return
        end
        finishScan("done", string.format("%d page(s) scanned; started on page %d, now on page %s (pages are 0-based)", scan.page, scan.startPage, tostring(shown)))
    end
end
end -- section: scanner

---------------------------------------------------------------------------------------------------------------
-- The party (T40): the local player's pawn -> its Otomo holder component -> the character container -> the slots. Only components that
-- belong to the local pawn are used (in co-op FindFirstOf could return another player's), class default objects are skipped, and a game function
-- is only called when the class really has it. The probe (F10) lists the holders, their owners and the members of the container.
---------------------------------------------------------------------------------------------------------------

local otomoHolders, containerOf, containerSlots, liveObjects, guidProperty, baseModelOf, baseContainerOf, structureUid
local interactionObjects, storageStructures, playerNames, pendingProbe, pendingScan, pendingClear, lastTickAt, tickCount, probeAction
local gameTick, debugTiming
do -- section: party, bases and loop
local partyNotes = {}
local function noteOnce(key, fmt, ...)
    if not partyNotes[key] then
        partyNotes[key] = true
        log(fmt, ...)
    end
end

local holderCache, lastHolderSearch = nil, -100

-- Every live Otomo holder component with the full name of its outer (the pawn that owns it).
function otomoHolders()
    local list = {}
    local ok, found = pcall(FindAllOf, "PalOtomoHolderComponentBase")
    if ok and type(found) == "table" then
        for _, component in pairs(found) do
            local object = unwrap(component)
            if valid(object) and not isDefaultObject(object) then
                local okOuter, outer = pcall(function() return unwrap(object:GetOuter()) end)
                list[#list + 1] = { object = object, outer = okOuter and valid(outer) and objectName(outer) or nil }
            end
        end
    end
    return list
end

local function ownHolder(controller)
    if holderCache ~= nil and valid(holderCache) then return holderCache end
    holderCache = nil
    local now = os.clock()
    if now - lastHolderSearch < FIND_INTERVAL then return nil end
    lastHolderSearch = now

    local wanted = { [objectName(controller)] = true }
    local pawn = member(controller, "Pawn")
    if pawn ~= nil and valid(pawn) and not isDefaultObject(pawn) then wanted[objectName(pawn)] = true end
    for _, holder in ipairs(otomoHolders()) do
        if holder.outer ~= nil and wanted[holder.outer] then
            holderCache = holder.object
            noteOnce("holder", "party: the Otomo holder of the local pawn was found (%s)", classNameOf(holder.object))
            return holderCache
        end
    end
    noteOnce("noholder", "party: no Otomo holder belongs to the local pawn or controller (the probe lists the holders)")
    return nil
end

-- A character container (PalIndividualCharacterContainer): Num(), GetSlots(), Get(i), FindEmptySlot() ... and the SlotArray property. The party's container
-- and a base's worker container are the same class, so one reader serves both. GetSlot does not exist on it.
local function looksLikeContainer(object)
    return object ~= nil and type(object) == "userdata" and valid(object) and not isDefaultObject(object)
        and (hasFunction(object, "Num") or hasFunction(object, "GetSlots") or member(object, "SlotArray") ~= nil)
end

function containerOf(holder)
    for _, name in ipairs({ "CharacterContainer", "OtomoContainer", "Container" }) do
        local container = member(holder, name)
        if looksLikeContainer(container) then return container end
    end
    for _, getter in ipairs({ "GetCharacterContainer", "GetOtomoContainer", "GetContainer" }) do
        if hasFunction(holder, getter) then
            local ok, result = pcall(function() return unwrap(holder[getter](holder)) end)
            if ok and looksLikeContainer(result) then return result end
        end
    end
    return nil
end

local CONTAINER_MAX_SLOTS = 40

-- The slots of a container, read only: the SlotArray property first; without it Get(i) limited by Num() (a native Get with an index outside the
-- array could crash the game, so there is never a loop without a real count). Returns slots, source.
function containerSlots(container)
    local list = items(member(container, "SlotArray"))
    if list ~= nil then
        local limited = {}
        for index = 1, math.min(#list, CONTAINER_MAX_SLOTS) do limited[index] = list[index] end
        return limited, "SlotArray"
    end
    if hasFunction(container, "Num") and hasFunction(container, "Get") then
        local ok, result = pcall(function() return container:Num() end)
        local count = ok and number(result) or nil
        if count ~= nil and count > 0 then
            local slots = {}
            for index = 0, math.min(math.floor(count), CONTAINER_MAX_SLOTS) - 1 do
                local okGet, slot = pcall(function() return unwrap(container:Get(index)) end)
                slots[#slots + 1] = okGet and slot or false
            end
            return slots, "Get(i), Num=" .. tostring(count)
        end
    end
    return nil, "no SlotArray and no Num/Get"
end

-- Pals of a container: slot index -> Pal (location filled in by the caller).
local function palsOfContainer(container, makeLocation)
    local slots, source = containerSlots(container)
    if slots == nil then return nil, source end
    local pals = {}
    for index, slot in ipairs(slots) do
        if slot and valid(slot) and not isDefaultObject(slot) then
            local parameter = parameterOf(slot)
            local pal = parameter ~= nil and readPal(parameter) or nil
            if pal ~= nil then
                pal.location = makeLocation(index - 1)
                pals[#pals + 1] = pal
            end
        end
    end
    return pals, source, #slots
end

-- Fallback count for a holder without a readable container (see TryGetSlotIndividualHandle below): the real slot count from a function the
-- class really has, else 0-4 (the game's party size).
local PARTY_DEFAULT_SLOTS = 5
local PARTY_MAX_SLOTS = 10

local function partySlotCount(container, holder, default)
    local candidates = { { container, "Num" }, { container, "GetSlotNum" }, { container, "GetSlotCount" }, { holder, "GetMaxOtomoNum" }, { holder, "GetSlotNum" } }
    for _, candidate in ipairs(candidates) do
        local object, name = candidate[1], candidate[2]
        if object ~= nil and valid(object) and hasFunction(object, name) then
            local ok, result = pcall(function() return object[name](object) end)
            local count = ok and number(result) or nil
            if count ~= nil and count > 0 then return math.min(math.floor(count), PARTY_MAX_SLOTS), classNameOf(object):match("[^%.]+$") .. ":" .. name end
        end
    end
    if default == nil then return nil, "no slot count found" end
    return default, "default (no slot count found)"
end

local function readParty()
    local controller = localController()
    if controller == nil then return end
    local holder = ownHolder(controller)
    if holder == nil then return end

    local pals, via
    local container = containerOf(holder)
    if container ~= nil then
        local found, source, slotCount = palsOfContainer(container, function(slot) return { kind = "Party", page = 0, slot = slot } end)
        if found ~= nil then
            pals, via = found, source
            noteOnce("slots", "party: %d slots from %s", slotCount, source)
        else
            noteOnce("nocontainerslots", "party: the container has no readable slots (%s)", tostring(source))
        end
    end
    if pals == nil and hasFunction(holder, "TryGetSlotIndividualHandle") then
        pals, via = {}, "handles"
        local slotCount, countSource = partySlotCount(container, holder, PARTY_DEFAULT_SLOTS)
        noteOnce("slots", "party: %d slots from %s", slotCount, countSource)
        for index = 0, slotCount - 1 do
            local out = {}
            local okHandle, result = pcall(function() return holder:TryGetSlotIndividualHandle(index, out) end)
            local handle = okHandle and unwrap(result) or nil
            if handle == nil or type(handle) == "boolean" then handle = unwrap(out.Handle or out.OutHandle or out.ReturnValue) end
            if handle ~= nil and type(handle) ~= "boolean" and valid(handle) and not isDefaultObject(handle) and hasFunction(handle, "TryGetIndividualParameter") then
                local outParam = {}
                local okParam, param = pcall(function() return handle:TryGetIndividualParameter(outParam) end)
                local parameter = okParam and unwrap(param) or nil
                if parameter == nil or type(parameter) == "boolean" then parameter = unwrap(outParam.Parameter or outParam.OutParameter or outParam.ReturnValue) end
                if parameter ~= nil and type(parameter) ~= "boolean" and valid(parameter) and not isDefaultObject(parameter) then
                    local pal = readPal(parameter)
                    if pal ~= nil then
                        pal.location = { kind = "Party", page = 0, slot = index }
                        pals[#pals + 1] = pal
                    end
                end
            end
        end
    end
    if pals == nil then
        noteOnce("nofn", "party: the Otomo holder has no readable container and no TryGetSlotIndividualHandle (see the probe)")
        return
    end

    -- The party is replaced as a whole: a Pal that left it must disappear.
    partyPage = partyPage or {}
    if #pals > 0 then
        if storePage(partyPage, 0, pals) then log("party: %d Pal(s) via %s", #pals, via) end
    elseif partyPage[0] ~= nil then
        partyPage[0] = nil
        dirty = true
        lastActivity = os.clock()
        log("party: empty")
    end
end

---------------------------------------------------------------------------------------------------------------
-- Base Pals (T40): the Pals that work at a base. Each PalBaseCampWorkerDirector has a CharacterContainer (the same class as the party's). The
-- director's own BaseCampId is all zeros on a client, so the base id is taken from the PalBaseCampModel it belongs to (its outer chain, or the
-- same order when no model is an outer of it; logged); the name comes from the model's BaseCampName.
---------------------------------------------------------------------------------------------------------------

local liveCache = {}

-- Live objects of a class (no default objects), searched at most every FIND_INTERVAL seconds and checked with IsValid on each use.
function liveObjects(className, interval)
    local entry = liveCache[className]
    local now = os.clock()
    if entry ~= nil and now - entry.at < (interval or FIND_INTERVAL) then
        local still = {}
        for _, object in ipairs(entry.list) do
            if valid(object) then still[#still + 1] = object end
        end
        return still
    end
    local list = {}
    local ok, found = pcall(FindAllOf, className)
    if ok and type(found) == "table" then
        for _, object in pairs(found) do
            local unwrapped = unwrap(object)
            if valid(unwrapped) and not isDefaultObject(unwrapped) then list[#list + 1] = unwrapped end
        end
    end
    liveCache[className] = { at = now, list = list }
    return list
end

function guidProperty(object, names)
    for _, name in ipairs(names) do
        local uid = guidText(member(object, name))
        if uid ~= nil and #uid == 32 and not isZeroGuid(uid) then return uid end
    end
    return nil
end


-- FVector (or FTransform: its Translation) -> x, y, z.
local function vectorOf(value)
    if value == nil then return nil end
    local vector = member(value, "Translation") or value
    local x, y, z = number(member(vector, "X")), number(member(vector, "Y")), number(member(vector, "Z"))
    if x ~= nil and y ~= nil then return x, y, z or 0 end
    return nil
end

-- Which model a director belongs to: the model that is one of its outers; else the model whose Transform is nearest to the director's
-- SpawnTransform within its AreaRange (the order of the two lists is not reliable); the same index only as the last resort.
-- The game's template name of a base the player has not named ("..(kari)", "..template name 1"): the player never sees it, so it is not exported.
-- UTF-8 escapes: \228\187\174 is the kanji of "temporary", the other is the katakana "template".
local function isPlaceholderBaseName(name)
    return name:find("(\228\187\174)", 1, true) ~= nil or name:find("\239\188\136\228\187\174\239\188\137", 1, true) ~= nil
        or name:find("\227\131\134\227\131\179\227\131\151\227\131\172\227\131\188\227\131\136", 1, true) ~= nil
end

-- T58: why the base cannot be matched to its model by position: the director's SpawnTransform, every model's Transform and AreaRange, or the
-- property names of the director when the position cannot be read at all. Logged once per base.
local function describeBasePositions(director, models)
    local function point(x, y, z) return x ~= nil and string.format("(%d, %d, %d)", math.floor(x), math.floor(y), math.floor(z or 0)) or "unreadable" end
    local parts = { "director SpawnTransform " .. point(vectorOf(member(director, "SpawnTransform"))) }
    for index, model in ipairs(models) do
        local mx, my, mz = vectorOf(member(model, "Transform"))
        parts[#parts + 1] = string.format("model %d Transform %s AreaRange %s", index, point(mx, my, mz), tostring(number(member(model, "AreaRange"))))
    end
    if vectorOf(member(director, "SpawnTransform")) == nil then
        local names = {}
        pcall(function()
            director:GetClass():ForEachProperty(function(property)
                local ok, n = pcall(function() return property:GetFName():ToString() end)
                if ok and n and #names < 60 then names[#names + 1] = n end
            end)
        end)
        parts[#parts + 1] = "director properties: " .. table.concat(names, ", ")
    end
    return table.concat(parts, "; ")
end

local function actorPosition(actor)
    local ok, location = pcall(function() return unwrap(actor:K2_GetActorLocation()) end)
    if ok and location ~= nil then
        local x, y, z = vectorOf(location)
        if x ~= nil then return x, y, z end
    end
    return nil
end

-- The models whose AreaRange contains the local player.
local function modelsAroundPlayer(models)
    local result = {}
    local controller = localController()
    local pawn = controller and member(controller, "Pawn")
    if pawn == nil or not valid(pawn) or isDefaultObject(pawn) then return result end
    local px, py = actorPosition(pawn)
    if px == nil then return result end
    for _, model in ipairs(models) do
        local mx, my = vectorOf(member(model, "Transform"))
        local range = number(member(model, "AreaRange"))
        if mx ~= nil and range ~= nil and math.sqrt((mx - px) ^ 2 + (my - py) ^ 2) <= range then result[#result + 1] = model end
    end
    return result
end

function baseModelOf(director, models, index, directorsFilled)
    local chain, current = {}, director
    for _ = 1, 8 do
        local ok, outer = pcall(function() return unwrap(current:GetOuter()) end)
        if not ok or not valid(outer) then break end
        chain[objectName(outer)] = true
        current = outer
    end
    for _, model in ipairs(models) do
        if chain[objectName(model)] then return model, "outer" end
    end
    local dx, dy, dz = vectorOf(member(director, "SpawnTransform"))
    -- The client reports the SpawnTransform as (0, 0, 0): that is "unknown", not a place.
    if dx ~= nil and dx == 0 and dy == 0 and (dz or 0) == 0 then dx = nil end
    if dx ~= nil then
        local best, bestDistance
        for _, model in ipairs(models) do
            local mx, my, mz = vectorOf(member(model, "Transform"))
            if mx ~= nil then
                local distance = math.sqrt((mx - dx) ^ 2 + (my - dy) ^ 2 + ((mz or 0) - (dz or 0)) ^ 2)
                local range = number(member(model, "AreaRange"))
                if (range == nil or distance <= range) and (bestDistance == nil or distance < bestDistance) then best, bestDistance = model, distance end
            end
        end
        if best ~= nil then return best, string.format("position (%d)", math.floor(bestDistance)) end
    end
    -- T59: no position to go by. The model that contains the player is the director's when it is the only one and this is the only director whose
    -- container holds Pals.
    if directorsFilled ~= nil then
        local count, filled = directorsFilled()
        if count == 1 and filled[index] then
            local around = modelsAroundPlayer(models)
            if #around == 1 then return around[1], "the player's area" end
        end
    end
    return models[index], "order"
end

function baseContainerOf(director)
    local container = member(director, "CharacterContainer")
    if looksLikeContainer(container) then return container end
    return nil
end

-- T118: the bases are read in steps of one base (one director), each in its own tick. The model and director lists are searched with FindAllOf at
-- most every BASE_FIND_INTERVAL seconds (a new base shows up a little later; a list that is still cached is checked with IsValid on use).
local BASE_FIND_INTERVAL = 30

-- Starts a pass over the bases: the lists are fetched here, the Pals are read by baseSweepStep, one director at a time.
local function beginBaseSweep()
    local models = liveObjects("PalBaseCampModel", BASE_FIND_INTERVAL)
    local directors = liveObjects("PalBaseCampWorkerDirector", BASE_FIND_INTERVAL)
    local sweep = { models = models, directors = directors, index = 0 }
    -- How many directors have a container that holds Pals (counted only when the position rule needs it).
    local filledCount, filledSet
    function sweep.directorsFilled()
        if filledCount == nil then
            filledCount, filledSet = 0, {}
            for position, other in ipairs(directors) do
                local otherContainer = valid(other) and baseContainerOf(other) or nil
                local otherPals = otherContainer ~= nil and palsOfContainer(otherContainer, function(slot) return { kind = "Base", page = 0, slot = slot } end) or nil
                if otherPals ~= nil and #otherPals > 0 then filledCount, filledSet[position] = filledCount + 1, true end
            end
        end
        return filledCount, filledSet
    end
    return sweep
end

-- Reads the next director of the pass; returns true when the pass is finished.
local function baseSweepStep(sweep)
    sweep.index = sweep.index + 1
    local index, models, directorsFilled = sweep.index, sweep.models, sweep.directorsFilled
    local director = sweep.directors[index]
    if director == nil then return true end
    if valid(director) then
        local container = baseContainerOf(director)
        if container ~= nil then
            local model, how = baseModelOf(director, models, index, directorsFilled)
            local baseId = model and guidProperty(model, { "ID", "Id", "BaseCampId" }) or guidProperty(director, { "BaseCampId" }) or ("base-" .. index)
            noteOnce("basemodel:" .. baseId, "base %s: matched to its model by %s", baseId:sub(-8), model and how or "nothing (no model)")
            noteOnce("basepos:" .. baseId, "base %s positions: %s", baseId:sub(-8), describeBasePositions(director, models))
            local name = model and text(member(model, "BaseCampName")) or nil
            if name ~= nil and name ~= "" and not isPlaceholderBaseName(name) then
                baseNames[baseId] = name
            elseif name ~= nil and name ~= "" then
                noteOnce("baseplaceholder:" .. baseId, "base %s: the name is the game's template name; not exported", baseId:sub(-8))
            end

            local pals, source, slotCount = palsOfContainer(container, function(slot) return { kind = "Base", page = 0, slot = slot, baseId = baseId, baseName = baseNames[baseId] } end)
            if pals ~= nil then noteOnce("basesummary:" .. baseId, "base %s: container %d slots, %d filled", baseId:sub(-8), slotCount or 0, #pals) end
            if pals == nil then
                noteOnce("basenoslots:" .. baseId, "base %s: the container has no readable slots (%s)", baseId:sub(-8), tostring(source))
            elseif #pals > 0 then
                basePages[baseId] = basePages[baseId] or {}
                if storePage(basePages[baseId], 0, pals) then log("base %s: %d Pal(s) from the worker container (%s)", baseId:sub(-8), #pals, source) end
            elseif basePages[baseId] ~= nil and (slotCount or 0) > 0 then
                -- The container is readable and every slot is empty: the Pals that worked here left (a base that is not loaded has no container at
                -- all and never reaches this line, so nothing of a remote base is deleted).
                if storePage(basePages[baseId], 0, {}) then log("base %s: now empty", baseId:sub(-8)) end
            end
        end
    end
    return index >= #sweep.directors
end

---------------------------------------------------------------------------------------------------------------
-- The owner of the storage the player opened (T28-17): the storage structure (map object) nearest to the player whose class name has
-- DimensionPalStorage; the uid of the player who built it is read from its fields and turned into a name with the player states. Every read is
-- a property read or a plain getter on a live, non-default actor; FindAllOf by class runs once per opening (resolveOwner keeps the choice).
---------------------------------------------------------------------------------------------------------------

local UID_PROPERTIES = { "BuildPlayerUId", "OwnerPlayerUId", "BuilderPlayerUId", "PlayerUId", "OwnerUId" }

-- The structure itself or the model it points to carries the uid of the player who placed it.
function structureUid(actor)
    local sources = { actor }
    for _, name in ipairs({ "MapObjectModel", "Model", "ConcreteModel", "MapObjectConcreteModel" }) do
        local sub = member(actor, name)
        if sub ~= nil and type(sub) == "userdata" and valid(sub) and not isDefaultObject(sub) then sources[#sources + 1] = sub end
    end
    for _, source in ipairs(sources) do
        for _, name in ipairs(UID_PROPERTIES) do
            local uid = guidText(member(source, name))
            if uid ~= nil and #uid == 32 and not isZeroGuid(uid) then return uid, name end
        end
    end
    return nil
end

-- Storage structures near the local player, nearest first: { actor, class, distance }.
function storageStructures(limit)
    local result = {}
    local controller = localController()
    local pawn = controller and member(controller, "Pawn")
    if pawn == nil or not valid(pawn) or isDefaultObject(pawn) then return result end
    local px, py, pz = actorPosition(pawn)
    -- T28-19: by exact class name (found by the probe), not through every actor in the world.
    local ok, found = pcall(FindAllOf, "BP_BuildObject_DimensionPalStorage_C")
    if not ok or type(found) ~= "table" then return result end
    for _, object in pairs(found) do
        local actor = unwrap(object)
        if valid(actor) then
            local className = classNameOf(actor)
            if not isDefaultObject(actor) then
                local x, y, z = actorPosition(actor)
                local distance = (x ~= nil and px ~= nil) and math.sqrt((x - px) ^ 2 + (y - py) ^ 2 + ((z or 0) - (pz or 0)) ^ 2) or math.huge
                result[#result + 1] = { actor = actor, class = className:match("[^%.]+$") or className, distance = distance }
            end
        end
    end
    table.sort(result, function(a, b) return a.distance < b.distance end)
    while #result > (limit or 5) do result[#result] = nil end
    return result
end

-- uid -> name from the player states (PlayerUId, PlayerNamePrivate).
function playerNames()
    local names = {}
    local ok, found = pcall(FindAllOf, "PalPlayerState")
    if ok and type(found) == "table" then
        for _, object in pairs(found) do
            local state = unwrap(object)
            if valid(state) and not isDefaultObject(state) then
                local uid = guidText(member(state, "PlayerUId"))
                if uid ~= nil and #uid == 32 and not isZeroGuid(uid) then
                    names[uid] = text(member(state, "PlayerNamePrivate")) or text(member(state, "PlayerName")) or names[uid]
                end
            end
        end
    end
    return names
end

-- The players the game state knows ({ uid, name }, newest read of playerNames, refreshed every 10 s): the "players" array of the JSON; the app
-- offers these names when the owner of a storage is unknown (T59/T60).
local playersCache, lastPlayersRead = {}, -100
playersList = function()
    local now = os.clock()
    if now - lastPlayersRead >= 10 then
        lastPlayersRead = now
        local list = {}
        for uid, name in pairs(playerNames()) do list[#list + 1] = { uid = uid, name = name } end
        local controller = localController()
        if controller ~= nil then
            local state = member(controller, "PlayerState")
            local ownName = state ~= nil and valid(state) and (text(member(state, "PlayerNamePrivate")) or text(member(state, "PlayerName"))) or nil
            local ownUid = state ~= nil and valid(state) and guidText(member(state, "PlayerUId")) or nil
            local known = false
            for _, entry in ipairs(list) do if entry.uid == ownUid then known = true end end
            if not known and ownUid ~= nil and not isZeroGuid(ownUid) then list[#list + 1] = { uid = ownUid, name = ownName } end
        end
        table.sort(list, function(a, b) return a.uid < b.uid end)
        playersCache = list
    end
    return playersCache
end

local INTERACT_DISTANCE = 600     -- cm: a storage closer than this can be the one the player stands at
local OTHER_OWNER_DISTANCE = 1500 -- cm: a storage of another owner this close makes "the nearest one" a guess

-- The property names of an object's class (at most `limit`).
local function propertyNames(object, limit)
    local names = {}
    pcall(function()
        object:GetClass():ForEachProperty(function(property)
            local ok, n = pcall(function() return property:GetFName():ToString() end)
            if ok and n and #names < limit then names[#names + 1] = n end
        end)
    end)
    return names
end

-- A property value that is an object (directly, or a weak pointer that resolves); nil otherwise.
local function objectOf(value)
    if value == nil or type(value) ~= "userdata" then return nil end
    if valid(value) and not isDefaultObject(value) then return value end
    return resolveWeak(value)
end

-- The uid of the Dimensional Pal Storage structure (or its model) that an object is, or sits in: the object itself, then its outers (GetOuter).
-- Only an element whose class name has DimensionPalStorage counts (the chain of a locker control also leads to windows, worlds, ...).
local function storageUidAlongOuters(object)
    local current = object
    for _ = 0, 8 do
        if current == nil or not valid(current) then return nil end
        if classNameOf(current):find("DimensionPalStorage", 1, true) then
            local uid = structureUid(current)
            if uid ~= nil then return uid, objectName(current) end
        end
        local ok, outer = pcall(function() return unwrap(current:GetOuter()) end)
        current = ok and outer or nil
    end
    return nil
end

-- What the player interacts with: the objects held by the Interact... properties of the controller and the pawn, and the target-like properties
-- of those components. Returns a list of { label, object, target } (the probe prints it; the owner search uses the targets).
function interactionObjects()
    local result = {}
    local controller = localController()
    if controller == nil then return result end
    local holders = { { "controller", controller } }
    local pawn = member(controller, "Pawn")
    if pawn ~= nil and valid(pawn) and not isDefaultObject(pawn) then holders[#holders + 1] = { "pawn", pawn } end
    for _, holder in ipairs(holders) do
        for _, name in ipairs(propertyNames(holder[2], 400)) do
            if name:find("Interact", 1, true) then
                local component = objectOf(member(holder[2], name))
                if component ~= nil then
                    result[#result + 1] = { label = holder[1] .. "." .. name, object = component }
                    for _, inner in ipairs(propertyNames(component, 300)) do
                        if inner:find("Target", 1, true) or inner:find("Object", 1, true) or inner:find("Actor", 1, true) then
                            local target = objectOf(member(component, inner))
                            if target ~= nil then result[#result + 1] = { label = holder[1] .. "." .. name .. "." .. inner, object = target, target = true } end
                        end
                    end
                end
            end
        end
    end
    return result
end

-- The owner of the storage the player opened, in this order (T58): a) the resolved WeakLockerControl: the storage structure or model on its
-- outer chain and the uid of the player who built it; b) the player's interaction target, when it is such a structure; c) the nearest storage
-- structure within reach, but only when no structure of another owner is close (the 8th test: the player stood 554 cm from one owner's storage
-- and 853 cm from the one he had opened). Returns uid, name, how; nil when it cannot be told.
structureOwner = function(dimension)
    local names
    local function named(uid) names = names or playerNames() return names[uid] end

    local control = dimension ~= nil and resolveWeak(member(dimension, "WeakLockerControl")) or nil
    if control ~= nil then
        local uid = storageUidAlongOuters(control)
        if uid ~= nil then return uid, named(uid), "the locker control chain" end
    end

    for _, entry in ipairs(interactionObjects()) do
        if entry.target then
            local uid = storageUidAlongOuters(entry.object)
            if uid ~= nil then return uid, named(uid), "the interaction target (" .. entry.label .. ")" end
        end
    end

    local near = storageStructures(8)
    local nearest = near[1]
    if nearest == nil then return nil end
    if nearest.distance > INTERACT_DISTANCE then
        noteOnce("farstructure", "storage owner: the nearest storage structure is %d cm away (more than %d): owner stays unknown", math.floor(nearest.distance), INTERACT_DISTANCE)
        return nil
    end
    local uid = structureUid(nearest.actor)
    if uid == nil then
        noteOnce("nouidstructure", "storage owner: the nearest storage structure (%s) has no readable uid: owner stays unknown", nearest.class)
        return nil
    end
    for _, other in ipairs(near) do
        if other.distance <= OTHER_OWNER_DISTANCE then
            local otherUid = structureUid(other.actor)
            if otherUid ~= nil and otherUid ~= uid then
                log("storage owner: storages of different owners are within %d cm (%s and %s): owner stays unknown", OTHER_OWNER_DISTANCE, uid:sub(-8), otherUid:sub(-8))
                return nil
            end
        end
    end
    return uid, named(uid), "the nearest structure (" .. math.floor(nearest.distance) .. " cm)"
end

---------------------------------------------------------------------------------------------------------------
-- The loop. Everything that touches the game runs on the game thread (ExecuteInGameThread); the LoopAsync thread only schedules.
--   every tick (250 ms): a cheap fingerprint check of the player's own Dimensional Pal Storage object (cached, checked with IsValid).
--   slow work (T118: at most one small job per tick, see slowStep): menu search, party, one base, one Pal Box page, one idle fingerprint page.
--   quick work (every 250 ms, only while a box UI is open or was in the last 10 s, or a scan runs): the page that is shown right now.
---------------------------------------------------------------------------------------------------------------

-- Key presses only set a flag; the loop acts on it at its next tick, so there is one owner of the game-thread work (the probe used to run in a
-- separate ExecuteInGameThread job next to the loop).
pendingProbe, pendingScan, pendingClear = false, false, false
lastTickAt, tickCount = os.time(), 0
-- probeAction (declared above) -- set after the probe is defined

local lastExport = -100
local ownDimension, lastDimensionLookup = nil, -100
local lastIdentityCheck, identity = -100, nil

---------------------------------------------------------------------------------------------------------------
-- T118: the game thread must never be held for long. Every job below is measured (os.clock): a summary line per minute goes to the log (and one
-- line per job when "debugTiming" is "on" in bridge\settings.json). The slow work is split into small jobs, at most ONE per tick:
--   party (one read), one base per job, one Pal Box page per job (two while a box menu is open), one cheap fingerprint page per job, the menu search.
-- The whole Pal Box is read again only when the box menu was open (once after it closed), after a cache clear, or when the fingerprint sees a change.
---------------------------------------------------------------------------------------------------------------

local JOB_BUDGET_MS = 2
local perf, perfSince, jobErrors = {}, os.clock(), {}

local function notePerf(name, ms)
    local s = perf[name]
    if s == nil then
        s = { n = 0, total = 0, max = 0, over = 0 }
        perf[name] = s
    end
    s.n, s.total = s.n + 1, s.total + ms
    if ms > s.max then s.max = ms end
    if ms > JOB_BUDGET_MS then s.over = s.over + 1 end
    if debugTiming then log("timing %s: %.2f ms", name, ms) end
end

local function flushPerf(now)
    perfSince = now
    local names = {}
    for name in pairs(perf) do names[#names + 1] = name end
    if #names == 0 then return end
    table.sort(names)
    local parts = {}
    for _, name in ipairs(names) do
        local s = perf[name]
        parts[#parts + 1] = string.format("%s n=%d avg %.2f max %.2f ms%s", name, s.n, s.total / s.n, s.max, s.over > 0 and (" over " .. s.over) or "")
    end
    perf = {}
    log("timing (last minute, one game-thread job each, budget %d ms): %s", JOB_BUDGET_MS, table.concat(parts, "; "))
end

-- Runs one job, measures it and keeps an error in it from stopping the others (logged once per message).
local function timed(name, fn, ...)
    local started = os.clock()
    local ok, a, b = pcall(fn, ...)
    notePerf(name, (os.clock() - started) * 1000)
    if not ok then
        local message = tostring(a)
        if jobErrors[name] ~= message then
            jobErrors[name] = message
            log("job %s failed: %s", name, message)
        end
        return nil
    end
    return a, b
end

-- Menu widgets found by the (rare) search, kept while they are valid: a closed menu stays in memory, so its opening is seen with a plain IsActivated
-- check every tick instead of a search.
local menuWidgets = { dim = {}, palbox = {} }
local lastQuickSearch = -100
local MENU_KNOWN_INTERVAL = 30   -- both menu classes are known: search again only this often (new instances)
local MENU_MISSING_INTERVAL = 10 -- a menu class has no widget yet (the game may create it when it is first opened): look every 10 s

-- The state of the slow jobs.
local box = { sweep = nil, requested = true, epoch = cacheEpoch, lastSweep = -100, wasOpen = false, fp = { page = 0 }, lastFp = -100 }
local lastParty, baseSweep, lastBaseSweep = -100, nil, -100
local PARTY_OPEN_INTERVAL, PARTY_IDLE_INTERVAL = 2, 10     -- seconds (box menu open / otherwise)
local BASE_OPEN_INTERVAL, BASE_IDLE_INTERVAL = 10, 30
local FINGERPRINT_INTERVAL = 0.5                           -- one box page is checked this often while the menu is closed

-- Every UObject the mod keeps between ticks is dropped here (T49-6: a stale pointer is what a game-thread crash looks like from the outside; the
-- caches are short anyway, and they are all emptied when the controller or the character changes). Read data (pages, names) is not touched.
local function dropObjectCaches()
    controllerCache, utilityCache, holderCache, ownDimension = nil, nil, nil, nil
    liveCache = {}
    uiCache.dims, uiCache.palboxControl, uiCache.palboxMenu, uiCache.at = {}, nil, nil, -100
    uiMenus = {}
    menuWidgets = { dim = {}, palbox = {} }
    lastQuickSearch = -100
    baseSweep = nil
    lastFingerprint = {}
    lastHolderSearch, lastControllerLookup = -100, -100
end

-- A different controller or pawn (joined another world, respawned): everything cached is thrown away, also the pages (they belong to the old session).
local function checkIdentity(now)
    if now - lastIdentityCheck < 1 then return end
    lastIdentityCheck = now
    local controller = localController()
    local key = "none"
    if controller ~= nil then
        local pawn = member(controller, "Pawn")
        key = objectName(controller) .. "|" .. ((pawn ~= nil and valid(pawn)) and objectName(pawn) or "no pawn")
    end
    if identity ~= nil and key ~= identity and key ~= "none" then
        log("controller or character changed: every cache is cleared")
        dropObjectCaches()
        resetCache()
        worldIdCache = nil
        lastContent = nil
        dirty = true
    end
    if key ~= "none" then identity = key end
end

-- Is a box menu open? From the cached widgets only (IsActivated is a plain call); no search.
local function storageMenuCached()
    for _, menu in ipairs(uiMenus) do
        if valid(menu) and isShown(menu) then return true end
    end
    return false
end

local function menuOpenCached()
    for _, menu in ipairs(uiMenus) do
        if valid(menu) and isShown(menu) then return true end
    end
    if uiCache.palboxMenu ~= nil and valid(uiCache.palboxMenu) and isShown(uiCache.palboxMenu) then return true end
    return false
end

-- The two menu classes by exact name (no page-list search): finds out whether a box menu is open. The full UI search (page controls) is refreshUi.
local function quickMenuSearch()
    lastQuickSearch = os.clock()
    uiMenus = allWidgets("WBP_IngameMenu_PalBoxLocker_C")
    menuWidgets.dim = uiMenus
    menuWidgets.palbox = allWidgets("WBP_PalStorageMenu_C")
    for _, menu in ipairs(menuWidgets.palbox) do
        if isShown(menu) then uiCache.palboxMenu = menu break end
    end
end

local function liveCount(list)
    local count = 0
    for _, object in ipairs(list) do
        if valid(object) then count = count + 1 end
    end
    return count
end

-- Is the menu search due? With both menu classes known (their widgets stay in memory when closed) only every MENU_KNOWN_INTERVAL (30 s); a class
-- with no live widget is looked for every MENU_MISSING_INTERVAL (10 s). Never more often than FIND_INTERVAL.
local function menuSearchDue(now)
    local since = now - lastQuickSearch
    if since < FIND_INTERVAL then return false end
    if since >= MENU_KNOWN_INTERVAL then return true end
    if since < MENU_MISSING_INTERVAL then return false end
    return liveCount(menuWidgets.dim) == 0 or liveCount(menuWidgets.palbox) == 0
end

-- The cheap opening check: is one of the known menu widgets shown now? (A plain IsActivated per widget, no search.) Returns true when one is.
local function peekMenus()
    for _, menu in ipairs(menuWidgets.dim) do
        if valid(menu) and isShown(menu) then
            uiMenus = menuWidgets.dim
            return true
        end
    end
    for _, menu in ipairs(menuWidgets.palbox) do
        if valid(menu) and isShown(menu) then
            uiCache.palboxMenu = menu
            return true
        end
    end
    return false
end

-- One slow job per call (at most). Order: the menu search, the party, a base, the box pages (a sweep), the idle box fingerprint.
-- T121: the capture progress of the own player (the first 5 catches of a species give bonus experience, the 5th a Mimog statue), from the record data
-- of the player state. One small job once a minute (a retry after 10 s while the data is not there); the property names are the save file's, so they
-- are tried as they are and the first miss is logged once. Nothing is written to the game.
local CAPTURE_INTERVAL, CAPTURE_RETRY = 60, 10
local lastCapture = -100

-- T132: on a client the game's running class keeps these fields as replicated fast arrays (a struct with Items[] { Key: Name, Value: Int }), not as
-- TMap properties. The Items array is read first; the map read stays as the fallback (a host or a game version that has the map).
local function readCountMap(record, name)
    local field = member(record, name)
    if field == nil then return nil end
    local result, size = {}, 0
    local fromItems = items(member(field, "Items"))
    if fromItems ~= nil then
        for _, element in ipairs(fromItems) do
            local k, v = text(member(element, "Key")), number(member(element, "Value"))
            if k ~= nil and v ~= nil and k ~= "None" and k ~= "" then
                result[k] = v
                size = size + 1
            end
        end
        return result, size
    end
    local ok = pcall(function()
        field:ForEach(function(key, value)
            local k, v = text(key), number(value)
            if k ~= nil and v ~= nil and k ~= "None" and k ~= "" then
                result[k] = v
                size = size + 1
            end
        end)
    end)
    if not ok then return nil end
    return result, size
end

-- EPalRelicType by its number (the usmap order); a client gives an enum value as a number, a save as a name.
local RELIC_TYPES = { [0] = "CapturePower", "HungerReduction", "SwimSpeed", "FoodDecayReduction", "JumpPower", "GliderSpeed", "ClimbSpeed",
    "StatusAilmentResist", "StaminaReduction", "SphereHoming", "ExpBonus", "RainbowPassiveRate", "MoveSpeed" }

local function relicTypeName(value)
    local n = type(value) == "number" and value or nil
    if n ~= nil then return RELIC_TYPES[n] end
    local s = text(value)
    if s == nil then return nil end
    s = s:match("([^:]+)$") or s
    local asNumber = tonumber(s)
    if asNumber ~= nil then return RELIC_TYPES[asNumber] end
    return s
end

-- T133: the effigies of the own player. have = RelicPossessNumArray.Entries[] { Type, Num } (not a fast array: it may not reach a client, the probe logs
-- it); collected = the number of Value = true entries of RelicObtainForInstanceFlag_<Type>.Items. Returns the table, and whether anything was readable.
local function readRelics(record)
    local result, found = {}, false
    local entries = items(member(member(record, "RelicPossessNumArray"), "Entries"))
    if entries ~= nil then
        found = true
        for _, element in ipairs(entries) do
            local kind, num = relicTypeName(member(element, "Type")), number(member(element, "Num"))
            if kind ~= nil and num ~= nil then
                result[kind] = result[kind] or {}
                result[kind].have = num
            end
        end
    end
    for _, kind in pairs(RELIC_TYPES) do
        -- A readable array that does not list a type means none in hand (the save's map does the same).
        if entries ~= nil then
            result[kind] = result[kind] or {}
            result[kind].have = result[kind].have or 0
        end
        local list = items(member(member(record, "RelicObtainForInstanceFlag_" .. kind), "Items"))
        if list ~= nil then
            found = true
            local collected = 0
            for _, element in ipairs(list) do
                local flag = unwrap(member(element, "Value"))
                if flag == true then collected = collected + 1 end
            end
            result[kind] = result[kind] or {}
            result[kind].collected = collected
        end
    end
    return result, found
end

-- T133: the cumulative effigy cost of every rank per type (data/gamedata.json "relics", from DT_PlayerStatusRankMasterDataTable). The level of a type is the
-- highest rank whose cumulative cost is <= what was spent (collected - in hand). Only the F10 log uses it; a test compares it with the data bundle.
local RELIC_THRESHOLDS = {
    CapturePower = { 1, 3, 6, 10, 15, 21, 28, 37, 46, 55, 64, 73, 82, 91, 100 },
    HungerReduction = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 },
    SwimSpeed = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 },
    FoodDecayReduction = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 },
    JumpPower = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 },
    GliderSpeed = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 },
    ClimbSpeed = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 },
    StatusAilmentResist = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 },
    StaminaReduction = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 },
    SphereHoming = { 1, 2, 3, 4 },
    ExpBonus = { 1, 2, 3, 4 },
    RainbowPassiveRate = { 1, 2, 3, 4 },
    MoveSpeed = { 1, 4, 7, 10, 13, 16, 19, 22, 25, 28, 31, 34, 37, 40, 43, 46, 49, 52, 55, 58, 61, 64, 67, 70, 73, 76, 79, 82, 85, 88, 91, 94, 97, 100, 103, 106, 109, 112, 115, 118, 121, 124, 127, 130, 133, 136, 139, 142, 145, 148, 151, 154, 157, 160, 163, 166, 169, 172, 175, 178, 181, 184, 187, 190, 193, 196, 199, 202, 205, 208, 211, 214, 217, 220, 223, 226, 229, 232, 235, 239, 243, 247, 251, 255, 259, 263, 267, 271, 275, 279, 283, 287 },
}

local function relicLevel(kind, spent)
    local level = 0
    for rank, needed in ipairs(RELIC_THRESHOLDS[kind] or {}) do
        if needed <= spent then level = rank end
    end
    return level
end

local captureEmpty = 0

local function captureStep(controller)
    local state = member(controller, "PlayerState")
    local record = state ~= nil and member(state, "RecordData") or nil
    if record == nil then
        noteOnce("capture-record", "capture: the player state has no readable RecordData (client without the record data?); capture progress is skipped")
        return false
    end
    local bonus, bonusSize = readCountMap(record, "PalCaptureBonusCount")
    local count, countSize = readCountMap(record, "PalCaptureCount")
    if bonus == nil and count == nil then
        noteOnce("capture-maps", "capture: RecordData has no readable PalCaptureBonusCount / PalCaptureCount (neither Items nor a map); capture progress is skipped")
        return false
    end
    -- Empty arrays right after joining: try again every 10 s; after three empty reads it is taken as "nothing caught yet".
    if (bonusSize or 0) == 0 and (countSize or 0) == 0 and captureEmpty < 3 then
        captureEmpty = captureEmpty + 1
        noteOnce("capture-empty", "capture: the capture arrays are empty (the data may not have arrived yet); trying again")
        return false
    end
    local relics, relicsFound = readRelics(record)
    local data = { bonus = bonus or {}, count = count or {} }
    if captureData == nil or jsonValue(captureData) ~= jsonValue(data) then
        captureData = data
        dirty = true
    end
    local relicData = relicsFound and relics or nil
    if relicData == nil then
        noteOnce("relics-none", "relics: RecordData has no readable RelicPossessNumArray / RelicObtainForInstanceFlag_*; effigy progress is skipped")
    end
    if (relicData == nil) ~= (relicsData == nil) or (relicData ~= nil and jsonValue(relicData) ~= jsonValue(relicsData)) then
        relicsData = relicData
        dirty = true
    end
    return true
end

local function slowStep(now, menuOpen)
    if box.epoch ~= cacheEpoch then
        -- Cache cleared (new character, F11): the whole box is read again, step by step.
        box.epoch, box.sweep, box.requested = cacheEpoch, nil, true
    end
    if scan == nil and not menuOpen and menuSearchDue(now) then
        timed("menu-find", quickMenuSearch)
        return
    end

    local partyDue = now - lastParty >= (menuOpen and PARTY_OPEN_INTERVAL or PARTY_IDLE_INTERVAL)
    local baseDue = baseSweep ~= nil or now - lastBaseSweep >= (menuOpen and BASE_OPEN_INTERVAL or BASE_IDLE_INTERVAL)
    local boxDue = box.sweep ~= nil or box.requested or (menuOpen and now - box.lastSweep >= AUTO_SECONDS)
    local fingerprintDue = not menuOpen and now - box.lastFp >= FINGERPRINT_INTERVAL
    local captureDue = now - lastCapture >= CAPTURE_INTERVAL
    if not (partyDue or baseDue or boxDue or fingerprintDue or captureDue) then return end

    local controller = localController()
    if controller == nil then return end
    if captureDue then
        lastCapture = now
        if timed("capture", captureStep, controller) ~= true then lastCapture = now - (CAPTURE_INTERVAL - CAPTURE_RETRY) end
        return
    end
    local storage, _, data = palStorage(controller)
    if storage == nil then return end
    if (ownDimension == nil or not valid(ownDimension)) and now - lastDimensionLookup >= AUTO_SECONDS then
        lastDimensionLookup = now
        ownDimension = dimensionStorage(storage, data)
    end

    if partyDue then
        lastParty = now
        timed("party", readParty)
    elseif baseDue then
        if baseSweep == nil then
            baseSweep = timed("bases-find", beginBaseSweep)
            lastBaseSweep = now
        else
            local finished = timed("base", baseSweepStep, baseSweep)
            if finished ~= false then baseSweep = nil end   -- done, or the step failed (nil): the next pass starts over
        end
    elseif boxDue then
        if box.sweep == nil then
            box.sweep, box.requested, box.lastSweep = beginPagesSweep(storage), false, now
        end
        for _ = 1, menuOpen and 2 or 1 do
            local finished = timed("box-page", sweepPagesStep, storage, box.sweep)
            if finished ~= false then
                box.sweep, box.lastSweep = nil, now   -- the interval for the next sweep while a menu is open counts from here
                break
            end
        end
    else
        box.lastFp = now
        local changed = timed("box-check", fingerprintPagesStep, storage, box.fp)
        if changed == "sweep" then
            box.requested = true
        elseif changed ~= nil then
            timed("box-page", readPalboxPage, storage, changed, false)
        end
    end
end

local function fastTick()
    refreshUi(false)
    for _, dimension in ipairs(uiCache.dims) do
        if valid(dimension) then pollDimension(dimension) end
    end
    local control = uiCache.palboxControl
    if control ~= nil and valid(control) then
        local controller = localController()
        local storage = controller and palStorage(controller)
        local page = currentPageOf(control)
        if storage ~= nil and page ~= nil then readPalboxPage(storage, page, true) end
    end
end

local function gameTickBody(now)
    lastTickAt, tickCount = os.time(), tickCount + 1
    checkIdentity(now)
    trackOpening()
    if pendingClear then
        pendingClear = false
        resetCache()
        lastContent = nil
        dirty = true
        lastFingerprint = {}
        log("cache cleared")
    end
    if pendingScan then
        pendingScan = false
        startScan("key")
    end
    if pendingProbe then
        pendingProbe = false
        local ok, err = pcall(probeAction)
        if not ok then log("probe error: %s", tostring(err)) end
    end
    scanTick()
    if ownDimension ~= nil and valid(ownDimension) then
        -- A page of the own storage arrived (or its capture was refused for want of an active menu): the storage UI is probably open, so its menus
        -- are looked for at once (at most once a second) and the opening starts in this tick.
        local changed, refused = pollDimension(ownDimension)
        -- Event-driven (a storage page arrived or was refused): at most every 2 s, and not while a storage menu is already known to be open.
        if (changed or refused) and not storageMenuCached() and now - lastQuickSearch >= 2 then
            timed("menu-find", quickMenuSearch)
            trackOpening()
        end
    end
    -- A menu that is already known (kept in memory by the game) is seen opening with a plain check, no search.
    if scan == nil and not menuOpenCached() then timed("menu-peek", peekMenus) end
    -- The quick work runs while a box menu is open (or a scan runs), whatever happened last; with the menu closed there is no search at all.
    local menuOpen = scan ~= nil or menuOpenCached()
    if menuOpen then
        box.wasOpen = true
        timed("fast", fastTick)
    else
        if next(uiCache.dims) ~= nil or uiCache.palboxControl ~= nil then
            -- The menu closed: the UI objects are let go (they are found again when a menu opens).
            uiCache.dims, uiCache.palboxControl, uiCache.palboxMenu, uiCache.at = {}, nil, nil, -100   -- (the next opening refreshes at once)
            uiMenus = {}
        end
        if box.wasOpen then
            -- The box menu was open and is closed now: the box is read once more (the Pals may have been moved), step by step.
            box.wasOpen, box.requested = false, true
        end
    end

    slowStep(now, menuOpen)
    if dirty and now - lastExport >= AUTO_SECONDS then
        lastExport = now
        dirty = false
        timed("export", export)
    end
    return menuOpen
end

-- One game-thread job of the loop (what ExecuteInGameThread runs); measured as a whole.
function gameTick()
    local started = os.clock()
    local ok, menuOpen = pcall(gameTickBody, started)
    notePerf(ok and (menuOpen and "tick (menu open)" or "tick (idle)") or "tick (failed)", (os.clock() - started) * 1000)
    if os.clock() - perfSince >= 60 then flushPerf(os.clock()) end
    if not ok then error(menuOpen, 0) end
end
end -- section: party, bases and loop

---------------------------------------------------------------------------------------------------------------
-- probe: what does the game expose? (output goes to UE4SS.log)
---------------------------------------------------------------------------------------------------------------

local probe, DEFAULT_SETTINGS, readSettings, parseShortcut
do -- section: probe and settings
local PROBE_MAX_LINES = 800 -- UE4SS.log is already large; the probe never writes more than this per press
local probeLines = 0

local function plog(fmt, ...)
    probeLines = probeLines + 1
    if probeLines > PROBE_MAX_LINES then
        if probeLines == PROBE_MAX_LINES + 1 then print(string.format("[%s] probe output truncated at %d lines\n", MOD, PROBE_MAX_LINES)) end
        return
    end
    local ok, line = pcall(string.format, fmt, ...)
    print(string.format("[%s] %s\n", MOD, ok and line or tostring(fmt)))
end

-- The struct behind a property (a struct property, or an array of structs through GetInner()).
local function structOf(property)
    local ok, struct = pcall(function() return property:GetStruct() end)
    if ok and struct ~= nil then return struct end
    local okInner, inner = pcall(function() return property:GetInner() end)
    if okInner and inner ~= nil then
        local okStruct, innerStruct = pcall(function() return inner:GetStruct() end)
        if okStruct then return innerStruct end
    end
    return nil
end

local function listStruct(label, struct, depth)
    local names, nested = {}, {}
    pcall(function()
        struct:ForEachProperty(function(field)
            local okF, n = pcall(function() return field:GetFName():ToString() end)
            if okF then
                names[#names + 1] = n
                if depth < 2 and n ~= "SaveParameter" then
                    local sub = structOf(field)
                    if sub ~= nil then nested[#nested + 1] = { n, sub } end
                end
            end
        end)
    end)
    plog("probe %s fields: %s", label, table.concat(names, ", "))
    for _, entry in ipairs(nested) do listStruct(label .. "." .. entry[1], entry[2], depth + 1) end
end

local STRUCT_PROBES = { SaveParameter = true, PageReplicationData = true }

local function describeClass(label, object)
    if not valid(object) then plog("probe %s: not available", label) return end
    local okName, className = pcall(function() return object:GetClass():GetFullName() end)
    plog("probe %s: %s", label, okName and className or "?")
    local ok = pcall(function()
        local class = object:GetClass()
        local names = {}
        local structs = {}
        class:ForEachProperty(function(property)
            local okP, n = pcall(function() return property:GetFName():ToString() end)
            if okP then
                names[#names + 1] = n
                if STRUCT_PROBES[n] then structs[#structs + 1] = { n, property } end
            end
        end)
        plog("probe %s properties: %s", label, table.concat(names, ", "))
        -- The fields of the structs that hold the Pal data (species, passives, IVs; the pages of the Dimensional storage).
        for _, entry in ipairs(structs) do
            local struct = structOf(entry[2])
            if struct ~= nil then listStruct(label .. "." .. entry[1], struct, 0) end
        end
        local functions = {}
        class:ForEachFunction(function(fn)
            local okF, n = pcall(function() return fn:GetFName():ToString() end)
            if okF then functions[#functions + 1] = n end
        end)
        plog("probe %s functions: %s", label, table.concat(functions, ", "))
    end)
    if not ok then plog("probe %s: members cannot be listed", label) end
end

-- F10: read-only discovery. It writes class, property and function NAMES and property values; it calls no game function, with two exceptions:
-- the weak pointer Get() that resolves WeakLockerControl, and findControl(), which is the same call the scan uses to pick its page control.
-- Class default objects ("Default__...") are never touched. Every section starts with a "probe step" line, so a crash shows where it happened.
function probe()
    probeLines = 0
    plog("probe step: controller")
    local controller = localController()
    if controller == nil then plog("probe: no local player controller") return end

    plog("probe step: record data (T132, T133, read only)")
    do
        local state = member(controller, "PlayerState")
        local record = state ~= nil and member(state, "RecordData") or nil
        if record == nil then
            plog("probe record: no readable RecordData")
        else
            for _, name in ipairs({ "PalCaptureBonusCount", "PalCaptureCount" }) do
                local field = member(record, name)
                local list = field ~= nil and items(member(field, "Items")) or nil
                if list == nil then
                    plog("probe record %s: %s", name, field == nil and "not readable" or "no Items array")
                else
                    local sample = {}
                    for index = 1, math.min(5, #list) do
                        sample[#sample + 1] = tostring(text(member(list[index], "Key"))) .. "=" .. tostring(number(member(list[index], "Value")))
                    end
                    plog("probe record %s: %d item(s); first: %s", name, #list, table.concat(sample, ", "))
                end
            end
            -- The effigies: what the game screen shows is compared with this list (collected, in hand, the level from collected - in hand).
            local entries = items(member(member(record, "RelicPossessNumArray"), "Entries"))
            plog("probe record RelicPossessNumArray.Entries: %s", entries == nil and "not readable (not synchronised to this client?)" or (#entries .. " entrie(s)"))
            local relics, found = readRelics(record)
            local mimog = 0
            if captureData ~= nil then
                for _, bonus in pairs(captureData.bonus) do if bonus >= 5 then mimog = mimog + 1 end end
            end
            if not found then plog("probe record relics: nothing readable") end
            for _, kind in ipairs({ "CapturePower", "HungerReduction", "SwimSpeed", "FoodDecayReduction", "JumpPower", "GliderSpeed", "ClimbSpeed",
                "StatusAilmentResist", "StaminaReduction", "SphereHoming", "ExpBonus", "RainbowPassiveRate", "MoveSpeed" }) do
                local entry = relics[kind] or {}
                local have, collected = entry.have, entry.collected
                if kind == "MoveSpeed" then collected = mimog end
                local spent = (collected ~= nil and have ~= nil) and (collected - have) or nil
                plog("probe relic %s: collected=%s have=%s spent=%s level=%s", kind, tostring(collected), tostring(have), tostring(spent), spent ~= nil and tostring(relicLevel(kind, spent)) or "?")
            end
        end
    end

    plog("probe step: controller storage/page functions")
    local function listFunctions(label, object)
        local names = {}
        for name in pairs(functionNamesOf(object)) do
            local lower = name:lower()
            if lower:find("storage", 1, true) or lower:find("page", 1, true) or lower:find("sync", 1, true) or lower:find("locker", 1, true) then names[#names + 1] = name end
        end
        table.sort(names)
        plog("probe %s (%s): %d function(s): %s", label, classNameOf(object):match("[^%.]+$") or "?", #names, table.concat(names, ", "))
    end
    listFunctions("player controller", controller)
    local transmitter = member(controller, "Transmitter")
    if transmitter ~= nil and valid(transmitter) and not isDefaultObject(transmitter) then
        listFunctions("transmitter", transmitter)
        local shown = 0
        pcall(function()
            transmitter:GetClass():ForEachProperty(function(property)
                if shown >= 16 then return end
                local ok, name = pcall(function() return property:GetFName():ToString() end)
                if ok and name then
                    local value = member(transmitter, name)
                    if value ~= nil and type(value) == "userdata" and valid(value) and not isDefaultObject(value) and classNameOf(value) ~= "" then
                        shown = shown + 1
                        listFunctions("transmitter." .. name, value)
                    end
                end
            end)
        end)
    else
        plog("probe transmitter: not available")
    end

    plog("probe step: storage classes")
    local storage, problem, data = palStorage(controller)
    describeClass("player data storage", data)
    describeClass("pal storage", storage)
    if storage == nil then plog("probe: %s", tostring(problem)) end

    plog("probe step: dimension storage objects")
    local boxes = dimensionObjects()
    plog("probe dimension: %d storage object(s); GetDimensionStoragePageNum = %s", #boxes, tostring(pageNumValue))
    for index, box in ipairs(boxes) do
        local replicated = member(box, "PageReplicationData")
        plog("probe dimension %d: %s owner=%s CurrentPage=%s replicatedPage=%s PageData=%s", index, objectName(box):sub(-60),
            tostring(guidText(member(box, "OwnerPlayerUId"))), tostring(number(member(box, "CurrentPage"))),
            tostring(replicated and number(member(replicated, "pageIndex"))), tostring(replicated and length(member(replicated, "PageData"))))
        if index == 1 then describeClass("dimension storage", box) end
    end

    plog("probe step: weak locker control")
    local resolvedLocker
    for _, box in ipairs(boxes) do
        resolvedLocker = resolveWeak(member(box, "WeakLockerControl"))
        if resolvedLocker ~= nil then break end
    end
    if resolvedLocker ~= nil then describeClass("weak locker control", resolvedLocker) else plog("probe weak locker control: not available") end

    do
        local op = (currentOpening ~= nil and boxes[1] ~= nil) and currentOpening(boxes[1]) or nil
        plog("probe opening: %s", op and string.format("#%d locker=%s", op.seq, tostring(op.locker)) or "none (no storage menu is active)")
        -- The outer chain of the resolved locker control: class, name and the uid fields of every element (the owner is looked for here).
        local current, depth = resolvedLocker, 0
        while current ~= nil and valid(current) and depth <= 8 do
            local uids = {}
            for _, name in ipairs({ "BuildPlayerUId", "OwnerPlayerUId", "BuilderPlayerUId", "PlayerUId", "OwnerUId" }) do
                local uid = guidText(member(current, name))
                if uid ~= nil and not isZeroGuid(uid) then uids[#uids + 1] = name .. "=" .. uid:sub(-8) end
            end
            plog("probe locker chain %d: %s %s %s", depth, classNameOf(current), objectName(current):sub(-80), table.concat(uids, " "))
            local ok, outer = pcall(function() return unwrap(current:GetOuter()) end)
            current, depth = ok and outer or nil, depth + 1
        end
        if resolvedLocker == nil then plog("probe locker chain: the locker control is not resolved") end
        for _, entry in ipairs(interactionObjects and interactionObjects() or {}) do
            plog("probe interaction: %s -> %s %s%s", entry.label, classNameOf(entry.object), objectName(entry.object):sub(-80), entry.target and " (target)" or "")
        end
    end

    plog("probe step: owner discovery (T59, read only)")
    do
        local function objectValue(value)
            if type(value) ~= "userdata" then return nil end
            if valid(value) and not isDefaultObject(value) then return value end
            return resolveWeak(value)
        end
        local function propertyList(object, limit)
            local names = {}
            pcall(function()
                object:GetClass():ForEachProperty(function(property)
                    local ok, n = pcall(function() return property:GetFName():ToString() end)
                    if ok and n and #names < limit then names[#names + 1] = n end
                end)
            end)
            return names
        end
        -- Every property of `object` that holds an object: class, name and the fields of that object that mention a uid or an owner.
        local function describeObjects(label, object)
            for _, name in ipairs(propertyList(object, 150)) do
                local value = objectValue(member(object, name))
                if value ~= nil then
                    local fields = {}
                    for _, inner in ipairs(propertyList(value, 120)) do
                        local lower = inner:lower()
                        if (lower:find("uid", 1, true) or lower:find("owner", 1, true)) and #fields < 8 then
                            local v = member(value, inner)
                            fields[#fields + 1] = inner .. "=" .. tostring(guidText(v) or text(v) or number(v) or "?")
                        end
                    end
                    plog("probe %s.%s: %s %s %s", label, name, classNameOf(value), objectName(value):sub(-70), table.concat(fields, " "))
                end
            end
        end

        plog("probe discovery: storage menu widgets")
        local menus = {}
        for _, menu in ipairs(uiMenus) do if valid(menu) and #menus < 2 then menus[#menus + 1] = menu end end
        for _, widget in ipairs(allWidgets("WBP_IngameMenu_PalBoxLockerList_C")) do if #menus < 4 then menus[#menus + 1] = widget end end
        for index, widget in ipairs(menus) do describeObjects("menu " .. index .. " (" .. classNameOf(widget) .. ")", widget) end
        if #menus == 0 then plog("probe discovery: no storage menu widget (open the storage and press the key again)") end

        plog("probe discovery: interact components")
        local okFind, components = pcall(FindAllOf, "PalInteractComponent")
        local controller = localController()
        local pawn = controller and member(controller, "Pawn")
        local mine = {}
        if controller ~= nil then mine[objectName(controller)] = "controller" end
        if pawn ~= nil and valid(pawn) then mine[objectName(pawn)] = "pawn" end
        local shown = 0
        if okFind and type(components) == "table" then
            for _, object in pairs(components) do
                local component = unwrap(object)
                if shown < 8 and valid(component) and not isDefaultObject(component) then
                    local okOuter, outer = pcall(function() return unwrap(component:GetOuter()) end)
                    local outerName = okOuter and valid(outer) and objectName(outer) or "?"
                    plog("probe interact component: %s %s outer=%s%s", classNameOf(component), objectName(component):sub(-60), outerName:sub(-60),
                        mine[outerName] and (" (" .. mine[outerName] .. " of the local player)") or "")
                    if mine[outerName] then
                        describeObjects("interact component", component)
                        shown = shown + 1
                    end
                end
            end
        end
        if shown == 0 then plog("probe discovery: no interact component belongs to the local controller or pawn") end

        plog("probe discovery: SaveParameter.OwnerPlayerUId of the Pals on the open page")
        for _, box in ipairs(boxes) do
            local replicated = member(box, "PageReplicationData")
            local list = replicated and items(member(replicated, "PageData")) or nil
            if list ~= nil and #list > 0 then
                local counts, order = {}, {}
                for _, element in ipairs(list) do
                    local save = member(element, "SaveParameter") or element
                    local uid = guidText(member(save, "OwnerPlayerUId"))
                    if uid ~= nil and uid ~= "" and not isZeroGuid(uid) then
                        if counts[uid] == nil then order[#order + 1] = uid end
                        counts[uid] = (counts[uid] or 0) + 1
                    end
                end
                if #order == 0 then plog("probe owner distribution: no Pal has a non-zero OwnerPlayerUId") end
                for _, uid in ipairs(order) do plog("probe owner distribution: %s = %d Pal(s)", uid, counts[uid]) end
                break
            end
        end
    end

    plog("probe step: storage menu title")
    refreshUi(true)
    plog("probe menus: %d storage menu widget(s); title owner name = %s", #uiMenus, tostring(menuTitleOwnerName()))
    if uiMenus[1] ~= nil and valid(uiMenus[1]) then describeClass("storage menu", uiMenus[1]) end

    plog("probe step: world id")
    for _, className in ipairs({ "PalGameStateInGame", "GameStateBase" }) do
        local okState, state = pcall(FindFirstOf, className)
        state = okState and unwrap(state) or nil
        if state ~= nil and valid(state) and not isDefaultObject(state) then
            plog("probe game state: %s", classNameOf(state))
            local lines = 0
            pcall(function()
                state:GetClass():ForEachProperty(function(property)
                    if lines >= 40 then return end
                    local ok, propertyName = pcall(function() return property:GetFName():ToString() end)
                    if ok and propertyName then
                        local value = member(state, propertyName)
                        local shown
                        if type(value) == "number" or type(value) == "string" or type(value) == "boolean" then
                            shown = tostring(value)
                        else
                            local guid = guidText(value)
                            if guid ~= nil and #guid == 32 then shown = "guid " .. guid else shown = text(value) end
                        end
                        if shown and shown ~= "" then
                            lines = lines + 1
                            plog("probe game state %s = %s", propertyName, shown)
                        end
                    end
                end)
            end)
        end
    end
    plog("probe world id now: %s", tostring(worldId()))

    plog("probe step: storage structures")
    local names = playerNames()
    local nameCount = 0
    for uid, playerName in pairs(names) do
        nameCount = nameCount + 1
        plog("probe player state: %s = %s", uid, tostring(playerName))
    end
    plog("probe player states: %d", nameCount)
    local structures = storageStructures(6)
    plog("probe storage structures near the player: %d", #structures)
    for index, structure in ipairs(structures) do
        local uid, field = structureUid(structure.actor)
        plog("probe structure %d: %s distance=%s uid=%s (%s) -> %s", index, structure.class, tostring(math.floor(structure.distance)), tostring(uid), tostring(field),
            tostring(uid and names[uid]))
        if index == 1 then
            describeClass("storage structure", structure.actor)
            local lines = 0
            pcall(function()
                structure.actor:GetClass():ForEachProperty(function(property)
                    if lines >= 40 then return end
                    local ok, propertyName = pcall(function() return property:GetFName():ToString() end)
                    if ok and propertyName then
                        local value = member(structure.actor, propertyName)
                        local shown
                        if type(value) == "number" or type(value) == "string" or type(value) == "boolean" then
                            shown = tostring(value)
                        else
                            local guid = guidText(value)
                            if guid ~= nil and #guid == 32 then shown = "guid " .. guid end
                        end
                        if shown then
                            lines = lines + 1
                            plog("probe structure 1 %s = %s", propertyName, shown)
                        end
                    end
                end)
            end)
        end
    end

    plog("probe step: storage texts")
    local texts = storageTexts()
    plog("probe texts: %d text widget(s) under the storage menu / display widget", #texts)
    for index, item in ipairs(texts) do
        if index <= 200 then plog("probe text: %s = %s", item.name, tostring(item.text)) end
    end

    plog("probe step: widgets")
    local seen, rows = {}, 0
    local function pageFunctionsOf(object)
        local names = {}
        for name in pairs(functionNamesOf(object)) do
            if name:find("Page", 1, true) then names[#names + 1] = name end
        end
        table.sort(names)
        return table.concat(names, ",")
    end
    for _, widget in ipairs(allWidgets("UserWidget")) do
        local name = classNameOf(widget)
        local lower = name:lower()
        if (lower:find("dimension", 1, true) or lower:find("storage", 1, true) or lower:find("box", 1, true) or lower:find("locker", 1, true)
            or lower:find("base", 1, true) or lower:find("camp", 1, true) or lower:find("worker", 1, true)) and not seen[name] then
            seen[name] = true
            rows = rows + 1
            local root
            pcall(function() root = unwrap(widget.WidgetTree.RootWidget) end)
            plog("probe widget: %s Visibility=%s page functions=[%s] root=%s root page functions=[%s]", name, tostring(number(member(widget, "Visibility"))),
                pageFunctionsOf(widget), root and valid(root) and classNameOf(root) or "-", root and valid(root) and pageFunctionsOf(root) or "-")
        end
    end
    plog("probe widgets: %d class(es) matched", rows)

    plog("probe step: page list outer chains")
    for index, list in ipairs(allWidgets("WBP_BoxPalList_C")) do
        local chain, current = {}, list
        for _ = 1, 8 do
            local ok, outer = pcall(function() return unwrap(current:GetOuter()) end)
            if not ok or not valid(outer) then break end
            chain[#chain + 1] = classNameOf(outer):match("[^%.]+$") or "?"
            current = outer
        end
        plog("probe BoxPalList %d: Visibility=%s outer chain: %s", index, tostring(number(member(list, "Visibility"))), table.concat(chain, " < "))
    end

    plog("probe step: page controls")
    local okPalbox, palboxControl = pcall(function() return (findControl("Palbox", false)) end)
    local okStorage, storageControl = pcall(function() return (findControl("DimensionalStorage", true)) end)
    plog("probe scan: Pal Box control=%s (%s), storage control=%s (%s)", tostring(okPalbox and palboxControl ~= nil), okPalbox and palboxControl and classNameOf(palboxControl) or "-",
        tostring(okStorage and storageControl ~= nil), okStorage and storageControl and classNameOf(storageControl) or "-")

    plog("probe step: party")
    local pawn = member(controller, "Pawn")
    plog("probe pawn: %s", pawn ~= nil and valid(pawn) and objectName(pawn):sub(-70) or "not available")
    local holders = otomoHolders()
    plog("probe party: %d Otomo holder component(s)", #holders)
    local wanted = { [objectName(controller)] = true }
    if pawn ~= nil and valid(pawn) then wanted[objectName(pawn)] = true end
    for index, holder in ipairs(holders) do
        local own = holder.outer ~= nil and wanted[holder.outer] == true
        plog("probe holder %d: %s outer=%s%s", index, classNameOf(holder.object), tostring(holder.outer and holder.outer:sub(-60)), own and " (local pawn or controller)" or "")
        if own then
            describeClass("own holder", holder.object)
            local container = containerOf(holder.object)
            if container ~= nil then
                describeClass("own container", container)
                local slots, source = containerSlots(container)
                plog("probe party container: %s slots (%s)", slots and tostring(#slots) or "-", tostring(source))
            else
                plog("probe own container: not found")
            end
        end
    end

    plog("probe step: base")
    local models = liveObjects("PalBaseCampModel")
    local directors = liveObjects("PalBaseCampWorkerDirector")
    plog("probe base: PalBaseCampModel=%d, PalBaseCampWorkerDirector=%d", #models, #directors)
    for index, model in ipairs(models) do
        plog("probe base model %d: ID=%s BaseCampName=%s", index, tostring(guidProperty(model, { "ID", "Id", "BaseCampId" })), tostring(text(member(model, "BaseCampName"))))
        if index == 1 then describeClass("base camp model", model) end
    end
    for index, director in ipairs(directors) do
        local container = baseContainerOf(director)
        local model, how = baseModelOf(director, models, index)
        plog("probe base director %d: BaseCampId=%s model by %s, container=%s", index, tostring(guidProperty(director, { "BaseCampId" })), model and how or "-",
            container and classNameOf(container) or "none")
        if container ~= nil then
            local slots, source = containerSlots(container)
            local filled = 0
            for _, slot in ipairs(slots or {}) do
                if slot and valid(slot) and not isDefaultObject(slot) and parameterOf(slot) ~= nil then filled = filled + 1 end
            end
            plog("probe base director %d container: %s slots (%s), %d filled", index, slots and tostring(#slots) or "-", tostring(source), filled)
            if index == 1 then describeClass("worker container", container) end
        end
    end
    plog("probe step: done")
end

---------------------------------------------------------------------------------------------------------------
-- key binds, settings and the loop
---------------------------------------------------------------------------------------------------------------

-- (Unused now: key presses only set flags. Kept for the rule it documents: a game object is never touched outside the game thread, so when
-- ExecuteInGameThread fails the action is NOT run directly, the failure is only logged.)
local function guarded(fn)
    return function()
        local scheduled, scheduleError = pcall(ExecuteInGameThread, function()
            local ok, err = pcall(fn)
            if not ok then log("error: %s", tostring(err)) end
        end)
        if not scheduled then log("ExecuteInGameThread failed for a key action (not run): %s", tostring(scheduleError)) end
    end
end

---------------------------------------------------------------------------------------------------------------
-- Shortcuts. The app writes bridge\settings.json (Settings > Mod shortcuts); it is read once at start-up, so a change needs a game restart.
--   { "scanKey": "F9", "probeKey": "F10", "clearKey": "F11", "scanMode": "smart" | "full", "debugTiming": "off" | "on" }
-- ("debugTiming" is not written by the app; add it by hand to log the time of every game-thread job.)
-- A shortcut is a key of the UE4SS Key table, optionally with Ctrl / Shift / Alt ("Ctrl+F9"). A bad name falls back to the default (logged).
---------------------------------------------------------------------------------------------------------------

DEFAULT_SETTINGS = { scanKey = "F9", probeKey = "F10", clearKey = "F11", scanMode = "smart", debugTiming = "off" }

function readSettings()
    local settings = { scanKey = DEFAULT_SETTINGS.scanKey, probeKey = DEFAULT_SETTINGS.probeKey, clearKey = DEFAULT_SETTINGS.clearKey, scanMode = DEFAULT_SETTINGS.scanMode,
                       debugTiming = DEFAULT_SETTINGS.debugTiming }
    local path = bridgeFile("settings.json")
    local file = path and io.open(path, "rb")
    if not file then return settings, false end
    local contents = file:read("*a")
    file:close()
    for name in pairs(settings) do
        local value = contents:match('"' .. name .. '"%s*:%s*"([^"]*)"')
        if value ~= nil and value ~= "" then settings[name] = value end
    end
    return settings, true
end

local keyByName = {}
for name, value in pairs(Key) do keyByName[tostring(name):upper()] = value end
local modifierByName = { CTRL = ModifierKey.CONTROL, CONTROL = ModifierKey.CONTROL, SHIFT = ModifierKey.SHIFT, ALT = ModifierKey.ALT }

-- "Ctrl+F9" -> { key = Key.F9, modifiers = { ModifierKey.CONTROL } }, or nil for an unknown name.
function parseShortcut(text)
    local shortcut = { modifiers = {} }
    for part in tostring(text):gmatch("[^%+%s]+") do
        local upper = part:upper()
        if modifierByName[upper] then
            shortcut.modifiers[#shortcut.modifiers + 1] = modifierByName[upper]
        elseif keyByName[upper] ~= nil and shortcut.key == nil then
            shortcut.key = keyByName[upper]
        else
            return nil
        end
    end
    return shortcut.key ~= nil and shortcut or nil
end
end -- section: probe and settings

local function modifierDown()
    for _, key in ipairs({ Key.CONTROL, Key.SHIFT, Key.ALT }) do
        local ok, down = pcall(IsKeyBindDown, key)
        if ok and down then return true end
    end
    return false
end

local boundShortcuts = {}

-- Binds one action. A plain key ignores the press while Ctrl/Shift/Alt is held, because UE4SS also fires the plain bind then
-- (so "Ctrl+F9" for one action does not also run the action bound to "F9").
local function bindShortcut(setting, text, handler)
    local shortcut = parseShortcut(text)
    if shortcut == nil then
        log("invalid shortcut '%s' for %s, using %s", tostring(text), setting, DEFAULT_SETTINGS[setting])
        text = DEFAULT_SETTINGS[setting]
        shortcut = parseShortcut(text)
    end
    local label = tostring(text):upper()
    if boundShortcuts[label] then
        log("shortcut %s for %s is already used by %s: not bound", tostring(text), setting, boundShortcuts[label])
        return text
    end
    boundShortcuts[label] = setting

    if #shortcut.modifiers == 0 then
        RegisterKeyBind(shortcut.key, function()
            log("shortcut pressed: %s", setting)
            if modifierDown() then log("shortcut %s ignored: Ctrl, Shift or Alt is reported as down", setting) return end
            handler()
        end)
    else
        RegisterKeyBind(shortcut.key, shortcut.modifiers, function()
            log("shortcut pressed: %s", setting)
            handler()
        end)
    end
    return text
end

probeAction = probe
local settings, settingsFileFound = readSettings()
scanMode = settings.scanMode == "full" and "full" or "smart"
debugTiming = settings.debugTiming == "on"   -- T118: one log line per game-thread job (the per-minute summary is always written)

-- If the loop has not ticked for 2 s the action runs directly (and the loop is reported as not running).
local function requestAction(name)
    local idle = os.time() - lastTickAt
    if idle > 2 then
        -- The flag stays set and the loop acts on it when it ticks again; the key thread never touches game objects.
        log("loop is not running (no tick for %d s): %s is queued until it ticks again", idle, name)
    end
    if name == "scan" then pendingScan = true elseif name == "clear" then pendingClear = true else pendingProbe = true end
end

local scanLabel = bindShortcut("scanKey", settings.scanKey, function() requestAction("scan") end)
local probeLabel = bindShortcut("probeKey", settings.probeKey, function() requestAction("probe") end)
local clearLabel = bindShortcut("clearKey", settings.clearKey, function() requestAction("clear") end)

-- The loop: LoopAsync runs on its own thread, so it only hands the work to the game thread (one job at a time). It also keeps a heartbeat
-- (every 60 s) and a watchdog: a job that has not finished after 5 s is reported and the flag is reset, so a lost job cannot stop the loop.
local busy, busySince, lastLoopError = false, 0, nil
local schedules, lastBeat, lastScheduleFailure = 0, os.time(), -100
local function schedule()
    schedules = schedules + 1
    local nowSeconds = os.time()
    if nowSeconds - lastBeat >= 60 then
        lastBeat = nowSeconds
        log("loop alive: %d schedules, %d ticks, last tick %d s ago, busy=%s", schedules, tickCount, nowSeconds - lastTickAt, tostring(busy))
    end
    if busy then
        if nowSeconds - busySince <= 5 then return end
        log("loop: the last tick has not finished for %d s (its job never ran or never ended); resetting", nowSeconds - busySince)
        busy = false
    end
    busy, busySince = true, nowSeconds
    local scheduled, err = pcall(ExecuteInGameThread, function()
        local ok, tickError = pcall(gameTick)
        if not ok and tostring(tickError) ~= lastLoopError then
            lastLoopError = tostring(tickError)
            log("loop error: %s", lastLoopError)
        end
        busy = false
    end)
    if not scheduled then
        busy = false
        if nowSeconds - lastScheduleFailure >= 10 then
            lastScheduleFailure = nowSeconds
            log("ExecuteInGameThread failed: %s", tostring(err))
        end
    end
end

local loopStarted = pcall(LoopAsync, POLL_MS, function() schedule() return false end)
if not loopStarted then
    local function again()
        local ok = pcall(ExecuteWithDelay, POLL_MS, function() schedule() again() end)
        if not ok then log("the loop is not available in this UE4SS version") end
        loopStarted = ok
    end
    again()
end

log("loaded. %s = scan all pages (%s), %s = probe, %s = clear cache. Settings file: %s. Automatic export: %s. Output: %s",
    scanLabel, scanMode, probeLabel, clearLabel, settingsFileFound and "read" or "not found (defaults)", loopStarted and "on" or "off", tostring(outputFolder()))
