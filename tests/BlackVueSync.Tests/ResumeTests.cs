namespace BlackVueSync.Tests;

public sealed class ResumeTests : IDisposable
{
    private const string Remote = "/Record/20261127_120000_EF.mp4";
    private readonly string _dir = Directory.CreateTempSubdirectory("bvsync-").FullName;
    private readonly FakeCamera _camera = new();
    private readonly byte[] _video = FakeCamera.MakeFile(1_000_000);
    private string Local => Path.Combine(_dir, "20261127_120000_EF.mp4");

    public ResumeTests() => _camera.Files[Remote] = _video;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private BlackVueClient Client() => new(new SyncOptions { Host = "cam", StallTimeoutSeconds = 5 }, _camera);

    [Fact]
    public async Task FreshDownload_WritesWholeFileAndNoPart()
    {
        var result = await Client().DownloadAsync(Remote, Local, default);

        Assert.Equal(new DownloadResult(1_000_000, 1_000_000, 0), result);
        Assert.Equal(_video, File.ReadAllBytes(Local));
        Assert.False(File.Exists(BlackVueClient.PartPath(Local)));
    }

    [Fact]
    public async Task InterruptedDownload_KeepsPart_ThenResumesWithRangeRequest()
    {
        _camera.DropAfterBytes = 400_000;
        await Assert.ThrowsAnyAsync<Exception>(() => Client().DownloadAsync(Remote, Local, default));

        Assert.False(File.Exists(Local));
        var partLength = new FileInfo(BlackVueClient.PartPath(Local)).Length;
        Assert.InRange(partLength, 1, 400_000);

        _camera.DropAfterBytes = null; // car is back
        var result = await Client().DownloadAsync(Remote, Local, default);

        Assert.Equal(partLength, _camera.RangesRequested[^1]!.Ranges.Single().From);
        Assert.Equal(partLength, result!.ResumedFrom);
        Assert.Equal(1_000_000 - partLength, result.TransferredBytes);
        Assert.Equal(_video, File.ReadAllBytes(Local)); // byte-for-byte identical to the camera's copy
        Assert.False(File.Exists(BlackVueClient.PartPath(Local)));
    }

    [Fact]
    public async Task ServerIgnoringRange_StartsOverInsteadOfCorruptingFile()
    {
        File.WriteAllBytes(BlackVueClient.PartPath(Local), _video[..300_000]);
        _camera.IgnoreRange = true;

        var result = await Client().DownloadAsync(Remote, Local, default);

        Assert.Equal(0, result!.ResumedFrom);
        Assert.Equal(_video, File.ReadAllBytes(Local));
    }

    [Fact]
    public async Task PartAsLongAsFile_IsDiscardedAndRetried()
    {
        File.WriteAllBytes(BlackVueClient.PartPath(Local), _video);

        await Assert.ThrowsAsync<IOException>(() => Client().DownloadAsync(Remote, Local, default));
        Assert.False(File.Exists(BlackVueClient.PartPath(Local)));

        var result = await Client().DownloadAsync(Remote, Local, default);
        Assert.Equal(_video, File.ReadAllBytes(Local));
        Assert.Equal(0, result!.ResumedFrom);
    }

    [Fact]
    public async Task FileGoneFromCamera_ReturnsNullAndDropsPart()
    {
        File.WriteAllBytes(BlackVueClient.PartPath(Local), _video[..1000]);
        _camera.Files.Clear();

        Assert.Null(await Client().DownloadAsync(Remote, Local, default));
        Assert.False(File.Exists(BlackVueClient.PartPath(Local)));
    }
}
