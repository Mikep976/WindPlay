using System.Reflection;
using AirPlay.Core2.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using WindPlay.App.Configuration;
using WindPlay.App.Services;

namespace WindPlay.App.Windows;

public sealed partial class MainWindow : Window
{
    private readonly ReceiverHostManager _receiver;
    private bool _updatingControls;
    private readonly List<DeviceSession> _sessions = [];
    private DeviceSession? _activeSession;

    public MainWindow(ReceiverHostManager receiver)
    {
        _receiver = receiver;
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        Title = "WindPlay";
        AppWindow.Resize(new global::Windows.Graphics.SizeInt32(1120, 780));
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "WindPlay.ico");
        if (File.Exists(iconPath))
            AppWindow.SetIcon(iconPath);

        LoadSettings(receiver.Settings);
        ReceiverNameText.Text = receiver.Settings.ReceiverName;
        PasscodeText.Text = receiver.Passcode;
        Version version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0);
        VersionText.Text = $"Version {version.Major}.{version.Minor}.{Math.Max(0, version.Build)} • ARM64";

        receiver.StateChanged += Receiver_StateChanged;
        receiver.SessionStarted += Receiver_SessionStarted;
        receiver.SessionEnded += Receiver_SessionEnded;
        Closed += MainWindow_Closed;
        UpdateState(receiver.State);
    }

    public async Task StartOnLaunchAsync()
    {
        if (_receiver.Settings.StartReceiverOnLaunch)
            await _receiver.StartAsync();
    }

    private void LoadSettings(ReceiverSettings settings)
    {
        ReceiverNameBox.Text = settings.ReceiverName;
        RequirePasscodeToggle.IsOn = settings.RequirePasscode;
        StartOnLaunchToggle.IsOn = settings.StartReceiverOnLaunch;
        FullScreenToggle.IsOn = settings.FullScreenOnConnect;
        KeepAwakeToggle.IsOn = settings.KeepDisplayAwake;
        PublicNetworksToggle.IsOn = settings.AllowNonPrivateNetworks;
        DiagnosticsToggle.IsOn = settings.DiagnosticsEnabled;
        PerformanceOverlayToggle.IsOn = settings.ShowPerformanceOverlay;
        MaximumConnectionsBox.Value = settings.MaximumConnections;
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        string destination = args.IsSettingsSelected
            ? "settings"
            : (args.SelectedItemContainer?.Tag as string ?? "home");

        HomeView.Visibility = destination == "home" ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = destination == "settings" ? Visibility.Visible : Visibility.Collapsed;
        PrivacyView.Visibility = destination == "privacy" ? Visibility.Visible : Visibility.Collapsed;
        AboutView.Visibility = destination == "about" ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ReceiverToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingControls)
            return;

        ReceiverToggle.IsEnabled = false;
        if (ReceiverToggle.IsOn)
            await _receiver.StartAsync();
        else
            await _receiver.StopAsync();
    }

    private void Receiver_StateChanged(object? sender, ReceiverStateChangedEventArgs e)
        => DispatcherQueue.TryEnqueue(() => UpdateState(e.State, e.ErrorMessage));

    private void UpdateState(ReceiverState state, string? errorMessage = null)
    {
        _updatingControls = true;
        try
        {
            ReceiverToggle.IsOn = state is ReceiverState.Ready or ReceiverState.Starting;
            ReceiverToggle.IsEnabled = state is ReceiverState.Ready or ReceiverState.Stopped or ReceiverState.Faulted;
        }
        finally
        {
            _updatingControls = false;
        }

        (StatusText.Text, StatusDot.Fill) = state switch
        {
            ReceiverState.Ready => ("Ready on your local network", new SolidColorBrush(Colors.LimeGreen)),
            ReceiverState.Starting => ("Starting receiver…", new SolidColorBrush(Colors.Goldenrod)),
            ReceiverState.Stopping => ("Stopping safely…", new SolidColorBrush(Colors.Goldenrod)),
            ReceiverState.Faulted => ("Needs attention", new SolidColorBrush(Colors.OrangeRed)),
            _ => ("Receiver is off", new SolidColorBrush(Colors.Gray)),
        };

        if (state == ReceiverState.Faulted)
        {
            ErrorBar.Message = errorMessage ?? "The receiver could not start.";
            ErrorBar.IsOpen = true;
        }
    }

    private void Receiver_SessionStarted(object? sender, DeviceSession session)
        => DispatcherQueue.TryEnqueue(() =>
        {
            if (_sessions.Contains(session))
                return;

            _sessions.Add(session);
            _activeSession = session;
            ShowActiveSession(session);
        });

    private void ShowActiveSession(DeviceSession session)
    {
        SessionTitleText.Text = session.DeviceDisplayName;
        SessionDetailText.Text = _sessions.Count > 1
            ? $"{session.DeviceModel ?? "Apple device"} • encrypted local stream • {_sessions.Count} active"
            : $"{session.DeviceModel ?? "Apple device"} • encrypted local stream";
        SessionIcon.Glyph = session.DeviceModel?.Contains("Mac", StringComparison.OrdinalIgnoreCase) == true ? "\uE770" : "\uE8EA";
        DisconnectSessionButton.Visibility = Visibility.Visible;
    }

    private void Receiver_SessionEnded(object? sender, DeviceSession session)
        => DispatcherQueue.TryEnqueue(() =>
        {
            _sessions.Remove(session);
            if (ReferenceEquals(_activeSession, session))
                _activeSession = _sessions.LastOrDefault();
            if (_activeSession is null)
            {
                SessionTitleText.Text = "Waiting for a device";
                SessionDetailText.Text = "Keep this app open, then connect from Control Center.";
                SessionIcon.Glyph = "\uE7F4";
                DisconnectSessionButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                ShowActiveSession(_activeSession);
            }
        });

    private void DisconnectSessionButton_Click(object sender, RoutedEventArgs e) => _activeSession?.Disconnect();

    private void CopyNameButton_Click(object sender, RoutedEventArgs e) => CopyText(_receiver.Settings.ReceiverName);

    private void CopyPasscodeButton_Click(object sender, RoutedEventArgs e) => CopyText(_receiver.Passcode);

    private static void CopyText(string value)
    {
        DataPackage package = new() { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(value);
        Clipboard.SetContent(package);
    }

    private async void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SaveSettingsButton.IsEnabled = false;
        try
        {
            ReceiverSettings settings = _receiver.Settings with
            {
                ReceiverName = ReceiverNameBox.Text,
                RequirePasscode = RequirePasscodeToggle.IsOn,
                StartReceiverOnLaunch = StartOnLaunchToggle.IsOn,
                FullScreenOnConnect = FullScreenToggle.IsOn,
                KeepDisplayAwake = KeepAwakeToggle.IsOn,
                AllowNonPrivateNetworks = PublicNetworksToggle.IsOn,
                DiagnosticsEnabled = DiagnosticsToggle.IsOn,
                ShowPerformanceOverlay = PerformanceOverlayToggle.IsOn,
                MaximumConnections = double.IsNaN(MaximumConnectionsBox.Value)
                    ? 4
                    : (int)MaximumConnectionsBox.Value,
            };
            await _receiver.ApplySettingsAsync(settings);
            LoadSettings(_receiver.Settings);
            ReceiverNameText.Text = _receiver.Settings.ReceiverName;
            ErrorBar.IsOpen = false;
        }
        catch (Exception exception)
        {
            ErrorBar.Title = "Settings were not saved";
            ErrorBar.Message = exception.Message;
            ErrorBar.IsOpen = true;
        }
        finally
        {
            SaveSettingsButton.IsEnabled = true;
        }
    }

    private async void OpenFirewallButton_Click(object sender, RoutedEventArgs e)
        => await Launcher.LaunchUriAsync(new Uri("ms-settings:windowsdefender-firewall"));

    private async void OpenLogsButton_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.LogDirectory);
        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(AppPaths.LogDirectory);
        await Launcher.LaunchFolderAsync(folder);
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _receiver.StateChanged -= Receiver_StateChanged;
        _receiver.SessionStarted -= Receiver_SessionStarted;
        _receiver.SessionEnded -= Receiver_SessionEnded;
        (Application.Current as App)?.BeginShutdown();
    }
}
