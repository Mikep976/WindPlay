namespace AirPlay.Core2.Models.Configs;

public class AirTunesConfig
{
    public string ServiceName { get; set; } = "WindPlay";

    public bool RequirePassword { get; set; } = true;

    public string Password { get; set; } = "0000";

    public bool AllowNonPrivateNetworks { get; set; }

    public int MaximumConcurrentConnections { get; set; } = 8;

    public ushort Port { get; set; } = 5000;
}
