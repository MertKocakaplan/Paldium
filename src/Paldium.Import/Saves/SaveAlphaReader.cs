using PalCalc.SaveReader;
using PalCalc.SaveReader.FArchive;
using PalCalc.SaveReader.SaveFile;

namespace Paldium.Import.Saves;

/// <summary>
/// Reads alpha (<c>BOSS_</c> prefix) instance ids directly from save GVAS data without touching the PalCalc submodule
/// (PalCalc's <c>ToPalInstance</c> strips <c>Boss_</c> before exposing <c>PalInstance</c>).
/// </summary>
public static class SaveAlphaReader
{
    public static bool IsBossCharacterId(string? characterId) =>
        !string.IsNullOrWhiteSpace(characterId) &&
        characterId.TrimStart().StartsWith("BOSS_", StringComparison.OrdinalIgnoreCase);

    /// <summary>Collects alpha instance ids from <c>Level.sav</c> and each player's <c>*_dps.sav</c>.</summary>
    internal static IReadOnlySet<string> ReadWorldAlphaIds(StandardSaveGame save, Action<string, Exception?>? log = null)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var visitor = new LevelAlphaVisitor();
            save.Level.ParseGvas(false, visitor);
            result.UnionWith(visitor.AlphaInstanceIds);
        }
        catch (Exception ex)
        {
            log?.Invoke("Could not read alpha flags from Level.sav.", ex);
        }

        foreach (var playerFile in save.Players)
        {
            var dps = playerFile.DimensionalPalStorageSaveFile;
            if (dps == null || !dps.Exists) continue;
            try
            {
                foreach (var c in dps.ReadRawCharacters())
                {
                    if (IsBossCharacterId(c.CharacterId))
                        result.Add(c.InstanceId.ToString());
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("Could not read alpha flags from player DPS save.", ex);
            }
        }

        return result;
    }

    /// <summary>Collects alpha instance ids from <c>GlobalPalStorage.sav</c>.</summary>
    internal static IReadOnlySet<string> ReadGlobalStorageAlphaIds(GlobalPalStorageSaveFile gpsFile, Action<string, Exception?>? log = null)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var raw = gpsFile.ReadRawCharacters();
            if (raw != null)
            {
                foreach (var c in raw)
                {
                    if (IsBossCharacterId(c.CharacterId))
                        result.Add(c.InstanceId.ToString());
                }
            }
        }
        catch (Exception ex)
        {
            log?.Invoke("Could not read alpha flags from GlobalPalStorage.sav.", ex);
        }

        return result;
    }

    private sealed class LevelAlphaVisitor() : IVisitor(".worldSaveData.CharacterSaveParameterMap")
    {
        private const string KeyInstanceId = ".Key.InstanceId";
        private const string CharIdSuffix = ".Value.RawData.SaveParameter.CharacterID";

        private Dictionary<string, object>? _keyVals;
        private Dictionary<string, object>? _charVals;

        public HashSet<string> AlphaInstanceIds { get; } = new(StringComparer.OrdinalIgnoreCase);

        public override IEnumerable<IVisitor> VisitMapEntryBegin(string path, int index, MapPropertyMeta meta)
        {
            _keyVals = null;
            _charVals = null;

            var keyCollector = new ValueCollectingVisitor(this, isCaseSensitive: false, KeyInstanceId);
            keyCollector.WithOnExit(v => _keyVals = v);
            yield return keyCollector;

            var charCollector = new ValueCollectingVisitor(this, isCaseSensitive: false, CharIdSuffix);
            charCollector.WithOnExit(v => _charVals = v);
            yield return charCollector;
        }

        public override void VisitMapEntryEnd(string path, int index, MapPropertyMeta meta)
        {
            if (_keyVals != null &&
                _charVals != null &&
                _keyVals.TryGetValue(KeyInstanceId, out var rawId) &&
                rawId is Guid instanceId &&
                _charVals.TryGetValue(CharIdSuffix, out var rawChar) &&
                IsBossCharacterId(rawChar?.ToString()))
            {
                AlphaInstanceIds.Add(instanceId.ToString());
            }

            _keyVals = null;
            _charVals = null;
        }
    }
}
