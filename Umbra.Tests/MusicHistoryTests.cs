using Umbra.Core;

namespace Umbra.Tests;

public class MusicHistoryTests : IDisposable
{
    private readonly string _tempDir;

    public MusicHistoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "umbra-music-tests-" + Guid.NewGuid());
        Config.DataDir = _tempDir;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void RecordPlayback_CountsOnlySamplesMarkedAsNewPlays()
    {
        MusicHistory.RecordPlayback("Orbit", "Weightlessness", 3, countAsPlay: true);
        MusicHistory.RecordPlayback("Orbit", "Weightlessness", 3);
        MusicHistory.RecordPlayback("Orbit", "Weightlessness", 3);

        var track = Assert.Single(MusicHistory.GetTopTracks(5));
        Assert.Equal(1, track.PlayCount);
        Assert.Equal(9, track.Seconds);
    }

    [Fact]
    public void RecordPlayback_StoresAndRefreshesArtwork()
    {
        MusicHistory.RecordPlayback("Orbit", "Weightlessness", 3, new byte[] { 1, 2 });
        MusicHistory.RecordPlayback("Orbit", "Weightlessness", 3, new byte[] { 3, 4 });

        Assert.Equal(new byte[] { 3, 4 }, Assert.Single(MusicHistory.GetTopTracks(5)).Thumbnail);
    }

    [Fact]
    public void RecordPlayback_BatchesOrdinarySamplesUntilFlush()
    {
        MusicHistory.RecordPlayback("Orbit", "Weightlessness", 3, countAsPlay: true);
        var persistedAfterNewPlay = File.ReadAllText(Config.MusicHistoryFile);

        MusicHistory.RecordPlayback("Orbit", "Weightlessness", 3);

        Assert.Equal(persistedAfterNewPlay, File.ReadAllText(Config.MusicHistoryFile));
        Assert.Equal(6, Assert.Single(MusicHistory.GetTopTracks(5)).Seconds);

        MusicHistory.Flush();
        Assert.NotEqual(persistedAfterNewPlay, File.ReadAllText(Config.MusicHistoryFile));
    }

    [Fact]
    public void Flush_KeepsArtworkOnlyForTheMostPlayedTracks()
    {
        for (var i = 1; i <= 160; i++)
            MusicHistory.RecordPlayback($"Track {i}", "Artist", i, new byte[] { 7 });

        MusicHistory.Flush();

        var all = MusicHistory.GetAllTracks();
        Assert.Equal(160, all.Count);
        Assert.NotNull(all[0].Thumbnail);   // le mieux classé garde sa pochette
        Assert.NotNull(all[149].Thumbnail); // dernier dans le budget
        Assert.Null(all[150].Thumbnail);    // au-delà : pochette libérée
        Assert.Equal(160, all[0].Seconds);  // le temps d'écoute, lui, reste intact
    }
}
