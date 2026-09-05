using System.Net;
using System.Net.Sockets;

namespace AirPlay.Core2.Security;

public static class MirrorLimits
{
    public const int MaximumFrameBytes = 2 * 1024 * 1024;
    public const int MaximumConfigBytes = 64 * 1024;
    public const int MaximumPixels = 3840 * 2160;

    public static void ValidateDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > 4096 || height > 4096 || (long)width * height > MaximumPixels)
            throw new InvalidDataException("Mirroring dimensions exceed the 4K pixel budget.");
    }

    internal static void ValidatePayload(int type, int length)
    {
        int maximum = type switch { 0 => MaximumFrameBytes, 1 => MaximumConfigBytes, _ => 0 };
        if (maximum == 0 || length <= 0 || length > maximum)
            throw new InvalidDataException("Unsupported mirroring payload type or length.");
    }

    internal static async Task<TcpClient> AcceptExpectedAsync(TcpListener listener, IPAddress expected,
        TimeSpan timeout, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        var wrongPeers = new WorkBudget(16, 4, TimeSpan.FromSeconds(1));
        while (true)
        {
            TcpClient candidate = await listener.AcceptTcpClientAsync(deadline.Token).ConfigureAwait(false);
            if (candidate.Client.RemoteEndPoint is IPEndPoint remote && remote.Address.Equals(expected)) return candidate;
            IPAddress source = (candidate.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
            candidate.Dispose();
            if (!wrongPeers.TryCharge(source)) await Task.Delay(100, deadline.Token).ConfigureAwait(false);
        }
    }

    internal static async Task ReadExactlyAsync(Stream stream, Memory<byte> destination,
        TimeSpan totalTimeout, TimeSpan progressTimeout, CancellationToken token)
    {
        using var total = CancellationTokenSource.CreateLinkedTokenSource(token);
        total.CancelAfter(totalTimeout);
        int offset = 0;
        while (offset < destination.Length)
        {
            using var progress = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
            progress.CancelAfter(offset == 0 ? totalTimeout : progressTimeout);
            int read = await stream.ReadAsync(destination[offset..], progress.Token).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }
}
