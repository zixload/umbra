using Umbra.Core;

namespace Umbra.Tests;

public class BrowserSessionControlTests : IDisposable
{
    private readonly string _tempDir;

    public BrowserSessionControlTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "umbra-browser-session-tests-" + Guid.NewGuid());
        Config.DataDir = _tempDir;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void GetStatus_ExposesAnActiveSessionForTheExtension()
    {
        Session.StartPomodoro(25, 5, 3, false, "Write report");

        var status = BrowserSessionControl.GetStatus();

        Assert.True(status.Active);
        Assert.Equal("pomodoro", status.Kind);
        Assert.Equal("work", status.Phase);
        Assert.Equal("Write report", status.QuestName);
        Assert.Equal(1, status.Cycle);
        Assert.Equal(3, status.CyclesTotal);
        Assert.True(status.CanStop);
        Assert.True(status.RemainingSeconds > 0);
    }

    [Fact]
    public void TryStop_StopsASoftSession()
    {
        Session.StartCustom(25, false, "Focus session");

        var stopped = BrowserSessionControl.TryStop(out var status, out var error);

        Assert.True(stopped);
        Assert.Null(error);
        Assert.False(status.Active);
        Assert.False(Session.Load().Active);
    }

    [Fact]
    public void TryStop_RejectsAnActiveHardModeSession()
    {
        Session.StartCustom(25, true, "Locked session");

        var stopped = BrowserSessionControl.TryStop(out var status, out var error);

        Assert.False(stopped);
        Assert.Equal("hardMode", error);
        Assert.True(status.Active);
        Assert.True(Session.Load().Active);
    }
}
