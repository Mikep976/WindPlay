using System.Text;
using AirPlay.Core2.Connections;
using Xunit;

namespace WindPlay.Protocol.Tests;

public sealed class ModifiedHttpConnectionTests
{
    [Fact]
    public void CompleteHeaderTerminatorIsLocated()
    {
        byte[] data = Encoding.ASCII.GetBytes("GET /server-info HTTP/1.1\r\nHost: receiver\r\n\r\nbody");

        int terminator = ModifiedHttpConnection.FindHeaderTerminator(data);

        Assert.Equal(41, terminator);
    }

    [Theory]
    [InlineData("")]
    [InlineData("GET / HTTP/1.1\r\n")]
    [InlineData("GET / HTTP/1.1\n\n")]
    public void IncompleteOrNonHttpTerminatorIsRejected(string text)
    {
        byte[] data = Encoding.ASCII.GetBytes(text);

        Assert.Equal(-1, ModifiedHttpConnection.FindHeaderTerminator(data));
    }
}
