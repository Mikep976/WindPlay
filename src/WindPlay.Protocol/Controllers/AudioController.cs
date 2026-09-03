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
    private readonly AudioTimingConnection _timingConnection;

    public ushort ControlPort { get; }
    public ushort TimingPort { get; }
    public ushort DataPort { get; }

    public ushort RemoteControlPort { get; }
    public ushort RemoteTimingPort { get; }
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
        ushort remoteControlPort,
        ushort remoteTimingPort)
    {
        AudioFormat = audioFormat;
        RemoteControlPort = remoteControlPort;
        RemoteTimingPort = remoteTimingPort;

        _dataConnection = new AudioDataConnection(audioFormat, aesSecret, remoteAddress);
        _controlConnection = new AudioControlConnection(remoteAddress, remoteControlPort);
        _timingConnection = new AudioTimingConnection(remoteAddress, remoteTimingPort);
        DataPort = _dataConnection.LocalPort;
        ControlPort = _controlConnection.LocalPort;
        TimingPort = _timingConnection.LocalPort;

        _controlConnection.SyncDataReceived += (_, data) => _dataConnection.HandleSyncData(data);
        _controlConnection.ResentDataReceived += (_, data) => _dataConnection.HandleResendBuffer(data);
        _dataConnection.ResendRequested += (_, r) => _controlConnection.HandleResendPacket(r);
    }

    public void BeginConnectionWorkers()
    {
        _controlConnection?.BeginControlMessageLoopWorker();
        _dataConnection?.BeginDataMessageLoopWorker();
        _timingConnection.BeginMessageLoopWorker();
    }

    public void EndConnectionWorkers()
    {
        _controlConnection?.EndControlMessageLoopWorker();
        _dataConnection?.EndDataMessageLoopWorker();
        _timingConnection.EndMessageLoopWorker();
    }

    public void Flush(int nextSeq) => _dataConnection?.Flush(nextSeq);

    public void Dispose()
    {
        _dataConnection.Dispose();
        _controlConnection.Dispose();
        _timingConnection.Dispose();
    }
}
