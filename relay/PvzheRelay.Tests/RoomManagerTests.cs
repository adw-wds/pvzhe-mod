using System.Collections.Generic;
using PvzheRelay;
using Xunit;

// ★ 同 RelayServerTests：v3 改造把 NetProtocol 改名成 NetProto（避让游戏 DLL 里的同名类型）
using NetProtocol = NetProto;

public class RoomManagerTests
{
    static RoomManager NewMgr() => new RoomManager();
    static Player P(string nick) => new Player { Nick = nick, Send = (_, __) => { } };

    [Fact]
    public void CreateRoom_CodeIsFourDigits_AndHostOwnsConnectionPlayer()
    {
        var m = NewMgr();
        var host = P("host");
        var room = m.Create(4, host);
        Assert.Equal(4, room.Code.Length);
        foreach (var c in room.Code) Assert.InRange(c, '0', '9');
        Assert.Equal(host.Id, room.HostId);
        Assert.Single(room.Players);
        Assert.Same(host, room.Players[0]);      // 房间必须持有连接自身的对象
        Assert.NotNull(room.Players[0].Send);
    }

    [Fact]
    public void Join_AddsPlayerWithDistinctId()
    {
        var m = NewMgr();
        var host = P("host");
        var room = m.Create(4, host);
        var guest = P("guest");
        var joined = m.Join(room.Code, guest);
        Assert.Same(room, joined);
        Assert.Equal(2, room.Players.Count);
        Assert.NotEqual(host.Id, guest.Id);
        Assert.NotEqual(0, guest.Id);
    }

    [Fact]
    public void Join_FullRoom_ThrowsErrFull()
    {
        var m = NewMgr();
        var room = m.Create(2, P("host"));
        m.Join(room.Code, P("g1"));
        var ex = Assert.Throws<RelayException>(() => m.Join(room.Code, P("g2")));
        Assert.Equal(NetProtocol.ErrFull, ex.Code);
    }

    [Fact]
    public void Join_BadCode_ThrowsErrNoRoom()
    {
        var m = NewMgr();
        var ex = Assert.Throws<RelayException>(() => m.Join("0000", P("x")));
        Assert.Equal(NetProtocol.ErrNoRoom, ex.Code);
    }

    [Fact]
    public void Leave_RemovesPlayer_AndDeletesEmptyRoom()
    {
        var m = NewMgr();
        var host = P("host");
        var room = m.Create(2, host);
        var guest = P("guest");
        m.Join(room.Code, guest);

        var (r1, empty1) = m.Leave(guest);
        Assert.Same(room, r1);
        Assert.False(empty1);
        Assert.Single(room.Players);

        var (r2, empty2) = m.Leave(host);
        Assert.Same(room, r2);
        Assert.True(empty2);
        Assert.Null(m.RoomOf(host));
    }

    [Fact]
    public void Leave_HostTransfer_UpdatesHostId()
    {
        var m = NewMgr();
        var host = P("host");
        var room = m.Create(4, host);
        var guest = P("guest");
        m.Join(room.Code, guest);

        m.Leave(host);
        Assert.Equal(guest.Id, room.HostId);
    }

    [Fact]
    public void Signal_DeliversToTargetOnly()
    {
        var m = NewMgr();
        var host = P("host");
        var room = m.Create(4, host);
        var guest = P("guest");
        m.Join(room.Code, guest);

        var toHost = new List<(byte t, byte[] p)>();
        var toGuest = new List<(byte t, byte[] p)>();
        host.Send = (t, p) => toHost.Add((t, p));
        guest.Send = (t, p) => toGuest.Add((t, p));

        // payload 由上层自行编码（此处 WriteStr），中继原样透传
        m.Signal(guest, host.Id, new BitWriter().WriteStr("127.0.0.1:22999").ToArray());

        Assert.Single(toHost);
        Assert.Empty(toGuest);
        Assert.Equal(NetProtocol.MsgSignalFrom, toHost[0].t);
        var r = new BitReader(toHost[0].p);
        Assert.Equal(guest.Id, r.ReadU16());
        Assert.Equal("127.0.0.1:22999", r.ReadStr());
    }

    [Fact]
    public void Relay_BroadcastReachesOthersOnly()
    {
        var m = NewMgr();
        var host = P("host");
        var room = m.Create(4, host);
        var guest = P("guest");
        m.Join(room.Code, guest);

        var toHost = new List<(byte t, byte[] p)>();
        var toGuest = new List<(byte t, byte[] p)>();
        host.Send = (t, p) => toHost.Add((t, p));
        guest.Send = (t, p) => toGuest.Add((t, p));

        m.Relay(host, 0, new byte[] { 0xAA });

        Assert.Empty(toHost);                 // 不回声给发送者
        Assert.Single(toGuest);
        Assert.Equal(NetProtocol.MsgRelayDataTo, toGuest[0].t);
        var r = new BitReader(toGuest[0].p);
        Assert.Equal(host.Id, r.ReadU16());
        Assert.Equal(0xAA, r.ReadByte());
    }

    [Fact]
    public void Signal_WhenNotInRoom_ThrowsErrNotInRoom()
    {
        var m = NewMgr();
        var orphan = P("x");
        var ex = Assert.Throws<RelayException>(() => m.Signal(orphan, 1, new byte[0]));
        Assert.Equal(NetProtocol.ErrNotInRoom, ex.Code);
    }

    [Fact]
    public void RoomOf_ReturnsRoomForJoinedPlayer()
    {
        var m = NewMgr();
        var host = P("host");
        var room = m.Create(4, host);
        Assert.Same(room, m.RoomOf(host));
        var guest = P("guest");
        m.Join(room.Code, guest);
        Assert.Same(room, m.RoomOf(guest));
    }
}
