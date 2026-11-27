namespace BlackVueSync.Tests;

public class RecordingTests
{
    private const string SampleList =
        "v:1.00\r\n" +
        "n:/Record/20261126_080000_NF.mp4,s:1000000\r\n" +
        "n:/Record/20261126_080000_NR.mp4,s:1000000\r\n" +
        "n:/Record/20261127_120000_EF.mp4,s:1000000\r\n" +
        "n:/Record/20261127_130000_PF.mp4,s:1000000\r\n";

    private static SyncOptions Options(Action<SyncOptions>? configure = null)
    {
        var o = new SyncOptions { Host = "cam", Destination = "/recordings" };
        configure?.Invoke(o);
        return o;
    }

    [Fact]
    public void ParseVideoList_ReadsPathsAndIgnoresHeader()
    {
        var paths = BlackVueClient.ParseVideoList(SampleList);

        Assert.Equal(4, paths.Count);
        Assert.Equal("/Record/20261126_080000_NF.mp4", paths[0]);
    }

    [Fact]
    public void ParseVideoList_HandlesMissingSizeAndBlankLines()
    {
        var paths = BlackVueClient.ParseVideoList("\n\nn:/Record/20261126_080000_NF.mp4\n  \n");

        Assert.Equal(["/Record/20261126_080000_NF.mp4"], paths);
    }

    [Theory]
    [InlineData("20261127_120000_EF.mp4", 'E', true)]
    [InlineData("20261127_120000_NF.mp4", 'N', false)]
    [InlineData("20261127_120000_PR.mp4", 'P', false)]
    [InlineData("20261127_120000_M.gps", 'M', true)]
    public void TryParse_ReadsTypeAndTimestamp(string name, char type, bool isEvent)
    {
        var rec = Recording.TryParse("/Record/" + name);

        Assert.NotNull(rec);
        Assert.Equal(type, rec.Type);
        Assert.Equal(isEvent, rec.IsEvent);
        Assert.Equal(new DateTime(2026, 11, 27, 12, 0, 0), rec.Timestamp);
    }

    [Theory]
    [InlineData("readme.txt")]
    [InlineData("20261327_120000_NF.mp4")] // month 13
    [InlineData("SYNOVIDEO_VIDEO_SCREENSHOT.jpg")]
    [InlineData("._20260630_030201_NF.mp4")] // macOS junk seen in a real camera's list; camera returns 500 for it
    public void TryParse_RejectsOtherFiles(string name) => Assert.Null(Recording.TryParse(name));

    [Fact]
    public void BuildPlan_PutsEventsFirstThenOldestWithVideoBeforeSidecars()
    {
        var plan = Recording.BuildPlan(BlackVueClient.ParseVideoList(SampleList), Options());

        Assert.Equal(
        [
            "20261127_120000_EF.mp4", "20261127_120000_E.3gf", "20261127_120000_E.gps",
            "20261126_080000_NF.mp4", "20261126_080000_NR.mp4", "20261126_080000_N.3gf", "20261126_080000_N.gps",
            "20261127_130000_PF.mp4", "20261127_130000_P.3gf", "20261127_130000_P.gps",
        ], plan.Select(r => r.FileName));
    }

    [Fact]
    public void BuildPlan_FiltersByTypeAndSkipsSidecarsWhenDisabled()
    {
        var plan = Recording.BuildPlan(BlackVueClient.ParseVideoList(SampleList),
            Options(o => { o.RecordingTypes = "e, p"; o.DownloadGps = false; }));

        Assert.Equal(["20261127_120000_EF.mp4", "20261127_130000_PF.mp4"], plan.Select(r => r.FileName));
    }

    [Fact]
    public void BuildPlan_AddsThumbnailsWhenEnabled()
    {
        var plan = Recording.BuildPlan(["/Record/20261126_080000_NF.mp4"],
            Options(o => { o.DownloadGps = false; o.DownloadThumbnails = true; }));

        Assert.Contains(plan, r => r.RemotePath == "/Record/20261126_080000_NF.thm");
    }

    [Theory]
    [InlineData(FolderLayout.Daily, "/recordings/2026-11-27/20261127_120000_EF.mp4")]
    [InlineData(FolderLayout.Monthly, "/recordings/2026-11/20261127_120000_EF.mp4")]
    [InlineData(FolderLayout.Flat, "/recordings/20261127_120000_EF.mp4")]
    public void LocalPath_FollowsLayout(FolderLayout layout, string expected)
    {
        var rec = Recording.TryParse("/Record/20261127_120000_EF.mp4")!;

        Assert.Equal(expected, rec.LocalPath(Options(o => o.Layout = layout)).Replace('\\', '/'));
    }
}
