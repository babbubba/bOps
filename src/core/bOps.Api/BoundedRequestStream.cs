// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Api;

/// <summary>Raised when a request body is larger than the transport bound. Distinct from a backend archive-limit rejection.</summary>
internal sealed class RequestBodyTooLargeException : IOException
{
    public RequestBodyTooLargeException() : this("The request body exceeds the configured upload limit.")
    {
    }

    public RequestBodyTooLargeException(string message) : base(message)
    {
    }

    public RequestBodyTooLargeException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// A forward-only, read-only view of a request body that counts the bytes actually delivered and refuses to deliver more than the
/// limit. <c>Content-Length</c> is a claim, not a fact, so the bound is enforced on real bytes; the wrapper also works on hosts
/// whose <c>IHttpMaxRequestBodySizeFeature</c> is absent or read-only. Nothing is buffered here: bytes flow straight into the
/// lifecycle backend's own bounded streaming.
/// </summary>
internal sealed class BoundedRequestStream(Stream inner, long limit) : Stream
{
    private long _total;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken);
        Count(read);
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        await ReadAsync(buffer.AsMemory(offset, count), cancellationToken);

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        Count(read);
        return read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private void Count(int read)
    {
        _total = checked(_total + read);
        if (_total > limit)
        {
            throw new RequestBodyTooLargeException();
        }
    }
}
