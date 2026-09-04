namespace AirPlay.Core2.Models.Messages.Rtsp;

/// <summary>A fully framed RTSP request. Parsing is performed by <see cref="RtspMessageReader"/>.</summary>
public sealed class RtspRequestMessage
{
    public WireProtocol Protocol { get; init; } = WireProtocol.Rtsp;

    public required RequestType Type { get; init; }

    public required string Path { get; init; }

    public required byte[] Body { get; init; }

    public required RtspHeadersCollection Headers { get; init; }

    public enum WireProtocol : ushort
    {
        Rtsp = 0,
        Http = 1,
    }

    public enum RequestType : ushort
    {
        GET = 0,
        POST = 1,
        SETUP = 2,
        GET_PARAMETER = 3,
        RECORD = 4,
        SET_PARAMETER = 5,
        ANNOUNCE = 6,
        FLUSH = 7,
        TEARDOWN = 8,
        OPTIONS = 9,
        PAUSE = 10,
    }

    internal static bool TryParseType(ReadOnlySpan<char> value, out RequestType type)
    {
        if (value.SequenceEqual("GET")) { type = RequestType.GET; return true; }
        if (value.SequenceEqual("POST")) { type = RequestType.POST; return true; }
        if (value.SequenceEqual("SETUP")) { type = RequestType.SETUP; return true; }
        if (value.SequenceEqual("GET_PARAMETER")) { type = RequestType.GET_PARAMETER; return true; }
        if (value.SequenceEqual("RECORD")) { type = RequestType.RECORD; return true; }
        if (value.SequenceEqual("SET_PARAMETER")) { type = RequestType.SET_PARAMETER; return true; }
        if (value.SequenceEqual("ANNOUNCE")) { type = RequestType.ANNOUNCE; return true; }
        if (value.SequenceEqual("FLUSH")) { type = RequestType.FLUSH; return true; }
        if (value.SequenceEqual("TEARDOWN")) { type = RequestType.TEARDOWN; return true; }
        if (value.SequenceEqual("OPTIONS")) { type = RequestType.OPTIONS; return true; }
        if (value.SequenceEqual("PAUSE")) { type = RequestType.PAUSE; return true; }

        type = default;
        return false;
    }
}
