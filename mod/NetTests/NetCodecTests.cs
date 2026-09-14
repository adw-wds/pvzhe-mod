using System;
using Xunit;

public class NetCodecTests
{
    [Fact]
    public void BitWriterReader_RoundTripAllTypes()
    {
        var w = new BitWriter();
        w.WriteByte(0x7F).WriteU16(65535).WriteU32(4000000000u).WriteI16(-12345)
         .WriteStr("杂交版联机").WriteBytes(new byte[] { 1, 2, 3 });
        var r = new BitReader(w.ToArray());
        Assert.Equal(0x7F, r.ReadByte());
        Assert.Equal(65535, r.ReadU16());
        Assert.Equal(4000000000u, r.ReadU32());
        Assert.Equal(-12345, r.ReadI16());
        Assert.Equal("杂交版联机", r.ReadStr());
        Assert.Equal(new byte[] { 1, 2, 3 }, r.ReadBytes(3));
        Assert.False(r.HasMore);
    }

    [Fact]
    public void Frame_RoundTrip()
    {
        byte[] payload = { 0x10, 0x20, 0x30 };
        byte[] framed = NetProtocol.Frame(NetProtocol.MsgChat, payload);
        Assert.True(NetProtocol.TryParseFrame(framed, out byte type, out byte[] got, out int used));
        Assert.Equal(NetProtocol.MsgChat, type);
        Assert.Equal(payload, got);
        Assert.Equal(framed.Length, used);
    }

    [Fact]
    public void Frame_PartialBuffer_ReturnsFalseWithZeroConsumed()
    {
        byte[] framed = NetProtocol.Frame(NetProtocol.MsgPing, new byte[] { 9, 9, 9, 9 });
        byte[] partial = new byte[framed.Length - 2];
        Array.Copy(framed, partial, partial.Length);
        Assert.False(NetProtocol.TryParseFrame(partial, out _, out _, out int used));
        Assert.Equal(0, used);
    }

    [Fact]
    public void Frame_TwoFrames_ParsesSequentially()
    {
        byte[] a = NetProtocol.Frame(NetProtocol.MsgPing, new byte[] { 1, 1, 1, 1 });
        byte[] b = NetProtocol.Frame(NetProtocol.MsgChat, new byte[] { 2, 2 });
        byte[] both = new byte[a.Length + b.Length];
        Array.Copy(a, both, a.Length);
        Array.Copy(b, 0, both, a.Length, b.Length);

        Assert.True(NetProtocol.TryParseFrame(both, out byte t1, out byte[] p1, out int c1));
        Assert.Equal(NetProtocol.MsgPing, t1);
        Assert.Equal(4, p1.Length);
        Assert.Equal(a.Length, c1);

        byte[] rest = new byte[both.Length - c1];
        Array.Copy(both, c1, rest, 0, rest.Length);
        Assert.True(NetProtocol.TryParseFrame(rest, out byte t2, out byte[] p2, out int c2));
        Assert.Equal(NetProtocol.MsgChat, t2);
        Assert.Equal(2, p2.Length);
        Assert.Equal(b.Length, c2);
    }

    [Fact]
    public void Frame_RejectsOversize()
    {
        byte[] bad = new byte[8];
        Array.Copy(BitConverter.GetBytes((uint)(NetConstants.MaxFrame + 1)), 0, bad, 0, 4);
        Assert.False(NetProtocol.TryParseFrame(bad, out _, out _, out _));
    }

    [Fact]
    public void SessionCore_DirectTimeoutFallsBackToRelay()
    {
        Assert.Equal(NetState.Relay, NetSessionCore.NextOnDirectTimeout(NetState.Signaling, relayAvailable: true));
        Assert.Equal(NetState.Ended, NetSessionCore.NextOnDirectTimeout(NetState.Signaling, relayAvailable: false));
        Assert.Equal(NetState.Direct, NetSessionCore.NextOnDirectEstablished(NetState.Signaling));
    }

    [Fact]
    public void SessionCore_TransitionsAreGuarded()
    {
        Assert.True(NetSessionCore.CanTransition(NetState.Offline, NetState.Signaling));
        Assert.False(NetSessionCore.CanTransition(NetState.Offline, NetState.Playing));
        Assert.True(NetSessionCore.CanTransition(NetState.Playing, NetState.Reconnecting));
        Assert.True(NetSessionCore.CanTransition(NetState.Reconnecting, NetState.Playing));
        Assert.False(NetSessionCore.CanTransition(NetState.Ended, NetState.Playing));
    }
}
