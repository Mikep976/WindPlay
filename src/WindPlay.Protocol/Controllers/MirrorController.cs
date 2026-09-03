using AirPlay.Core2.Connections.Mirror;
using AirPlay.Core2.Models.Messages.Mirror;
using System.Drawing;
using System.Net;

using AesSecret = (byte[] DecryptedAesKey, byte[] AesIv, byte[] EcdhShared);

namespace AirPlay.Core2.Controllers;

public class MirrorController : IDisposable
{
    private readonly MirrorDataConnection _dataConnection;

    public ushort DataPort { get; }

    public Size? FrameSize => _dataConnection.FrameSize;

    public event EventHandler<Size>? FrameSizeChanged
    {
        add => _dataConnection?.FrameSizeChanged += value;
        remove => _dataConnection?.FrameSizeChanged -= value;
    }
    public event EventHandler<H264Data>? H264DataReceived
    {
        add => _dataConnection?.DataReceived += value;
        remove => _dataConnection?.DataReceived -= value;
    }

    public MirrorController(string streamConnectionId, AesSecret aesSecret, IPAddress remoteAddress)
    {
        _dataConnection = new MirrorDataConnection(streamConnectionId, aesSecret, remoteAddress);
        DataPort = _dataConnection.DataPort;
    }

    public void BeginConnectionWorkers()
    {
        _dataConnection?.BeginDataMessageLoopWorker();
    }

    public void EndConnectionWorkers()
    {
        _dataConnection?.EndDataMessageLoopWorker();
    }

    public void Dispose()
    {
        _dataConnection.Dispose();
    }
}
