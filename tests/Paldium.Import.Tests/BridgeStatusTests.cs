using Paldium.Import.Bridge;

namespace Paldium.Import.Tests;

public class BridgeStatusTests : IDisposable
{
    private readonly string _dir = TestSupport.NewTempFolder();

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Theory]
    [InlineData("idle")]
    [InlineData("waitingForUi")]
    [InlineData("scanning")]
    [InlineData("done")]
    [InlineData("cancelled")]
    [InlineData("failed")]
    public void StatusStates_AreParsed(string state)
    {
        var status = BridgeStatus.Parse($$"""
            { "state": "{{state}}", "kind": "Palbox", "boxOwnerUid": "0001", "page": 2, "pageCount": 8,
              "message": "hello", "updatedAtUtc": "2026-11-12T19:00:00Z" }
            """);
        Assert.Equal(state, status.State);
        Assert.Equal("Palbox", status.Kind);
        Assert.Equal(2, status.Page);
        Assert.Equal(8, status.PageCount);
        Assert.Equal(DateTimeOffset.Parse("2026-11-12T19:00:00Z"), status.UpdatedAtUtc);
        Assert.Equal(state is "waitingForUi" or "scanning", BridgeScanState.IsRunning(state));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{ \"state\": ")]
    public void BrokenStatus_Throws(string json)
    {
        Assert.Throws<InvalidDataException>(() => BridgeStatus.Parse(json));
    }

    [Fact]
    public void Poll_FollowsTheStatusFile_RaisesChanged_AndKeepsTheLastGoodStatusOnBrokenFiles()
    {
        using var service = new BridgeScanService(_dir, TimeSpan.FromHours(1)); // polled by hand
        var raised = 0;
        service.Changed += (_, _) => raised++;
        service.Poll();
        Assert.Equal(0, raised); // no file yet
        Assert.Null(service.Current);

        var path = Path.Combine(_dir, "status.json");
        File.WriteAllText(path, """{ "state": "scanning", "kind": "Palbox", "page": 0, "pageCount": 8 }""");
        service.Poll();
        Assert.Equal(1, raised);
        Assert.True(service.IsRunning);

        service.Poll(); // unchanged file: nothing raised
        Assert.Equal(1, raised);

        File.WriteAllText(path, """{ "state": "done", "kind": "Palbox", "page": 8, "pageCount": 8, "message": "8 page(s) scanned" }""");
        service.Poll();
        Assert.Equal(2, raised);
        Assert.False(service.IsRunning);

        File.WriteAllText(path, "{ \"state\": ");
        service.Poll();
        Assert.Equal(2, raised);
        Assert.Equal("done", service.Current!.State);
    }
}
