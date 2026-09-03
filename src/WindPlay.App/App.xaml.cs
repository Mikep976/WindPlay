using System.Diagnostics.CodeAnalysis;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WindPlay.App.Configuration;
using WindPlay.App.Playback;
using WindPlay.App.Security;
using WindPlay.App.Services;
using WindPlay.App.Windows;

namespace WindPlay.App;

[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "WinUI owns the Application lifetime; BeginShutdown deterministically disposes the receiver and playback coordinator.")]
public partial class App : Application
{
    private MainWindow? _mainWindow;
    private PlaybackCoordinator? _playbackCoordinator;
    private int _shutdownStarted;

    public App()
    {
        InitializeComponent();
        ReceiverSettings settings = SettingsStore.Load();
        ReceiverSecrets secrets = IdentityStore.LoadOrCreate();
        Receiver = new ReceiverHostManager(settings, secrets);
    }

    public ReceiverHostManager Receiver { get; }

    public static DispatcherQueue UiDispatcher { get; private set; } = null!;

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        UiDispatcher = DispatcherQueue.GetForCurrentThread();
        _playbackCoordinator = new PlaybackCoordinator(Receiver, UiDispatcher);
        _mainWindow = new MainWindow(Receiver);
        _mainWindow.Activate();
        await _mainWindow.StartOnLaunchAsync();
    }

    public async void BeginShutdown()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
            return;

        _playbackCoordinator?.Dispose();
        _playbackCoordinator = null;
        await Receiver.DisposeAsync();
        Exit();
    }
}
