namespace Sockseek.Core.IO;

/// <summary>
/// Owns a readable stream plus an external lease/permit release action. The
/// owner is released on EOF, disposal, or an idle deadline. Expiration guarantees
/// Sockseek-owned resource release and failed future reads; it does not claim to
/// cancel a network write already in progress outside this stream.
/// </summary>
public sealed class SelfExpiringReadStream : Stream
{
    private readonly Stream inner;
    private readonly Action releaseOwner;
    private readonly TimeSpan idleTimeout;
    private readonly Timer timer;
    private int disposed;
    private int ownerReleased;

    public SelfExpiringReadStream(
        Stream inner,
        TimeSpan idleTimeout,
        Action releaseOwner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(releaseOwner);
        if (!inner.CanRead)
            throw new ArgumentException("Underlying stream must be readable.", nameof(inner));
        if (idleTimeout <= TimeSpan.Zero || idleTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(idleTimeout));

        this.inner = inner;
        this.releaseOwner = releaseOwner;
        this.idleTimeout = idleTimeout;
        timer = new Timer(
            static state => ((SelfExpiringReadStream)state!).Expire(),
            this,
            idleTimeout,
            Timeout.InfiniteTimeSpan);
    }

    public bool IsExpired => Volatile.Read(ref disposed) != 0;

    public override bool CanRead => !IsExpired && inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
        => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ThrowIfExpired();
        int read = inner.Read(buffer);
        AfterRead(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ThrowIfExpired();
        int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        AfterRead(read);
        return read;
    }

    public override int ReadByte()
    {
        Span<byte> value = stackalloc byte[1];
        return Read(value) == 0 ? -1 : value[0];
    }

    private void AfterRead(int read)
    {
        ThrowIfExpired();
        if (read == 0)
        {
            Dispose();
            return;
        }

        _ = timer.Change(idleTimeout, Timeout.InfiniteTimeSpan);
    }

    private void Expire() => Dispose();

    private void ThrowIfExpired()
        => ObjectDisposedException.ThrowIf(IsExpired, this);

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            try
            {
                timer.Dispose();
                inner.Dispose();
            }
            finally
            {
                ReleaseOwner();
            }
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            try
            {
                await timer.DisposeAsync().ConfigureAwait(false);
                await inner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                ReleaseOwner();
            }
        }

        GC.SuppressFinalize(this);
    }

    private void ReleaseOwner()
    {
        if (Interlocked.Exchange(ref ownerReleased, 1) == 0)
            releaseOwner();
    }

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
