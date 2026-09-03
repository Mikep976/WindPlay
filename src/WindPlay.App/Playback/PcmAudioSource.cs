using System.Runtime.InteropServices.WindowsRuntime;
using AirPlay.Core2.Models.Messages.Audio;
using Windows.Media.Core;
using Windows.Media.MediaProperties;

namespace WindPlay.App.Playback;

public sealed class PcmAudioSource : IDisposable
{
    private const int MaximumQueuedPackets = 64;
    private static readonly TimeSpan PacketDuration = TimeSpan.FromTicks(352L * TimeSpan.TicksPerSecond / 44_100L);

    private readonly object _gate = new();
    private readonly Queue<PcmAudioData> _packets = new();
    private readonly MediaStreamSource _streamSource;
    private MediaStreamSourceSampleRequest? _pendingRequest;
    private MediaStreamSourceSampleRequestDeferral? _pendingDeferral;
    private ulong? _basePts;
    private long _lastTimestampTicks = -1;
    private bool _disposed;

    public PcmAudioSource()
    {
        AudioEncodingProperties properties = AudioEncodingProperties.CreatePcm(44_100, 2, 16);
        _streamSource = new MediaStreamSource(new AudioStreamDescriptor(properties))
        {
            BufferTime = TimeSpan.FromMilliseconds(24),
            CanSeek = false,
            IsLive = true,
        };
        _streamSource.Starting += OnStarting;
        _streamSource.SampleRequested += OnSampleRequested;
        _streamSource.Closed += OnClosed;
        MediaSource = MediaSource.CreateFromMediaStreamSource(_streamSource);
    }

    public MediaSource MediaSource { get; }

    public long PacketsDropped { get; private set; }

    public void Enqueue(PcmAudioData packet)
    {
        if (packet.Data is null || packet.Length <= 0 || packet.Length > packet.Data.Length)
            return;

        MediaStreamSourceSampleRequest? pendingRequest = null;
        MediaStreamSourceSampleRequestDeferral? pendingDeferral = null;
        lock (_gate)
        {
            if (_disposed)
                return;

            if (_pendingRequest is not null)
            {
                pendingRequest = _pendingRequest;
                pendingDeferral = _pendingDeferral;
                _pendingRequest = null;
                _pendingDeferral = null;
            }
            else
            {
                while (_packets.Count >= MaximumQueuedPackets)
                {
                    _packets.Dequeue();
                    PacketsDropped++;
                }
                _packets.Enqueue(packet);
                return;
            }
        }

        CompleteRequest(pendingRequest, pendingDeferral, packet);
    }

    private void OnStarting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
        => args.Request.SetActualStartPosition(TimeSpan.Zero);

    private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        PcmAudioData? packet = null;
        lock (_gate)
        {
            if (_disposed)
                return;
            if (_packets.Count > 0)
            {
                packet = _packets.Dequeue();
            }
            else if (_pendingRequest is null)
            {
                args.Request.ReportSampleProgress(0);
                _pendingRequest = args.Request;
                _pendingDeferral = args.Request.GetDeferral();
                return;
            }
        }

        if (packet.HasValue)
            CompleteRequest(args.Request, null, packet.Value);
    }

    private void CompleteRequest(
        MediaStreamSourceSampleRequest? request,
        MediaStreamSourceSampleRequestDeferral? deferral,
        PcmAudioData packet)
    {
        try
        {
            if (request is null)
                return;

            var buffer = WindowsRuntimeBufferExtensions.AsBuffer(packet.Data, 0, packet.Length);
            MediaStreamSample sample = MediaStreamSample.CreateFromBuffer(buffer, NormalizeTimestamp(packet.Pts));
            sample.Duration = TimeSpan.FromTicks(
                Math.Max(1, packet.Length / 4L) * TimeSpan.TicksPerSecond / 44_100L);
            request.Sample = sample;
            request.ReportSampleProgress(100);
        }
        finally
        {
            deferral?.Complete();
        }
    }

    private TimeSpan NormalizeTimestamp(ulong ptsMicroseconds)
    {
        lock (_gate)
        {
            _basePts ??= ptsMicroseconds;
            ulong relative = ptsMicroseconds >= _basePts.Value ? ptsMicroseconds - _basePts.Value : 0;
            long ticks = relative > (ulong)(long.MaxValue / 10) ? long.MaxValue : (long)relative * 10;
            if (ticks <= _lastTimestampTicks)
                ticks = _lastTimestampTicks + PacketDuration.Ticks;
            _lastTimestampTicks = ticks;
            return TimeSpan.FromTicks(ticks);
        }
    }

    private void OnClosed(MediaStreamSource sender, MediaStreamSourceClosedEventArgs args) => Dispose();

    public void Dispose()
    {
        MediaStreamSourceSampleRequestDeferral? deferral;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _packets.Clear();
            deferral = _pendingDeferral;
            _pendingDeferral = null;
            _pendingRequest = null;
        }

        deferral?.Complete();
        _streamSource.Starting -= OnStarting;
        _streamSource.SampleRequested -= OnSampleRequested;
        _streamSource.Closed -= OnClosed;
        MediaSource.Dispose();
    }
}
