using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Options;

namespace BlackVueSync;

/// <summary>
/// Polls for the dashcam. Whenever it answers (car is home and on Wi-Fi), downloads every recording
/// not already on the NAS. If the car leaves mid-download, the partial file is kept and the next
/// visit resumes it from where it stopped.
/// A recording is never downloaded twice: it is skipped if a file with that name is anywhere under the
/// destination folder, or if the download history says it was fetched before (see <see cref="DownloadHistory"/>).
/// </summary>
public sealed class SyncWorker(BlackVueClient camera, IOptions<SyncOptions> options, ILogger<SyncWorker> logger)
    : BackgroundService
{
    private readonly SyncOptions _options = options.Value;
    private readonly DownloadHistory _history = new(options.Value.Destination);

    // Sidecar files (.gps/.3gf/.thm) we guessed at but the camera doesn't have. Avoids re-requesting every poll.
    private readonly HashSet<string> _knownMissing = new(StringComparer.OrdinalIgnoreCase);

    // Names of all recordings currently stored anywhere under the destination (any subfolder, any layout).
    // Rebuilt each time the camera arrives, and kept up to date as files are downloaded.
    private readonly HashSet<string> _stored = new(StringComparer.OrdinalIgnoreCase);

    private bool _connected;
    private DateTime _lastRetentionRun = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(_options.Destination);
        logger.LogInformation("Watching for BlackVue at {Host}; saving to {Destination} (checking every {Interval}s)",
            _options.Host, _options.Destination, _options.PollIntervalSeconds);
        if (_options.RememberDownloads)
            logger.LogInformation("Download history: {Count} files already downloaded ({Path})", _history.Count, _history.FilePath);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error during sync");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var videos = await camera.TryListVideosAsync(ct);
        if (videos is null)
        {
            if (_connected) logger.LogInformation("Dashcam left the network");
            _connected = false;
            return;
        }

        var plan = Recording.BuildPlan(videos, _options);

        if (!_connected)
        {
            logger.LogInformation("Dashcam connected ({Count} videos on SD card)", videos.Count);
            _connected = true;
            _knownMissing.Clear(); // new visit, new files
            OnCameraArrived(videos, plan);
        }

        var pending = plan
            .Where(r => !_knownMissing.Contains(r.RemotePath) && !IsAlreadyDownloaded(r))
            .ToList();

        if (pending.Count > 0)
            await DownloadAllAsync(pending, ct);

        if (_options.KeepDays > 0 && DateTime.Now - _lastRetentionRun > TimeSpan.FromHours(1))
        {
            ApplyRetention();
            _lastRetentionRun = DateTime.Now;
        }
    }

    private bool IsAlreadyDownloaded(Recording rec)
    {
        lock (_stored)
            if (_stored.Contains(rec.FileName)) return true;
        if (_options.RememberDownloads && _history.Contains(rec.FileName)) return true;
        return File.Exists(rec.LocalPath(_options)); // e.g. copied in by hand since the camera arrived
    }

    private void MarkDownloaded(Recording rec)
    {
        lock (_stored) _stored.Add(rec.FileName);
        if (_options.RememberDownloads) _history.Add(rec.FileName);
    }

    /// <summary>Once per visit: index what's on the NAS, tidy up partial files and the history.</summary>
    private void OnCameraArrived(List<string> videos, List<Recording> plan)
    {
        var wanted = plan.Select(r => r.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var partsKept = 0;
        lock (_stored)
        {
            _stored.Clear();
            foreach (var file in EnumerateOurFiles(_options.Destination))
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith(".part", StringComparison.Ordinal))
                {
                    // Keep partial downloads the camera can still finish; drop ones it no longer has.
                    if (wanted.Contains(name[..^5])) partsKept++;
                    else TryDelete(file);
                }
                else if (Recording.TryParse(name) is not null)
                {
                    _stored.Add(name);
                }
            }
        }
        logger.LogInformation("{Stored} recordings already on the NAS{Parts}", _stored.Count,
            partsKept > 0 ? $"; {partsKept} partial downloads will be resumed" : "");

        // The camera can never offer anything older than its oldest file again, so stop remembering those.
        var oldest = videos.Select(Recording.TryParse).OfType<Recording>().Select(r => r.Timestamp).DefaultIfEmpty().Min();
        if (_options.RememberDownloads && oldest != default)
            _history.Prune(oldest.Date.AddDays(-1));
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* ignore */ }
    }

    private async Task DownloadAllAsync(List<Recording> pending, CancellationToken ct)
    {
        var videoCount = pending.Count(r => r.FileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));
        logger.LogInformation("{Count} files to download ({Videos} videos)", pending.Count, videoCount);

        int done = 0, failed = 0;
        long bytes = 0;
        var total = Stopwatch.StartNew();

        // Cancelling this stops every in-flight download (car drove off, disk full, too many errors).
        // Their .part files are kept and resumed on the next poll.
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var aborted = 0;
        void Abort(string reason, params object?[] args)
        {
            // Several downloads usually fail at the same moment when the car leaves; only log the first.
            if (Interlocked.Exchange(ref aborted, 1) == 1) return;
            logger.LogWarning(reason, args);
            abort.Cancel();
        }

        // Parallel.ForEachAsync takes items from the list in order, so priority order is kept:
        // whenever a slot frees up, the next most important file starts.
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = _options.MaxParallelDownloads, CancellationToken = abort.Token };
        try
        {
            await Parallel.ForEachAsync(pending, parallel, async (rec, token) =>
            {
                if (!HasEnoughFreeSpace()) { Abort("Stopping downloads: destination is low on free space"); return; }

                var sw = Stopwatch.StartNew();
                try
                {
                    var result = await camera.DownloadAsync(rec.RemotePath, rec.LocalPath(_options), token);
                    if (result is null)
                    {
                        lock (_knownMissing) _knownMissing.Add(rec.RemotePath);
                        if (rec.FileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                            logger.LogWarning("{File} disappeared from the camera before it could be downloaded (overwritten?)", rec.FileName);
                        return;
                    }

                    MarkDownloaded(rec);
                    var n = Interlocked.Increment(ref done);
                    Interlocked.Add(ref bytes, result.TransferredBytes);
                    var speed = result.TransferredBytes / 1e6 / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
                    if (result.ResumedFrom > 0)
                        logger.LogInformation("[{Done}/{Total}] {File} {MB:N1} MB at {Speed:N1} MB/s (resumed at {From:N1} MB)",
                            n, pending.Count, rec.FileName, result.TotalBytes / 1e6, speed, result.ResumedFrom / 1e6);
                    else
                        logger.LogInformation("[{Done}/{Total}] {File} {MB:N1} MB at {Speed:N1} MB/s",
                            n, pending.Count, rec.FileName, result.TotalBytes / 1e6, speed);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // Stopped because another download aborted the batch, or the service is shutting down.
                }
                catch (HttpRequestException ex) when (ex.StatusCode is not null)
                {
                    // Camera answered but refused this one file: skip it, keep going.
                    logger.LogWarning("Skipping {File}: HTTP {Status}", rec.FileName, (int)ex.StatusCode);
                    if (Interlocked.Increment(ref failed) >= 5)
                        Abort("Too many failures, pausing until next poll");
                }
                catch (Exception ex)
                {
                    // Connection-level failure: most likely the car drove away. Stop everything and retry on the next poll.
                    Abort("Sync interrupted while downloading {File}: {Message}", rec.FileName, ex.Message);
                }
            });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Batch aborted; the reason has already been logged.
        }

        ct.ThrowIfCancellationRequested();
        logger.LogInformation("Downloaded {Count} files, {GB:N2} GB in {Elapsed:hh\\:mm\\:ss}", done, bytes / 1e9, total.Elapsed);
    }

    private bool HasEnoughFreeSpace()
    {
        try
        {
            var free = new DriveInfo(_options.Destination).AvailableFreeSpace / 1e9;
            if (free >= _options.MinFreeSpaceGb) return true;
            logger.LogError("Only {Free:N1} GB free on destination (minimum {Min} GB); not downloading", free, _options.MinFreeSpaceGb);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not determine free space; continuing");
            return true;
        }
    }

    /// <summary>
    /// Synology creates "@eaDir" (indexing thumbnails/metadata) inside folders, and "#recycle" (Recycle Bin)
    /// at the root of a shared folder. Never look inside or touch those. Also skips our own ".blackvue-sync"
    /// folder (download history) and any other hidden folder.
    /// </summary>
    private static bool IsSynologySystemFolder(string dir)
    {
        var name = Path.GetFileName(dir);
        return name.StartsWith('@') || name.StartsWith('#') || name.StartsWith('.');
    }

    private static IEnumerable<string> EnumerateOurFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            IEnumerable<string> files, subdirs;
            try
            {
                files = Directory.GetFiles(dir);
                subdirs = Directory.GetDirectories(dir);
            }
            catch (Exception) { continue; } // e.g. permission denied on a folder DSM created
            foreach (var f in files) yield return f;
            foreach (var d in subdirs)
                if (!IsSynologySystemFolder(d)) pending.Push(d);
        }
    }

    private void ApplyRetention()
    {
        var cutoff = DateTime.Now.Date.AddDays(-_options.KeepDays);
        var deleted = 0;
        foreach (var file in EnumerateOurFiles(_options.Destination))
        {
            var rec = Recording.TryParse(Path.GetFileName(file));
            if (rec is null || rec.Timestamp >= cutoff) continue;
            try { File.Delete(file); deleted++; } catch (Exception ex) { logger.LogWarning("Could not delete {File}: {Message}", file, ex.Message); }
        }

        // Remove day/month folders that are now empty. A folder holding nothing but Synology's
        // @eaDir index data counts as empty (the index entries are for files we just deleted).
        foreach (var dir in Directory.EnumerateDirectories(_options.Destination).Where(d => !IsSynologySystemFolder(d)))
        {
            try
            {
                var entries = Directory.EnumerateFileSystemEntries(dir).ToList();
                if (entries.All(e => Directory.Exists(e) && Path.GetFileName(e) == "@eaDir"))
                    Directory.Delete(dir, recursive: true);
            }
            catch { /* ignore */ }
        }

        if (deleted > 0)
            logger.LogInformation("Retention: deleted {Count} files older than {Days} days", deleted, _options.KeepDays);
    }
}
