using System.Buffers;
using System.Globalization;
using System.Text;
using AirPlay.Core2.Security;

namespace AirPlay.Core2.Models.Messages.Rtsp;

/// <summary>
/// Incrementally frames RTSP messages without converting arbitrary network data to hex or
/// waiting on <c>NetworkStream.DataAvailable</c>. Limits prevent a LAN peer exhausting memory.
/// </summary>
public sealed class RtspMessageReader : IDisposable
{
    public const int DefaultMaximumHeaderBytes = 8 * 1024;
    public const int DefaultMaximumBodyBytes = 512 * 1024;

    private readonly int _maximumHeaderBytes;
    private readonly int _maximumBodyBytes;
    private byte[] _buffer;
    private int _start;
    private int _end;
    private bool _disposed;
    private readonly Func<int, bool>? _admit;
    private readonly TimeSpan _headerTimeout, _bodyTimeout, _progressTimeout;

    public RtspMessageReader(
        int maximumHeaderBytes = DefaultMaximumHeaderBytes,
        int maximumBodyBytes = DefaultMaximumBodyBytes,
        Func<int, bool>? admit = null,
        TimeSpan? headerTimeout = null, TimeSpan? bodyTimeout = null, TimeSpan? progressTimeout = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumHeaderBytes, 256);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBodyBytes, 0);

        _maximumHeaderBytes = maximumHeaderBytes;
        _maximumBodyBytes = maximumBodyBytes;
        _buffer = ArrayPool<byte>.Shared.Rent(maximumHeaderBytes);
        _admit = admit;
        _headerTimeout = headerTimeout ?? TimeSpan.FromSeconds(60);
        _bodyTimeout = bodyTimeout ?? TimeSpan.FromSeconds(15);
        _progressTimeout = progressTimeout ?? TimeSpan.FromSeconds(5);
    }

    public async ValueTask<RtspRequestMessage?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(stream);
        using var headerDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headerDeadline.CancelAfter(_headerTimeout);
        int headerEnd;
        while ((headerEnd = FindHeaderTerminator(_buffer.AsSpan(_start, _end - _start))) < 0)
        {
            if (_end - _start >= _maximumHeaderBytes)
                throw new RtspProtocolException("RTSP headers exceed their limit.");
            if (_start > 0)
            {
                _buffer.AsSpan(_start, _end - _start).CopyTo(_buffer);
                _end -= _start; _start = 0;
            }
            int read = await ReadWithProgressAsync(stream,
                _buffer.AsMemory(_end, _maximumHeaderBytes - _end),
                _end == _start ? _headerTimeout : _progressTimeout, headerDeadline.Token);
            if (read == 0)
            {
                if (_end == _start) return null;
                throw new RtspProtocolException("The peer closed the stream during an RTSP header.");
            }
            _end += read;
        }
        headerEnd += _start;
        int headerLength = headerEnd - _start;
        var (protocol, requestType, path, headers, contentLength) = ParseHeader(_buffer.AsSpan(_start, headerLength));
        if (headerLength + 4 > _maximumHeaderBytes || contentLength > _maximumBodyBytes)
            throw new RtspProtocolException("RTSP message exceeds configured limits.");
        TransportLimits.ValidateBody(protocol, requestType, path, headers, contentLength);
        if (_admit is not null && !_admit(checked(headerLength + 4 + contentLength)))
            throw new RtspProtocolException("RTSP peer exceeded its request/byte budget.");

        // Allocate once, only after route admission. The header buffer never grows to body size.
        byte[] body = contentLength == 0 ? [] : new byte[contentLength];
        int buffered = Math.Min(contentLength, _end - headerEnd - 4);
        _buffer.AsSpan(headerEnd + 4, buffered).CopyTo(body);
        _start = headerEnd + 4 + buffered;
        if (_start == _end) _start = _end = 0;
        using var bodyDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bodyDeadline.CancelAfter(_bodyTimeout);
        for (int offset = buffered; offset < contentLength;)
        {
            int read = await ReadWithProgressAsync(stream, body.AsMemory(offset), _progressTimeout, bodyDeadline.Token);
            if (read == 0) throw new RtspProtocolException("The peer closed the stream during an RTSP body.");
            offset += read;
        }
        return new RtspRequestMessage { Protocol = protocol, Type = requestType, Path = path, Headers = headers, Body = body };
    }

    private static async ValueTask<int> ReadWithProgressAsync(Stream stream, Memory<byte> buffer, TimeSpan timeout, CancellationToken token)
    {
        using var progress = CancellationTokenSource.CreateLinkedTokenSource(token);
        progress.CancelAfter(timeout);
        return await stream.ReadAsync(buffer, progress.Token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
        _buffer = [];
    }

    internal static int FindHeaderTerminator(ReadOnlySpan<byte> data)
    {
        for (int i = 0; i <= data.Length - 4; i++)
        {
            if (data[i] == (byte)'\r' && data[i + 1] == (byte)'\n' &&
                data[i + 2] == (byte)'\r' && data[i + 3] == (byte)'\n')
                return i;
        }

        return -1;
    }

    private static (RtspRequestMessage.WireProtocol Protocol, RtspRequestMessage.RequestType Type, string Path, RtspHeadersCollection Headers, int ContentLength)
        ParseHeader(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            if (value is not (>= 0x20 and <= 0x7e) and not (byte)'\r' and not (byte)'\n' and not (byte)'\t')
                throw new RtspProtocolException("RTSP headers must contain ASCII text only.");
        }

        string text = Encoding.ASCII.GetString(bytes);
        string[] lines = text.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0)
            throw new RtspProtocolException("RTSP request line is missing.");

        string[] requestLine = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length != 3)
            throw new RtspProtocolException("Invalid RTSP request line.");
        RtspRequestMessage.WireProtocol protocol = requestLine[2] switch
        {
            "RTSP/1.0" => RtspRequestMessage.WireProtocol.Rtsp,
            "HTTP/1.1" => RtspRequestMessage.WireProtocol.Http,
            _ => throw new RtspProtocolException("Unsupported request protocol."),
        };
        if (!RtspRequestMessage.TryParseType(requestLine[0], out var type))
            throw new RtspProtocolException("Unsupported RTSP method.");
        if (requestLine[1].Length is 0 or > 2048 || ContainsControlCharacter(requestLine[1]))
            throw new RtspProtocolException("Invalid RTSP request target.");

        RtspHeadersCollection headers = [];
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length == 0)
                continue;
            if (line[0] is ' ' or '\t')
                throw new RtspProtocolException("Folded RTSP headers are not accepted.");

            int separator = line.IndexOf(':');
            if (separator <= 0)
                throw new RtspProtocolException("Malformed RTSP header.");

            string name = line[..separator];
            string value = line[(separator + 1)..].Trim();
            if (!IsHeaderName(name) || ContainsControlCharacter(value))
                throw new RtspProtocolException("Malformed RTSP header.");

            headers.AddOrAppend(name, value);
            if (headers.Count > 100)
                throw new RtspProtocolException("Too many RTSP headers.");
        }

        int contentLength = 0;
        if (headers.TryGetValue("Content-Length", out RtspHeader? contentLengthHeader))
        {
            if (contentLengthHeader.Values.Length != 1 ||
                !int.TryParse(contentLengthHeader.Values[0], NumberStyles.None, CultureInfo.InvariantCulture, out contentLength) ||
                contentLength < 0)
                throw new RtspProtocolException("Invalid RTSP Content-Length.");
        }

        if (headers.TryGetValue("CSeq", out RtspHeader? sequenceHeader) &&
            (sequenceHeader.Values.Length != 1 ||
             !uint.TryParse(sequenceHeader.Values[0], NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            throw new RtspProtocolException("Invalid RTSP CSeq.");

        return (protocol, type, requestLine[1], headers, contentLength);
    }

    private static bool ContainsControlCharacter(string value)
        => value.Any(character => character is < ' ' and not '\t' or '\u007f');

    private static bool IsHeaderName(string value)
        => value.Length is > 0 and <= 128 && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
