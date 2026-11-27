using System.Net;
using Microsoft.Extensions.Options;

namespace BlackVueSync;

/// <summary>
/// Talks to the dashcam's built-in HTTP server (available when it is connected to Wi-Fi in client/station mode).
///   GET /blackvue_vod.cgi   -> list of recordings, one per line: "n:/Record/20261127_153012_NF.mp4,s:1000000"
///   GET /Record/{file}      -> the file itself
/// </summary>
public sealed class BlackVueClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly SyncOptions _options;

    public BlackVueClient(IOptions<SyncOptions> options)
    {
        _options = options.Value;
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            // The camera's web server is slow and single-threaded; don't hammer it.
            MaxConnectionsPerServer = 1,
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

    /// <summary>
    /// Downloads to "file.part" then renames, so a half-finished file is never mistaken for a complete one.
    /// Returns bytes written, or null if the file doesn't exist on the camera (404).
    /// </summary>
    public async Task<long?> DownloadAsync(string remotePath, string localPath, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        var partPath = localPath + ".part";
        var stallTimeout = TimeSpan.FromSeconds(_options.StallTimeoutSeconds);

        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(stallTimeout);
        try
        {
            using var response = await _http.GetAsync(remotePath.TrimStart('/'), HttpCompletionOption.ResponseHeadersRead, stall.Token);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();

            var expected = response.Content.Headers.ContentLength;
            long total = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(stall.Token))
            await using (var target = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                while (true)
                {
                    stall.CancelAfter(stallTimeout); // reset the stall timer on every read
                    var read = await source.ReadAsync(buffer, stall.Token);
                    if (read == 0) break;
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    total += read;
                }
            }

            if (expected is long len && total != len)
                throw new IOException($"Incomplete download: got {total:N0} of {len:N0} bytes");

            File.Move(partPath, localPath, overwrite: true);
            return total;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryDelete(partPath);
            throw new TimeoutException($"No data received for {stallTimeout.TotalSeconds:0}s");
        }
        catch
        {
            TryDelete(partPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }

    public void Dispose() => _http.Dispose();
}
