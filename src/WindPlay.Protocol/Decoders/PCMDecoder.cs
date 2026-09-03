using AirPlay.Core2.Models.Messages.Audio;

namespace AirPlay.Core2.Decoders;

public class PCMDecoder : IDecoder
{
    public AudioFormat Type => AudioFormat.PCM;

    public int Config(int sampleRate, int channels, int bitDepth, int frameLength) => 0;

    public int GetOutputStreamLength() => -1;

    public int DecodeFrame(byte[] input, ref byte[] output)
    {
        if (output.Length < input.Length)
            output = new byte[input.Length];

        int index = 0;
        for (; index + 1 < input.Length; index += 2)
        {
            // RTP L16 samples are transmitted in network byte order; Windows PCM is LE.
            output[index] = input[index + 1];
            output[index + 1] = input[index];
        }
        if (index < input.Length)
            output[index] = input[index];

        return 0;
    }
}
