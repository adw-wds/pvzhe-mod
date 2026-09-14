using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace PvzheRelay
{
    /// <summary>
    /// 中继服务器：TcpListener + 每连接一个后台线程。
    /// 职责仅限：鉴权、房间/信令/转发、防刷量（限流/大小/IP/违规/心跳）、房主设置与房间 TTL。
    /// 不含任何游戏逻辑；日志不记录游戏数据包内容。
    /// </summary>
    public class RelayServer
    {
        /// <summary>单连接上下文。</summary>
        sealed class Conn
        {
            public TcpClient Tcp;
            public NetworkStream Stream;
            public Player Me = new Player();
            /// <summary>上次响应大厅列表请求的时刻（节流用）。</summary>
            public long LastRoomListMs;
            public string Ip = "";
            public bool Authed;
            public byte[] Nonce;
            public double Tokens = NetConstants.RateBurst;
            public long LastTokenMs;
            public long LastFrameMs;
            public int Violations;
            public long ViolationWindowStartMs;
            /// <summary>累计收到的帧数（诊断用，用于推算客户端发送速率）。</summary>
            public long Frames;
            /// <summary>最近一个被忽略的未知消息类型（仅记一次日志用）。</summary>
            public byte UnknownType;
            public volatile bool Closing;
            /// <summary>v3 加密会话（握手后持有链路密钥；房间密钥由房主在业务层分发，中继拿不到）。</summary>
            public NetSecureSession Secure = new NetSecureSession
            {
                SendPrefix = NetSecureSession.DirServerToClient,
                RecvPrefix = NetSecureSession.DirClientToServer,
            };
            /// <summary>客户端公钥（供房主包裹房间密钥时定位成员）。</summary>
            public byte[] ClientPub;
            /// <summary>建房/加房频率窗口（该连接线程独占，无需加锁）。</summary>
            public long RoomOpWindowMs;
            public int RoomOps;
        }

        public RoomManager Manager { get; } = new RoomManager();
        public int Port { get; }
        public bool Verbose { get; set; } = true;
        /// <summary>是否要求挑战响应鉴权（测试可关）。</summary>
        public bool RequireAuth { get; set; } = true;
        public int IdleTimeoutMs { get; set; } = NetConstants.HeartbeatTimeoutSec * 1000;
        /// <summary>测试用：&gt;0 时覆盖房间 TTL（秒）。</summary>
        public int TtlOverrideSeconds { get; set; } = -1;
        /// <summary>巡检间隔（心跳/TTL/冷却）；测试可调小。</summary>
        public int ScanIntervalMs { get; set; } = NetConstants.ScanIntervalMs;
        /// <summary>空房间回收宽限期（毫秒）。建房过程中房间会短暂为 0 人，必须避开。</summary>
        public int EmptyRoomGraceMs { get; set; } = 30_000;
        public long NowMs => Environment.TickCount64;

        readonly ConcurrentDictionary<string, int> _ipLive = new ConcurrentDictionary<string, int>();
        readonly ConcurrentDictionary<string, long> _ipCooldown = new ConcurrentDictionary<string, long>();
        /// <summary>同 IP 新建连接频率表（防止快速建连/断连刷 CPU）。</summary>
        readonly ConcurrentDictionary<string, ConnRate> _ipConnRate = new ConcurrentDictionary<string, ConnRate>();
        readonly ConcurrentDictionary<string, Conn> _conns = new ConcurrentDictionary<string, Conn>();
        /// <summary>当日出现过的唯一 IP（M5 在线统计）。同 IP 一天只累计一条 → 字典天然去重。</summary>
        readonly ConcurrentDictionary<string, byte> _todayIps = new ConcurrentDictionary<string, byte>();
        /// <summary>上次统计所属的“天”（DateTime.Today.Ticks），用于跨天清零。</summary>
        long _todayDay = -1;
        TcpListener _listener;
        volatile bool _running;
        Thread _scan;
        int _peak;

        public RelayServer(int port) { Port = port; }

        public int LiveConnections => _conns.Count;
        public int PeakConnections => _peak;

        /// <summary>当日在线（唯一 IP 数）。跨天自动清零。</summary>
        public int TodayUniqueIps
        {
            get
            {
                long day = DateTime.Today.Ticks;
                if (Volatile.Read(ref _todayDay) != day)
                    lock (_todayIps) { if (_todayDay != day) { _todayIps.Clear(); _todayDay = day; } }
                return _todayIps.Count;
            }
        }

        /// <summary>记一笔“这个 IP 今天来过”。同一 IP 重复调不会重复计数。</summary>
        void NoteIpToday(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return;
            long day = DateTime.Today.Ticks;
            if (Volatile.Read(ref _todayDay) != day)
                lock (_todayIps) { if (_todayDay != day) { _todayIps.Clear(); _todayDay = day; } }
            _todayIps.TryAdd(ip, 0);
        }

        public void Start()
        {
            _listener = new TcpListener(IPAddress.Any, Port);
            _listener.Start();
            _running = true;
            new Thread(AcceptLoop) { IsBackground = true, Name = "relay-accept" }.Start();
            _scan = new Thread(ScanLoop) { IsBackground = true, Name = "relay-scan" };
            _scan.Start();
            Log("中继监听 0.0.0.0:" + Port + "（帧上限 " + NetConstants.MaxFrame + "B，限流 " + NetConstants.RateLimitPerSec +
                "/s，每 IP 连接上限 " + NetConstants.MaxConnPerIp + "，心跳超时 " + NetConstants.HeartbeatTimeoutSec + "s，鉴权 " + (RequireAuth ? "开" : "关") + "）");
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            Log("中继已停止（峰值连接 " + _peak + "）");
        }

        // ================= 连接接入 =================
        void AcceptLoop()
        {
            while (_running)
            {
                TcpClient c;
                try { c = _listener.AcceptTcpClient(); }
                catch { return; }

                var ip = (c.Client.RemoteEndPoint as IPEndPoint)?.Address?.ToString() ?? "?";
                long now = NowMs;

                CleanupCaches(now);

                // 违规冷却中：直接拒绝
                if (_ipCooldown.TryGetValue(ip, out long until) && until > now)
                {
                    Log("拒绝连接（IP 冷却中）：" + ip);
                    try { c.Close(); } catch { }
                    continue;
                }

                // 全服连接上限（2GB 内存 + 每连接一线程的安全线）
                if (_conns.Count >= NetConstants.MaxTotalConn)
                {
                    Log("拒绝连接（全服连接已达上限 " + NetConstants.MaxTotalConn + "）：" + ip);
                    try { c.Close(); } catch { }
                    continue;
                }

                // 同 IP 新建连接频率
                if (!AllowNewConn(ip, now))
                {
                    try { c.Close(); } catch { }
                    continue;
                }

                // 同 IP 并发上限
                int live = _ipLive.AddOrUpdate(ip, 1, (_, v) => v + 1);
                if (live > NetConstants.MaxConnPerIp)
                {
                    _ipLive.AddOrUpdate(ip, 0, (_, v) => Math.Max(0, v - 1));
                    Log("拒绝连接（同 IP 并发超限 " + live + "）：" + ip);
                    try
                    {
                        var s = c.GetStream();
                        WriteFrame(s, NetProto.MsgError, NetProto.EncPlain, new BitWriter().WriteByte(NetProto.ErrIpLimit).WriteStr("同 IP 连接过多").ToArray());
                    }
                    catch { }
                    try { c.Close(); } catch { }
                    continue;
                }

                var conn = new Conn { Tcp = c, Stream = c.GetStream(), Ip = ip };
                conn.Me = new Player { Id = 0, Nick = "", Ip = ip };
                conn.Me.Send = (type, payload) => TrySend(conn, type, payload);
                conn.Me.SendRaw = (type, rawBody) => TrySendRaw(conn, type, rawBody);
                conn.LastTokenMs = now;
                conn.LastFrameMs = now;
                conn.ViolationWindowStartMs = now;
                if (_conns.TryAdd(ip + "#" + Guid.NewGuid().ToString("N").Substring(0, 6), conn) && _conns.Count > _peak) _peak = _conns.Count;
                // 当日在线统计：连接真正建立成功才算（被冷却/限流拒掉的不算）
                NoteIpToday(ip);

                new Thread(() => HandleClient(conn)) { IsBackground = true, Name = "relay-conn" }.Start();
            }
        }

        void HandleClient(Conn c)
        {
            try
            {
                c.Tcp.ReceiveTimeout = IdleTimeoutMs;
                c.Tcp.SendTimeout = IdleTimeoutMs;
                c.Tcp.NoDelay = true;

                if (RequireAuth)
                {
                    // v3：挑战 = [nonce8][中继X25519公钥32]，客户端据此派生链路密钥
                    c.Nonce = NetX25519.RandomBytes(8);
                    c.Secure.BeginHandshake();
                    var chal = new byte[40];
                    Array.Copy(c.Nonce, 0, chal, 0, 8);
                    Array.Copy(c.Secure.MyPublicKey, 0, chal, 8, 32);
                    WriteFrame(c.Stream, NetProto.MsgAuthChallenge, NetProto.EncPlain, chal);
                }
                else
                {
                    c.Authed = true;
                }

                while (_running && !c.Closing)
                {
                    if (!TryReadFrame(c.Stream, out byte type, out byte enc, out byte[] payload, out bool fatal))
                    {
                        if (fatal && !AddViolation(c, "非法帧/超长帧")) { }
                        break;
                    }
                    long now = NowMs;
                    c.LastFrameMs = now;
                    c.Frames++;

                    if (!AllowFrame(c, now))
                    {
                        if (!AddViolation(c, "发送过快")) break;
                        continue;   // 丢帧（惩罚）
                    }

                    // E2E 帧：中继没有房间密钥，校验归属后盲转发（内容一律不可读）
                    if (enc == NetProto.EncE2e)
                    {
                        if (!DispatchE2e(c, type, payload)) break;
                        continue;
                    }

                    // 其余帧必须能通过链路密钥认证，否则丢弃并记违规
                    byte[] plain = enc == NetProto.EncPlain ? payload : c.Secure.OpenLink(type, payload);
                    if (plain == null)
                    {
                        if (!AddViolation(c, "帧解密/认证失败")) break;
                        continue;
                    }
                    if (!Dispatch(c, type, plain)) break;
                }
            }
            catch (Exception ex)
            {
                if (_running) Log("连接结束 " + c.Ip + " : " + ex.Message);
            }
            finally
            {
                Cleanup(c);
            }
        }

        void Cleanup(Conn c)
        {
            try
            {
                if (c.Me.Id != 0)
                {
                    var room = Manager.RoomOf(c.Me);
                    var mpId = c.Me.Id; var nick = c.Me.Nick;
                    var (r, empty) = Manager.Leave(c.Me);
                    if (r != null && !empty)
                    {
                        r.BroadcastExcept(mpId, NetProto.MsgPeerLeft, new BitWriter().WriteU16(mpId).WriteStr("断开").ToArray());
                        r.BroadcastPlayerList();
                        Log("玩家 " + mpId + "(" + nick + ") 断开，房间 " + r.Code + " 剩 " + r.PlayerCount + " 人");
                    }
                    else if (r != null && empty)
                    {
                        Log("房间 " + r.Code + " 已空 → 销毁");
                    }
                }
            }
            catch { }
            try { c.Tcp?.Close(); } catch { }
            try { c.Stream?.Dispose(); } catch { }
            _ipLive.AddOrUpdate(c.Ip, 0, (_, v) => Math.Max(0, v - 1));
            foreach (var kv in _conns)
                if (ReferenceEquals(kv.Value, c)) { _conns.TryRemove(kv.Key, out _); break; }
        }

        // ================= 防刷量 =================

        /// <summary>同 IP 新建连接频率（超限 → 冷却该 IP）。</summary>
        bool AllowNewConn(string ip, long now)
        {
            var cr = _ipConnRate.GetOrAdd(ip, _ => new ConnRate { WindowStartMs = now });
            lock (cr)
            {
                if (now - cr.WindowStartMs > NetConstants.ConnRateWindowSec * 1000L)
                {
                    cr.WindowStartMs = now;
                    cr.Count = 0;
                }
                cr.Count++;
                if (cr.Count > NetConstants.MaxConnPerMinPerIp)
                {
                    _ipCooldown[ip] = now + NetConstants.IpCooldownSec * 1000L;
                    Log("新建连接过频（" + cr.Count + " 次/" + NetConstants.ConnRateWindowSec + "s）→ 冷却 " +
                        NetConstants.IpCooldownSec + "s：" + ip);
                    return false;
                }
            }
            return true;
        }

        /// <summary>同 IP 建房/加房频率。</summary>
        bool AllowRoomOp(Conn c)
        {
            long now = NowMs;
            if (now - c.RoomOpWindowMs > NetConstants.ConnRateWindowSec * 1000L)
            {
                c.RoomOpWindowMs = now;
                c.RoomOps = 0;
            }
            c.RoomOps++;
            if (c.RoomOps > NetConstants.MaxRoomOpsPerMinPerIp)
            {
                Log("建房/加房过频（" + c.RoomOps + " 次/" + NetConstants.ConnRateWindowSec + "s）：" + c.Ip);
                return false;
            }
            return true;
        }

        /// <summary>
        /// 端到端加密帧：中继没有房间密钥，只校验归属后原样转发。
        /// ★ 该帧体不是明文，绝不能按普通业务消息处理。
        /// </summary>
        bool DispatchE2e(Conn c, byte type, byte[] rawBody)
        {
            var me = c.Me;
            var room = me.Id != 0 ? Manager.RoomOf(me) : null;
            if (room == null)
            {
                if (!AddViolation(c, "未进房即发游戏数据")) return false;
                return true;
            }
            if (!NetSecureSession.IsBlindForwardType(type))
            {
                if (!AddViolation(c, "E2E 帧类型非法 0x" + type.ToString("X2"))) return false;
                return true;
            }
            // 未开战时【静默丢弃】而不记违规：
            // 光标/状态同步这类帧，客户端进房就会开始发，记违规会在 2 秒内累计 20 次
            // → 断线 + IP 冷却 300s（用户表现为“房间建了一瞬间就没了”）。
            // 丢弃只是省点带宽，滥用场景已由令牌桶限流覆盖。
            if (!room.Started)
            {
                return true;
            }
            // 归属校验：密文外的 senderId 必须等于发送者本人，
            // 否则可冒充他人身份污染其 nonce 重放窗口（拒绝服务）
            ushort sid = NetSecureSession.PeekSenderId(rawBody);
            if (sid != me.Id)
            {
                if (!AddViolation(c, "senderId 与连接不符")) return false;
                return true;
            }
            room.BroadcastRawExcept(me.Id, type, rawBody);
            return true;
        }

        /// <summary>清理过期的冷却/频率/计数记录，避免长期运行内存只增不减。</summary>
        void CleanupCaches(long now)
        {
            foreach (var kv in _ipCooldown)
                if (kv.Value <= now) _ipCooldown.TryRemove(kv.Key, out _);

            foreach (var kv in _ipLive)
                if (kv.Value <= 0) _ipLive.TryRemove(kv.Key, out _);

            foreach (var kv in _ipConnRate)
                if (now - kv.Value.WindowStartMs > NetConstants.ConnRateWindowSec * 1000L * 5)
                    _ipConnRate.TryRemove(kv.Key, out _);
        }

        /// <summary>同 IP 新建连接频率记录。</summary>
        sealed class ConnRate
        {
            public long WindowStartMs;
            public int Count;
        }

        bool AllowFrame(Conn c, long now)
        {
            double elapsed = Math.Max(0, now - c.LastTokenMs) / 1000.0;
            c.LastTokenMs = now;
            c.Tokens = Math.Min(NetConstants.RateBurst, c.Tokens + elapsed * NetConstants.RateLimitPerSec);
            if (c.Tokens < 1) return false;
            c.Tokens -= 1;
            return true;
        }

        /// <summary>累计违规；返回 false 表示应断开连接（并给 IP 上冷却）。</summary>
        bool AddViolation(Conn c, string reason)
        {
            long now = NowMs;
            if (now - c.ViolationWindowStartMs > NetConstants.ViolationWindowSec * 1000L)
            {
                c.ViolationWindowStartMs = now;
                c.Violations = 0;
            }
            c.Violations++;
            if (c.Violations > NetConstants.ViolationLimit)
            {
                _ipCooldown[c.Ip] = now + NetConstants.IpCooldownSec * 1000L;
                Log("违规超限 → 断开并冷却 " + NetConstants.IpCooldownSec + "s ：" + c.Ip + "（" + reason + "）");
                TrySend(c, NetProto.MsgKick, new BitWriter().WriteByte(NetProto.KickViolation).WriteStr("违规过多").ToArray());
                return false;
            }
            Log("违规 " + c.Violations + "/" + NetConstants.ViolationLimit + " " + c.Ip + "（" + reason + "）累计帧=" + c.Frames);
            return true;
        }

        /// <summary>按长度读一帧（阻塞读满）。fatal=true 表示帧非法（需断开）。</summary>
        bool TryReadFrame(NetworkStream s, out byte type, out byte enc, out byte[] payload, out bool fatal)
        {
            type = 0; enc = 0; payload = null; fatal = false;
            var head = new byte[4];
            if (!ReadFull(s, head, 4)) return false;
            uint total = (uint)(head[0] | (head[1] << 8) | (head[2] << 16) | (head[3] << 24));
            if (total < 2 || total > NetConstants.MaxFrame)
            {
                fatal = true;
                return false;
            }
            var body = new byte[total];
            if (!ReadFull(s, body, (int)total)) return false;
            type = body[0];
            enc = body[1];
            payload = new byte[total - 2];
            if (payload.Length > 0) Array.Copy(body, 2, payload, 0, payload.Length);
            return true;
        }

        static bool ReadFull(NetworkStream s, byte[] buf, int len)
        {
            int got = 0;
            while (got < len)
            {
                int n = s.Read(buf, got, len - got);
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }

        /// <summary>发送信令（链路加密）。密钥未就绪时静默丢弃（如握手前收到 Ping）。</summary>
        void TrySend(Conn c, byte type, byte[] payload)
        {
            try
            {
                var body = c.Secure.SealLink(type, payload);
                if (body == null) return;
                lock (c.Stream) WriteFrame(c.Stream, type, NetProto.EncLink, body);
            }
            catch { }
        }

        /// <summary>发送明文帧（仅握手阶段：挑战、鉴权失败提示、IP 超限提示）。</summary>
        void TrySendPlain(Conn c, byte type, byte[] payload)
        {
            try { lock (c.Stream) WriteFrame(c.Stream, type, NetProto.EncPlain, payload); } catch { }
        }

        /// <summary>盲转发 E2E 帧：原样写出，不修改任何字节。</summary>
        void TrySendRaw(Conn c, byte type, byte[] rawBody)
        {
            try { lock (c.Stream) WriteFrame(c.Stream, type, NetProto.EncE2e, rawBody); } catch { }
        }

        /// <summary>帧打包 v3：[len][type][enc][payload]（len = 2 + payload 长度）。</summary>
        static void WriteFrame(NetworkStream s, byte type, byte enc, byte[] payload)
        {
            int total = 2 + (payload != null ? payload.Length : 0);
            var f = new byte[4 + total];
            f[0] = (byte)total; f[1] = (byte)(total >> 8); f[2] = (byte)(total >> 16); f[3] = (byte)(total >> 24);
            f[4] = type;
            f[5] = enc;
            if (payload != null && payload.Length > 0) Array.Copy(payload, 0, f, 6, payload.Length);
            s.Write(f, 0, f.Length);
            s.Flush();
        }

        // ================= 分发 =================
        bool Dispatch(Conn c, byte type, byte[] body)
        {
            var me = c.Me;
            var r = new BitReader(body);

            if (RequireAuth && !c.Authed)
            {
                if (type == NetProto.MsgAuthResponse)
                {
                    // v3：payload = [clientPub32][HMAC-SHA256 32]
                    byte[] clientPub = r.ReadBytes(32);
                    byte[] mac = r.ReadBytes(32);
                    if (clientPub.Length != 32 || mac.Length != 32)
                    {
                        Log("握手数据不完整 " + c.Ip);
                        return false;
                    }
                    if (!c.Secure.CompleteHandshake(clientPub, c.Nonce) || !c.Secure.VerifyAuthMac(c.Nonce, mac))
                    {
                        Log("鉴权失败 " + c.Ip);
                        TrySendPlain(c, NetProto.MsgError, new BitWriter().WriteByte(NetProto.ErrAuth).WriteStr("鉴权失败").ToArray());
                        return false;
                    }
                    c.ClientPub = clientPub;
                    c.Authed = true;
                    c.Me.PublicKey = clientPub;   // 随玩家名单下发，供房主包裹房间密钥
                    Log("鉴权通过 " + c.Ip + "（加密链路指纹 " + c.Secure.LinkFingerprint + "）");
                    return true;
                }
                if (type == NetProto.MsgPing)
                {
                    // ★ 必须用 TrySendPlain：此刻链路密钥还没建立，
                    //   TrySend 内部的 SealLink 会返回 null → 消息被静默丢弃，客户端永远等不到 Pong。
                    TrySendPlain(c, NetProto.MsgPong, body);
                    return true;
                }
                Log("未鉴权即发业务消息（0x" + type.ToString("X2") + "）→ 断开 " + c.Ip);
                // 同上：这里也要发明文，否则客户端收不到「未鉴权」提示，只看到被断开
                TrySendPlain(c, NetProto.MsgError, new BitWriter().WriteByte(NetProto.ErrAuth).WriteStr("未鉴权").ToArray());
                return false;
            }

            switch (type)
            {
                case NetProto.MsgPing:
                    TrySend(c, NetProto.MsgPong, body);
                    return true;

                case NetProto.MsgStatsReq:
                    // 在线统计：中继自己就能答，不需要已进房（大厅页也要显示）
                    TrySend(c, NetProto.MsgStatsRes,
                        new BitWriter().WriteU32((uint)LiveConnections).WriteU32((uint)TodayUniqueIps).ToArray());
                    return true;

                case NetProto.MsgRoomCreateReq:
                {
                    int maxPlayers = r.ReadByte();
                    string nick = r.ReadStr();
                    if (!AllowRoomOp(c)) { SendErr(c, NetProto.ErrRateLimited, "建房过于频繁，请稍后再试"); return true; }
                    if (Manager.RoomOf(me) != null) { SendErr(c, NetProto.ErrNotInRoom, "已在房间内"); return true; }
                    me.Nick = string.IsNullOrEmpty(nick) ? "房主" : nick;
                    var room = Manager.Create(maxPlayers, me);
                    ApplyTtlOverride(room);
                    me.Send(NetProto.MsgRoomCreateRes, new BitWriter().WriteByte(0).WriteU32(room.Id).WriteU16(me.Id).WriteStr(room.Code).ToArray());
                    me.Send(NetProto.MsgRoomSettings, RoomSettingsCodec.Write(room.Settings, RoomSettingsFlags.Full));
                    room.BroadcastPlayerList();
                    Log("房间 " + room.Code + " 已创建（host=" + me.Id + " " + me.Nick + " max=" + room.MaxPlayers +
                        " ttl=" + room.Settings.TtlMinutes + "min 作弊=" + (room.Settings.AllowCheats ? "允许" : "禁止") + "）");
                    return true;
                }

                case NetProto.MsgRoomJoinReq:
                {
                    string code = r.ReadStr();
                    string nick = r.ReadStr();
                    string pass = r.ReadStr();
                    if (!AllowRoomOp(c)) { SendErr(c, NetProto.ErrRateLimited, "加房过于频繁，请稍后再试"); return true; }
                    if (Manager.RoomOf(me) != null) { SendErr(c, NetProto.ErrNotInRoom, "已在房间内"); return true; }
                    me.Nick = string.IsNullOrEmpty(nick) ? "玩家" : nick;
                    Room room;
                    try { room = Manager.Join(code, me, pass); }
                    catch (RelayException ex) { SendErr(c, ex.Code, ex.Message); return true; }
                    me.Send(NetProto.MsgRoomJoinRes, new BitWriter().WriteByte(0).WriteU16(me.Id).WriteStr(room.Code).ToArray());
                    room.BroadcastExcept(me.Id, NetProto.MsgPeerJoined, new BitWriter().WriteU16(me.Id).WriteStr(me.Nick).ToArray());
                    me.Send(NetProto.MsgRoomSettings, RoomSettingsCodec.Write(room.Settings, RoomSettingsFlags.Full));
                    room.BroadcastPlayerList();
                    // 已开战：把当前对局告知后加入者（客机据此进关）
                    if (room.Started)
                        me.Send(NetProto.MsgBattleStart, new BitWriter().WriteU32(room.BattleId).WriteStr(room.LevelKey).ToArray());
                    Log("玩家 " + me.Id + "(" + me.Nick + ") 加入房间 " + room.Code + "（共 " + room.PlayerCount + " 人）");
                    return true;
                }

                case NetProto.MsgRoomKeyWrap:
                {
                    // 房主分发房间密钥：payload 是「房主↔成员 ECDH 包裹」的密文，
                    // 中继既无房间密钥也无成员私钥，解开不了；只负责转发给房内其他成员。
                    var room = Manager.RoomOf(me);
                    if (room == null) { if (!AddViolation(c, "未进房发密钥包裹")) return false; return true; }
                    if (me.Id != room.HostId) { if (!AddViolation(c, "非房主发密钥包裹")) return false; return true; }
                    if (body.Length < 60 || body.Length > 256) { if (!AddViolation(c, "密钥包裹长度非法")) return false; return true; }
                    room.BroadcastExcept(me.Id, NetProto.MsgRoomKeyWrap, body);
                    return true;
                }

                case NetProto.MsgRoomLeave:
                {
                    if (me.Id == 0) return true;
                    var mpId = me.Id; var room = Manager.RoomOf(me);
                    var (_, empty) = Manager.Leave(me);
                    if (room != null)
                    {
                        room.BroadcastExcept(mpId, NetProto.MsgPeerLeft, new BitWriter().WriteU16(mpId).WriteStr("主动离开").ToArray());
                        room.BroadcastPlayerList();
                        Log("玩家 " + mpId + " 主动离开房间 " + room.Code + (empty ? "（房间已空 → 销毁）" : ""));
                    }
                    me.Id = 0;
                    return true;
                }

                case NetProto.MsgSetRoomSettings:
                {
                    var s = RoomSettingsCodec.Read(body);
                    Room room;
                    try { room = Manager.SetSettings(me, s); }
                    catch (RelayException ex) { SendErr(c, ex.Code, ex.Message); return true; }
                    ApplyTtlOverride(room);
                    byte[] outBody = RoomSettingsCodec.Write(room.Settings, RoomSettingsFlags.Full);
                    room.BroadcastExcept(0, NetProto.MsgRoomSettings, outBody);
                    Log("房间 " + room.Code + " 设置已更新（作弊=" + (room.Settings.AllowCheats ? "允许" : "禁止") +
                        " 掩码=0x" + room.Settings.CheatMask.ToString("X8") + " ttl=" + room.Settings.TtlMinutes +
                        "min 口令=" + (string.IsNullOrEmpty(room.Settings.Password) ? "无" : "有") +
                        " 中途加入=" + (room.Settings.AllowLateJoin ? "允许" : "禁止") + "）");
                    return true;
                }

                case NetProto.MsgSetFaction:
                {
                    // 对战模式分边（v4）。中继不另发应答：成功广播新名单，失败回 MsgError。
                    var fRoom = Manager.RoomOf(me);
                    if (fRoom == null) { SendErr(c, NetProto.ErrNotInRoom, "未加入房间"); return true; }
                    byte want = r.ReadByte();
                    if (want > NetFaction.Zombie) want = NetFaction.None;
                    if (!fRoom.Settings.BattleMode) { SendErr(c, NetProto.ErrNotBattleMode, "该房间不是对战模式"); return true; }
                    if (fRoom.Started) { SendErr(c, NetProto.ErrFactionLocked, "对局已开始，阵营已锁定"); return true; }

                    // 人数平衡：先把"自己"从原阵营摘掉再算，否则切边时自己会被算两次
                    if (fRoom.Settings.BalanceTeams && want != NetFaction.None)
                    {
                        int pc = fRoom.CountFaction(NetFaction.Plant);
                        int zc = fRoom.CountFaction(NetFaction.Zombie);
                        if (me.Faction == NetFaction.Plant) pc--;
                        else if (me.Faction == NetFaction.Zombie) zc--;
                        int targetAfter = (want == NetFaction.Plant ? pc : zc) + 1;
                        int otherAfter = (want == NetFaction.Plant ? zc : pc);
                        // 允许差 1：多的一方多 1 人可以，再多就必须换边
                        if (targetAfter > otherAfter + 1)
                        {
                            SendErr(c, NetProto.ErrFactionBalance,
                                want == NetFaction.Plant ? "植物方人数已满，请加入僵尸方" : "僵尸方人数已满，请加入植物方");
                            return true;
                        }
                    }

                    me.Faction = want;
                    fRoom.BroadcastPlayerList();
                    Log("玩家 " + me.Id + " 在房间 " + fRoom.Code + " 选择阵营：" + NetFaction.Name(want));
                    return true;
                }

                case NetProto.MsgRoomClosed:
                {
                    // 该消息由房主发出即为“解散房间”请求
                    var room = Manager.RoomOf(me);
                    if (room == null) { SendErr(c, NetProto.ErrNotInRoom, "未加入房间"); return true; }
                    if (me.Id != room.HostId) { if (!AddViolation(c, "非房主解散房间")) return false; return true; }
                    CloseRoom(room, NetProto.CloseHostClosed);
                    return true;
                }

                case NetProto.MsgKick:
                {
                    var room = Manager.RoomOf(me);
                    if (room == null) { SendErr(c, NetProto.ErrNotInRoom, "未加入房间"); return true; }
                    if (me.Id != room.HostId) { if (!AddViolation(c, "非房主踢人")) return false; return true; }
                    ushort target = r.ReadU16();
                    var victim = room.Find(target);
                    if (victim == null || victim.Id == me.Id) return true;
                    try { victim.Send?.Invoke(NetProto.MsgKick, new BitWriter().WriteByte(NetProto.KickByHost).WriteStr("被房主移出房间").ToArray()); } catch { }
                    room.BroadcastExcept(target, NetProto.MsgPeerLeft, new BitWriter().WriteU16(target).WriteStr("被踢出").ToArray());
                    Manager.Leave(victim);
                    room.BroadcastPlayerList();
                    Log("房主将玩家 " + target + " 移出房间 " + room.Code);
                    return true;
                }

                case NetProto.MsgBattleStart:
                {
                    string levelKey = r.ReadStr();
                    Room room;
                    try { room = Manager.StartBattle(me, levelKey); }
                    catch (RelayException ex) { SendErr(c, ex.Code, ex.Message); return true; }
                    room.BroadcastExcept(0, NetProto.MsgBattleStart,
                        new BitWriter().WriteU32(room.BattleId).WriteStr(room.LevelKey ?? "").ToArray());
                    Log("房间 " + room.Code + " 对局开始 battleId=" + room.BattleId + " 关卡=" +
                        (string.IsNullOrEmpty(room.LevelKey) ? "?" : room.LevelKey) + "（" + room.PlayerCount + " 人）");
                    return true;
                }

                case NetProto.MsgBattleEnd:
                {
                    Room room;
                    try { room = Manager.EndBattle(me); }
                    catch (RelayException ex) { SendErr(c, ex.Code, ex.Message); return true; }
                    room.BroadcastExcept(0, NetProto.MsgBattleEnd, new BitWriter().WriteU32(room.BattleId).ToArray());
                    Log("房间 " + room.Code + " 对局结束 battleId=" + room.BattleId);
                    return true;
                }

                case NetProto.MsgCursor:
                {
                    // 光标：客户端只发自己的坐标（x:i32,y:i32），中继补上发送者 id 后转发给其他人
                    var room = Manager.RoomOf(me);
                    if (room == null || room.PlayerCount < 2) return true;
                    byte[] xy = r.ReadBytes(body.Length - r.Position);
                    if (xy.Length < 8) return true;
                    room.BroadcastExcept(me.Id, NetProto.MsgCursor,
                        new BitWriter().WriteU16(me.Id).WriteBytes(xy).ToArray());
                    return true;
                }

                case NetProto.MsgStateSync:
                case NetProto.MsgEntitySnapshot:
                {
                    // 房主→客机：对局状态 / 实体快照。payload 自带语义，中继只做原样广播。
                    var room = Manager.RoomOf(me);
                    if (room == null || room.PlayerCount < 2) return true;
                    byte[] rest = r.ReadBytes(body.Length - r.Position);
                    room.BroadcastExcept(me.Id, type, rest);
                    return true;
                }

                case NetProto.MsgRoomListReq:
                {
                    // 大厅：列出所有房间（只含房间码/房主昵称/人数/状态，不含任何 IP）
                    long nowList = NowMs;
                    // 节流：直接忽略本次请求（★ 绝不要回空列表，否则客户端会把已有的大厅列表清空，
                    //   表现为“联机大厅看不到房间”）
                    if (nowList - c.LastRoomListMs < 1500) return true;
                    c.LastRoomListMs = nowList;
                    var list = new System.Collections.Generic.List<RoomInfo>();
                    foreach (var rm in Manager.AllRooms())
                    {
                        if (rm == null) continue;
                        var st = rm.Settings ?? new RoomSettings();
                        ushort left = RoomListCodec.TtlUnlimited;
                        if (rm.ExpiresAtMs > 0)
                        {
                            long ms = rm.ExpiresAtMs - nowList;
                            left = ms <= 0 ? (ushort)0 : (ushort)System.Math.Min(65534, ms / 60000);
                        }
                        list.Add(new RoomInfo
                        {
                            Code = rm.Code,
                            HostNick = HostNickOf(rm),
                            Players = (byte)rm.PlayerCount,
                            Max = (byte)rm.MaxPlayers,
                            Started = rm.Started,
                            NeedPass = !string.IsNullOrEmpty(st.Password),
                            AllowCheats = st.AllowCheats,
                            TtlMinutesLeft = left
                        });
                    }
                    list.Sort((a, b) => b.Players.CompareTo(a.Players));
                    TrySend(c, NetProto.MsgRoomList, RoomListCodec.Write(list));
                    Log("大厅列表请求 " + c.Ip + " → " + list.Count + " 个房间");
                    return true;
                }

                case NetProto.MsgSignalTo:
                {
                    ushort target = r.ReadU16();
                    byte[] rest = r.ReadBytes(body.Length - r.Position);
                    try { Manager.Signal(me, target, rest); }
                    catch (RelayException ex) { SendErr(c, ex.Code, ex.Message); }
                    return true;
                }

                case NetProto.MsgRelayData:
                case NetProto.MsgClientReport:
                case NetProto.MsgCorrectState:
                {
                    ushort target = r.ReadU16();
                    byte[] rest = r.ReadBytes(body.Length - r.Position);
                    try { Manager.Relay(me, target, rest); }
                    catch (RelayException ex) { SendErr(c, ex.Code, ex.Message); }
                    return true;
                }

                case NetProto.MsgChat:
                {
                    var room = Manager.RoomOf(me);
                    if (room == null) { SendErr(c, NetProto.ErrNotInRoom, "未加入房间"); return true; }
                    string text = r.ReadStr();
                    if (text.Length > NetConstants.MaxChatBytes) text = text.Substring(0, NetConstants.MaxChatBytes);
                    room.BroadcastExcept(0, NetProto.MsgChat, new BitWriter().WriteStr(text).ToArray());
                    Log("[聊天] " + me.Nick + ": " + text);
                    return true;
                }

                default:
                    // 未知消息：一律只忽略并低频记录，不计违规也不断开。
                    // （客户端版本可能比中继新；旧中继把新消息当违规会把正常玩家踢下线）
                    if (c.UnknownType != type)
                    {
                        c.UnknownType = type;
                        Log("忽略未知消息 0x" + type.ToString("X2") + " " + c.Ip);
                    }
                    return true;
            }
        }

        void ApplyTtlOverride(Room room)
        {
            if (TtlOverrideSeconds > 0 && room != null)
                room.ExpiresAtMs = NowMs + TtlOverrideSeconds * 1000L;
        }

        /// <summary>房间列表里的房主昵称（找不到时给占位，不泄露 IP）。</summary>
        static string HostNickOf(Room room)
        {
            try
            {
                var p = room.Find(room.HostId);
                if (p != null && !string.IsNullOrEmpty(p.Nick)) return PeerListCodec.Shorten(p.Nick);
            }
            catch { }
            return "房主";
        }

        void SendErr(Conn c, byte code, string msg)
        {
            TrySend(c, NetProto.MsgError, new BitWriter().WriteByte(code).WriteStr(msg).ToArray());
        }

        // ================= 巡检：心跳 / TTL / 冷却 =================
        void ScanLoop()
        {
            while (_running)
            {
                Thread.Sleep(ScanIntervalMs);
                if (!_running) return;
                long now = NowMs;

                // 1) 心跳超时
                foreach (var kv in _conns)
                {
                    var c = kv.Value;
                    if (now - c.LastFrameMs > IdleTimeoutMs)
                    {
                        Log("心跳超时（" + ((now - c.LastFrameMs) / 1000) + "s 无数据）→ 断开 " + c.Ip);
                        c.Closing = true;
                        try { c.Tcp.Close(); } catch { }
                    }
                }

                // 2) IP 冷却清理
                foreach (var kv in _ipCooldown)
                    if (kv.Value <= now) _ipCooldown.TryRemove(kv.Key, out _);

                // 3) 房间 TTL
                foreach (var room in Manager.AllRooms())
                {
                    if (room.ExpiresAtMs <= 0) continue;
                    long left = room.ExpiresAtMs - now;
                    if (left <= NetConstants.RoomClosingSoonSec * 1000L && !room.ClosingSoonSent)
                    {
                        room.ClosingSoonSent = true;
                        int mins = (int)Math.Max(1, left / 60000);
                        room.BroadcastExcept(0, NetProto.MsgRoomClosingSoon, new BitWriter().WriteByte((byte)mins).ToArray());
                        Log("房间 " + room.Code + " 将在 " + mins + " 分钟后销毁（已提醒）");
                    }
                    if (left <= 0) CloseRoom(room, NetProto.CloseExpired);
                }

                // 4) 空房间兜底回收（防“幽灵房间”堆积）
                //    正常路径：RoomManager.Leave 在房间变空时会立刻把房间移出 _rooms。
                //    这里兜的是异常路径——连接处理中途抛异常、Player.Id 被清零导致
                //    Leave 找不到所属房间等，会让房间永远挂在房间列表里占码位。
                //    留 30 秒宽限，避免建房过程中（房间已注册、host 还没加完）被误回收。
                foreach (var room in Manager.AllRooms())
                {
                    if (room.PlayerCount > 0) continue;
                    if (now - room.CreatedAtMs < EmptyRoomGraceMs) continue;
                    Log("回收空房间 " + room.Code + "（无成员，已存在 " +
                        ((now - room.CreatedAtMs) / 1000) + "s）");
                    CloseRoom(room, NetProto.CloseExpired);
                }
            }
        }

        void CloseRoom(Room room, byte reason)
        {
            if (room == null) return;
            var closed = Manager.Close(room.Code);
            if (closed == null) return;
            var body = new BitWriter().WriteByte(reason).WriteStr(reason == NetProto.CloseExpired ? "房间已到期" : "房主已解散房间").ToArray();
            closed.BroadcastExcept(0, NetProto.MsgRoomClosed, body);
            Log("房间 " + closed.Code + " 已销毁（原因 " + reason + "，成员 " + closed.PlayerCount + "）");
            Player[] ps;
            lock (closed.Sync) ps = closed.Players.ToArray();
            foreach (var p in ps)
            {
                try { p.Id = 0; } catch { }
            }
        }

        void Log(string s)
        {
            if (!Verbose) return;
            Console.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s);
            Console.Out.Flush();
        }
    }
}
