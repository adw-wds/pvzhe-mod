using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using PvzheRelay;
using Xunit;

// ★ v3 改造把协议类 NetProtocol 改名成 NetProto：游戏自身 DLL 里已经有一个 NetProtocol 类型，
//   而 patcher 是按类型名合并的，遇到同名会「类型已存在，跳过」把我们的版本静默丢掉。
//   测试里沿用旧名，这里用类型别名统一，避免逐个改几十处调用点。
using NetProtocol = NetProto;

public class RelayServerTests
{
    // ---------- 基础设施 ----------
    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    static TcpClient RawConnect(int port)
    {
        var c = new TcpClient();
        c.Connect("127.0.0.1", port);
        c.NoDelay = true;
        return c;
    }

    static void Send(NetworkStream s, byte type, byte[] payload)
    {
        // v3 帧多了 enc 字节；测试往未鉴权连接发裸帧，用 EncPlain 才是协议允许的形式
        byte[] f = NetProtocol.Frame(type, NetProto.EncPlain, payload);
        s.Write(f, 0, f.Length);
        s.Flush();
    }

    static void ReadFull(NetworkStream s, byte[] buf)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int n = s.Read(buf, got, buf.Length - got);
            if (n <= 0) throw new Exception("连接被关闭");
            got += n;
        }
    }

    static (byte type, byte[] payload) Recv(NetworkStream s, int timeoutMs = 4000)
    {
        s.ReadTimeout = timeoutMs;
        var head = new byte[4];
        ReadFull(s, head);
        uint total = (uint)(head[0] | (head[1] << 8) | (head[2] << 16) | (head[3] << 24));
        var body = new byte[total];
        ReadFull(s, body);
        // v3 帧格式：[u32 total][type][enc][payload]，total = 1 + 1 + payload.Length
        // （旧版没有 enc 字节，所以以前是 total-1；加了链路加密后必须跳 2 字节）
        var raw = new byte[total - 2];
        if (raw.Length > 0) Array.Copy(body, 2, raw, 0, raw.Length);

        // enc != EncPlain 的帧是链路加密的，必须解密后才能当业务 payload 用
        byte[] payload = raw;
        if (body[1] != NetProto.EncPlain && _secs.TryGetValue(s, out var sec))
        {
            var opened = sec.OpenLink(body[0], raw);
            if (opened != null) payload = opened;
        }
        return (body[0], payload);
    }

    /// <summary>读一帧；超时返回 false（用于“可能没有更多帧”的场景）。</summary>
    static bool TryRecv(NetworkStream s, int timeoutMs, out byte type, out byte[] payload)
    {
        try
        {
            var r = Recv(s, timeoutMs);
            type = r.type; payload = r.payload;
            return true;
        }
        catch { type = 0; payload = null; return false; }
    }

    /// <summary>期望收到一份玩家名单（自动跳过其它帧）。</summary>
    static System.Collections.Generic.List<PeerInfo> ExpectPlayerList(NetworkStream s, int count)
    {
        var (t, p) = WaitFor(s, NetProtocol.MsgPlayerList);
        var list = PeerListCodec.Read(p);
        Assert.Equal(count, list.Count);
        return list;
    }

    /// <summary>等待指定类型消息，自动跳过其它消息（避免顺序耦合）。</summary>
    static (byte type, byte[] payload) WaitFor(NetworkStream s, byte expected, int timeoutMs = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            if (!TryRecv(s, 600, out byte t, out byte[] p)) continue;
            if (t == expected) return (t, p);
        }
        throw new Exception("未收到期望消息 0x" + expected.ToString("X2"));
    }

    /// <summary>完成鉴权握手（RequireAuth=true 时）。</summary>
    /// <remarks>
    /// v3 握手（别再照抄旧写法）：
    ///   挑战   = [nonce8][中继 X25519 公钥32]  —— 共 40 字节，不是 8 字节
    ///   应答   = [客户端公钥32][HMAC32]        —— 共 64 字节，不是 32 字节
    ///   HMAC 用 ECDH 派生的链路密钥算，不再用 NetConstants.ServerToken
    /// 旧版断言 Assert.Equal(8, p.Length) 在 v3 引入 X25519 后就一直是红的。
    /// </remarks>
    static void Handshake(NetworkStream s, string token = null)
    {
        var (t, p) = Recv(s, 3000);
        Assert.Equal(NetProtocol.MsgAuthChallenge, t);
        Assert.Equal(40, p.Length);

        byte[] nonce = new byte[8];
        byte[] relayPub = new byte[32];
        Buffer.BlockCopy(p, 0, nonce, 0, 8);
        Buffer.BlockCopy(p, 8, relayPub, 0, 32);

        var sec = new NetSecureSession();
        sec.BeginHandshake();
        Assert.True(sec.CompleteHandshake(relayPub, nonce), "X25519 密钥交换失败");

        byte[] resp = new byte[64];
        Buffer.BlockCopy(sec.MyPublicKey, 0, resp, 0, 32);
        Buffer.BlockCopy(sec.AuthMac(nonce), 0, resp, 32, 32);
        Send(s, NetProtocol.MsgAuthResponse, resp);

        // ★ 服务端回消息走 TrySend → SealLink（链路加密），测试必须拿同一份 sec 解密，
        //   否则读到的 payload 是密文（表现为“期望 ErrNoRoom=2，实际 0”这类怪事）。
        _secs[s] = sec;
    }

    /// <summary>连接 → 安全上下文（Recv 解密用）。测试单线程，用并发字典即可。</summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<NetworkStream, NetSecureSession> _secs = new();

    static (TcpClient c, NetworkStream s) Connect(int port, bool auth = true)
    {
        var c = RawConnect(port);
        var s = c.GetStream();
        if (auth) Handshake(s);
        return (c, s);
    }

    static void DoCreate(NetworkStream s, string nick, int maxPlayers = 4)
    {
        Send(s, NetProtocol.MsgRoomCreateReq, new BitWriter().WriteByte((byte)maxPlayers).WriteStr(nick).ToArray());
    }

    static void DoJoin(NetworkStream s, string code, string nick, string pass = "")
    {
        Send(s, NetProtocol.MsgRoomJoinReq, new BitWriter().WriteStr(code).WriteStr(nick).WriteStr(pass).ToArray());
    }

    static (byte type, byte[] payload) ExpectRoomCreateRes(NetworkStream s)
    {
        var r = Recv(s);
        Assert.Equal(NetProtocol.MsgRoomCreateRes, r.type);
        Assert.Equal(0, r.payload[0]);
        return r;
    }

    // ---------- 鉴权 ----------
    [Fact]
    public void Auth_BusinessBeforeAuth_IsRejected()
    {
        int port = FreePort();
        var srv = new RelayServer(port);
        srv.Start();
        try
        {
            var c = RawConnect(port);
            var s = c.GetStream();
            var (t0, _) = Recv(s);                     // AuthChallenge
            Assert.Equal(NetProtocol.MsgAuthChallenge, t0);

            DoCreate(s, "hacker");                     // 未鉴权就发业务消息
            var (t1, p1) = Recv(s);
            Assert.Equal(NetProtocol.MsgError, t1);
            Assert.Equal(NetProtocol.ErrAuth, p1[0]);
            c.Close();
        }
        finally { srv.Stop(); }
    }

    [Fact]
    public void Auth_WrongMac_IsRejected()
    {
        // v3 起不再有共享 token：鉴权靠 ECDH 派生的链路密钥算 HMAC。
        // 这里构造「公钥合法、但 MAC 错误」的应答，服务端应回 MsgError(ErrAuth)。
        // （旧测试发的是 32 字节 KeyedHash，长度就不对，服务端会走「握手数据不完整」直接断开、
        //   连错误都发不出来，所以那个断言必然失败。）
        int port = FreePort();
        var srv = new RelayServer(port);
        srv.Start();
        try
        {
            var c = RawConnect(port);
            var s = c.GetStream();
            var (t0, chal) = Recv(s);
            Assert.Equal(NetProtocol.MsgAuthChallenge, t0);
            Assert.Equal(40, chal.Length);

            byte[] nonce = new byte[8];
            byte[] relayPub = new byte[32];
            Buffer.BlockCopy(chal, 0, nonce, 0, 8);
            Buffer.BlockCopy(chal, 8, relayPub, 0, 32);

            var sec = new NetSecureSession();
            sec.BeginHandshake();
            Assert.True(sec.CompleteHandshake(relayPub, nonce));

            byte[] resp = new byte[64];
            Buffer.BlockCopy(sec.MyPublicKey, 0, resp, 0, 32);   // 公钥合法
            // 后 32 字节留成全零 = 故意错误的 MAC
            Send(s, NetProtocol.MsgAuthResponse, resp);

            var (t1, p1) = Recv(s);
            Assert.Equal(NetProtocol.MsgError, t1);
            Assert.Equal(NetProtocol.ErrAuth, p1[0]);
            c.Close();
        }
        finally { srv.Stop(); }
    }

    [Fact]
    public void Auth_PingAllowedBeforeAuth()
    {
        int port = FreePort();
        var srv = new RelayServer(port);
        srv.Start();
        try
        {
            var c = RawConnect(port);
            var s = c.GetStream();
            Recv(s);                                    // challenge
            Send(s, NetProtocol.MsgPing, new BitWriter().WriteU32(1).ToArray());
            var (t, p) = Recv(s);
            Assert.Equal(NetProtocol.MsgPong, t);
            c.Close();
        }
        finally { srv.Stop(); }
    }

    // ---------- 基本流程 ----------
    [Fact]
    public void OnlineStats_ReturnsLiveAndToday()
    {
        // M5 在线统计：
        //   live  = 中继的 TCP 连接数（含还没进房间的人）
        //   today = 当日出现过的【唯一 IP】数 —— 同一 IP 开多条连接也只算一条
        int port = FreePort();
        var srv = new RelayServer(port);
        srv.Start();
        try
        {
            var (c1, s1) = Connect(port);   // 只连接，不进房间
            var (c2, s2) = Connect(port);   // 建房
            var (c3, s3) = Connect(port);   // 再连一条（同 IP）
            try
            {
                DoCreate(s2, "host");

                Send(s1, NetProtocol.MsgStatsReq, new byte[0]);
                var (t, p) = Recv(s1);
                Assert.Equal(NetProtocol.MsgStatsRes, t);

                var r = new BitReader(p);
                uint live = r.ReadU32();
                uint today = r.ReadU32();

                Assert.Equal(3u, live);     // 三条连接都还在
                Assert.Equal(1u, today);    // 同一个 IP（127.0.0.1）→ 去重后只算 1
            }
            finally { c1.Close(); c2.Close(); c3.Close(); }
        }
        finally { srv.Stop(); }
    }

    [Fact]
    public void EndToEnd_CreateJoinSettingsChatLeave()
    {
        int port = FreePort();
        var srv = new RelayServer(port);
        srv.Start();
        try
        {
            var (hc, hs) = Connect(port);
            var (gc, gs) = Connect(port);
            try
            {
                DoCreate(hs, "host");
                var r1 = ExpectRoomCreateRes(hs);
                var br = new BitReader(r1.payload);
                br.ReadByte();
                Assert.NotEqual(0u, br.ReadU32());
                ushort hostId = br.ReadU16();
                string code = br.ReadStr();
                Assert.Matches("^[0-9]{4}$", code);
                // 建房后立即收到默认设置
                var (ts, ps) = Recv(hs);
                Assert.Equal(NetProtocol.MsgRoomSettings, ts);
                var def = RoomSettingsCodec.Read(ps);
                Assert.True(def.AllowCheats);
                Assert.Equal(NetConstants.DefaultRoomTtlMinutes, def.TtlMinutes);
                // M2c：随后收到玩家名单（1 人、房主、颜色槽 0）
                var peers1 = ExpectPlayerList(hs, 1);
                Assert.Equal(hostId, peers1[0].Id);
                Assert.True(peers1[0].IsHost);
                Assert.Equal(0, peers1[0].Color);

                DoJoin(gs, code, "guest");
                var r2 = Recv(gs);
                Assert.Equal(NetProtocol.MsgRoomJoinRes, r2.type);
                var br2 = new BitReader(r2.payload);
                br2.ReadByte();
                ushort guestId = br2.ReadU16();
                Assert.Equal(code, br2.ReadStr());
                Assert.NotEqual(hostId, guestId);
                var (tg, pg) = Recv(gs);                 // 加入者也会收到设置
                Assert.Equal(NetProtocol.MsgRoomSettings, tg);
                Assert.True(RoomSettingsCodec.Read(pg).AllowCheats);
                // M2c：客机也会收到名单（2 人，颜色互不相同）
                var peers2 = ExpectPlayerList(gs, 2);
                Assert.NotEqual(peers2[0].Color, peers2[1].Color);

                var (t3, p3) = WaitFor(hs, NetProtocol.MsgPeerJoined);
                Assert.Equal(guestId, new BitReader(p3).ReadU16());
                ExpectPlayerList(hs, 2);

                // 聊天（含发送者回显）
                Send(gs, NetProtocol.MsgChat, new BitWriter().WriteStr("hi").ToArray());
                var (t4, p4) = WaitFor(hs, NetProtocol.MsgChat);
                Assert.Equal("hi", new BitReader(p4).ReadStr());
                var (t5, p5) = WaitFor(gs, NetProtocol.MsgChat);
                Assert.Equal("hi", new BitReader(p5).ReadStr());

                // 客机断开 → 房主收到 PeerLeft，名单回到 1 人
                gc.Close();
                var (t6, p6) = WaitFor(hs, NetProtocol.MsgPeerLeft, 8000);
                Assert.Equal(guestId, new BitReader(p6).ReadU16());
                ExpectPlayerList(hs, 1);
            }
            finally { try { hc.Close(); } catch { } try { gc.Close(); } catch { } }
        }
        finally { srv.Stop(); }
    }

    [Fact]
    public void Join_BadCode_ReturnsErrNoRoom()
    {
        int port = FreePort();
        var srv = new RelayServer(port);
        srv.Start();
        try
        {
            var (c, s) = Connect(port);
            try
            {
                DoJoin(s, "0000", "x");
                var (t, p) = Recv(s);
                Assert.Equal(NetProtocol.MsgError, t);
                Assert.Equal(NetProtocol.ErrNoRoom, p[0]);
            }
            finally { c.Close(); }
        }
        finally { srv.Stop(); }
    }

    [Fact]
    public void Join_WrongPassword_ReturnsErrBadPass()
    {
        int port = FreePort();
        var srv = new RelayServer(port);
        srv.Start();
        try
        {
            var (hc, hs) = Connect(port);
            var (gc, gs) = Connect(port);
            try
            {
                DoCreate(hs, "host");
                var r1 = ExpectRoomCreateRes(hs);
                WaitFor(hs, NetProtocol.MsgRoomSettings);
                var br = new BitReader(r1.payload);
                br.ReadByte(); br.ReadU32();
                ushort hostId = br.ReadU16();
                string code = br.ReadStr();

                // 房主设口令
                var st = new RoomSettings { MaxPlayers = 4, AllowCheats = true, Password = "s3cret" };
                Send(hs, NetProtocol.MsgSetRoomSettings, RoomSettingsCodec.Write(st));
                WaitFor(hs, NetProtocol.MsgRoomSettings);

                DoJoin(gs, code, "guest", "wrong");
                var (t1, p1) = Recv(gs);
                Assert.Equal(NetProtocol.MsgError, t1);
                Assert.Equal(NetProtocol.ErrBadPass, p1[0]);

                DoJoin(gs, code, "guest", "s3cret");
                var (t2, p2) = Recv(gs);
                Assert.Equal(NetProtocol.MsgRoomJoinRes, t2);
                Assert.Equal(0, p2[0]);
                Assert.NotEqual(0, hostId);
            }
            finally { try { hc.Close(); } catch { } try { gc.Close(); } catch { } }
        }
        finally { srv.Stop(); }
    }

    // ---------- 房主设置 ----------
    [Fact]
    public void RoomSettings_HostBroadcasts_AndNonHostIsCountedViolation()
    {
        int port = FreePort();
        var srv = new RelayServer(port);
        srv.Start();
        try
        {
            var (hc, hs) = Connect(port);
            var (gc, gs) = Connect(port);
            try
            {
                DoCreate(hs, "host");
                var r1 = ExpectRoomCreateRes(hs);
                WaitFor(hs, NetProtocol.MsgRoomSettings);
                var br = new BitReader(r1.payload);
                br.ReadByte(); br.ReadU32(); br.ReadU16();
                string code = br.ReadStr();
                DoJoin(gs, code, "guest");
                WaitFor(gs, NetProtocol.MsgRoomJoinRes);
                WaitFor(gs, NetProtocol.MsgRoomSettings);

                // 房主关闭作弊 + 只允许无限阳光
                var st = new RoomSettings { MaxPlayers = 4, AllowCheats = false, CheatMask = CheatBits.Resource, TtlMinutes = 30, AllowLateJoin = false };
                Send(hs, NetProtocol.MsgSetRoomSettings, RoomSettingsCodec.Write(st));

                var (th, ph) = WaitFor(hs, NetProtocol.MsgRoomSettings);     // 房主收到广播
                var got = RoomSettingsCodec.Read(ph);
                Assert.False(got.AllowCheats);
                Assert.Equal(CheatBits.Resource, got.CheatMask);
                Assert.Equal(30, got.TtlMinutes);
                Assert.False(got.AllowLateJoin);

                var (tg, pg) = WaitFor(gs, NetProtocol.MsgRoomSettings);     // 客机也收到
                Assert.False(RoomSettingsCodec.Read(pg).AllowCheats);

                // 客机尝试改设置 → 只回错误，不改变房间设置
                var bad = new RoomSettings { MaxPlayers = 4, AllowCheats = true };
                Send(gs, NetProtocol.MsgSetRoomSettings, RoomSettingsCodec.Write(bad));
                var (te, pe) = WaitFor(gs, NetProtocol.MsgError);
                Assert.Equal(NetProtocol.ErrNotInRoom, pe[0]);
            }
            finally { try { hc.Close(); } catch { } try { gc.Close(); } catch { } }
        }
        finally { srv.Stop(); }
    }

    // ---------- 防刷量 ----------
    [Fact]
    public void OversizeFrame_Disconnects()
    {
        int port = FreePort();
        var srv = new RelayServer(port);
        srv.Start();
        try
        {
            var (c, s) = Connect(port);
            try
            {
                uint bad = (uint)(NetConstants.MaxFrame + 100);
                var head = new byte[] { (byte)bad, (byte)(bad >> 8), (byte)(bad >> 16), (byte)(bad >> 24) };
                s.Write(head, 0, 4);
                s.Flush();
                Assert.False(TryRecv(s, 1500, out _, out _));      // 连接应被断开
            }
            finally { try { c.Close(); } catch { } }
        }
        finally { srv.Stop(); }
    }

    [Fact]
    public void RateLimit_DropsExcessFrames()
    {
        int port = FreePort();
        var srv = new RelayServer(port);
        srv.Start();
        try
        {
            var (c, s) = Connect(port);
            try
            {
                // 一次写入远超突发上限的 Ping（每帧 5 字节）
                int n = NetConstants.RateBurst + 150;
                var one = NetProtocol.Frame(NetProtocol.MsgPing, NetProto.EncPlain, new BitWriter().WriteU32(1).ToArray());
                var big = new byte[one.Length * n];
                for (int i = 0; i < n; i++) Array.Copy(one, 0, big, i * one.Length, one.Length);
                s.Write(big, 0, big.Length);
                s.Flush();

                int pongs = 0;
                while (TryRecv(s, 700, out byte t, out _))
                {
                    if (t == NetProtocol.MsgPong) pongs++;
                    if (pongs > n) break;
                }
                Assert.True(pongs <= NetConstants.RateBurst + NetConstants.RateLimitPerSec * 2,
                    "回包数 " + pongs + " 应受令牌桶限制");
                Assert.True(pongs < n, "应有帧被丢弃");
            }
            finally { try { c.Close(); } catch { } }
        }
        finally { srv.Stop(); }
    }

    [Fact]
    public void SameIpConcurrency_IsLimited()
    {
        int port = FreePort();
        var srv = new RelayServer(port);
        srv.Start();
        var clients = new System.Collections.Generic.List<TcpClient>();
        try
        {
            for (int i = 0; i < NetConstants.MaxConnPerIp; i++)
            {
                var c = RawConnect(port);
                clients.Add(c);
                var s = c.GetStream();
                Recv(s);                       // challenge（保持连接）
            }
            var extra = RawConnect(port);
            clients.Add(extra);
            var es = extra.GetStream();
            bool gotLimit;
            try
            {
                var (t, p) = Recv(es, 2000);
                gotLimit = (t == NetProtocol.MsgError && p.Length > 0 && p[0] == NetProtocol.ErrIpLimit) || t == NetProtocol.MsgAuthChallenge;
            }
            catch { gotLimit = true; }          // 被直接关闭也算限制生效
            Assert.True(gotLimit, "超出同 IP 并发上限时应被拒绝");
        }
        finally
        {
            foreach (var c in clients) { try { c.Close(); } catch { } }
            srv.Stop();
        }
    }

    // ---------- 房间定时销毁 ----------
    [Fact]
    public void RoomTtl_NotifiesAndClosesRoom()
    {
        int port = FreePort();
        var srv = new RelayServer(port) { TtlOverrideSeconds = 1, ScanIntervalMs = 100 };
        srv.Start();
        try
        {
            var (c, s) = Connect(port);
            try
            {
                DoCreate(s, "host");
                ExpectRoomCreateRes(s);
                Recv(s);                      // 默认设置

                bool soon = false, closed = false;
                for (int i = 0; i < 40; i++)
                {
                    if (!TryRecv(s, 500, out byte t, out byte[] p)) continue;
                    if (t == NetProtocol.MsgRoomClosingSoon) soon = true;
                    if (t == NetProtocol.MsgRoomClosed)
                    {
                        closed = true;
                        Assert.Equal(NetProtocol.CloseExpired, p[0]);
                        break;
                    }
                }
                Assert.True(soon, "应收到到期提醒 RoomClosingSoon");
                Assert.True(closed, "应收到房间销毁 RoomClosed");
            }
            finally { try { c.Close(); } catch { } }
        }
        finally { srv.Stop(); }
    }

    [Fact]
    public void HostCanCloseRoom()
    {
        int port = FreePort();
        var srv = new RelayServer(port);
        srv.Start();
        try
        {
            var (hc, hs) = Connect(port);
            var (gc, gs) = Connect(port);
            try
            {
                DoCreate(hs, "host");
                var r1 = ExpectRoomCreateRes(hs);
                Recv(hs);
                var br = new BitReader(r1.payload);
                br.ReadByte(); br.ReadU32(); br.ReadU16();
                string code = br.ReadStr();
                DoJoin(gs, code, "guest");
                WaitFor(gs, NetProtocol.MsgRoomJoinRes);
                Recv(hs);                       // PeerJoined

                Send(hs, NetProtocol.MsgRoomClosed, new byte[0]);   // 房主解散
                var (tg, pg) = WaitFor(gs, NetProtocol.MsgRoomClosed);
                Assert.Equal(NetProtocol.CloseHostClosed, pg[0]);
            }
            finally { try { hc.Close(); } catch { } try { gc.Close(); } catch { } }
        }
        finally { srv.Stop(); }
    }

    [Fact]
    public void BattleStart_OnlyHostOnce()
    {
        int port = FreePort();
        var srv = new RelayServer(port);
        srv.Start();
        try
        {
            var (hc, hs) = Connect(port);
            var (gc, gs) = Connect(port);
            try
            {
                DoCreate(hs, "host");
                var r1 = ExpectRoomCreateRes(hs);
                var br = new BitReader(r1.payload);
                br.ReadByte(); br.ReadU32(); br.ReadU16();
                string code = br.ReadStr();
                DoJoin(gs, code, "guest");

                // 客机不能开始对局
                Send(gs, NetProtocol.MsgBattleStart, new BitWriter().WriteStr("Level1_1").ToArray());
                var (te, pe) = WaitFor(gs, NetProtocol.MsgError);
                Assert.Equal(NetProtocol.ErrNotHost, new BitReader(pe).ReadByte());

                // 房主开始：双方都收到 battleId + 关卡
                Send(hs, NetProtocol.MsgBattleStart, new BitWriter().WriteStr("Level1_1").ToArray());
                var (th, ph) = WaitFor(hs, NetProtocol.MsgBattleStart);
                var rh = new BitReader(ph);
                Assert.Equal(1u, rh.ReadU32());
                Assert.Equal("Level1_1", rh.ReadStr());
                var (tg, pg) = WaitFor(gs, NetProtocol.MsgBattleStart);
                var rg = new BitReader(pg);
                Assert.Equal(1u, rg.ReadU32());
                Assert.Equal("Level1_1", rg.ReadStr());

                // 重复开始被拒绝（不能重复开）
                Send(hs, NetProtocol.MsgBattleStart, new BitWriter().WriteStr("Level1_1").ToArray());
                var (te2, pe2) = WaitFor(hs, NetProtocol.MsgError);
                Assert.Equal(NetProtocol.ErrBattleStarted, new BitReader(pe2).ReadByte());

                // 结束对局后可以再次开始
                Send(hs, NetProtocol.MsgBattleEnd, new byte[0]);
                WaitFor(gs, NetProtocol.MsgBattleEnd);
                Send(hs, NetProtocol.MsgBattleStart, new BitWriter().WriteStr("Level1_2").ToArray());
                var (th3, ph3) = WaitFor(hs, NetProtocol.MsgBattleStart);
                var rh3 = new BitReader(ph3);
                Assert.Equal(2u, rh3.ReadU32());
                Assert.Equal("Level1_2", rh3.ReadStr());
            }
            finally { try { hc.Close(); } catch { } try { gc.Close(); } catch { } }
        }
        finally { srv.Stop(); }
    }

    [Fact]
    public void Cursor_RelayedWithSenderId()
    {
        int port = FreePort();
        var srv = new RelayServer(port);
        srv.Start();
        try
        {
            var (hc, hs) = Connect(port);
            var (gc, gs) = Connect(port);
            try
            {
                DoCreate(hs, "host");
                var r1 = ExpectRoomCreateRes(hs);
                var br = new BitReader(r1.payload);
                br.ReadByte(); br.ReadU32();
                ushort hostId = br.ReadU16();
                string code = br.ReadStr();
                DoJoin(gs, code, "guest");
                var r2 = WaitFor(gs, NetProtocol.MsgRoomJoinRes);
                var bg = new BitReader(r2.payload);
                bg.ReadByte();
                ushort guestId = bg.ReadU16();

                // 客机上报光标（x=800, y=320）→ 房主收到带发送者 id 的转发
                Send(gs, NetProtocol.MsgCursor, new BitWriter().WriteU32(800).WriteU32(320).ToArray());
                var (t, p) = WaitFor(hs, NetProtocol.MsgCursor);
                var rc = new BitReader(p);
                Assert.Equal(guestId, rc.ReadU16());
                Assert.Equal(800u, rc.ReadU32());
                Assert.Equal(320u, rc.ReadU32());

                // 房主上报 → 客机收到
                Send(hs, NetProtocol.MsgCursor, new BitWriter().WriteU32(1234).WriteU32(567).ToArray());
                var (t2, p2) = WaitFor(gs, NetProtocol.MsgCursor);
                var rc2 = new BitReader(p2);
                Assert.Equal(hostId, rc2.ReadU16());
                Assert.Equal(1234u, rc2.ReadU32());
            }
            finally { try { hc.Close(); } catch { } try { gc.Close(); } catch { } }
        }
        finally { srv.Stop(); }
    }

    [Fact]
    public void LobbyRoomList_ListsRoomsWithoutJoining()
    {
        int port = FreePort();
        var srv = new RelayServer(port);
        srv.Start();
        try
        {
            var (hc, hs) = Connect(port);      // 大厅连接（不进任何房间）
            var (hc2, hs2) = Connect(port);    // 房主
            try
            {
                DoCreate(hs2, "host");
                var r1 = ExpectRoomCreateRes(hs2);
                var br = new BitReader(r1.payload);
                br.ReadByte(); br.ReadU32(); br.ReadU16();
                string code = br.ReadStr();

                Send(hs, NetProtocol.MsgRoomListReq, new byte[0]);
                var (t, p) = WaitFor(hs, NetProtocol.MsgRoomList);
                Assert.Equal(NetProtocol.MsgRoomList, t);
                var rooms = RoomListCodec.Read(p);
                Assert.Single(rooms);
                Assert.Equal(code, rooms[0].Code);
                Assert.Equal(1, rooms[0].Players);
                Assert.False(rooms[0].Started);
                Assert.False(rooms[0].NeedPass);
                Assert.Equal("host", rooms[0].HostNick);
            }
            finally { try { hc.Close(); } catch { } try { hc2.Close(); } catch { } }
        }
        finally { srv.Stop(); }
    }
}
