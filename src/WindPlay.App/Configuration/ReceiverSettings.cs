namespace WindPlay.App.Configuration;

public sealed record ReceiverSettings
{
    public int Version { get; init; } = 1;

    public string ReceiverName { get; init; } = "WindPlay";

    public bool RequirePasscode { get; init; } = true;

    public bool StartReceiverOnLaunch { get; init; } = true;

    public bool FullScreenOnConnect { get; init; }

    public bool KeepDisplayAwake { get; init; } = true;

    public bool AllowNonPrivateNetworks { get; init; }

    public bool DiagnosticsEnabled { get; init; }

    public bool ShowPerformanceOverlay { get; init; }

    public int MaximumConnections { get; init; } = 4;
}
