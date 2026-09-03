namespace AirPlay.Core2.Models.Messages.Audio;

public class RaopBuffer
{
    public const int RAOP_BUFFER_LENGTH = 256;
    public const int MAXIMUM_REORDER_WAIT_PACKETS = 16;

    public bool IsEmpty { get; set; }
    public ushort FirstSeqNum { get; set; }
    public ushort LastSeqNum { get; set; }

    public RaopBufferEntry[] Entries { get; } = new RaopBufferEntry[RAOP_BUFFER_LENGTH];

    public int BufferSize { get; set; }
    public byte[] Buffer { get; set; } = [];

    public static RaopBuffer Create()
    {
        const int audioBufferSize = 2048;
        RaopBuffer raopBuffer = new();

        for (int i = 0; i < RAOP_BUFFER_LENGTH; i++)
        {
            raopBuffer.Entries[i].AudioBufferSize = audioBufferSize;
            raopBuffer.Entries[i].AudioBufferLen = 0;
            raopBuffer.Entries[i].AudioBuffer = new byte[audioBufferSize];
        }

        raopBuffer.IsEmpty = true;
        return raopBuffer;
    }
}
