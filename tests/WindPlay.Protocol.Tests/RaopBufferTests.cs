using AirPlay.Core2.Decoders;
using AirPlay.Core2.Extensions;
using AirPlay.Core2.Models.Messages.Audio;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using Xunit;

namespace WindPlay.Protocol.Tests;

public sealed class RaopBufferTests
{
    [Fact]
    public void DequeueWaitsForMissingPacketWithinReorderWindow()
    {
        RaopBuffer buffer = RaopBuffer.Create();
        buffer.IsEmpty = false;
        buffer.FirstSeqNum = 100;
        buffer.LastSeqNum = 101;
        SetEntry(buffer, 101, 5_000);
        uint timestamp = 0;

        RaopBufferEntry? result = buffer.Dequeue(ref timestamp, noResend: false);

        Assert.Null(result);
        Assert.Equal((ushort)100, buffer.FirstSeqNum);
        Assert.Equal(0U, timestamp);
    }

    [Fact]
    public void DequeueSkipsMissingPacketAtReorderLimit()
    {
        RaopBuffer buffer = RaopBuffer.Create();
        buffer.IsEmpty = false;
        buffer.FirstSeqNum = 100;
        buffer.LastSeqNum = 100 + RaopBuffer.MAXIMUM_REORDER_WAIT_PACKETS - 1;
        SetEntry(buffer, buffer.LastSeqNum, 5_000);
        uint timestamp = 0;

        RaopBufferEntry? result = buffer.Dequeue(ref timestamp, noResend: false);

        Assert.Null(result);
        Assert.Equal((ushort)101, buffer.FirstSeqNum);
    }

    [Fact]
    public void DequeueHandlesSequenceWrapAndBecomesEmpty()
    {
        RaopBuffer buffer = RaopBuffer.Create();
        buffer.IsEmpty = false;
        buffer.FirstSeqNum = ushort.MaxValue;
        buffer.LastSeqNum = 0;
        SetEntry(buffer, ushort.MaxValue, 1_000);
        SetEntry(buffer, 0, 2_000);
        uint timestamp = 0;

        RaopBufferEntry? first = buffer.Dequeue(ref timestamp, noResend: false);
        Assert.True(first.HasValue);
        Assert.Equal(ushort.MaxValue, first.Value.SeqNum);
        Assert.Equal(1_000U, timestamp);
        Assert.False(buffer.IsEmpty);
        Assert.Equal((ushort)0, buffer.FirstSeqNum);

        RaopBufferEntry? second = buffer.Dequeue(ref timestamp, noResend: false);
        Assert.True(second.HasValue);
        Assert.Equal((ushort)0, second.Value.SeqNum);
        Assert.Equal(2_000U, timestamp);
        Assert.True(buffer.IsEmpty);
        Assert.Equal((ushort)1, buffer.FirstSeqNum);
    }

    [Theory]
    [InlineData(0, 0, ushort.MaxValue)]
    [InlineData(ushort.MaxValue, ushort.MaxValue, ushort.MaxValue - 1)]
    public void FlushAcceptsSequenceNumberBoundaries(int next, ushort expectedFirst, ushort expectedLast)
    {
        RaopBuffer buffer = RaopBuffer.Create();

        buffer.Flush(next);

        Assert.True(buffer.IsEmpty);
        Assert.Equal(expectedFirst, buffer.FirstSeqNum);
        Assert.Equal(expectedLast, buffer.LastSeqNum);
    }

    [Fact]
    public void QueueHandlesSequenceWrapWithoutChangingInput()
    {
        RaopBuffer buffer = RaopBuffer.Create();
        buffer.IsEmpty = false;
        buffer.FirstSeqNum = ushort.MaxValue;
        buffer.LastSeqNum = ushort.MaxValue;
        SetEntry(buffer, ushort.MaxValue, 1_000);

        byte[] packet = CreatePacket(sequenceNumber: 0, payloadLength: 16);
        byte[] original = [.. packet];
        IBufferedCipher cipher = CreateCipher();
        PCMDecoder decoder = new();

        int result = buffer.Queue(cipher, decoder, packet, checked((ushort)packet.Length));

        Assert.Equal(1, result);
        Assert.Equal((ushort)0, buffer.LastSeqNum);
        Assert.Equal(original, packet);
        Assert.True(buffer.Entries[0].Available);
    }

    private static void SetEntry(RaopBuffer buffer, ushort sequenceNumber, uint timestamp)
    {
        int index = sequenceNumber % RaopBuffer.RAOP_BUFFER_LENGTH;
        RaopBufferEntry entry = buffer.Entries[index];
        entry.Available = true;
        entry.SeqNum = sequenceNumber;
        entry.TimeStamp = timestamp;
        entry.AudioBuffer[0] = 0x42;
        entry.AudioBufferLen = 1;
        buffer.Entries[index] = entry;
    }

    private static byte[] CreatePacket(ushort sequenceNumber, int payloadLength)
    {
        byte[] packet = new byte[12 + payloadLength];
        packet[0] = 0x80;
        packet[1] = 0x60;
        packet[2] = (byte)(sequenceNumber >> 8);
        packet[3] = (byte)sequenceNumber;
        for (int index = 0; index < payloadLength; index++)
            packet[12 + index] = (byte)(index + 1);
        return packet;
    }

    private static IBufferedCipher CreateCipher()
    {
        IBufferedCipher cipher = CipherUtilities.GetCipher("AES/CBC/NoPadding");
        cipher.Init(false, new ParametersWithIV(new KeyParameter(new byte[16]), new byte[16]));
        return cipher;
    }
}
