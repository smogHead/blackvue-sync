using System.Net;
using System.Net.Http.Headers;

namespace BlackVueSync.Tests;

/// <summary>In-memory stand-in for the dashcam's web server, including Range support.</summary>
internal sealed class FakeCamera : HttpMessageHandler
{
    public Dictionary<string, byte[]> Files { get; } = new();

    /// <summary>Simulate the car driving off: the connection dies after this many body bytes.</summary>
    public long? DropAfterBytes { get; set; }

    /// <summary>Simulate a server that ignores Range and always sends the whole file.</summary>
    public bool IgnoreRange { get; set; }

    public List<RangeHeaderValue?> RangesRequested { get; } = new();

    public static byte[] MakeFile(int size) => Enumerable.Range(0, size).Select(i => (byte)(i * 31 % 251)).ToArray();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        RangesRequested.Add(request.Headers.Range);
        if (!Files.TryGetValue(request.RequestUri!.AbsolutePath, out var data))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        long from = 0;
        var status = HttpStatusCode.OK;
        if (!IgnoreRange && request.Headers.Range?.Ranges.FirstOrDefault()?.From is long start)
        {
            if (start >= data.Length)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
            from = start;
            status = HttpStatusCode.PartialContent;
        }

        var body = data.AsMemory((int)from).ToArray();
        var content = new StreamContent(new DroppingStream(body, DropAfterBytes));
        content.Headers.ContentLength = body.Length;
        if (status == HttpStatusCode.PartialContent)
            content.Headers.ContentRange = new ContentRangeHeaderValue(from, data.Length - 1, data.Length);
        return Task.FromResult(new HttpResponseMessage(status) { Content = content });
    }

    /// <summary>A body stream that throws (like a reset connection) once a byte limit is reached.</summary>
    private sealed class DroppingStream(byte[] data, long? dropAfter) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (dropAfter is long limit)
            {
                if (Position >= limit) throw new IOException("connection reset (simulated)");
                count = (int)Math.Min(count, limit - Position);
            }
            return base.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            var temp = new byte[buffer.Length];
            var n = Read(temp, 0, temp.Length);
            temp.AsSpan(0, n).CopyTo(buffer);
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            Task.FromResult(Read(buffer, offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            ValueTask.FromResult(Read(buffer.Span));
    }
}
