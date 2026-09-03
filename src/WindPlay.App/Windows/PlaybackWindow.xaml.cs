using AirPlay.Core2.Models;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.System.Display;
using WindPlay.App.Configuration;

namespace WindPlay.App.Windows;

public sealed partial class PlaybackWindow : Window
{
    private readonly DeviceSession _session;
    private readonly ReceiverSettings _settings;
    private readonly DispatcherTimer _chromeTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DisplayRequest? _displayRequest;
    private MediaPlayer? _mediaPlayer;
    private bool _fullScreen;
    private bool _sessionEnding;

    public PlaybackWindow(DeviceSession session, ReceiverSettings settings, int width, int height)
    {
        _session = session;
        _settings = settings;
        InitializeComponent();

        Title = $"{session.DeviceDisplayName} — WindPlay";
        DeviceNameText.Text = session.DeviceDisplayName;
        StreamDetailsText.Text = $"{width} × {height}  •  H.264 hardware decode";
        PerformancePill.Visibility = settings.ShowPerformanceOverlay ? Visibility.Visible : Visibility.Collapsed;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TopBar);
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = Microsoft.UI.Colors.White;
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "WindPlay.ico");
        if (File.Exists(iconPath))
            AppWindow.SetIcon(iconPath);

        ResizeForVideo(width, height);
        _chromeTimer.Tick += ChromeTimer_Tick;
        _chromeTimer.Start();
        Closed += PlaybackWindow_Closed;

        if (settings.KeepDisplayAwake)
        {
            _displayRequest = new DisplayRequest();
            _displayRequest.RequestActive();
        }

        if (settings.FullScreenOnConnect)
            SetFullScreen(true);

        Root.KeyboardAccelerators.Add(CreateAccelerator(global::Windows.System.VirtualKey.F11, ToggleFullScreen));
        Root.KeyboardAccelerators.Add(CreateAccelerator(global::Windows.System.VirtualKey.Escape, ExitFullScreen));
    }

    public void AttachVideo(MediaSource source)
    {
        _mediaPlayer ??= new MediaPlayer
        {
            AutoPlay = true,
            RealTimePlayback = true,
            IsLoopingEnabled = false,
        };
        _mediaPlayer.CommandManager.IsEnabled = false;
        _mediaPlayer.Source = source;
        PlayerElement.SetMediaPlayer(_mediaPlayer);
        _mediaPlayer.Play();
    }

    public void UpdatePerformance(long received, long dropped)
        => PerformanceText.Text = $"Frames {received:n0}  •  Dropped {dropped:n0}";

    public void EndSession()
    {
        _sessionEnding = true;
        Close();
    }

    private void ResizeForVideo(int width, int height)
    {
        DisplayArea display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        double maximumWidth = display.WorkArea.Width * 0.78;
        double maximumHeight = display.WorkArea.Height * 0.78;
        double scale = Math.Min(Math.Min(maximumWidth / width, maximumHeight / height), 1.0);
        int windowWidth = Math.Max(640, (int)(width * scale));
        int windowHeight = Math.Max(400, (int)(height * scale) + 80);
        AppWindow.Resize(new SizeInt32(windowWidth, windowHeight));
    }

    private static KeyboardAccelerator CreateAccelerator(global::Windows.System.VirtualKey key, Action action)
    {
        KeyboardAccelerator accelerator = new() { Key = key };
        accelerator.Invoked += (_, args) =>
        {
            action();
            args.Handled = true;
        };
        return accelerator;
    }

    private void Root_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => ToggleFullScreen();

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        TopBar.Opacity = 1;
        _chromeTimer.Stop();
        _chromeTimer.Start();
    }

    private void ChromeTimer_Tick(object? sender, object e)
    {
        _chromeTimer.Stop();
        TopBar.Opacity = 0;
    }

    private void FullScreenButton_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void DisconnectButton_Click(object sender, RoutedEventArgs e) => _session.Disconnect();

    private void ToggleFullScreen() => SetFullScreen(!_fullScreen);

    private void ExitFullScreen()
    {
        if (_fullScreen)
            SetFullScreen(false);
    }

    private void SetFullScreen(bool value)
    {
        _fullScreen = value;
        AppWindow.SetPresenter(value ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
        FullScreenButton.Content = new FontIcon { Glyph = value ? "\uE73F" : "\uE740", Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White) };
    }

    private void PlaybackWindow_Closed(object sender, WindowEventArgs args)
    {
        _chromeTimer.Stop();
        _displayRequest?.RequestRelease();
        PlayerElement.SetMediaPlayer(null);
        _mediaPlayer?.Dispose();
        _mediaPlayer = null;

        if (!_sessionEnding)
            _session.Disconnect();
    }
}
