using System.Text;
using AirPlay.Core2.Models.Messages.Rtsp;
using Xunit;

namespace WindPlay.Protocol.Tests;

public sealed class RtspMessageReaderTests
{
    [Fact]
    public async Task FragmentedRequestPreservesHeadersAndBinaryBody()
    {
        byte[] body = [0x00, 0x0d, 0x0a, 0xff, 0x42];
        byte[] request = BuildRequest(
            "POST /pair-verify RTSP/1.0\r\nCSeq: 9\r\nContent-Type: application/octet-stream\r\n",
            body);
        await using var stream = new FragmentingStream(request, maximumReadSize: 1);
        using var reader = new RtspMessageReader();

        RtspRequestMessage? message = await reader.ReadAsync(stream, TestContext.Current.CancellationToken);

        Assert.NotNull(message);
        Assert.Equal(RtspRequestMessage.WireProtocol.Rtsp, message.Protocol);
        Assert.Equal(RtspRequestMessage.RequestType.POST, message.Type);
        Assert.Equal("/pair-verify", message.Path);
        Assert.Equal("9", Assert.Single(message.Headers["CSeq"]));
        Assert.Equal(body, message.Body);
    }

    [Fact]
    public async Task HttpProbeCanShareTheAirPlayListener()
    {
        byte[] request = Encoding.ASCII.GetBytes("GET /server-info HTTP/1.1\r\nHost: receiver.local\r\n\r\n");
        await using var stream = new MemoryStream(request);
        using var reader = new RtspMessageReader();

        RtspRequestMessage? message = await reader.ReadAsync(stream, TestContext.Current.CancellationToken);

        Assert.NotNull(message);
        Assert.Equal(RtspRequestMessage.WireProtocol.Http, message.Protocol);
        Assert.Equal(RtspRequestMessage.RequestType.GET, message.Type);
        Assert.Equal("/server-info", message.Path);
    }

    [Fact]
    public async Task PipelinedRequestsReturnExactlyOneAtATime()
    {
        byte[] first = BuildRequest("GET /info RTSP/1.0\r\nCSeq: 1\r\n", []);
        byte[] second = BuildRequest("POST /feedback RTSP/1.0\r\nCSeq: 2\r\n", []);
        await using var stream = new MemoryStream([.. first, .. second]);
        using var reader = new RtspMessageReader();

        RtspRequestMessage? firstMessage = await reader.ReadAsync(stream, TestContext.Current.CancellationToken);
        RtspRequestMessage? secondMessage = await reader.ReadAsync(stream, TestContext.Current.CancellationToken);
        RtspRequestMessage? end = await reader.ReadAsync(stream, TestContext.Current.CancellationToken);

        Assert.Equal("/info", firstMessage?.Path);
        Assert.Equal("/feedback", secondMessage?.Path);
        Assert.Null(end);
    }

    [Theory]
    [InlineData("Content-Length: nope\r\n")]
    [InlineData("Content-Length: -1\r\n")]
    [InlineData("Content-Length: 0\r\nContent-Length: 0\r\n")]
    public async Task InvalidContentLengthRejectsRequest(string header)
    {
        byte[] request = Encoding.ASCII.GetBytes($"POST /pair RTSP/1.0\r\n{header}\r\n");
        await using var stream = new MemoryStream(request);
        using var reader = new RtspMessageReader();

        await Assert.ThrowsAsync<RtspProtocolException>(
            () => reader.ReadAsync(stream, TestContext.Current.CancellationToken).AsTask());
    }

    [Theory]
    [InlineData("CSeq: -1\r\n")]
    [InlineData("CSeq: nope\r\n")]
    [InlineData("CSeq: 1\r\nCSeq: 2\r\n")]
    public async Task InvalidSequenceRejectsRequest(string header)
    {
        byte[] request = Encoding.ASCII.GetBytes($"GET /info RTSP/1.0\r\n{header}\r\n");
        await using var stream = new MemoryStream(request);
        using var reader = new RtspMessageReader();

        await Assert.ThrowsAsync<RtspProtocolException>(
            () => reader.ReadAsync(stream, TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task BodyOverConfiguredLimitIsRejectedBeforeReadingBody()
    {
        byte[] request = Encoding.ASCII.GetBytes("POST /pair RTSP/1.0\r\nContent-Length: 17\r\n\r\n");
        await using var stream = new MemoryStream(request);
        using var reader = new RtspMessageReader(maximumHeaderBytes: 512, maximumBodyBytes: 16);

        await Assert.ThrowsAsync<RtspProtocolException>(
            () => reader.ReadAsync(stream, TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task TruncatedHeaderIsRejected()
    {
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes("GET /info RTSP/1.0\r\nCSeq: 1"));
        using var reader = new RtspMessageReader();

        RtspProtocolException exception = await Assert.ThrowsAsync<RtspProtocolException>(
            () => reader.ReadAsync(stream, TestContext.Current.CancellationToken).AsTask());
        Assert.Contains("header", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NonAsciiHeaderIsRejected()
    {
        byte[] request = Encoding.ASCII.GetBytes("GET /info RTSP/1.0\r\nX-Name: ok\r\n\r\n");
        request[^5] = 0xff;
        await using var stream = new MemoryStream(request);
        using var reader = new RtspMessageReader();

        await Assert.ThrowsAsync<RtspProtocolException>(
            () => reader.ReadAsync(stream, TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task TooManyHeadersAreRejected()
    {
        StringBuilder request = new("GET /info RTSP/1.0\r\n");
        for (int index = 0; index < 101; index++)
            request.Append("X-").Append(index).Append(": value\r\n");
        request.Append("\r\n");
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes(request.ToString()));
        using var reader = new RtspMessageReader();

        await Assert.ThrowsAsync<RtspProtocolException>(
            () => reader.ReadAsync(stream, TestContext.Current.CancellationToken).AsTask());
    }

    private static byte[] BuildRequest(string headerWithoutLength, byte[] body)
    {
        byte[] header = Encoding.ASCII.GetBytes(
            $"{headerWithoutLength}Content-Length: {body.Length}\r\n\r\n");
        return [.. header, .. body];
    }

    private sealed class FragmentingStream(byte[] contents, int maximumReadSize) : MemoryStream(contents)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, maximumReadSize)], cancellationToken);
    }
}
