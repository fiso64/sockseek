namespace Sockseek.Core.IO;

/// <summary>
/// Restricts reads to an exact declared length and converts an early underlying
/// EOF into a deterministic failure instead of allowing a caller to spin.
/// A seekable source retains its initial absolute position: Soulseek.NET reads
/// Position again at completion, including when the peer resumed partway through.
/// </summary>
public sealed class ExactLengthReadStream : Stream
{
    private readonly Stream inner;
    private readonly bool leaveOpen;
    private long remaining;
    private bool disposed;

    public ExactLengthReadStream(Stream inner, long length, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!inner.CanRead)
            throw new ArgumentException("Underlying stream must be readable.", nameof(inner));
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length));

        this.inner = inner;
        this.leaveOpen = leaveOpen;
        long startOffset = inner.CanSeek ? inner.Position : 0;
        if (startOffset < 0 || startOffset > length)
            throw new ArgumentOutOfRangeException(nameof(length), "Declared length must include the current stream position.");
        remaining = length - startOffset;
        Length = length;
    }

    public long Remaining => remaining;

    public override bool CanRead => !disposed && inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length { get; }

    public override long Position
    {
        get => Length - remaining;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
        => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (remaining == 0 || buffer.Length == 0)
            return 0;

        int requested = (int)Math.Min(buffer.Length, remaining);
        int read = inner.Read(buffer[..requested]);
        Consume(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (remaining == 0 || buffer.Length == 0)
            return 0;

        int requested = (int)Math.Min(buffer.Length, remaining);
        int read = await inner
            .ReadAsync(buffer[..requested], cancellationToken)
            .ConfigureAwait(false);
        Consume(read);
        return read;
    }

    public override int ReadByte()
    {
        Span<byte> value = stackalloc byte[1];
        return Read(value) == 0 ? -1 : value[0];
    }

    private void Consume(int read)
    {
        if (read == 0)
            throw new EndOfStreamException(
                $"Underlying stream ended with {remaining} declared bytes remaining.");
        remaining -= read;
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposed)
        {
            disposed = true;
            if (disposing && !leaveOpen)
                inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            disposed = true;
            if (!leaveOpen)
                await inner.DisposeAsync().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
