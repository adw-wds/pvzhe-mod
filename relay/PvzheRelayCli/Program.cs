using System;
using System.Net.Sockets;
using System.Threading;

/// <summary>
/// 中继命令行客户端 / 自检工具（复用共享协议 + 鉴权握手）。
///   PvzheRelayCli -relay relay.example.com:8231 -selftest
///   PvzheRelayCli -relay 127.0.0.1:8231 -create
///   PvzheRelayCli -relay 127.0.0.1:8231 -join 1234 -pass s3cret -chat hi
/// </summary>
public static class Program
{
    static string _host = "127.0.0.1";
    static int _port = NetConstants.RelayDefaultPort;
    /// <summary>-keep N：建房/加房后保持连接 N 秒（用于验证大厅列表/延迟）。</summary>
    static int _keepSec;

    public static int Main(string[] args)
    {
        string mode = "";
        string joinCode = "", chat = "", pass = "", nick = "";
        bool selftest = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-relay":
                    if (i + 1 < args.Length)
                    {
                        var parts = args[++i].Split(':');
                        _host = parts[0];
                        if (parts.Length > 1) int.TryParse(parts[1], out _port);
                    }
                    break;
                case "-create": mode = "create"; break;
                case "-join": if (i + 1 < args.Length) { joinCode = args[++i]; mode = "join"; } break;
                case "-chat": if (i + 1 < args.Length) chat = args[++i]; break;
                case "-pass": if (i + 1 < args.Length) pass = args[++i]; break;
                case "-nick": if (i + 1 < args.Length) nick = args[++i]; break;
                case "-keep": if (i + 1 < args.Length) int.TryParse(args[++i], out _keepSec); break;
                case "-selftest": selftest = true; break;
                case "-stats": mode = "stats"; break;
                case "-rooms": mode = "rooms"; break;
                case "-help":
                case "--help":
                    Console.WriteLine("用法: PvzheRelayCli [-relay host:port] (-selftest | -stats | -rooms | -create | -join CODE [-pass P] [-chat TEXT]) [-nick N]");
                    return 0;
            }
        }

        if (selftest) return SelfTest();
        if (mode == "stats") return QueryStats();
        if (mode == "rooms") return QueryRooms();
        if (mode == "") { Console.WriteLine("需要 -selftest / -stats / -rooms / -create / -join CODE"); return 2; }
        return Single(mode == "create", joinCode, nick, pass, chat, _keepSec);
    }

    // ---------------- 连接与鉴权 ----------------
    class Conn
    {
        public TcpClient Tcp;
        public NetworkStream S;
        public ushort MyId;
        public string RoomCode = "";
        /// <summary>v3 握手后的链路密钥持有者（发业务消息要用它 SealLink）。</summary>
        public NetSecureSession Secure;
    }

    static Conn OpenConn(out string err)
    {
        err = null;
        try
        {
            var c = new TcpClient();
            c.Connect(_host, _port);
            c.NoDelay = true;
            var s = c.GetStream();
            // v3 握手：挑战 = [nonce8][中继 X25519 公钥32]，应答 = [客户端公钥32][HMAC32]
            // （旧版是 8 字节 nonce + 32 字节 HMAC，v3 引入 X25519 后已失效，别再照抄旧写法）
            var (type, payload) = Recv(s, 5000);
            if (type != NetProto.MsgAuthChallenge) { err = "期望鉴权挑战，收到 0x" + type.ToString("X2"); return null; }
            if (payload == null || payload.Length != 40) { err = "挑战长度异常（" + (payload == null ? "null" : payload.Length.ToString()) + "，期望 40）"; return null; }

            byte[] nonce = new byte[8];
            byte[] relayPub = new byte[32];
            Buffer.BlockCopy(payload, 0, nonce, 0, 8);
            Buffer.BlockCopy(payload, 8, relayPub, 0, 32);

            var sec = new NetSecureSession();
            sec.BeginHandshake();
            if (!sec.CompleteHandshake(relayPub, nonce)) { err = "X25519 密钥交换失败"; return null; }

            byte[] resp = new byte[64];
            Buffer.BlockCopy(sec.MyPublicKey, 0, resp, 0, 32);
            Buffer.BlockCopy(sec.AuthMac(nonce), 0, resp, 32, 32);
            Send(s, NetProto.MsgAuthResponse, resp);
            _secs[s] = sec;
            return new Conn { Tcp = c, S = s, Secure = sec };
        }
        catch (Exception ex) { err = ex.Message; return null; }
    }

    /// <summary>查询中继在线统计（M5）。用于部署后验证 / 日常看人数。</summary>
    static int QueryStats()
    {
        var c = OpenConn(out string err);
        if (c == null) { Console.WriteLine("连接失败: " + err); return 1; }
        try
        {
            SendSecure(c, NetProto.MsgStatsReq, new byte[0]);
            var (t, p) = Recv(c.S, 5000);
            if (t != NetProto.MsgStatsRes) { Console.WriteLine("期望 MsgStatsRes(0x23)，收到 0x" + t.ToString("X2")); return 1; }
            var r = new BitReader(p);
            uint live = r.ReadU32();
            uint today = r.ReadU32();
            Console.WriteLine($"当前在线: {live}");
            Console.WriteLine($"当日在线(唯一 IP): {today}");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("查询失败: " + ex.Message); return 1; }
        finally { try { c.Tcp.Close(); } catch { } }
    }

    /// <summary>列出服务器上存活的房间（用于排查“残留房间”/看当前有多少局在等）。</summary>
    static int QueryRooms()
    {
        var c = OpenConn(out string err);
        if (c == null) { Console.WriteLine("连接失败: " + err); return 1; }
        try
        {
            SendSecure(c, NetProto.MsgRoomListReq, new byte[0]);
            var (t, p) = Recv(c.S, 5000);
            if (t != NetProto.MsgRoomList) { Console.WriteLine("期望 MsgRoomList(0x1F)，收到 0x" + t.ToString("X2")); return 1; }

            var rooms = RoomListCodec.Read(p);
            Console.WriteLine($"线上房间数: {rooms.Count}（上限单帧 {RoomListCodec.MaxRoomsPerFrame}）");
            foreach (var r in rooms)
            {
                string ttl = r.TtlMinutesLeft == RoomListCodec.TtlUnlimited ? "不限时" : ("剩 " + r.TtlMinutesLeft + " 分");
                string flags = (r.Started ? " 对局中" : " 等待中")
                             + (r.NeedPass ? " 需口令" : "")
                             + (r.AllowCheats ? "" : " 禁作弊")
                             + (r.Joinable ? "" : " 不可加入");
                Console.WriteLine($"  {r.Code}  房主 {r.HostNick}  {r.Players}/{r.Max}  {ttl}{flags}");
            }
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("查询失败: " + ex.Message); return 1; }
        finally { try { c.Tcp.Close(); } catch { } }
    }

    /// <summary>发业务消息（鉴权后用链路加密，与服务端 TrySend 对称）。</summary>
    static void SendSecure(Conn c, byte type, byte[] payload)
    {
        var body = c.Secure.SealLink(type, payload);
        if (body == null) throw new Exception("链路密钥未就绪，无法发送");
        byte[] f = NetProto.Frame(type, NetProto.EncLink, body);
        c.S.Write(f, 0, f.Length);
        c.S.Flush();
    }

    static void Send(NetworkStream s, byte type, byte[] payload, byte enc = NetProto.EncPlain)
    {
        // v3 帧格式：[u32 total][type][enc][payload]（Frame 现在必须带 enc）
        byte[] f = NetProto.Frame(type, enc, payload);
        s.Write(f, 0, f.Length);
        s.Flush();
    }

    /// <summary>连接 → 安全上下文（Recv 解密回包用）。</summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<NetworkStream, NetSecureSession> _secs = new();

    static (byte type, byte[] payload) Recv(NetworkStream s, int timeoutMs)
    {
        s.ReadTimeout = timeoutMs;
        var head = new byte[4];
        ReadFull(s, head);
        uint total = (uint)(head[0] | (head[1] << 8) | (head[2] << 16) | (head[3] << 24));
        if (total < 1 || total > NetConstants.MaxFrame) throw new Exception("非法帧长度 " + total);
        var body = new byte[total];
        ReadFull(s, body);
        // v3 帧格式：[u32 total][type][enc][payload]，total = 1 + 1 + payload.Length
        // （旧版没有 enc 字节，所以以前是 total-1；加了链路加密后必须跳 2 字节）
        var raw = new byte[total - 2];
        if (raw.Length > 0) Array.Copy(body, 2, raw, 0, raw.Length);

        // 服务端回包走 SealLink，不解密会读到密文
        byte[] payload = raw;
        if (body[1] != NetProto.EncPlain && _secs.TryGetValue(s, out var sec))
        {
            var opened = sec.OpenLink(body[0], raw);
            if (opened != null) payload = opened;
        }
        return (body[0], payload);
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

    static bool TryRecv(NetworkStream s, int timeoutMs, out byte type, out byte[] payload)
    {
        try { var r = Recv(s, timeoutMs); type = r.type; payload = r.payload; return true; }
        catch { type = 0; payload = null; return false; }
    }

    static bool WaitFor(NetworkStream s, byte expected, int timeoutMs, out byte[] payload)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            if (!TryRecv(s, 600, out byte t, out byte[] p)) continue;
            if (t == expected) { payload = p; return true; }
        }
        payload = null;
        return false;
    }

    /// <summary>持续打印收到的非业务消息（后台线程）。</summary>
    static void Pump(NetworkStream s, string tag, int seconds, ref int chatCount)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (!TryRecv(s, 400, out byte t, out byte[] p)) continue;
            switch (t)
            {
                case NetProto.MsgChat:
                    Console.WriteLine("[" + tag + "][聊天] " + new BitReader(p).ReadStr());
                    chatCount++;
                    break;
                case NetProto.MsgPeerJoined:
                    Console.WriteLine("[" + tag + "] 玩家加入 id=" + new BitReader(p).ReadU16());
                    break;
                case NetProto.MsgPeerLeft:
                    Console.WriteLine("[" + tag + "] 玩家离开 id=" + new BitReader(p).ReadU16());
                    break;
                case NetProto.MsgRoomClosingSoon:
                    Console.WriteLine("[" + tag + "] 房间即将到期：" + p[0] + " 分钟后销毁");
                    break;
                case NetProto.MsgRoomClosed:
                    Console.WriteLine("[" + tag + "] 房间已销毁（原因 " + p[0] + "）");
                    return;
                case NetProto.MsgKick:
                    Console.WriteLine("[" + tag + "] 被移出房间（原因 " + p[0] + "）");
                    return;
                case NetProto.MsgRoomSettings:
                    var st = RoomSettingsCodec.Read(p);
                    Console.WriteLine("[" + tag + "] 房间设置：作弊=" + (st.AllowCheats ? "允许" : "禁止") +
                                      " 掩码=0x" + st.CheatMask.ToString("X8") + " TTL=" + st.TtlMinutes + "min" +
                                      " 口令=" + (string.IsNullOrEmpty(st.Password) ? "无" : "有"));
                    break;
                case NetProto.MsgError:
                    Console.WriteLine("[" + tag + "][错误] code=" + p[0] + " " + new BitReader(p).ReadStr());
                    break;
            }
        }
    }

    // ---------------- 单连接模式 ----------------
    static int Single(bool create, string code, string nick, string pass, string chat, int keepSec = 0)
    {
        var c = OpenConn(out string err);
        if (c == null) { Console.WriteLine("连接/鉴权失败: " + err); return 1; }
        Console.WriteLine("已连接并鉴权通过 " + _host + ":" + _port);
        try
        {
            if (create)
            {
                Send(c.S, NetProto.MsgRoomCreateReq, new BitWriter().WriteByte(4).WriteStr(nick.Length > 0 ? nick : "host").ToArray());
            }
            else
            {
                Send(c.S, NetProto.MsgRoomJoinReq,
                    new BitWriter().WriteStr(code).WriteStr(nick.Length > 0 ? nick : "guest").WriteStr(pass ?? "").ToArray());
            }

            bool sent = false;
            int chatCount = 0;
            double totalSec = keepSec > 0 ? keepSec : (chat.Length > 0 ? 6 : 5);
            var until = DateTime.UtcNow.AddSeconds(totalSec);
            var nextPing = DateTime.UtcNow;
            uint pingSeq = 0;
            while (DateTime.UtcNow < until)
            {
                // ★ 保持期间必须每 1 秒发一次 Ping，否则中继 15s 心跳超时会断开（房间随之被销毁）
                if (DateTime.UtcNow >= nextPing)
                {
                    nextPing = DateTime.UtcNow.AddSeconds(1);
                    try { Send(c.S, NetProto.MsgPing, new BitWriter().WriteU32(++pingSeq).ToArray()); } catch { }
                }
                if (!TryRecv(c.S, 300, out byte t, out byte[] p)) continue;
                if (t == NetProto.MsgRoomCreateRes && p[0] == 0)
                {
                    var r = new BitReader(p);
                    r.ReadByte(); uint roomId = r.ReadU32(); c.MyId = r.ReadU16(); c.RoomCode = r.ReadStr();
                    Console.WriteLine("建房成功：roomId=" + roomId + " myId=" + c.MyId + " 邀请码=" + c.RoomCode);
                }
                else if (t == NetProto.MsgRoomJoinRes && p[0] == 0)
                {
                    var r = new BitReader(p);
                    r.ReadByte(); c.MyId = r.ReadU16(); c.RoomCode = r.ReadStr();
                    Console.WriteLine("加入成功：房间=" + c.RoomCode + " myId=" + c.MyId);
                }
                else if (t == NetProto.MsgChat)
                {
                    Console.WriteLine("[聊天] " + new BitReader(p).ReadStr());
                    chatCount++;
                }
                else if (t == NetProto.MsgRoomSettings)
                {
                    var st = RoomSettingsCodec.Read(p);
                    Console.WriteLine("房间设置：作弊=" + (st.AllowCheats ? "允许" : "禁止") +
                                      " 掩码=0x" + st.CheatMask.ToString("X8") + " TTL=" + st.TtlMinutes + "min");
                }
                else if (t == NetProto.MsgPeerJoined)
                {
                    Console.WriteLine("有玩家加入 id=" + new BitReader(p).ReadU16());
                }
                else if (t == NetProto.MsgError)
                {
                    Console.WriteLine("[错误] code=" + p[0] + " " + new BitReader(p).ReadStr());
                }
                else if (t == NetProto.MsgRoomClosed)
                {
                    Console.WriteLine("房间已销毁（原因 " + p[0] + "）");
                    break;
                }
                else if (t == NetProto.MsgRoomList)
                {
                    var rl = RoomListCodec.Read(p);
                    Console.WriteLine("大厅列表：" + rl.Count + " 个房间");
                    for (int k = 0; k < rl.Count; k++)
                        Console.WriteLine("  " + rl[k].Code + "  " + rl[k].HostNick + "  " + rl[k].Players + "/" + rl[k].Max + (rl[k].AllowCheats ? "  作弊开" : "  作弊关"));
                }
                else if (t == NetProto.MsgPong) { /* 忽略心跳回显 */ }

                if (chat.Length > 0 && !sent && c.MyId != 0)
                {
                    Send(c.S, NetProto.MsgChat, new BitWriter().WriteStr(chat).ToArray());
                    sent = true;
                }
            }
            Console.WriteLine("结束（收到聊天 " + chatCount + " 条）");
            return 0;
        }
        finally { try { c.Tcp.Close(); } catch { } }
    }

    // ---------------- 自检模式 ----------------
    static int SelfTest()
    {
        int fail = 0;
        Console.WriteLine("=== 中继自检 " + _host + ":" + _port + " ===");

        var host = OpenConn(out string e1);
        if (host == null) { Console.WriteLine("房主连接失败: " + e1); return 1; }
        Console.WriteLine("[1/12] 房主连接 + 鉴权通过");

        Send(host.S, NetProto.MsgRoomCreateReq, new BitWriter().WriteByte(4).WriteStr("host").ToArray());
        if (!WaitFor(host.S, NetProto.MsgRoomCreateRes, 5000, out byte[] res) || res[0] != 0)
        { Console.WriteLine("建房失败"); return 1; }
        var rh = new BitReader(res);
        rh.ReadByte(); uint roomId = rh.ReadU32(); host.MyId = rh.ReadU16(); host.RoomCode = rh.ReadStr();
        Console.WriteLine("[2/12] 建房成功：roomId=" + roomId + " myId=" + host.MyId + " 邀请码=" + host.RoomCode);

        // 大厅：未进入房间的连接也能拿到房间列表（M2d）
        var lobby = OpenConn(out string e3);
        bool lobbyOk = false;
        if (lobby != null)
        {
            Send(lobby.S, NetProto.MsgRoomListReq, new byte[0]);
            if (WaitFor(lobby.S, NetProto.MsgRoomList, 4000, out byte[] rl))
            {
                var rooms = RoomListCodec.Read(rl);
                lobbyOk = rooms.Count == 1 && rooms[0].Code == host.RoomCode && rooms[0].Players == 1;
            }
            try { lobby.Tcp.Close(); } catch { }
        }
        if (lobbyOk) Console.WriteLine("[3/12] 大厅房间列表（未进房也能看到房间）✓");
        else { Console.WriteLine("[3/12] 大厅房间列表异常 ✗"); fail++; }

        bool gotSettings = WaitFor(host.S, NetProto.MsgRoomSettings, 3000, out byte[] defSet);
        var def = gotSettings ? RoomSettingsCodec.Read(defSet) : null;
        Console.WriteLine("[4/12] 默认房间设置：" + (def == null ? "未收到 ✗" : ("作弊=" + (def.AllowCheats ? "允许" : "禁止") + " TTL=" + def.TtlMinutes + "min ✓")));
        if (def == null) fail++;

        var guest = OpenConn(out string e2);
        if (guest == null) { Console.WriteLine("客机连接失败: " + e2); return 1; }
        Send(guest.S, NetProto.MsgRoomJoinReq, new BitWriter().WriteStr(host.RoomCode).WriteStr("guest").WriteStr("").ToArray());
        if (!WaitFor(guest.S, NetProto.MsgRoomJoinRes, 5000, out byte[] jr) || jr[0] != 0)
        { Console.WriteLine("加入失败"); return 1; }
        var rg = new BitReader(jr);
        rg.ReadByte(); guest.MyId = rg.ReadU16(); guest.RoomCode = rg.ReadStr();
        Console.WriteLine("[5/12] 客机加入成功 myId=" + guest.MyId);

        // 玩家名单（M2c：人数不再本地估算，含昵称/颜色槽/房主标记）
        bool gotList = WaitFor(guest.S, NetProto.MsgPlayerList, 4000, out byte[] lp);
        var peers = gotList ? PeerListCodec.Read(lp) : new System.Collections.Generic.List<PeerInfo>();
        bool listOk = peers.Count == 2 && peers[0].Color != peers[1].Color && (peers[0].IsHost ^ peers[1].IsHost);
        if (listOk) Console.WriteLine("[6/12] 玩家名单（2 人 + 颜色互异 + 房主标记）✓");
        else { Console.WriteLine("[6/12] 玩家名单异常 count=" + peers.Count + " ✗"); fail++; }

        bool peerJoined = WaitFor(host.S, NetProto.MsgPeerJoined, 4000, out byte[] pj);
        if (peerJoined && new BitReader(pj).ReadU16() == guest.MyId) Console.WriteLine("[7/12] 房主收到 PeerJoined ✓");
        else { Console.WriteLine("[7/12] 房主未收到 PeerJoined ✗"); fail++; }

        // 房主下发作弊总闸（关闭作弊 + 只允许无限阳光），客机应收到
        var st = new RoomSettings { MaxPlayers = 4, AllowCheats = false, CheatMask = CheatBits.Resource, TtlMinutes = 60, AllowLateJoin = true };
        Send(host.S, NetProto.MsgSetRoomSettings, RoomSettingsCodec.Write(st));
        WaitFor(host.S, NetProto.MsgRoomSettings, 3000, out _);
        bool gotSet = WaitFor(guest.S, NetProto.MsgRoomSettings, 4000, out byte[] gs);
        var applied = gotSet ? RoomSettingsCodec.Read(gs) : null;
        if (applied != null && !applied.AllowCheats && applied.CheatMask == CheatBits.Resource && applied.TtlMinutes == 60)
            Console.WriteLine("[8/12] 房主设置同步到客机（禁作弊 + 掩码 + TTL60）✓");
        else { Console.WriteLine("[8/12] 房主设置未正确同步 ✗"); fail++; }

        // 开战（M2c）：非房主被拒 / 房主广播 / 重复开被拒
        Send(guest.S, NetProto.MsgBattleStart, new BitWriter().WriteStr("Level1_1").ToArray());
        bool notHost = WaitFor(guest.S, NetProto.MsgError, 4000, out byte[] ne) && ne[0] == NetProto.ErrNotHost;

        Send(host.S, NetProto.MsgBattleStart, new BitWriter().WriteStr("Level1_1").ToArray());
        bool hStart = WaitFor(host.S, NetProto.MsgBattleStart, 4000, out byte[] hs2);
        bool hOk = false;
        if (hStart) { var rb = new BitReader(hs2); hOk = rb.ReadU32() == 1 && rb.ReadStr() == "Level1_1"; }
        bool gStart = WaitFor(guest.S, NetProto.MsgBattleStart, 4000, out byte[] gs2);
        bool gOk = false;
        if (gStart) { var rb2 = new BitReader(gs2); gOk = rb2.ReadU32() == 1 && rb2.ReadStr() == "Level1_1"; }

        Send(host.S, NetProto.MsgBattleStart, new BitWriter().WriteStr("Level1_1").ToArray());
        bool dup = WaitFor(host.S, NetProto.MsgError, 4000, out byte[] de) && de[0] == NetProto.ErrBattleStarted;

        if (notHost && hOk && gOk && dup) Console.WriteLine("[9/12] 开战广播 + 非房主被拒 + 重复开被拒 ✓");
        else { Console.WriteLine("[9/12] 开战异常 notHost=" + notHost + " host=" + hOk + " guest=" + gOk + " dup=" + dup + " ✗"); fail++; }

        Send(host.S, NetProto.MsgBattleEnd, new byte[0]);
        WaitFor(guest.S, NetProto.MsgBattleEnd, 4000, out _);

        // 联机光标：客机上报 → 房主收到带发送者 id 的转发
        Send(guest.S, NetProto.MsgCursor, new BitWriter().WriteU32(800).WriteU32(320).ToArray());
        bool cur = WaitFor(host.S, NetProto.MsgCursor, 4000, out byte[] cp) && new BitReader(cp).ReadU16() == guest.MyId;
        if (cur) Console.WriteLine("[10/12] 光标转发（带发送者 id）✓");
        else { Console.WriteLine("[10/12] 光标转发失败 ✗"); fail++; }

        // 聊天广播
        Send(guest.S, NetProto.MsgChat, new BitWriter().WriteStr("hello-from-guest").ToArray());
        bool h = WaitFor(host.S, NetProto.MsgChat, 4000, out byte[] hc) && new BitReader(hc).ReadStr() == "hello-from-guest";
        bool g = WaitFor(guest.S, NetProto.MsgChat, 4000, out byte[] gc2) && new BitReader(gc2).ReadStr() == "hello-from-guest";
        if (h && g) Console.WriteLine("[11/12] 聊天广播（双方都收到）✓");
        else { Console.WriteLine("[11/12] 聊天异常 host=" + h + " guest=" + g + " ✗"); fail++; }

        // 客机断开 → 房主收到 PeerLeft，名单回到 1 人
        guest.Tcp.Close();
        bool left = WaitFor(host.S, NetProto.MsgPeerLeft, 6000, out byte[] pl) && new BitReader(pl).ReadU16() == guest.MyId;
        bool list1 = WaitFor(host.S, NetProto.MsgPlayerList, 4000, out byte[] lp2) && PeerListCodec.Read(lp2).Count == 1;
        if (left && list1) Console.WriteLine("[12/12] 客机断开后房主收到 PeerLeft，名单回到 1 人 ✓");
        else { Console.WriteLine("[12/12] 断开处理异常 left=" + left + " list1=" + list1 + " ✗"); fail++; }

        host.Tcp.Close();
        Console.WriteLine(fail == 0 ? "=== 自检全部通过 ✓ ===" : ("=== 自检失败 " + fail + " 项 ✗ ==="));
        return fail == 0 ? 0 : 1;
    }
}
