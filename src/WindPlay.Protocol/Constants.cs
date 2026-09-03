namespace AirPlay.Core2;

public static class Constants
{
    public const string AIPLAY_SERVICE_VERSION = "220.68";
    public const string AIRTUNES_SERVER_VERSION = "AirTunes/220.68";

    public const string DEVICE_MODEL = "AppleTV5,3";

    // Compatible mirroring/audio feature set. Do not advertise unimplemented HLS,
    // rotation, or multi-codec paths: senders rely on these bits during negotiation.
    public const string FEATURES = "0x5A7FFEE6,0x0";

    public const ulong FEATURES_VALUE = 0x5A7FFEE6;

    public const int MAX_FPS = 60;
}
