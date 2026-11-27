using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Options;

namespace BlackVueSync;

/// <summary>
/// Polls for the dashcam. Whenever it answers (car is home and on Wi-Fi), downloads every recording
/// not already on the NAS. If the car leaves mid-download the partial file is discarded and the
/// next visit picks up where it left off.
/// </summary>
public sealed class SyncWorker(BlackVueClient camera, IOptions<SyncOptions> options, ILogger<SyncWorker> logger)
    : BackgroundService
{
    private readonly SyncOptions _options = options.Value;

    // Sidecar files (.gps/.3gf/.thm) we guessed at but the camera doesn't have. Avoids re-requesting every poll.
    private readonly HashSet<string> _knownMissing = new(StringComparer.OrdinalIgnoreCase);

    private bool _connected;
    private DateTime _lastRetentionRun = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(_options.Destination);
        DeleteLeftoverPartFiles();
        logger.LogInformation("Watching for BlackVue at {Host}; saving to {Destination} (checking every {Interval}s)",
            _options.Host, _options.Destination, _options.PollIntervalSeconds);

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

        if (!_connected)
        {
            logger.LogInformation("Dashcam connected ({Count} videos on SD card)", videos.Count);
            _connected = true;
            _knownMissing.Clear(); // new visit, new files
        }

        var pending = Recording.BuildPlan(videos, _options)
            .Where(r => !_knownMissing.Contains(r.RemotePath) && !File.Exists(r.LocalPath(_options)))
            .ToList();

        if (pending.Count > 0)
            await DownloadAllAsync(pending, ct);

        if (_options.KeepDays > 0 && DateTime.Now - _lastRetentionRun > TimeSpan.FromHours(1))
        {
            ApplyRetention();
            _lastRetentionRun = DateTime.Now;
        }
    }

    private async Task DownloadAllAsync(List<Recording> pending, CancellationToken ct)
    {
        var videoCount = pending.Count(r => r.FileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));
        logger.LogInformation("{Count} files to download ({Videos} videos)", pending.Count, videoCount);

        int done = 0, failed = 0;
        long bytes = 0;
        var total = Stopwatch.StartNew();

        foreach (var rec in pending)
        {
            if (!HasEnoughFreeSpace()) return;

            var local = rec.LocalPath(_options);
            var sw = Stopwatch.StartNew();
            try
            {
                var size = await camera.DownloadAsync(rec.RemotePath, local, ct);
                if (size is null)
                {
                    _knownMissing.Add(rec.RemotePath);
                    if (rec.FileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                        logger.LogWarning("{File} disappeared from the camera before it could be downloaded (overwritten?)", rec.FileName);
                    continue;
                }

                done++;
                bytes += size.Value;
                logger.LogInformation("[{Done}/{Total}] {File} {MB:N1} MB at {Speed:N1} MB/s",
                    done, pending.Count, rec.FileName, size.Value / 1e6, size.Value / 1e6 / Math.Max(sw.Elapsed.TotalSeconds, 0.001));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException { StatusCode: not null and not HttpStatusCode.NotFound })
            {
                // Camera answered but refused this one file: skip it, keep going.
                failed++;
                logger.LogWarning("Skipping {File}: HTTP {Status}", rec.FileName, (int)((HttpRequestException)ex).StatusCode!);
                if (failed >= 5) { logger.LogWarning("Too many failures, pausing until next poll"); break; }
            }
            catch (Exception ex)
            {
                // Connection-level failure: most likely the car drove away. Stop and retry on the next poll.
                logger.LogWarning("Sync interrupted while downloading {File}: {Message}", rec.FileName, ex.Message);
                break;
            }
        }

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
    /// at the root of a shared folder. Never look inside or touch those.
    /// </summary>
    private static bool IsSynologySystemFolder(string dir)
    {
        var name = Path.GetFileName(dir);
        return name.StartsWith('@') || name.StartsWith('#');
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

    private void DeleteLeftoverPartFiles()
    {
        foreach (var part in EnumerateOurFiles(_options.Destination).Where(f => f.EndsWith(".part", StringComparison.Ordinal)))
        {
            try { File.Delete(part); } catch { /* ignore */ }
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
