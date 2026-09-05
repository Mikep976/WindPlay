using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using AirPlay.Core2.Models.Messages.Mirror;
using Windows.Media.Core;
using Windows.Media.MediaProperties;

namespace WindPlay.App.Playback;

/// <summary>Feeds compressed H.264 directly to Media Foundation's hardware decode path.</summary>
public sealed class HardwareVideoSource : IDisposable
{
    private const int MaximumQueuedFrames = 3;
    private static readonly TimeSpan NominalFrameDuration = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60);

    private readonly object _gate = new();
    private readonly Queue<H264Data> _frames = new();
    private readonly MediaStreamSource _streamSource;
    private MediaStreamSourceSampleRequest? _pendingRequest;
    private MediaStreamSourceSampleRequestDeferral? _pendingDeferral;
    private long? _basePts;
    private long _lastTimestampTicks = -1;
    private bool _disposed;

    public HardwareVideoSource(int width, int height)
    {
        AirPlay.Core2.Security.MirrorLimits.ValidateDimensions(width, height);
        if (width is <= 0 or > 16_384)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (height is <= 0 or > 16_384)
            throw new ArgumentOutOfRangeException(nameof(height));

        VideoEncodingProperties properties = VideoEncodingProperties.CreateH264();
        properties.Width = (uint)width;
        properties.Height = (uint)height;
        properties.FrameRate.Numerator = 60;
        properties.FrameRate.Denominator = 1;

        _streamSource = new MediaStreamSource(new VideoStreamDescriptor(properties))
        {
            BufferTime = TimeSpan.Zero,
            CanSeek = false,
            IsLive = true,
        };
        _streamSource.Starting += OnStarting;
        _streamSource.SampleRequested += OnSampleRequested;
        _streamSource.Closed += OnClosed;
        MediaSource = MediaSource.CreateFromMediaStreamSource(_streamSource);
    }

    public MediaSource MediaSource { get; }

    public long FramesReceived { get; private set; }

    public long FramesDropped { get; private set; }

    public void Enqueue(H264Data frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        H264Data retainedFrame = frame.Retain();
        MediaStreamSourceSampleRequest? pendingRequest = null;
        MediaStreamSourceSampleRequestDeferral? pendingDeferral = null;

        lock (_gate)
        {
            if (_disposed)
            {
                retainedFrame.Dispose();
                return;
            }

            FramesReceived++;
            if (_pendingRequest is not null)
            {
                pendingRequest = _pendingRequest;
                pendingDeferral = _pendingDeferral;
                _pendingRequest = null;
                _pendingDeferral = null;
            }
            else
            {
                while (_frames.Count >= MaximumQueuedFrames)
                {
                    _frames.Dequeue().Dispose();
                    FramesDropped++;
                }
                _frames.Enqueue(retainedFrame);
                return;
            }
        }

        CompleteRequest(pendingRequest, pendingDeferral, retainedFrame);
    }

    private void OnStarting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
        => args.Request.SetActualStartPosition(TimeSpan.Zero);

    private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        H264Data? frame = null;
        lock (_gate)
        {
            if (_disposed)
                return;

            if (_frames.Count > 0)
            {
                frame = _frames.Dequeue();
            }
            else if (_pendingRequest is null)
            {
                args.Request.ReportSampleProgress(0);
                _pendingRequest = args.Request;
                _pendingDeferral = args.Request.GetDeferral();
                return;
            }
        }

        if (frame is not null)
            CompleteRequest(args.Request, null, frame);
    }

    private void CompleteRequest(
        MediaStreamSourceSampleRequest? request,
        MediaStreamSourceSampleRequestDeferral? deferral,
        H264Data frame)
    {
        try
        {
            if (request is null || !MemoryMarshal.TryGetArray(frame.Data, out ArraySegment<byte> segment) || segment.Array is null)
                return;

            var buffer = WindowsRuntimeBufferExtensions.AsBuffer(segment.Array, segment.Offset, segment.Count);
            MediaStreamSample sample = MediaStreamSample.CreateFromBuffer(buffer, NormalizeTimestamp(frame.Pts));
            sample.Duration = NominalFrameDuration;
            sample.KeyFrame = frame.FrameType == 5;
            sample.Processed += (_, _) => frame.Dispose();
            request.Sample = sample;
            request.ReportSampleProgress(100);
            frame = null!; // The Processed callback now owns the retained reference.
        }
        finally
        {
            frame?.Dispose();
            deferral?.Complete();
        }
    }

    private TimeSpan NormalizeTimestamp(long ptsMicroseconds)
    {
        lock (_gate)
        {
            _basePts ??= ptsMicroseconds;
            long ticks = Math.Max(0, checked((ptsMicroseconds - _basePts.Value) * 10));
            if (ticks <= _lastTimestampTicks)
                ticks = _lastTimestampTicks + NominalFrameDuration.Ticks;
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

            while (_frames.Count > 0)
                _frames.Dequeue().Dispose();
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
