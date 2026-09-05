using AirPlay.Core2.Extensions;
using AirPlay.Core2.Models;
using AirPlay.Core2.Models.Configs;
using AirPlay.Core2.Security;
using AirPlay.Core2.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using WindPlay.App.Configuration;
using WindPlay.App.Security;

namespace WindPlay.App.Services;

public enum ReceiverState
{
    Stopped,
    Starting,
    Ready,
    Stopping,
    Faulted,
}

public sealed class ReceiverStateChangedEventArgs(ReceiverState state, string? errorMessage = null) : EventArgs
{
    public ReceiverState State { get; } = state;

    public string? ErrorMessage { get; } = errorMessage;
}

public sealed class ReceiverHostManager : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly ConcurrentDictionary<DeviceSession, byte> _activeSessions = [];
    private IHost? _host;
    private SessionManager? _sessionManager;

    public ReceiverHostManager(
        ReceiverSettings settings,
        ReceiverSecrets secrets)
    {
        Settings = settings;
        Identity = secrets.Identity;
        Passcode = secrets.Passcode;
    }

    public ReceiverSettings Settings { get; private set; }

    public ReceiverIdentity Identity { get; }

    public string Passcode { get; private set; }

    public async Task RotatePasswordAsync()
    {
        bool restart = State == ReceiverState.Ready;
        await StopAsync().ConfigureAwait(false);
        Passcode = IdentityStore.RotatePassword();
        if (restart) await StartAsync().ConfigureAwait(false);
    }

    public ReceiverState State { get; private set; } = ReceiverState.Stopped;

    public event EventHandler<ReceiverStateChangedEventArgs>? StateChanged;

    public event EventHandler<DeviceSession>? SessionStarted;

    public event EventHandler<DeviceSession>? SessionEnded;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_host is not null || State is ReceiverState.Starting or ReceiverState.Ready)
                return;

            SetState(ReceiverState.Starting);
            IHost host = BuildHost();
            SessionManager sessions = host.Services.GetRequiredService<SessionManager>();
            sessions.SessionCreated += OnSessionCreated;
            sessions.SessionClosed += OnSessionClosed;

            try
            {
                await host.StartAsync(cancellationToken).ConfigureAwait(false);
                _host = host;
                _sessionManager = sessions;
                SetState(ReceiverState.Ready);
            }
            catch
            {
                sessions.SessionCreated -= OnSessionCreated;
                sessions.SessionClosed -= OnSessionClosed;
                host.Dispose();
                throw;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetState(ReceiverState.Faulted, ToUserMessage(exception));
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IHost? host = _host;
            if (host is null)
            {
                EndRemainingSessions();
                SetState(ReceiverState.Stopped);
                return;
            }

            SetState(ReceiverState.Stopping);
            _host = null;
            SessionManager? sessions = _sessionManager;
            _sessionManager = null;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await host.StopAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Disposal below still closes all listeners.
            }
            finally
            {
                host.Dispose();
                if (sessions is not null)
                {
                    sessions.SessionCreated -= OnSessionCreated;
                    sessions.SessionClosed -= OnSessionClosed;
                }
                EndRemainingSessions();
            }

            SetState(ReceiverState.Stopped);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task ApplySettingsAsync(ReceiverSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        bool restart = State == ReceiverState.Ready;
        if (restart)
            await StopAsync(cancellationToken).ConfigureAwait(false);

        Settings = SettingsStore.Save(settings);

        if (restart)
            await StartAsync(cancellationToken).ConfigureAwait(false);
    }

    private IHost BuildHost()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(Identity);
        builder.Services.Configure<AirTunesConfig>(options =>
        {
            options.ServiceName = Settings.ReceiverName;
            options.RequirePassword = Settings.RequirePasscode;
            options.Password = Passcode;
            options.AllowNonPrivateNetworks = Settings.AllowNonPrivateNetworks;
            options.MaximumConcurrentConnections = Settings.MaximumConnections;
        });
        builder.Services.UseAirPlayService();

        if (Settings.DiagnosticsEnabled)
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);
            builder.Services.AddSerilog(configuration => configuration
                .MinimumLevel.Information()
                .Enrich.FromLogContext()
                .WriteTo.File(
                    Path.Combine(AppPaths.LogDirectory, "windplay-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    fileSizeLimitBytes: 5 * 1024 * 1024,
                    rollOnFileSizeLimit: true,
                    shared: false,
                    formatProvider: CultureInfo.InvariantCulture));
        }

        return builder.Build();
    }

    private void OnSessionCreated(object? sender, DeviceSession session)
    {
        if (_activeSessions.TryAdd(session, 0))
            SessionStarted?.Invoke(this, session);
    }

    private void OnSessionClosed(object? sender, DeviceSession session)
    {
        if (_activeSessions.TryRemove(session, out _))
            SessionEnded?.Invoke(this, session);
    }

    private void EndRemainingSessions()
    {
        foreach (DeviceSession session in _activeSessions.Keys)
        {
            if (_activeSessions.TryRemove(session, out _))
                SessionEnded?.Invoke(this, session);
        }
    }

    private void SetState(ReceiverState state, string? errorMessage = null)
    {
        State = state;
        StateChanged?.Invoke(this, new ReceiverStateChangedEventArgs(state, errorMessage));
    }

    private static string ToUserMessage(Exception exception) => exception switch
    {
        SocketException => "WindPlay could not open its local-network ports. Another receiver may already be running.",
        UnauthorizedAccessException => "Windows blocked local-network access. Check the firewall permission for WindPlay.",
        _ => "WindPlay could not start the receiver. Open diagnostics for technical details.",
    };

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        Identity.Dispose();
        _lifecycleGate.Dispose();
    }
}
