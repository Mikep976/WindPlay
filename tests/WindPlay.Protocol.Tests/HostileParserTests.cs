using System.Buffers.Binary;
using System.Text;
using AirPlay.Core2.Discovery;
using AirPlay.Core2.Models;
using AirPlay.Core2.Security;
using Claunia.PropertyList;
using Xunit;

namespace WindPlay.Protocol.Tests;

public sealed class HostileParserTests
{
    // These inputs are NEVER passed to the former unbounded dependency parsers.
    [Fact]
    public void PlistSelfCycleIsRejected() => RejectPlist(Plist([0xa1, 0]));

    [Fact]
    public void PlistIndirectCycleIsRejected() => RejectPlist(Plist([0xa1, 1], [0xa1, 0]));

    [Fact]
    public void PlistDepthIsBounded()
    {
        var objects = Enumerable.Range(0, 20).Select(i => new byte[] { 0xa1, (byte)(i + 1) }).ToList();
        objects.Add([0x09]);
        RejectPlist(Plist(objects.ToArray()));
    }

    [Fact]
    public void PlistHugeTrailerCountCannotAllocate()
    {
        byte[] bytes = Plist([0x09]);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(bytes.Length - 24), ulong.MaxValue);
        AssertSmallAllocation(() => RejectPlist(bytes));
    }

    [Fact]
    public void PlistHugeScalarLengthCannotAllocate() =>
        AssertSmallAllocation(() => RejectPlist(Plist([0x4f, 0x13, 0x7f, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff])));

    [Fact]
    public void PlistReferenceAndOffsetBounds()
    {
        RejectPlist(Plist([0xa1, 0xff]));
        byte[] bytes = Plist([0x09]);
        bytes[9] = 0xff;
        RejectPlist(bytes);
    }

    [Fact]
    public void PlistSharedGraphExpansionIsBounded()
    {
        var objects = Enumerable.Range(0, 14).Select(i => new byte[] { 0xa2, (byte)(i + 1), (byte)(i + 1) }).ToList();
        objects.Add([0x09]);
        RejectPlist(Plist(objects.ToArray()));
    }

    [Theory]
    [InlineData("<?xml version=\"1.0\"?><plist><dict/></plist>")]
    [InlineData("{ qualifier = (txtRAOP); }")]
    public void OnlyBinaryPlistsAreAccepted(string value) => RejectPlist(Encoding.UTF8.GetBytes(value));

    [Fact]
    public void ValidQualifiersRoundTrip()
    {
        byte[] bytes = BinaryPropertyListWriter.WriteToArray(NSObject.Wrap(new Dictionary<string, object>
        { ["qualifier"] = (string[])["txtAirPlay", "txtRAOP"] }));
        Assert.IsType<NSDictionary>(BoundedPlist.Parse(bytes));
    }

    [Fact]
    public void DmapHugeClaimDoesNotAllocate()
    {
        byte[] body = Dmap("minm", 0x7fffffff, []);
        var decoder = new DMapTagged();
        AssertSmallAllocation(() => Assert.Throws<InvalidDataException>(() => decoder.Decode(body)));
    }

    [Theory]
    [InlineData(0xffffffff)]
    [InlineData(1)]
    public void DmapItemPastEndIsRejected(uint length) =>
        Assert.Throws<InvalidDataException>(() => new DMapTagged().Decode(Dmap("minm", length, [])));

    [Fact]
    public void DmapValidUtf8AndUnknownTags()
    {
        var decoder = new DMapTagged();
        Assert.Equal("music", decoder.Decode(Dmap("minm", 5, "music"u8.ToArray()))["minm"]);
        Assert.Empty(decoder.Decode(Dmap("zzzz", 1, [0])));
    }

    [Fact]
    public void DmapNumericWidthAndStringLengthAreBounded()
    {
        Assert.Throws<InvalidDataException>(() => new DMapTagged().Decode(Dmap("minm", 1, [0x8a])));
        Assert.Throws<InvalidDataException>(() => new DMapTagged().Decode(Dmap("astm", 1, [0])));
        Assert.Throws<InvalidDataException>(() => new DMapTagged().Decode(Dmap("minm", 4097, new byte[4097])));
        Assert.Throws<InvalidDataException>(() => new DMapTagged().Decode(new byte[9]));
    }

    [Fact]
    public void DmapDuplicateAndTrailingBytesAreRejected()
    {
        byte[] one = Dmap("minm", 1, [(byte)'a']);
        byte[] duplicates = [.. one, .. one.AsSpan(8).ToArray()];
        BinaryPrimitives.WriteUInt32BigEndian(duplicates.AsSpan(4), (uint)duplicates.Length - 8);
        Assert.Throws<InvalidDataException>(() => new DMapTagged().Decode(duplicates));
        byte[] trailing = [.. one, 0];
        BinaryPrimitives.WriteUInt32BigEndian(trailing.AsSpan(4), (uint)trailing.Length - 8);
        Assert.Throws<InvalidDataException>(() => new DMapTagged().Decode(trailing));
    }

    [Fact]
    public void DnsCyclesAndInvalidLabelsAreRejected()
    {
        foreach (byte[] name in new byte[][] { [0xc0, 12], [0x40, 0], [0xc0, 255], [63, 1] })
            Assert.Throws<InvalidDataException>(() => DnsPacket.Parse(DnsQuestion(name)));
    }

    [Fact]
    public void DnsExpandedNameAndPacketSizeAreBounded()
    {
        var name = Enumerable.Range(0, 130).SelectMany(_ => new byte[] { 1, (byte)'a' }).Append((byte)0).ToArray();
        Assert.Throws<InvalidDataException>(() => DnsPacket.Parse(DnsQuestion(name)));
        AssertSmallAllocation(() => Assert.Throws<InvalidDataException>(() => DnsPacket.Parse(new byte[9001])));
        byte[] packet = new byte[12]; packet[6] = 0xff; packet[7] = 0xff;
        Assert.Throws<InvalidDataException>(() => DnsPacket.Parse(packet));
    }

    [Fact]
    public void DnsValidQuestionAndAdvertisementRoundTrip()
    {
        Assert.Single(DnsPacket.Parse(DnsWriter.Query("_airplay._tcp.local")).Questions);
        byte[] packet = DnsWriter.Advertise("windplay.local", System.Net.IPAddress.Loopback,
            [("_airplay._tcp.local", "WindPlay._airplay._tcp.local", 5000, [4, 112, 119, 61, 49])], 120);
        Assert.Equal(4, DnsPacket.Parse(packet).Records.Length);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5394", Justification = "Seeded fuzz-test corpus must be reproducible; no security secret is generated here.")]
    public void DeterministicMalformedCorpusStaysWithinBudget()
    {
        var random = new Random(9264);
        for (int i = 0; i < 2000; i++)
        {
            byte[] bytes = new byte[random.Next(0, 512)]; random.NextBytes(bytes);
            try { _ = DnsPacket.Parse(bytes); } catch (InvalidDataException) { }
            try { _ = BoundedPlist.Parse(bytes); } catch (InvalidDataException) { }
        }
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5394", Justification = "Reproducible structured parser mutations, not security secrets.")]
    public void StructuredMutationsExerciseParserInternalsWithBoundedAllocation()
    {
        byte[][] seeds = [
            BinaryPropertyListWriter.WriteToArray(NSObject.Wrap(new Dictionary<string, object>
                { ["qualifier"] = (string[])["txtAirPlay", "txtRAOP"] })),
            DnsWriter.Advertise("windplay.local", System.Net.IPAddress.Loopback,
                [("_airplay._tcp.local", "WindPlay._airplay._tcp.local", 5000, [4, 112, 119, 61, 49])], 120),
            Dmap("minm", 5, "music"u8.ToArray())];
        var random = new Random(20260905);
        var dmap = new DMapTagged();
        for (int iteration = 0; iteration < 12000; iteration++)
        {
            int parser = iteration % seeds.Length;
            byte[] bytes = seeds[parser].ToArray();
            int mutations = 1 + random.Next(4);
            for (int change = 0; change < mutations; change++) bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
            if (iteration % 7 == 0) bytes = bytes[..random.Next(bytes.Length + 1)];
            long before = GC.GetAllocatedBytesForCurrentThread();
            try
            {
                if (parser == 0) _ = BoundedPlist.Parse(bytes);
                else if (parser == 1) _ = DnsPacket.Parse(bytes);
                else _ = dmap.Decode(bytes);
            }
            catch (InvalidDataException) { }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(allocated < 2 * 1024 * 1024, $"Parser {parser}, mutation {iteration}: {allocated} allocated bytes.");
        }
    }

    internal static byte[] Plist(params byte[][] objects)
    {
        using MemoryStream stream = new(); stream.Write("bplist00"u8);
        var offsets = new List<ushort>();
        foreach (byte[] value in objects) { offsets.Add((ushort)stream.Length); stream.Write(value); }
        ulong table = (ulong)stream.Length;
        foreach (ushort offset in offsets) { stream.WriteByte((byte)(offset >> 8)); stream.WriteByte((byte)offset); }
        byte[] trailer = new byte[32]; trailer[6] = 2; trailer[7] = 1;
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(8), (ulong)objects.Length);
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(24), table);
        stream.Write(trailer); return stream.ToArray();
    }

    private static void RejectPlist(byte[] bytes) => Assert.Throws<InvalidDataException>(() => BoundedPlist.Parse(bytes));
    private static byte[] DnsQuestion(byte[] name)
    { byte[] bytes = new byte[12 + name.Length + 4]; bytes[5] = 1; name.CopyTo(bytes, 12); return bytes; }
    private static byte[] Dmap(string tag, uint claim, byte[] value)
    {
        byte[] bytes = new byte[16 + value.Length]; "mlit"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), (uint)bytes.Length - 8);
        Encoding.ASCII.GetBytes(tag).CopyTo(bytes, 8); BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12), claim);
        value.CopyTo(bytes, 16); return bytes;
    }
    private static void AssertSmallAllocation(Action action)
    {
        action(); // Warm exception/type metadata before measuring.
        long before = GC.GetAllocatedBytesForCurrentThread(); action();
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 128 * 1024);
    }
}
