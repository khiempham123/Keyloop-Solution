namespace DocumentViewer.Infrastructure.Sources;

/// <summary>
/// Thrown when an upstream response body exceeds the size this service is willing to read.
/// </summary>
public sealed class UpstreamBodyTooLargeException(long limitBytes)
    : Exception($"Upstream response body exceeded the {limitBytes:N0}-byte limit.")
{
    public long LimitBytes { get; } = limitBytes;
}

/// <summary>
/// A read-only pass-through that refuses to hand over more than
/// <paramref name="limitBytes"/> bytes.
///
/// Spec §2, principle 3 says upstream systems are untrusted. The response body was the
/// one place they were trusted: an upstream that keeps sending and never finishes used
/// to make the read run for ever, taking the healthy source's request with it. The limit
/// has to be enforced <b>while</b> reading, not from <c>Content-Length</c> alone, because
/// a chunked response does not declare one.
/// </summary>
internal sealed class BoundedStream(Stream inner, long limitBytes) : Stream
{
    private long readSoFar;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Count(inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(buffer, cancellationToken));

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        Count(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken));

    private int Count(int bytesRead)
    {
        readSoFar += bytesRead;
        if (readSoFar > limitBytes)
        {
            throw new UpstreamBodyTooLargeException(limitBytes);
        }

        return bytesRead;
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
