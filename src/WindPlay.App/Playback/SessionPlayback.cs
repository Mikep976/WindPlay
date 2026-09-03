using System.Drawing;
using AirPlay.Core2.Controllers;
using AirPlay.Core2.Models;
using AirPlay.Core2.Models.Messages.Audio;
using AirPlay.Core2.Models.Messages.Mirror;
using Microsoft.UI.Dispatching;
using Windows.Media.Playback;
using WindPlay.App.Configuration;
using WindPlay.App.Windows;

namespace WindPlay.App.Playback;

internal sealed class SessionPlayback : IDisposable
{
    private const int MaximumPrerollFrames = 3;
    private const int MaximumPrerollAudioPackets = 32;

    private readonly DeviceSession _session;
    private readonly ReceiverSettings _settings;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly object _gate = new();
    private readonly Queue<H264Data> _videoPreroll = new();
    private readonly Queue<PcmAudioData> _audioPreroll = new();
    private readonly System.Threading.Timer _statisticsTimer;

    private HardwareVideoSource? _videoSource;
    private PcmAudioSource? _audioSource;
    private MediaPlayer? _audioPlayer;
    private PlaybackWindow? _window;
    private MirrorController? _mirrorController;
    private AudioController? _audioController;
    private bool _disposed;

    public SessionPlayback(DeviceSession session, ReceiverSettings settings, DispatcherQueue dispatcherQueue)
    {
        _session = session;
        _settings = settings;
        _dispatcherQueue = dispatcherQueue;
        session.MirrorControllerCreated += OnMirrorControllerCreated;
        session.AudioControllerCreated += OnAudioControllerCreated;
        session.RemoteSetVolumeRequest += OnRemoteSetVolumeRequest;
        _statisticsTimer = new System.Threading.Timer(
            _ => _dispatcherQueue.TryEnqueue(UpdateStatistics),
            null,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
    }

    private void OnMirrorControllerCreated(object? sender, EventArgs args)
    {
        MirrorController? controller = _session.MirrorController;
        if (controller is null)
            return;

        _mirrorController = controller;
        controller.FrameSizeChanged += OnFrameSizeChanged;
        controller.H264DataReceived += OnVideoFrameReceived;
        if (controller.FrameSize is Size size)
            OnFrameSizeChanged(controller, size);
    }

    private void OnAudioControllerCreated(object? sender, EventArgs args)
    {
        AudioController? controller = _session.AudioController;
        if (controller is null)
            return;

        _audioController = controller;
        controller.AudioDataReceived += OnAudioDataReceived;
        _dispatcherQueue.TryEnqueue(ConfigureAudio);
    }

    private void OnFrameSizeChanged(object? sender, Size size)
    {
        if (size.Width <= 0 || size.Height <= 0)
            return;
        _dispatcherQueue.TryEnqueue(() => ConfigureVideo(size.Width, size.Height));
    }

    private void OnVideoFrameReceived(object? sender, H264Data frame)
    {
        HardwareVideoSource? source;
        lock (_gate)
        {
            if (_disposed)
                return;
            source = _videoSource;
            if (source is null)
            {
                while (_videoPreroll.Count >= MaximumPrerollFrames)
                    _videoPreroll.Dequeue().Dispose();
                _videoPreroll.Enqueue(frame.Retain());
                return;
            }
        }

        source.Enqueue(frame);
    }

    private void OnAudioDataReceived(object? sender, PcmAudioData packet)
    {
        PcmAudioSource? source;
        lock (_gate)
        {
            if (_disposed)
                return;
            source = _audioSource;
            if (source is null)
            {
                while (_audioPreroll.Count >= MaximumPrerollAudioPackets)
                    _audioPreroll.Dequeue();
                _audioPreroll.Enqueue(packet);
                return;
            }
        }

        source.Enqueue(packet);
    }

    private void ConfigureVideo(int width, int height)
    {
        if (_disposed)
            return;

        HardwareVideoSource newSource = new(width, height);
        HardwareVideoSource? oldSource;
        H264Data[] preroll;
        lock (_gate)
        {
            oldSource = _videoSource;
            _videoSource = newSource;
            preroll = [.. _videoPreroll];
            _videoPreroll.Clear();
        }

        _window ??= new PlaybackWindow(_session, _settings, width, height);
        _window.AttachVideo(newSource.MediaSource);
        _window.Activate();
        oldSource?.Dispose();

        foreach (H264Data frame in preroll)
        {
            newSource.Enqueue(frame);
            frame.Dispose();
        }
    }

    private void ConfigureAudio()
    {
        if (_disposed || _audioSource is not null)
            return;

        PcmAudioSource source = new();
        PcmAudioData[] preroll;
        lock (_gate)
        {
            _audioSource = source;
            preroll = [.. _audioPreroll];
            _audioPreroll.Clear();
        }

        _audioPlayer = new MediaPlayer
        {
            AutoPlay = true,
            RealTimePlayback = true,
            AudioCategory = MediaPlayerAudioCategory.Media,
            Volume = Math.Clamp(_session.Volume / 100, 0, 1),
            Source = source.MediaSource,
        };
        _audioPlayer.CommandManager.IsEnabled = false;
        _audioPlayer.Play();

        foreach (PcmAudioData packet in preroll)
            source.Enqueue(packet);
    }

    private void OnRemoteSetVolumeRequest(object? sender, double volume)
        => _dispatcherQueue.TryEnqueue(() =>
        {
            if (_audioPlayer is not null)
                _audioPlayer.Volume = Math.Clamp(volume / 100, 0, 1);
        });

    private void UpdateStatistics()
    {
        HardwareVideoSource? source = _videoSource;
        if (_window is not null && source is not null)
            _window.UpdatePerformance(source.FramesReceived, source.FramesDropped);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            while (_videoPreroll.Count > 0)
                _videoPreroll.Dequeue().Dispose();
            _audioPreroll.Clear();
        }

        _statisticsTimer.Dispose();
        _session.MirrorControllerCreated -= OnMirrorControllerCreated;
        _session.AudioControllerCreated -= OnAudioControllerCreated;
        _session.RemoteSetVolumeRequest -= OnRemoteSetVolumeRequest;
        if (_mirrorController is not null)
        {
            _mirrorController.FrameSizeChanged -= OnFrameSizeChanged;
            _mirrorController.H264DataReceived -= OnVideoFrameReceived;
        }
        if (_audioController is not null)
            _audioController.AudioDataReceived -= OnAudioDataReceived;

        _window?.EndSession();
        _window = null;
        _audioPlayer?.Dispose();
        _audioPlayer = null;
        _audioSource?.Dispose();
        _audioSource = null;
        _videoSource?.Dispose();
        _videoSource = null;
    }
}
