using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

namespace BlackVueSync;

/// <summary>Outcome of one download call.</summary>
/// <param name="TotalBytes">Final size of the file on disk.</param>
/// <param name="TransferredBytes">Bytes actually fetched this time (less than TotalBytes when resumed).</param>
/// <param name="ResumedFrom">Offset the download continued from, or 0 for a fresh download.</param>
public sealed record DownloadResult(long TotalBytes, long TransferredBytes, long ResumedFrom);

/// <summary>
/// Talks to the dashcam's built-in HTTP server (available when it is connected to Wi-Fi in client/station mode).
///   GET /blackvue_vod.cgi   -> list of recordings, one per line: "n:/Record/20261127_153012_NF.mp4,s:1000000"
///   GET /Record/{file}      -> the file itself; supports Range requests, which is what makes resuming possible
/// </summary>
public sealed class BlackVueClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly SyncOptions _options;

    public BlackVueClient(IOptions<SyncOptions> options) : this(options.Value, null) { }

    /// <summary>For tests: lets a fake handler stand in for the camera.</summary>
    internal BlackVueClient(SyncOptions options, HttpMessageHandler? handler)
    {
        _options = options;
        handler ??= new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            // One connection per parallel download, plus one so the file-list request never waits behind them.
            MaxConnectionsPerServer = _options.MaxParallelDownloads + 1,
        };
        var host = _options.Host.Contains("://") ? _options.Host : "http://" + _options.Host;
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(host.TrimEnd('/') + "/"),
            Timeout = Timeout.InfiniteTimeSpan, // we enforce our own per-read stall timeout instead
        };
    }

    /// <summary>Returns the dashcam's video list, or null if the dashcam isn't reachable (i.e. not home).</summary>
    public async Task<List<string>?> TryListVideosAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds * 3));
        try
        {
            var body = await _http.GetStringAsync("blackvue_vod.cgi", timeout.Token);
            return ParseVideoList(body);
        }
        catch (HttpRequestException) { return null; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    public static List<string> ParseVideoList(string body)
    {
        var result = new List<string>();
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("n:", StringComparison.OrdinalIgnoreCase)) continue;
            var comma = line.IndexOf(',');
            var path = (comma < 0 ? line[2..] : line[2..comma]).Trim();
            if (path.Length > 0) result.Add(path);
        }
        return result;
    }

    public static string PartPath(string localPath) => localPath + ".part";

    /// <summary>
    /// Downloads into "file.part", then renames it to the final name once complete, so a half-finished file is
    /// never mistaken for a finished one. If a .part file is already there from an interrupted visit, it asks the
    /// camera only for the missing bytes and appends them. The .part is kept when a download is interrupted, so
    /// the next visit can carry on from there.
    /// Returns null if the file doesn't exist on the camera (404).
    /// </summary>
    public async Task<DownloadResult?> DownloadAsync(string remotePath, string localPath, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        var partPath = PartPath(localPath);
        var stallTimeout = TimeSpan.FromSeconds(_options.StallTimeoutSeconds);
        var existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(stallTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, remotePath.TrimStart('/'));
            if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                TryDelete(partPath); // gone from the camera; the partial copy is useless now
                return null;
            }
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // Our .part is as long as (or longer than) the file. We can't tell if it's intact, so start over.
                TryDelete(partPath);
                throw new IOException("Partial file did not match the camera's copy; it will be downloaded again");
            }
            response.EnsureSuccessStatusCode();

            // Work out where this response starts. Only append if the camera really sent the bytes we asked for;
            // if it ignored the Range header (200) or answered with a different range, start from zero.
            var range = response.Content.Headers.ContentRange;
            var resumed = response.StatusCode == HttpStatusCode.PartialContent && range?.From == existing;
            var offset = resumed ? existing : 0;
            long? expectedTotal = resumed
                ? range!.Length
                : response.Content.Headers.ContentLength;

            long written = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(stall.Token))
            await using (var target = new FileStream(partPath, resumed ? FileMode.Append : FileMode.Create,
                             FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                while (true)
                {
                    stall.CancelAfter(stallTimeout); // reset the stall timer on every read
                    var read = await source.ReadAsync(buffer, stall.Token);
                    if (read == 0) break;
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    written += read;
                }
            }

            var total = offset + written;
            if (expectedTotal is long len && total != len)
            {
                if (total > len) TryDelete(partPath); // can't be right; don't build on it
                throw new IOException($"Incomplete download: got {total:N0} of {len:N0} bytes");
            }

            File.Move(partPath, localPath, overwrite: true);
            return new DownloadResult(total, written, offset);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"No data received for {stallTimeout.TotalSeconds:0}s");
        }
        // Any other failure (car drove off, shutdown): keep the .part so the next visit can resume it.
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }

    public void Dispose() => _http.Dispose();
}
