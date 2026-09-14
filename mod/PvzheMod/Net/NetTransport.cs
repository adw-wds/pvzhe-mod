using System;
using System.Collections.Generic;
using Godot;

/// <summary>网络通道抽象：直连（房主 TcpServer / 客机连接）与中继（连中继 8231）共用同一接口。</summary>
public interface INetTransport
{
    bool IsConnected { get; }
    /// <summary>安全会话（握手状态 / 房间密钥 / 帧加解密）。</summary>
    NetSecureSession Secure { get; }
    void Send(byte type, byte[] payload);
    bool Poll();
    List<KeyValuePair<byte, byte[]>> Drain();
    void Close();
}

/// <summary>直连通道。注意：只能用 Godot 原生网络类（System.Net.Sockets 在 AOT 裁剪下 MissingMethodException）。</summary>
public class DirectTransport : INetTransport
{
    TcpServer _server;
    StreamPeerTcp _conn;
    readonly List<byte> _buf = new List<byte>(8192);
    readonly List<KeyValuePair<byte, byte[]>> _inbox = new List<KeyValuePair<byte, byte[]>>();

    public bool Listen(int port = NetConstants.DirectPort)
    {
        _server = new TcpServer();
        var err = _server.Listen((ushort)port, "*");
        if (err != Error.Ok) { NetLog.Warn("直连监听失败: " + err); return false; }
        NetLog.Info("直连监听 " + port + "（等待对方连接）");
        return true;
    }

    public bool ConnectToHost(string host, int port = NetConstants.DirectPort)
    {
        _conn = new StreamPeerTcp();
        var err = _conn.ConnectToHost(host, port);
        if (err != Error.Ok) { NetLog.Warn("直连连接失败: " + err); return false; }
        return true;
    }

    public bool IsConnected
    {
        get
        {
            try { return _conn != null && _conn.GetStatus() == StreamPeerSocket.Status.Connected; }
            catch { return false; }
        }
    }

    /// <summary>加密会话：直连通道用房间密钥做端到端加密。</summary>
    public NetSecureSession Secure { get; } = new NetSecureSession();

    public void Send(byte type, byte[] payload)
    {
        if (_conn == null) return;
        try
        {
            byte enc = NetSecureSession.EncFor(type);
            byte[] body = Secure.SealFrame(type, payload);
            if (body == null) { NetLog.Warn("直连发送被拒（密钥未就绪）type=0x" + type.ToString("X2")); return; }
            _conn.PutData(NetProto.Frame(type, enc, body));
        }
        catch (System.Exception ex) { NetLog.Warn("直连发送失败: " + ex.Message); }
    }

    public bool Poll()
    {
        bool got = false;
        if (_server != null)
        {
            while (_server.IsConnectionAvailable())
            {
                var c = _server.TakeConnection();
                if (c != null && _conn == null)
                {
                    _conn = c;
                    NetLog.Info("直连已建立（房主侧收到连接）");
                }
            }
        }
        if (_conn == null) return false;
        try
        {
            _conn.Poll();
            int avail = _conn.GetAvailableBytes();
            if (avail > 0)
            {
                var res = _conn.GetData(avail);   // Godot.Collections.Array: [0]=Error, [1]=Variant(bytes)
                if (res.Count >= 2 && (Error)(int)res[0].AsInt64() == Error.Ok)
                {
                    var data = res[1].AsByteArray();
                    for (int i = 0; i < data.Length; i++) _buf.Add(data[i]);
                    ParseBuffered();
                    got = true;
                }
            }
        }
        catch (System.Exception ex) { NetLog.Warn("直连读取异常: " + ex.Message); }
        return got;
    }

    void ParseBuffered()
    {
        while (NetProto.TryParseFrame(_buf.ToArray(), out byte t, out byte enc, out byte[] p, out int used))
        {
            _buf.RemoveRange(0, used);
            byte[] plain = Secure.OpenFrame(t, enc, p);
            if (plain == null) { NetLog.Warn("直连帧解密失败 type=0x" + t.ToString("X2") + " enc=" + enc); continue; }
            _inbox.Add(new KeyValuePair<byte, byte[]>(t, plain));
        }
    }

    public List<KeyValuePair<byte, byte[]>> Drain()
    {
        var r = new List<KeyValuePair<byte, byte[]>>(_inbox);
        _inbox.Clear();
        return r;
    }

    public void Close()
    {
        try { _conn?.DisconnectFromHost(); } catch { }
        _conn = null;
        try { _server?.Stop(); } catch { }
        _server = null;
        _buf.Clear();
        _inbox.Clear();
    }
}

/// <summary>中继通道：连接到 Go/C# 中继并加入房间；房间/信令/转发由中继完成。</summary>
public class RelayTransport : INetTransport
{
    StreamPeerTcp _conn;
    readonly string _addr;
    readonly int _port;
    readonly List<byte> _buf = new List<byte>(8192);
    readonly List<KeyValuePair<byte, byte[]>> _inbox = new List<KeyValuePair<byte, byte[]>>();
    public ushort MyPlayerId { get; private set; }
    /// <summary>诊断计数器（中继通道状态日志节流，每 120 帧一条）。</summary>
    static int _pollDiag;

    public RelayTransport(string addr, int port = NetConstants.RelayDefaultPort) { _addr = addr; _port = port; }

    public bool Connect()
    {
        _conn = new StreamPeerTcp();
        var err = _conn.ConnectToHost(_addr, _port);
        if (err != Error.Ok) { NetLog.Warn("连接中继失败(" + err + "): " + _addr + ":" + _port); return false; }
        NetLog.Info("正在连接中继 " + _addr + ":" + _port);
        return true;
    }

    public bool IsConnected
    {
        get
        {
            try { return _conn != null && _conn.GetStatus() == StreamPeerSocket.Status.Connected; }
            catch { return false; }
        }
    }

    /// <summary>加密会话：先与中继做 X25519 握手得到链路密钥，进房后再拿房间密钥做 E2E。</summary>
    public NetSecureSession Secure { get; } = new NetSecureSession();

    public void Send(byte type, byte[] payload)
    {
        if (_conn == null) return;
        try
        {
            byte enc = NetSecureSession.EncFor(type);
            byte[] body = Secure.SealFrame(type, payload);
            if (body == null) { NetLog.Warn("中继发送被拒（密钥未就绪）type=0x" + type.ToString("X2")); return; }
            _conn.PutData(PackFrame(type, enc, body));
        }
        catch (System.Exception ex) { NetLog.Warn("中继发送失败: " + ex.Message); }
    }

    /// <summary>帧打包 v3 [len][type][enc][payload]（内联版：不调其它类型方法，避开 AOT 运行时解析）。</summary>
    static byte[] PackFrame(byte type, byte enc, byte[] payload)
    {
        int total = 2 + (payload != null ? payload.Length : 0);
        var o = new byte[4 + total];
        o[0] = (byte)total; o[1] = (byte)(total >> 8); o[2] = (byte)(total >> 16); o[3] = (byte)(total >> 24);
        o[4] = type;
        o[5] = enc;
        if (payload != null && payload.Length > 0) System.Array.Copy(payload, 0, o, 6, payload.Length);
        return o;
    }

    /// <summary>从缓冲区取一帧 v3 [len][type][enc][payload]（内联版）。</summary>
    bool TryParseLocal(out byte type, out byte enc, out byte[] payload, out int used)
    {
        type = 0; enc = 0; payload = null; used = 0;
        int n = _buf.Count;
        if (n < 6) return false;
        uint total = (uint)(_buf[0] | (_buf[1] << 8) | (_buf[2] << 16) | (_buf[3] << 24));
        if (total < 2 || total > NetConstants.MaxFrame) { _buf.Clear(); return false; }
        if ((uint)n < 4u + total) return false;
        type = _buf[4];
        enc = _buf[5];
        int plen = (int)total - 2;
        payload = new byte[plen];
        for (int i = 0; i < plen; i++) payload[i] = _buf[6 + i];
        used = (int)(4 + total);
        return true;
    }

    /// <summary>鉴权哈希（内联副本：不依赖 NetCrypto 类型，避开 AOT 裁剪）。</summary>
    static byte[] KeyedHashLocal(string key, byte[] data)
    {
        unchecked
        {
            const ulong P1 = 0x100000001B3UL;
            const ulong P2 = 0x9E3779B97F4A7C15UL;
            ulong h1 = 0xcbf29ce484222325UL ^ 0x9E3779B97F4A7C15UL;
            ulong h2 = 0x84222325cbf29ce4UL ^ 0xBF58476D1CE4E5B9UL;

            byte[] kb = System.Text.Encoding.UTF8.GetBytes(key ?? "");
            for (int i = 0; i < kb.Length; i++)
            {
                h1 = (h1 ^ kb[i]) * P1;
                h2 = (h2 + kb[i]) * P1 + (h2 >> 7);
            }
            int n = data != null ? data.Length : 0;
            for (int i = 0; i < n; i++)
            {
                byte b = data[i];
                h1 = (h1 ^ b) * P1;
                h1 ^= h1 >> 29;
                h2 = (h2 + (ulong)(b + 1)) * P2;
                h2 ^= h2 >> 31;
                ulong t = h1;
                h1 = h2;
                h2 = t ^ (h1 << 13);
            }
            for (int r = 0; r < 8; r++)
            {
                h1 ^= h1 >> 33;
                h1 *= 0xFF51AFD7ED558CCDUL;
                h2 ^= h2 >> 29;
                h2 *= 0xC4CEB9FE1A85EC53UL;
                ulong t = h1 ^ h2;
                h2 ^= t >> 17;
                h1 += t;
            }
            var outb = new byte[8];
            for (int i = 0; i < 8; i++)
            {
                ulong v = (i < 4) ? h1 : h2;
                outb[i] = (byte)(v >> (8 * (i % 4)));
            }
            return outb;
        }
    }

    public bool Poll()
    {
        if (_conn == null) return false;
        try
        {
            _conn.Poll();
            int avail = _conn.GetAvailableBytes();

            // ★ 诊断：仅在【尚未鉴权】时每 120 帧打一次连接状态。
            //   鉴权成功即停止，避免正常联机时刷日志。
            //   用途：区分“TCP 没连上”与“连上了但收不到数据”。
            if (!Authenticated && ++_pollDiag % 120 == 1)
            {
                try
                {
                    NetLog.Warn("中继通道诊断: status=" + _conn.GetStatus() + " avail=" + avail +
                                " authed=" + Authenticated + " link=" + (Secure.Handshaken ? "有" : "无"));
                }
                catch { }
            }

            if (avail <= 0) return false;
            var res = _conn.GetData(avail);
            if (res.Count >= 2 && (Error)(int)res[0].AsInt64() == Error.Ok)
            {
                var data = res[1].AsByteArray();
                for (int i = 0; i < data.Length; i++) _buf.Add(data[i]);
                ParseBuffered();
                return true;
            }
        }
        catch (System.Exception ex) { NetLog.Warn("中继读取异常: " + ex.Message); }
        return false;
    }

    /// <summary>是否已通过中继鉴权（挑战响应）；未通过前不能发业务消息。</summary>
    public bool Authenticated { get; private set; }

    void ParseBuffered()
    {
        while (TryParseLocal(out byte t, out byte enc, out byte[] p, out int used))
        {
            _buf.RemoveRange(0, used);

            // ① 握手：中继发 [nonce8][serverPub32]
            if (t == NetProto.MsgAuthChallenge)
            {
                if (p.Length < 40)
                {
                    NetLog.Warn("中继握手数据不完整（需 40 字节，收到 " + p.Length + "）→ 服务端可能还是旧版 v2，无法建立加密连接");
                    Authenticated = false;
                    continue;
                }
                // ★ 必须包 try/catch：熵源/X25519 一旦抛异常，异常会一路上冒到 Poll 的 catch，
                //   日志被冲掉后就只剩“客户端莫名 reset”，极难定位。
                try
                {
                    var nonce = new byte[8];
                    var serverPub = new byte[32];
                    System.Array.Copy(p, 0, nonce, 0, 8);
                    System.Array.Copy(p, 8, serverPub, 0, 32);

                    Secure.BeginHandshake();
                    if (!Secure.CompleteHandshake(serverPub, nonce))
                    {
                        NetLog.Warn("与中继的 X25519 握手失败（公钥非法）");
                        continue;
                    }
                    var resp = new byte[64];
                    System.Array.Copy(Secure.MyPublicKey, 0, resp, 0, 32);
                    System.Array.Copy(Secure.AuthMac(nonce), 0, resp, 32, 32);
                    try { _conn?.PutData(PackFrame(NetProto.MsgAuthResponse, NetProto.EncPlain, resp)); } catch { }
                    Authenticated = true;
                    NetLog.Info("已通过中继鉴权（v3 加密链路，指纹 " + Secure.LinkFingerprint + "）");
                }
                catch (System.Exception ex)
                {
                    NetLog.Warn("握手异常（已捕获，连接作废）：" + ex.GetType().Name + " " + ex.Message);
                    Authenticated = false;
                }
                continue;
            }

            // ② 业务帧：按 enc 解密（认证失败一律丢弃，绝不入 inbox）
            byte[] plain = Secure.OpenFrame(t, enc, p);
            if (plain == null)
            {
                NetLog.Warn("帧解密/认证失败 type=0x" + t.ToString("X2") + " enc=" + enc + "（丢弃）");
                continue;
            }

            // ③ 记录自己的玩家 id（E2E nonce 靠它区分发送者）
            if (t == NetProto.MsgRoomCreateRes && plain.Length >= 7)
            {
                var r = new BitReader(plain);
                r.ReadByte();
                r.ReadU32();
                MyPlayerId = r.ReadU16();
                Secure.MyPlayerId = MyPlayerId;
            }
            else if (t == NetProto.MsgRoomJoinRes && plain.Length >= 3)
            {
                var r = new BitReader(plain);
                r.ReadByte();
                MyPlayerId = r.ReadU16();
                Secure.MyPlayerId = MyPlayerId;
            }

            _inbox.Add(new KeyValuePair<byte, byte[]>(t, plain));
        }
    }

    public List<KeyValuePair<byte, byte[]>> Drain()
    {
        var r = new List<KeyValuePair<byte, byte[]>>(_inbox);
        _inbox.Clear();
        return r;
    }

    public void Close()
    {
        try { _conn?.DisconnectFromHost(); } catch { }
        _conn = null;
        _buf.Clear();
        _inbox.Clear();
    }
}
