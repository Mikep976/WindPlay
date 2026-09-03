using AirPlay.Core2.Models.Messages.Audio;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace AirPlay.Core2.Decoders;

/// <summary>
/// Decodes the raw audio access units used by AirPlay through WindPlay's bounded
/// native FFmpeg shim. Only codec APIs are exposed; no FFmpeg network or demuxer
/// entry points are reachable from the receiver process.
/// </summary>
public sealed class NativeAudioDecoder(AudioFormat audioFormat) : IDecoder, IDisposable
{
    private SafeAudioDecoderHandle? _handle;
    private int _maximumOutputLength;

    public AudioFormat Type { get; } = audioFormat;

    public int Config(int sampleRate, int channels, int bitDepth, int frameLength)
    {
        bool isExpectedProfile = sampleRate == 44_100 && channels == 2 && bitDepth == 16 &&
            ((Type == AudioFormat.ALAC && frameLength == 352) ||
             (Type == AudioFormat.AAC_ELD && frameLength == 480) ||
             (Type == AudioFormat.AAC && frameLength == 1_024));
        if (!isExpectedProfile)
            return -1;

        _handle?.Dispose();
        _handle = NativeMethods.Create((int)Type, sampleRate, channels, bitDepth, frameLength);
        if (_handle.IsInvalid)
        {
            _handle.Dispose();
            _handle = null;
            return -1;
        }

        _maximumOutputLength = checked(frameLength * channels * sizeof(short));
        return 0;
    }

    public int GetOutputStreamLength() => _maximumOutputLength;

    public int DecodeFrame(byte[] input, ref byte[] output)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (_handle is null || _handle.IsInvalid || input.Length is <= 0 or > 64 * 1024)
            return -1;

        if (output.Length < _maximumOutputLength)
            output = new byte[_maximumOutputLength];

        int decodedLength = NativeMethods.Decode(
            _handle,
            input,
            input.Length,
            output,
            _maximumOutputLength);
        if (decodedLength < 0 || decodedLength > output.Length)
            return -1;

        if (decodedLength != output.Length)
            Array.Resize(ref output, decodedLength);
        return 0;
    }

    public void Dispose()
    {
        _handle?.Dispose();
        _handle = null;
    }

    private sealed class SafeAudioDecoderHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeAudioDecoderHandle() : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle()
        {
            NativeMethods.Destroy(handle);
            return true;
        }
    }

    private static class NativeMethods
    {
        private const string LibraryName = "WindPlay.Codecs.dll";

        [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32)]
        [DllImport(LibraryName, EntryPoint = "windplay_audio_decoder_create", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern SafeAudioDecoderHandle Create(
            int audioFormat,
            int sampleRate,
            int channels,
            int bitDepth,
            int frameLength);

        [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32)]
        [DllImport(LibraryName, EntryPoint = "windplay_audio_decoder_decode", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Decode(
            SafeAudioDecoderHandle decoder,
            byte[] input,
            int inputLength,
            [Out] byte[] output,
            int outputCapacity);

        [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32)]
        [DllImport(LibraryName, EntryPoint = "windplay_audio_decoder_destroy", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Destroy(nint decoder);
    }
}
