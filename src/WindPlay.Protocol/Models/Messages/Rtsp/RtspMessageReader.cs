using System.Buffers;
using System.Globalization;
using System.Text;

namespace AirPlay.Core2.Models.Messages.Rtsp;

/// <summary>
/// Incrementally frames RTSP messages without converting arbitrary network data to hex or
/// waiting on <c>NetworkStream.DataAvailable</c>. Limits prevent a LAN peer exhausting memory.
/// </summary>
public sealed class RtspMessageReader : IDisposable
{
    public const int DefaultMaximumHeaderBytes = 32 * 1024;
    public const int DefaultMaximumBodyBytes = 8 * 1024 * 1024;

    private readonly int _maximumHeaderBytes;
    private readonly int _maximumBodyBytes;
    private byte[] _buffer;
    private int _start;
    private int _end;
    private bool _disposed;

    public RtspMessageReader(
        int maximumHeaderBytes = DefaultMaximumHeaderBytes,
        int maximumBodyBytes = DefaultMaximumBodyBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumHeaderBytes, 256);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBodyBytes, 0);

        _maximumHeaderBytes = maximumHeaderBytes;
        _maximumBodyBytes = maximumBodyBytes;
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Min(16 * 1024, maximumHeaderBytes));
    }

    public async ValueTask<RtspRequestMessage?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(stream);

        int headerEnd;
        while ((headerEnd = FindHeaderTerminator(_buffer.AsSpan(_start, _end - _start))) < 0)
        {
            if (_end - _start >= _maximumHeaderBytes)
                throw new RtspProtocolException($"RTSP headers exceed {_maximumHeaderBytes.ToString(CultureInfo.InvariantCulture)} bytes.");

            CompactOrGrow(_maximumHeaderBytes);
            int read = await stream.ReadAsync(_buffer.AsMemory(_end, _buffer.Length - _end), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (_end == _start)
                    return null;

                throw new RtspProtocolException("The peer closed the stream during an RTSP header.");
            }

            _end += read;
        }

        headerEnd += _start;
        int headerLength = headerEnd - _start;
        var (requestType, path, headers, contentLength) = ParseHeader(_buffer.AsSpan(_start, headerLength));

        if (contentLength > _maximumBodyBytes)
            throw new RtspProtocolException($"RTSP body exceeds {_maximumBodyBytes.ToString(CultureInfo.InvariantCulture)} bytes.");

        int messageLength = checked(headerLength + 4 + contentLength);
        while (_end - _start < messageLength)
        {
            CompactOrGrow(messageLength);
            int read = await stream.ReadAsync(_buffer.AsMemory(_end, _buffer.Length - _end), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new RtspProtocolException("The peer closed the stream during an RTSP body.");
            _end += read;
        }

        byte[] body = contentLength == 0
            ? []
            : _buffer.AsSpan(headerEnd + 4, contentLength).ToArray();

        _start += messageLength;
        if (_start == _end)
            _start = _end = 0;

        return new RtspRequestMessage
        {
            Type = requestType,
            Path = path,
            Headers = headers,
            Body = body,
        };
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

    private static (RtspRequestMessage.RequestType Type, string Path, RtspHeadersCollection Headers, int ContentLength)
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
        if (requestLine.Length != 3 || !string.Equals(requestLine[2], "RTSP/1.0", StringComparison.Ordinal))
            throw new RtspProtocolException("Invalid RTSP request line.");
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

        return (type, requestLine[1], headers, contentLength);
    }

    private void CompactOrGrow(int requiredCapacity)
    {
        int used = _end - _start;
        if (_start > 0 && (_buffer.Length - used >= 4096 || _buffer.Length >= requiredCapacity))
        {
            _buffer.AsSpan(_start, used).CopyTo(_buffer);
            _start = 0;
            _end = used;
            return;
        }

        if (_buffer.Length >= requiredCapacity && _end < _buffer.Length)
            return;

        int maximum = checked(_maximumHeaderBytes + _maximumBodyBytes + 4);
        int nextLength = Math.Min(Math.Max(Math.Max(_buffer.Length * 2, used + 4096), requiredCapacity), maximum);
        if (nextLength < requiredCapacity)
            throw new RtspProtocolException("RTSP message exceeds configured limits.");

        byte[] replacement = ArrayPool<byte>.Shared.Rent(nextLength);
        _buffer.AsSpan(_start, used).CopyTo(replacement);
        ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
        _buffer = replacement;
        _start = 0;
        _end = used;
    }

    private static bool ContainsControlCharacter(string value)
        => value.Any(character => character is < ' ' and not '\t' or '\u007f');

    private static bool IsHeaderName(string value)
        => value.Length is > 0 and <= 128 && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
