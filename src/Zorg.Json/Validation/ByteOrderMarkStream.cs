using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Zorg.Json.Validation;

/// <summary>
/// Passes a stream through and notes whether it began with a UTF-8 byte order mark. <c>JsonDocument.ParseAsync</c> skips the
/// mark without saying so, while the string and byte overloads refuse it, so the stream overload uses this to refuse it too.
/// </summary>
internal sealed class ByteOrderMarkStream(Stream inner) : Stream
{
    private readonly byte[] _head = new byte[3];
    private int _seen;

    public bool StartsWithMark => _seen == 3 && _head[0] == 0xEF && _head[1] == 0xBB && _head[2] == 0xBF;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int read = inner.Read(buffer);
        Note(buffer[..read]);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Note(buffer.Span[..read]);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private void Note(ReadOnlySpan<byte> bytes)
    {
        int take = Math.Min(bytes.Length, _head.Length - _seen);
        bytes[..take].CopyTo(_head.AsSpan(_seen));
        _seen += take;
    }
}
