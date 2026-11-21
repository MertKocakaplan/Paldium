namespace Paldium.Core.Data.Schema;

/// <summary>
/// Schema DTO for data/breeding.json (ARCHITECTURE section 4.2).
/// <see cref="Children"/> is an upper-triangular matrix (i &lt;= j) of palOrder indices, row-major.
/// </summary>
public sealed record BreedingFile(
    IReadOnlyList<string> PalOrder,
    IReadOnlyList<int> Children,
    IReadOnlyList<GenderSpecificDto> GenderSpecific);

public sealed record GenderSpecificDto(string A, string AGender, string B, string BGender, string Child);
