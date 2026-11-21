using System.Text.Encodings.Web;
using System.Text.Json;

namespace Paldium.Core.Data.Schema;

/// <summary>Shared JSON settings for the data bundle (camelCase, UTF-8, LF newlines).</summary>
public static class SchemaJson
{
    /// <summary>Schema version written by the current DataBuilder.</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>Oldest schema version the loader still reads (new fields are empty/null there).</summary>
    public const int MinSupportedSchemaVersion = 1;

    public static JsonSerializerOptions CreateOptions(bool indented) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        WriteIndented = indented,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static readonly JsonSerializerOptions Indented = CreateOptions(true);
    public static readonly JsonSerializerOptions Compact = CreateOptions(false);
}
