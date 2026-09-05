using AirPlay.Core2.Models.Messages.Rtsp;
using static AirPlay.Core2.Models.Messages.Rtsp.RtspRequestMessage;

namespace AirPlay.Core2.Security;

internal static class TransportLimits
{
    internal static readonly WorkBudget Bytes = new(4 * 1024 * 1024, 1024 * 1024, TimeSpan.FromSeconds(1));
    internal static readonly WorkBudget Requests = new(128, 32, TimeSpan.FromSeconds(1));

    public static void ValidateBody(WireProtocol protocol, RequestType method, string path, RtspHeadersCollection headers, int length)
    {
        string media = "";
        if (headers.ContainsKey("Content-Type"))
        {
            if (!headers.TryGetSingleValue("Content-Type", out var value)) throw Invalid();
            media = value.Split(';')[0].Trim().ToLowerInvariant();
        }
        int maximum = 0;
        if (protocol == WireProtocol.Rtsp)
        {
            if (method == RequestType.POST && path.Equals("/pair-verify", StringComparison.OrdinalIgnoreCase))
            { if (length != 68) throw Invalid(); maximum = 68; }
            else if (method == RequestType.POST && path.Equals("/fp-setup", StringComparison.OrdinalIgnoreCase))
            { if (length is not (16 or 164)) throw Invalid(); maximum = 164; }
            else if (method == RequestType.GET && (path.Equals("/info", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/info?", StringComparison.OrdinalIgnoreCase)))
                maximum = media == "application/x-apple-binary-plist" ? 4096 : 0;
            else if (method is RequestType.SETUP or RequestType.TEARDOWN)
                maximum = 64 * 1024;
            else if (method == RequestType.GET_PARAMETER) maximum = 4096;
            else if (method == RequestType.SET_PARAMETER)
                maximum = media switch { "text/parameters" => 4096, "application/x-dmap-tagged" => 64 * 1024,
                    "image/jpeg" or "image/png" => 512 * 1024, _ => 0 };
            else if (method == RequestType.POST && path.Equals("/feedback", StringComparison.OrdinalIgnoreCase)) maximum = 4096;
        }
        if (length < 0 || length > maximum) throw Invalid();
    }

    private static RtspProtocolException Invalid() => new("RTSP body violates the route-specific length or content-type policy.");
}
