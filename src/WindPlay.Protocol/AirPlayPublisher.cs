using AirPlay.Core2.Models.Configs;
using AirPlay.Core2.Security;
using Makaretu.Dns;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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

        // PCM, ALAC, AAC-LC, and AAC-ELD. Compressed formats use native Arm64 FFmpeg.
        airTunesProfile.AddProperty("cn", "0,1,2,3");
        airTunesProfile.AddProperty("da", "true"); // rfc2617DigestAuthKey
        airTunesProfile.AddProperty("et", "0,3,5"); // encryptionTypes: 0=none, 1=rsa (airport express), 3=fairplay, 4=MFiSAP, 5=fairplay SAPv2.5
        airTunesProfile.AddProperty("ft", Constants.FEATURES); // originally "0x5A7FFFF7,0x1E" https://openairplay.github.io/airplay-spec/features.html
        airTunesProfile.AddProperty("sf", airTunesConfig.Value.RequirePassword ? "0x84" : "0x4"); // systemFlags
        airTunesProfile.AddProperty("md", "0,1,2"); // metadataTypes 0=text, 1=artwork, 2=progress
        airTunesProfile.AddProperty("am", Constants.DEVICE_MODEL); // deviceModel
        airTunesProfile.AddProperty("pw", airTunesConfig.Value.RequirePassword ? "true" : "false");
        airTunesProfile.AddProperty("pk", identity.PublicKeyHex); // publicKey
        airTunesProfile.AddProperty("tp", "UDP"); // transportTypes
        airTunesProfile.AddProperty("vn", "65537");
        airTunesProfile.AddProperty("vs", Constants.AIPLAY_SERVICE_VERSION);
        airTunesProfile.AddProperty("ov", "11"); // 	vodkaVersion
        airTunesProfile.AddProperty("vv", "2"); // 	vodkaVersion

        //airTunesProfile.AddProperty("sr", "44100"); // sample rate
        //airTunesProfile.AddProperty("ss", "16"); // bitdepth
        //airTunesProfile.AddProperty("sv", "false"); // unk

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

        airPlayProfile.AddProperty("acl", "0"); // accessControlLevel
        airPlayProfile.AddProperty("deviceid", identity.DeviceId);
        airPlayProfile.AddProperty("features", Constants.FEATURES); // originally "0x5A7FFFF7,0x1E" https://openairplay.github.io/airplay-spec/features.html
        airPlayProfile.AddProperty("rsf", "0x0"); // requiredSenderFeatures
        airPlayProfile.AddProperty("flags", "0x4");
        airPlayProfile.AddProperty("pw", airTunesConfig.Value.RequirePassword ? "true" : "false");
        airPlayProfile.AddProperty("model", Constants.DEVICE_MODEL);
        airPlayProfile.AddProperty("protovers", "1.1");
        airPlayProfile.AddProperty("srcvers", Constants.AIPLAY_SERVICE_VERSION);
        airPlayProfile.AddProperty("pi", identity.PairingIdentifier.ToString("D"));
        airPlayProfile.AddProperty("gid", identity.GroupIdentifier.ToString("D"));
        airPlayProfile.AddProperty("gcgl", "0");
        //airPlayProfile.AddProperty("vv", "2");
        airPlayProfile.AddProperty("pk", identity.PublicKeyHex); // publicKey

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
