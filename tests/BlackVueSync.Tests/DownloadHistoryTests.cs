namespace BlackVueSync.Tests;

public sealed class DownloadHistoryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bvsync-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void RemembersNamesAcrossRestarts()
    {
        var history = new DownloadHistory(_dir);
        history.Add("20261127_120000_EF.mp4");
        history.Add("20261127_120000_EF.mp4"); // duplicate is ignored

        var reloaded = new DownloadHistory(_dir);
        Assert.True(reloaded.Contains("20261127_120000_ef.MP4")); // case-insensitive
        Assert.Equal(1, reloaded.Count);
        Assert.Single(File.ReadAllLines(reloaded.FilePath));
    }

    [Fact]
    public void PruneDropsOnlyRecordingsOlderThanCutoff()
    {
        var history = new DownloadHistory(_dir);
        history.Add("20261101_080000_NF.mp4");
        history.Add("20261126_080000_NF.mp4");
        history.Add("20261127_080000_N.gps");

        Assert.Equal(1, history.Prune(new DateTime(2026, 11, 20)));

        var reloaded = new DownloadHistory(_dir);
        Assert.False(reloaded.Contains("20261101_080000_NF.mp4"));
        Assert.True(reloaded.Contains("20261126_080000_NF.mp4"));
        Assert.True(reloaded.Contains("20261127_080000_N.gps"));
    }
}
