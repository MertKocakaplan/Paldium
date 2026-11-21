using Paldium.Import.Saves;

namespace Paldium.Import.Tests;

public class StorageOwnerTests
{
    [Theory]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA_DPS", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("abc_dps", "abc")]
    [InlineData("_DPS", null)]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", null)]
    [InlineData(null, null)]
    public void TheOwnerOfASaveStorage_IsInItsContainerId(string? containerId, string? expected) =>
        Assert.Equal(expected, SaveWorldReader.StorageOwnerId(containerId));
}
