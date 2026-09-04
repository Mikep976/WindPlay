using AirPlay.Core2.Models.Configs;
using AirPlay.Core2.Security;
using Makaretu.Dns;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;

namespace AirPlay.Core2;

public partial class AirPlayPublisher(MulticastService multicastService, ILogger<AirPlayPublisher> logger,
    IOptions<AirTunesConfig> airTunesConfig, ReceiverIdentity identity) : IHostedService
{
    private readonly ServiceDiscovery _serviceDiscovery = new(multicastService);

    Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        #region AirTunes Service

        ServiceProfile airTunesProfile = new
        (
            $"{identity.DeviceIdCompact}@{airTunesConfig.Value.ServiceName}",
            AirTunesType,
            airTunesConfig.Value.Port
        );

        foreach ((string key, string value) in GetAirTunesTxtProperties(airTunesConfig.Value, identity))
            airTunesProfile.AddProperty(key, value);

        _serviceDiscovery.Advertise(airTunesProfile);
        logger.AirTunesPublished(airTunesConfig.Value.Port);

        #endregion

        #region AirPlay Service

        ServiceProfile airPlayProfile = new
        (
            airTunesConfig.Value.ServiceName,
            AirPlayType,
            airTunesConfig.Value.Port
        );

        foreach ((string key, string value) in GetAirPlayTxtProperties(airTunesConfig.Value, identity))
            airPlayProfile.AddProperty(key, value);

        _serviceDiscovery.Advertise(airPlayProfile);
        logger.AirPlayPublished(airTunesConfig.Value.Port);

        #endregion

        multicastService.Start();

        return Task.CompletedTask;
    }

    Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        _serviceDiscovery.Dispose();
        multicastService.Stop();

        return Task.CompletedTask;
    }

    internal static IReadOnlyList<KeyValuePair<string, string>> GetAirTunesTxtProperties(
        AirTunesConfig config,
        ReceiverIdentity identity)
        =>
        [
            new("ch", "2"),
            new("cn", "0,1,2,3"),
            new("da", "true"),
            new("et", "0,3,5"),
            new("vv", "2"),
            new("ft", Constants.FEATURES),
            new("am", Constants.DEVICE_MODEL),
            new("md", "0,1,2"),
            new("rhd", "5.6.0.0"),
            new("pw", config.RequirePassword ? "true" : "false"),
            new("sr", "44100"),
            new("ss", "16"),
            new("sv", "false"),
            new("tp", "UDP"),
            new("txtvers", "1"),
            new("sf", config.RequirePassword ? "0x84" : "0x4"),
            new("vs", Constants.AIPLAY_SERVICE_VERSION),
            new("vn", "65537"),
            new("ov", "11"),
            new("pk", identity.PublicKeyHex),
        ];

    internal static IReadOnlyList<KeyValuePair<string, string>> GetAirPlayTxtProperties(
        AirTunesConfig config,
        ReceiverIdentity identity)
        =>
        [
            new("acl", "0"),
            new("deviceid", identity.DeviceId),
            new("features", Constants.FEATURES),
            new("rsf", "0x0"),
            new("flags", "0x4"),
            new("pw", config.RequirePassword ? "true" : "false"),
            new("model", Constants.DEVICE_MODEL),
            new("protovers", "1.1"),
            new("srcvers", Constants.AIPLAY_SERVICE_VERSION),
            new("pi", identity.PairingIdentifier.ToString("D")),
            new("gid", identity.GroupIdentifier.ToString("D")),
            new("gcgl", "0"),
            new("vv", "2"),
            new("pk", identity.PublicKeyHex),
        ];

    internal static byte[] PackTxtRecord(IEnumerable<KeyValuePair<string, string>> properties)
    {
        using MemoryStream output = new();
        foreach ((string key, string value) in properties)
        {
            byte[] item = Encoding.UTF8.GetBytes($"{key}={value}");
            if (item.Length > byte.MaxValue)
                throw new InvalidOperationException("An AirPlay TXT property exceeds the DNS-SD record limit.");
            output.WriteByte((byte)item.Length);
            output.Write(item);
        }
        return output.ToArray();
    }
}

partial class AirPlayPublisher
{
    public const string AirPlayType = "_airplay._tcp";
    public const string AirTunesType = "_raop._tcp";

}

internal static partial class AirPlayPublisherLoggers
{
    [LoggerMessage(LogLevel.Information, "AirTunes Service [{port}] Published on mDns")]
    public static partial void AirTunesPublished(this ILogger logger, ushort port);

    [LoggerMessage(LogLevel.Information, "AirPlay Service [{port}] Published on mDns")]
    public static partial void AirPlayPublished(this ILogger logger, ushort port);
}
