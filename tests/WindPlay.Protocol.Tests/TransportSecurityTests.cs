using System.Net;
using System.Net.Sockets;
using System.Text;
using AirPlay.Core2.Models.Messages.Rtsp;
using AirPlay.Core2.Security;
using Xunit;

namespace WindPlay.Protocol.Tests;

public sealed class TransportSecurityTests
{
    [Theory]
    [InlineData("POST /pair-setup", "application/octet-stream", 1)]
    [InlineData("POST /pair-verify", "application/octet-stream", 67)]
    [InlineData("POST /pair-verify", "application/octet-stream", 69)]
    [InlineData("POST /fp-setup", "application/octet-stream", 165)]
    [InlineData("GET /info", "application/x-apple-binary-plist", 4097)]
    [InlineData("SETUP /stream", "application/x-apple-binary-plist", 65537)]
    [InlineData("SET_PARAMETER /stream", "application/x-dmap-tagged", 65537)]
    [InlineData("SET_PARAMETER /stream", "text/parameters", 4097)]
    [InlineData("SET_PARAMETER /stream", "image/png", 524289)]
    [InlineData("OPTIONS *", "application/octet-stream", 8388608)]
    public async Task RouteLimitRejectsAtHeadersWithoutReadingOrAllocatingBody(string route, string type, int length)
    {
        using var stream = new HeaderThenStallStream(Encoding.ASCII.GetBytes(
            $"{route} RTSP/1.0\r\nContent-Type: {type}\r\nContent-Length: {length}\r\n\r\n"), failOnBodyRead: true);
        using var reader = new RtspMessageReader();
        await Assert.ThrowsAsync<RtspProtocolException>(() => reader.ReadAsync(stream, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(1, stream.ReadCalls);
    }

    [Fact]
    public async Task BodyDeadlineAppliesAfterValidHeaders()
    {
        using var stream = new HeaderThenStallStream("POST /pair-verify RTSP/1.0\r\nContent-Length: 68\r\n\r\n"u8.ToArray());
        using var reader = new RtspMessageReader(bodyTimeout: TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(stream, TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task IdleDeadlineAppliesToEveryReadNotOnlyFirstHandshake()
    {
        using var stream = new HeaderThenStallStream("OPTIONS * RTSP/1.0\r\n\r\n"u8.ToArray());
        using var reader = new RtspMessageReader(headerTimeout: TimeSpan.FromMilliseconds(50));
        Assert.NotNull(await reader.ReadAsync(stream, TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(stream, TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task GlobalAdmissionRunsBeforeBodyRead()
    {
        using var stream = new HeaderThenStallStream("POST /pair-verify RTSP/1.0\r\nContent-Length: 68\r\n\r\n"u8.ToArray(), true);
        using var reader = new RtspMessageReader(admit: _ => false);
        await Assert.ThrowsAsync<RtspProtocolException>(() => reader.ReadAsync(stream, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(1, stream.ReadCalls);
    }

    [Fact]
    public void FloodBudgetCapsEachSourceAndGlobalWork()
    {
        var budget = new WorkBudget(4, 2, TimeSpan.FromMinutes(1));
        Assert.True(budget.TryCharge(IPAddress.Loopback, 2));
        Assert.False(budget.TryCharge(IPAddress.Loopback));
        Assert.True(budget.TryCharge(IPAddress.Parse("127.0.0.2"), 2));
        Assert.False(budget.TryCharge(IPAddress.Parse("127.0.0.3")));
    }

    [Theory]
    [InlineData("192.168.1.22", true)]
    [InlineData("192.168.2.22", false)]
    [InlineData("10.0.0.2", false)]
    [InlineData("::1", false)]
    [InlineData("::ffff:192.168.1.22", true)]
    public void PrivateRangeAloneDoesNotGrantLanAccess(string peer, bool expected)
        => Assert.Equal(expected, new LanScope(IPAddress.Parse("192.168.1.10"), 24).Contains(IPAddress.Parse(peer)));

    [Fact]
    public async Task WrongMirrorPeerDoesNotConsumeListener()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task<TcpClient> accept = MirrorLimits.AcceptExpectedAsync(listener, IPAddress.Loopback, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        using var wrong = new TcpClient(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
        await wrong.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
        using var right = new TcpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await right.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
        using var accepted = await accept;
        Assert.Equal(IPAddress.Loopback, ((IPEndPoint)accepted.Client.RemoteEndPoint!).Address);
    }

    [Fact]
    public async Task MirrorAcceptAndBodyReadsExpire()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MirrorLimits.AcceptExpectedAsync(listener,
            IPAddress.Loopback, TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken));
        using var stream = new HeaderThenStallStream([1]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MirrorLimits.ReadExactlyAsync(stream,
            new byte[128], TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(255, 1)]
    [InlineData(0, 2097153)]
    [InlineData(1, 65537)]
    [InlineData(0, 0)]
    public void MirrorTypeSpecificLimits(int type, int length)
        => Assert.Throws<InvalidDataException>(() => MirrorLimits.ValidatePayload(type, length));

    [Theory]
    [InlineData(16384, 16384)]
    [InlineData(4096, 4096)]
    [InlineData(0, 1080)]
    public void MirrorSurfacePixelBudget(int width, int height)
        => Assert.Throws<InvalidDataException>(() => MirrorLimits.ValidateDimensions(width, height));

    private sealed class HeaderThenStallStream(byte[] header, bool failOnBodyRead = false) : Stream
    {
        public int ReadCalls { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (++ReadCalls == 1) { header.CopyTo(buffer); return header.Length; }
            if (failOnBodyRead) throw new InvalidOperationException("Body was read before rejecting headers.");
            await Task.Delay(Timeout.Infinite, cancellationToken); return 0;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
