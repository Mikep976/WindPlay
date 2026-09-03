using AirPlay.Core2.Controllers;
using AirPlay.Core2.Decoders;
using AirPlay.Core2.Models.Messages.Audio;
using Org.BouncyCastle.Crypto;
using System.Buffers.Binary;

namespace AirPlay.Core2.Extensions;

internal static class RaopBufferExtensions
{
    public static void Flush(this RaopBuffer raopBuffer, int nextSequence)
    {
        ArgumentNullException.ThrowIfNull(raopBuffer);

        lock (raopBuffer)
        {
            for (int index = 0; index < RaopBuffer.RAOP_BUFFER_LENGTH; index++)
            {
                raopBuffer.Entries[index].Available = false;
                raopBuffer.Entries[index].AudioBufferLen = 0;
            }

            raopBuffer.IsEmpty = true;
            if ((uint)nextSequence <= ushort.MaxValue)
            {
                raopBuffer.FirstSeqNum = (ushort)nextSequence;
                raopBuffer.LastSeqNum = unchecked((ushort)(nextSequence - 1));
            }
        }
    }

    public static int Queue(
        this RaopBuffer raopBuffer,
        IBufferedCipher decryptor,
        IDecoder decoder,
        byte[] data,
        ushort dataLength)
    {
        ArgumentNullException.ThrowIfNull(raopBuffer);
        ArgumentNullException.ThrowIfNull(decryptor);
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentNullException.ThrowIfNull(data);

        lock (raopBuffer)
        {
            if (dataLength < 12 || dataLength > AudioController.RAOP_PACKET_LENGTH || dataLength > data.Length)
                return -1;

            if (dataLength == 12 ||
                (dataLength == 16 && data[12] == 0x00 && data[13] == 0x68 && data[14] == 0x34 && data[15] == 0x00))
                return 0;

            ushort sequenceNumber = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2));
            if (!raopBuffer.IsEmpty && SequenceNumberCompare(sequenceNumber, raopBuffer.FirstSeqNum) < 0)
                return 0;

            ushort bufferEnd = unchecked((ushort)(raopBuffer.FirstSeqNum + RaopBuffer.RAOP_BUFFER_LENGTH));
            if (!raopBuffer.IsEmpty && SequenceNumberCompare(sequenceNumber, bufferEnd) >= 0)
                raopBuffer.Flush(sequenceNumber);

            int entryIndex = sequenceNumber % RaopBuffer.RAOP_BUFFER_LENGTH;
            RaopBufferEntry entry = raopBuffer.Entries[entryIndex];
            if (entry.Available && entry.SeqNum == sequenceNumber)
                return 0;

            int payloadLength = dataLength - 12;
            byte[] raw = new byte[payloadLength];
            Array.Copy(data, 12, raw, 0, payloadLength);

            int expectedOutputLength = decoder.GetOutputStreamLength();
            if (expectedOutputLength < 0)
                expectedOutputLength = raw.Length;
            if (expectedOutputLength <= 0 || expectedOutputLength > 64 * 1024)
                return -1;

            byte[] output = new byte[expectedOutputLength];
            try
            {
                int encryptedLength = payloadLength / 16 * 16;
                if (encryptedLength > 0 &&
                    decryptor.ProcessBytes(raw, 0, encryptedLength, raw, 0) != encryptedLength)
                    return -1;

                if (decoder.DecodeFrame(raw, ref output) != 0)
                    return -1;
            }
            catch (Exception)
            {
                // A malformed network packet must not terminate the receive worker.
                return -1;
            }

            if (output.Length <= 0 || output.Length > 64 * 1024)
                return -1;

            if (entry.AudioBuffer.Length < output.Length)
            {
                entry.AudioBuffer = new byte[output.Length];
                entry.AudioBufferSize = output.Length;
            }

            Array.Copy(output, 0, entry.AudioBuffer, 0, output.Length);
            entry.AudioBufferLen = output.Length;
            entry.Flags = data[0];
            entry.Type = data[1];
            entry.SeqNum = sequenceNumber;
            entry.TimeStamp = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));
            entry.SSrc = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8));
            entry.Available = true;

            if (raopBuffer.IsEmpty)
            {
                raopBuffer.FirstSeqNum = sequenceNumber;
                raopBuffer.LastSeqNum = sequenceNumber;
                raopBuffer.IsEmpty = false;
            }
            else if (SequenceNumberCompare(sequenceNumber, raopBuffer.LastSeqNum) > 0)
            {
                raopBuffer.LastSeqNum = sequenceNumber;
            }

            raopBuffer.Entries[entryIndex] = entry;
            return 1;
        }
    }

    public static RaopBufferEntry? Dequeue(this RaopBuffer raopBuffer, ref uint timestamp, bool noResend)
    {
        ArgumentNullException.ThrowIfNull(raopBuffer);

        lock (raopBuffer)
        {
            if (raopBuffer.IsEmpty)
                return null;

            int entryCount = unchecked((ushort)(raopBuffer.LastSeqNum - raopBuffer.FirstSeqNum)) + 1;
            int entryIndex = raopBuffer.FirstSeqNum % RaopBuffer.RAOP_BUFFER_LENGTH;
            RaopBufferEntry entry = raopBuffer.Entries[entryIndex];

            if (!entry.Available)
            {
                // Give a LAN retransmission a short opportunity to arrive, then
                // skip the gap rather than allowing latency to grow unbounded.
                if (!noResend && entryCount < RaopBuffer.MAXIMUM_REORDER_WAIT_PACKETS)
                    return null;

                AdvanceFirstSequence(raopBuffer);
                return null;
            }

            RaopBufferEntry result = entry;
            entry.Available = false;
            entry.AudioBufferLen = 0;
            raopBuffer.Entries[entryIndex] = entry;
            timestamp = result.TimeStamp;
            AdvanceFirstSequence(raopBuffer);
            return result;
        }
    }

    private static void AdvanceFirstSequence(RaopBuffer raopBuffer)
    {
        if (raopBuffer.FirstSeqNum == raopBuffer.LastSeqNum)
            raopBuffer.IsEmpty = true;

        raopBuffer.FirstSeqNum = unchecked((ushort)(raopBuffer.FirstSeqNum + 1));
    }

    private static short SequenceNumberCompare(ushort left, ushort right)
        => unchecked((short)(left - right));
}
