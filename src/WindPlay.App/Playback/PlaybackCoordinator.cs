using System.Collections.Concurrent;
using AirPlay.Core2.Models;
using Microsoft.UI.Dispatching;
using WindPlay.App.Services;

namespace WindPlay.App.Playback;

public sealed class PlaybackCoordinator : IDisposable
{
    private readonly ReceiverHostManager _receiver;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly ConcurrentDictionary<DeviceSession, SessionPlayback> _sessions = [];

    public PlaybackCoordinator(ReceiverHostManager receiver, DispatcherQueue dispatcherQueue)
    {
        _receiver = receiver;
        _dispatcherQueue = dispatcherQueue;
        receiver.SessionStarted += OnSessionStarted;
        receiver.SessionEnded += OnSessionEnded;
    }

    private void OnSessionStarted(object? sender, DeviceSession session)
        => _sessions.TryAdd(session, new SessionPlayback(session, _receiver.Settings, _dispatcherQueue));

    private void OnSessionEnded(object? sender, DeviceSession session)
    {
        if (_sessions.TryRemove(session, out SessionPlayback? playback))
            _dispatcherQueue.TryEnqueue(playback.Dispose);
    }

    public void Dispose()
    {
        _receiver.SessionStarted -= OnSessionStarted;
        _receiver.SessionEnded -= OnSessionEnded;
        foreach (SessionPlayback playback in _sessions.Values)
            playback.Dispose();
        _sessions.Clear();
    }
}
