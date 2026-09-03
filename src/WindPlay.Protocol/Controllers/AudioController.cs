using AirPlay.Core2.Connections.Audio;
using AirPlay.Core2.Models.Messages.Audio;
using System.Net;

using AesSecret = (byte[] DecryptedAesKey, byte[] AesIv, byte[] EcdhShared);

namespace AirPlay.Core2.Controllers;

public class AudioController : IDisposable
{
    public const int RAOP_PACKET_LENGTH = 50000;

    private readonly AudioDataConnection _dataConnection;
    private readonly AudioControlConnection _controlConnection;

    public ushort ControlPort { get; }
    public ushort DataPort { get; }

    public ushort RemoteControlPort { get; }
    public AudioFormat AudioFormat { get; }

    public int? LatencyMin { get; init; }
    public int? LatencyMax { get; init; }

    public event EventHandler<PcmAudioData>? AudioDataReceived
    {
        add => _dataConnection?.DataReceived += value;
        remove => _dataConnection?.DataReceived -= value;
    }

    public AudioController(
        AudioFormat audioFormat,
        AesSecret aesSecret,
        IPAddress remoteAddress,
        ushort remoteControlPort)
    {
        AudioFormat = audioFormat;
        RemoteControlPort = remoteControlPort;

        _dataConnection = new AudioDataConnection(audioFormat, aesSecret, remoteAddress);
        _controlConnection = new AudioControlConnection(remoteAddress, remoteControlPort);
        DataPort = _dataConnection.LocalPort;
        ControlPort = _controlConnection.LocalPort;

        _controlConnection.SyncDataReceived += (_, data) => _dataConnection.HandleSyncData(data);
        _controlConnection.ResentDataReceived += (_, data) => _dataConnection.HandleResendBuffer(data);
        _dataConnection.ResendRequested += (_, r) => _controlConnection.HandleResendPacket(r);
    }

    public void BeginConnectionWorkers()
    {
        _controlConnection?.BeginControlMessageLoopWorker();
        _dataConnection?.BeginDataMessageLoopWorker();
    }

    public void EndConnectionWorkers()
    {
        _controlConnection?.EndControlMessageLoopWorker();
        _dataConnection?.EndDataMessageLoopWorker();
    }

    public void Flush(int nextSeq) => _dataConnection?.Flush(nextSeq);

    public void Dispose()
    {
        _dataConnection.Dispose();
        _controlConnection.Dispose();
    }
}
