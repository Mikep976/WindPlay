using AirPlay.Core2.Controllers;
using AirPlay.Core2.Decoders;
using AirPlay.Core2.Extensions;
using AirPlay.Core2.Models.Messages.Audio;
using AirPlay.Core2.Utils;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

using AesSecret = (byte[] DecryptedAesKey, byte[] AesIv, byte[] EcdhShared);
using ResendRequest = (ushort MissingSeqNum, ushort Count);
using SyncData = (ulong SyncTime, ulong SyncTimestamp);

namespace AirPlay.Core2.Connections.Audio;

public class AudioDataConnection : IDisposable
{
    private readonly byte[] _aesKey;
    private readonly IDecoder _decoder;
    private readonly IBufferedCipher _aesCbcDecrypt = CipherUtilities.GetCipher("AES/CBC/NoPadding");

    private readonly AesSecret _aesSecret;
    private readonly IPAddress _expectedRemoteAddress;
    private readonly Socket _udpListener = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

    private readonly CancellationTokenSource _tokenSource = new();
    private readonly RaopBuffer _raopBuffer = RaopBuffer.Create();
    private readonly Lock _cipherLock = new();
    private readonly Lock _syncLock = new();

    private SyncData _syncData;
    private bool _hasSyncData;

    public event EventHandler<PcmAudioData>? DataReceived;
    public event EventHandler<ResendRequest>? ResendRequested;

    public AudioDataConnection(AudioFormat audioFormat, AesSecret aesSecret, IPAddress expectedRemoteAddress)
    {
        ArgumentNullException.ThrowIfNull(expectedRemoteAddress);
        if (expectedRemoteAddress.AddressFamily != AddressFamily.InterNetwork)
            throw new NotSupportedException("The current RAOP audio transport requires IPv4.");

        _expectedRemoteAddress = expectedRemoteAddress;
        _udpListener.Bind(new IPEndPoint(IPAddress.Any, 0));
        LocalPort = checked((ushort)((IPEndPoint)_udpListener.LocalEndPoint!).Port);
        _aesSecret = aesSecret;
        _aesKey = AESUtils.HashAndTruncate(_aesSecret.DecryptedAesKey, _aesSecret.EcdhShared);

        if (audioFormat == AudioFormat.ALAC)
        {
            // RTP info: 96 AppleLossless, 96 352 0 16 40 10 14 2 255 0 0 44100
            // (ALAC -> PCM)

            _decoder = new NativeAudioDecoder(audioFormat);
            if (_decoder.Config(sampleRate: 44100, channels: 2, bitDepth: 16, frameLength: 352) != 0)
                throw new NotSupportedException("The native ALAC decoder could not be initialized.");
        }
        else if (audioFormat == AudioFormat.AAC_ELD)
        {
            _decoder = new NativeAudioDecoder(audioFormat);
            if (_decoder.Config(sampleRate: 44100, channels: 2, bitDepth: 16, frameLength: 480) != 0)
                throw new NotSupportedException("The native AAC-ELD decoder could not be initialized.");
        }
        else if (audioFormat == AudioFormat.AAC)
        {
            _decoder = new NativeAudioDecoder(audioFormat);
            if (_decoder.Config(sampleRate: 44100, channels: 2, bitDepth: 16, frameLength: 1024) != 0)
                throw new NotSupportedException("The native AAC decoder could not be initialized.");
        }
        else if (audioFormat == AudioFormat.PCM)
        {
            // (PCM -> PCM)
            _decoder = new PCMDecoder();
        }
        else
        {
            throw new NotSupportedException($"The sender selected unsupported audio format {audioFormat}.");
        }
    }

    public ushort LocalPort { get; }

    public void BeginDataMessageLoopWorker() => Task.Run(async () => await DataMessageLoopWorker(_tokenSource.Token), _tokenSource.Token);

    public void EndDataMessageLoopWorker()
    {
        _udpListener.Close();
        _tokenSource.Cancel();
    }

    public void HandleResendBuffer(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        lock (_cipherLock)
        {
            InitAesCbcCipher();

            _ = _raopBuffer.Queue(_aesCbcDecrypt, _decoder, buffer, (ushort)buffer.Length);
        }
    }

    public void HandleSyncData(SyncData syncData)
    {
        lock (_syncLock)
        {
            _syncData = syncData;
            _hasSyncData = true;
        }
    }

    public void Flush(int nextSeq) => _raopBuffer.Flush(nextSeq);

    private async Task DataMessageLoopWorker(CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(AudioController.RAOP_PACKET_LENGTH);

        try
        {
            EndPoint receiveFrom = new IPEndPoint(IPAddress.Any, 0);
            while (!cancellationToken.IsCancellationRequested)
            {
                SocketReceiveFromResult receiveResult = await _udpListener.ReceiveFromAsync(
                    buffer,
                    SocketFlags.None,
                    receiveFrom,
                    cancellationToken);
                if (receiveResult.RemoteEndPoint is not IPEndPoint remoteEndPoint ||
                    !remoteEndPoint.Address.Equals(_expectedRemoteAddress))
                    continue;

                int udpReceiveResult = receiveResult.ReceivedBytes;
                if (udpReceiveResult < 12) continue;

                RaopBufferEntry? audiobuf;
                uint timestamp = 0;

                lock (_cipherLock)
                {
                    InitAesCbcCipher();
                    _ = _raopBuffer.Queue(_aesCbcDecrypt, _decoder, buffer, (ushort)udpReceiveResult);
                }

                SyncData syncData;
                lock (_syncLock)
                {
                    if (!_hasSyncData)
                        continue;
                    syncData = _syncData;
                }

                while ((audiobuf = _raopBuffer.Dequeue(ref timestamp, noResend: false)) != null)
                {
                    uint relativeTimestamp = unchecked(timestamp - (uint)syncData.SyncTimestamp);
                    PcmAudioData pcmData = new()
                    {
                        Length = audiobuf.Value.AudioBufferLen,
                        Data = audiobuf.Value.AudioBuffer.AsSpan(0, audiobuf.Value.AudioBufferLen).ToArray(),
                        Pts = (relativeTimestamp * 1_000_000UL / 44_100UL) + syncData.SyncTime
                    };

                    DataReceived?.Invoke(this, pcmData);
                }

                CheckAndRequestResend();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Dispose()
    {
        if (_decoder is IDisposable disposableDecoder)
            disposableDecoder.Dispose();
        CryptographicOperations.ZeroMemory(_aesKey);
        _tokenSource.Dispose();
        _udpListener.Dispose();

    }

    private void InitAesCbcCipher()
    {
        var keyParameter = ParameterUtilities.CreateKeyParameter("AES", _aesKey);
        var cipherParameters = new ParametersWithIV(keyParameter, _aesSecret.AesIv, 0, _aesSecret.AesIv.Length);

        _aesCbcDecrypt.Init(false, cipherParameters);
    }

    private void CheckAndRequestResend()
    {
        ResendRequest? request = null;
        lock (_raopBuffer)
        {
            ushort seqnum;

            for (seqnum = _raopBuffer.FirstSeqNum; SeqNumCmp(seqnum, _raopBuffer.LastSeqNum) < 0; seqnum++)
            {
                var entry = _raopBuffer.Entries[seqnum % RaopBuffer.RAOP_BUFFER_LENGTH];
                if (entry.Available)
                    break;
            }

            if (SeqNumCmp(seqnum, _raopBuffer.FirstSeqNum) != 0)
            {
                int count = unchecked((ushort)(seqnum - _raopBuffer.FirstSeqNum));
                request = (_raopBuffer.FirstSeqNum, (ushort)count);
            }
        }

        if (request.HasValue)
            ResendRequested?.Invoke(this, request.Value);
    }

    private static short SeqNumCmp(ushort s1, ushort s2) => unchecked((short)(s1 - s2));
}
