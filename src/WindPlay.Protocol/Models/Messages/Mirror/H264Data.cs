using System.Buffers;

namespace AirPlay.Core2.Models.Messages.Mirror;

/// <summary>
/// A pooled Annex-B H.264 access unit. Event consumers that keep the frame after
/// their callback returns must call <see cref="Retain"/> and later <see cref="Dispose"/>.
/// </summary>
public sealed class H264Data : EventArgs, IDisposable
{
    private byte[]? _buffer;
    private int _referenceCount = 1;

    internal H264Data(byte[] buffer, int length, int frameType, long pts, int width, int height)
    {
        _buffer = buffer;
        Length = length;
        FrameType = frameType;
        Pts = pts;
        Width = width;
        Height = height;
    }

    public int FrameType { get; }

    public int Length { get; }

    public long Pts { get; }

    public int Width { get; }

    public int Height { get; }

    public ReadOnlyMemory<byte> Data
    {
        get
        {
            byte[] buffer = Volatile.Read(ref _buffer)
                ?? throw new ObjectDisposedException(nameof(H264Data));
            return buffer.AsMemory(0, Length);
        }
    }

    public H264Data Retain()
    {
        while (true)
        {
            int count = Volatile.Read(ref _referenceCount);
            if (count <= 0 || Volatile.Read(ref _buffer) is null)
                throw new ObjectDisposedException(nameof(H264Data));
            if (Interlocked.CompareExchange(ref _referenceCount, count + 1, count) == count)
                return this;
        }
    }

    public void Dispose()
    {
        int count = Interlocked.Decrement(ref _referenceCount);
        if (count > 0)
            return;
        if (count < 0)
            throw new ObjectDisposedException(nameof(H264Data));

        byte[]? buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
    }
}
