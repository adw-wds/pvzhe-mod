using System;
using System.Net.Sockets;
using System.Text;

/// <summary>
/// 中继防护测试（对自有服务器做安全验证）：限流、鉴权、非法帧、E2E 归属校验。
/// 用法：dotnet run -c Release [host] [port]
/// 仅用于测试自己部署的中继。
/// </summary>
static class AttackTest
{
    static int _pass, _fail;
    static string Host = "127.0.0.1";
    static int Port = 8231;

    static void Ok(string name, bool cond, string extra = "")
    {
        if (cond) { _pass++; Console.WriteLine("  PASS  " + name); }
        else { _fail++; Console.WriteLine("  FAIL  " + name + (extra.Length > 0 ? "  [" + extra + "]" : "")); }
    }

    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    static TcpClient Connect(out NetworkStream s, int timeoutMs = 4000)
    {
        var c = new TcpClient();
        c.ReceiveTimeout = timeoutMs;
        c.SendTimeout = timeoutMs;
        c.Connect(Host, Port);
        c.NoDelay = true;
        s = c.GetStream();
        return c;
    }

    /// <summary>v3 帧：[len:u32][type][enc][payload]</summary>
    static void SendRaw(NetworkStream s, byte type, byte enc, byte[] payload)
    {
        int total = 2 + (payload != null ? payload.Length : 0);
        var f = new byte[4 + total];
        f[0] = (byte)total; f[1] = (byte)(total >> 8); f[2] = (byte)(total >> 16); f[3] = (byte)(total >> 24);
        f[4] = type; f[5] = enc;
        if (payload != null && payload.Length > 0) Array.Copy(payload, 0, f, 6, payload.Length);
        s.Write(f, 0, f.Length);
        s.Flush();
    }

    /// <summary>读一帧；连接被断开或超时返回 false。</summary>
    static bool ReadFrame(NetworkStream s, out byte type, out byte enc, out byte[] payload)
    {
        type = 0; enc = 0; payload = null;
        try
        {
            var head = new byte[4];
            if (!ReadFull(s, head, 4)) return false;
            uint total = (uint)(head[0] | (head[1] << 8) | (head[2] << 16) | (head[3] << 24));
            if (total < 2 || total > 4096) return false;
            var body = new byte[total];
            if (!ReadFull(s, body, (int)total)) return false;
            type = body[0]; enc = body[1];
            payload = new byte[total - 2];
            if (payload.Length > 0) Array.Copy(body, 2, payload, 0, payload.Length);
            return true;
        }
        catch { return false; }
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

    /// <summary>正常完成 v3 握手；失败返回 false。</summary>
    static bool Handshake(NetworkStream s, out NetSecureSession sec)
    {
        sec = null;
        if (!ReadFrame(s, out byte t, out byte enc, out byte[] p)) return false;
        if (t != 0x00 || p.Length < 40) return false;

        var nonce = new byte[8];
        var serverPub = new byte[32];
        Array.Copy(p, 0, nonce, 0, 8);
        Array.Copy(p, 8, serverPub, 0, 32);

        sec = new NetSecureSession();
        sec.BeginHandshake();
        if (!sec.CompleteHandshake(serverPub, nonce)) return false;

        var resp = new byte[64];
        Array.Copy(sec.MyPublicKey, 0, resp, 0, 32);
        Array.Copy(sec.AuthMac(nonce), 0, resp, 32, 32);
        SendRaw(s, 0x0F, 0, resp);
        return true;
    }

    static void Main(string[] args)
    {
        if (args.Length > 0) Host = args[0];
        if (args.Length > 1) Port = int.Parse(args[1]);

        Console.WriteLine("===== 中继防护测试 " + Host + ":" + Port + " =====");

        TestUnauthedBusiness();
        TestMalformedFrames();
        TestBadMac();
        TestE2eForgery();
        TestFullRoomFlow();
        TestPreBattleFrames();
        TestConnFlood();

        Console.WriteLine();
        Console.WriteLine("============================");
        Console.WriteLine("PASS=" + _pass + "  FAIL=" + _fail);
        Environment.Exit(_fail == 0 ? 0 : 1);
    }

    /// <summary>未鉴权就发业务消息：必须被断开。</summary>
    static void TestUnauthedBusiness()
    {
        Console.WriteLine("[未鉴权业务消息]");
        try
        {
            var c = Connect(out var s);
            ReadFrame(s, out _, out _, out _);                        // 收挑战
            SendRaw(s, 0x01, 0, new byte[] { 4 });                    // 直接建房
            bool closed = !ReadFrame(s, out byte t, out _, out _);
            Ok("未鉴权建房被断开", closed, closed ? "" : "服务端仍可通信");
            try { c.Close(); } catch { }
        }
        catch (Exception ex) { Ok("未鉴权建房被断开", false, ex.Message); }
    }

    /// <summary>畸形/超长帧：必须被断开，且计入违规。</summary>
    static void TestMalformedFrames()
    {
        Console.WriteLine("[畸形帧]");
        // len = 0
        try
        {
            var c = Connect(out var s);
            ReadFrame(s, out _, out _, out _);
            s.Write(new byte[] { 0, 0, 0, 0 }, 0, 4);
            s.Flush();
            bool closed = !ReadFrame(s, out _, out _, out _);
            Ok("len=0 被断开", closed);
            try { c.Close(); } catch { }
        }
        catch (Exception ex) { Ok("len=0 被断开", false, ex.Message); }

        // len 超过 MaxFrame(4096)
        try
        {
            var c = Connect(out var s);
            ReadFrame(s, out _, out _, out _);
            uint bad = 999999;
            s.Write(new byte[] { (byte)bad, (byte)(bad >> 8), (byte)(bad >> 16), (byte)(bad >> 24) }, 0, 4);
            s.Flush();
            bool closed = !ReadFrame(s, out _, out _, out _);
            Ok("超长帧被断开", closed);
            try { c.Close(); } catch { }
        }
        catch (Exception ex) { Ok("超长帧被断开", false, ex.Message); }
    }

    /// <summary>握手后发错误的 MAC：必须被拒绝。</summary>
    static void TestBadMac()
    {
        Console.WriteLine("[鉴权]");
        try
        {
            var c = Connect(out var s);
            ReadFrame(s, out _, out _, out var chal);
            var nonce = new byte[8];
            Array.Copy(chal, 0, nonce, 0, 8);
            var serverPub = new byte[32];
            Array.Copy(chal, 8, serverPub, 0, 32);

            var sec = new NetSecureSession();
            sec.BeginHandshake();
            sec.CompleteHandshake(serverPub, nonce);
            var resp = new byte[64];
            Array.Copy(sec.MyPublicKey, 0, resp, 0, 32);
            Array.Copy(sec.AuthMac(nonce), 0, resp, 32, 32);
            resp[40] ^= 0xFF;                                          // 破坏 MAC
            SendRaw(s, 0x0F, 0, resp);
            // 中继会先回 MsgError 再断开，因此「读到错误帧」与「连接被断」都算拒绝
            bool got = ReadFrame(s, out byte et, out _, out _);
            bool refused = !got || et == 0x7F;
            Ok("错误 MAC 被拒绝", refused, "got=" + got + " type=0x" + et.ToString("X2"));
            try { c.Close(); } catch { }
        }
        catch (Exception ex) { Ok("错误 MAC 被拒绝", false, ex.Message); }

        // 正确 MAC 应通过（能收到后续响应）
        try
        {
            var c = Connect(out var s);
            bool hs = Handshake(s, out var sec);
            // MsgPing 必须真的用链路密钥加密后再发（发明文会被判认证失败而断开）
            var sealedPing = sec.SealLink(NetProto.MsgPing, new byte[0]);
            SendRaw(s, NetProto.MsgPing, NetProto.EncLink, sealedPing);
            bool got = ReadFrame(s, out byte t, out byte enc, out byte[] p);
            bool ok = hs && got && t == NetProto.MsgPong && enc == NetProto.EncLink
                      && sec.OpenLink(t, p) != null;
            Ok("正确 MAC 通过并能收发加密帧", ok,
                ok ? "" : ("got=" + got + " type=0x" + t.ToString("X2") + " enc=" + enc));
            try { c.Close(); } catch { }
        }
        catch (Exception ex) { Ok("正确 MAC 通过并能收发加密帧", false, ex.Message); }
    }

    /// <summary>E2E 帧归属校验：未进房发 E2E、伪造 senderId，都必须被罚。</summary>
    static void TestE2eForgery()
    {
        Console.WriteLine("[E2E 归属校验]");
        try
        {
            var c = Connect(out var s);
            if (!Handshake(s, out var sec)) { Ok("未进房发 E2E 被罚", false, "握手失败"); return; }
            // 伪造一个 E2E 帧体：[senderId=7][seq][假密文]
            var body = new byte[10 + 16 + 4];
            body[0] = 0; body[1] = 7;
            for (int i = 2; i < body.Length; i++) body[i] = 0xAB;
            SendRaw(s, 0x18, 2, body);                                  // MsgStateSync + EncE2e
            bool closed = !ReadFrame(s, out _, out _, out _);
            Ok("未进房/伪造 senderId 的 E2E 帧被罚", closed);
            try { c.Close(); } catch { }
        }
        catch (Exception ex) { Ok("未进房/伪造 senderId 的 E2E 帧被罚", false, ex.Message); }
    }

    /// <summary>等一个指定类型的消息并解密（跳过其它消息）。</summary>
    static byte[] WaitFor(NetworkStream s, NetSecureSession sec, byte wantType, int maxFrames = 40)
    {
        for (int i = 0; i < maxFrames; i++)
        {
            if (!ReadFrame(s, out byte t, out byte enc, out byte[] p)) return null;
            if (t != wantType) continue;
            if (enc == 0) return p;
            return sec.OpenFrame(t, enc, p);
        }
        return null;
    }

    /// <summary>
    /// 双客户端全流程：建房 → 加房 → 公钥下发 → 房间密钥分发 → 开战 → E2E 双向交换。
    /// 这条链只要断一环，表现就是“能进房能开战，但对局内没有任何同步”。
    /// </summary>
    static void TestFullRoomFlow()
    {
        Console.WriteLine("[双客户端全流程 · 房间密钥 + E2E]");
        try
        {
            // ---- A 建房 ----
            var ca = Connect(out var sa);
            if (!Handshake(sa, out var secA)) { Ok("A 握手", false, "握手失败"); return; }
            var cw = new BitWriter(); cw.WriteByte(4); cw.WriteStr("HostA");
            SendRaw(sa, NetProto.MsgRoomCreateReq, NetProto.EncLink,
                    secA.SealLink(NetProto.MsgRoomCreateReq, cw.ToArray()));

            var resA = WaitFor(sa, secA, NetProto.MsgRoomCreateRes);
            if (resA == null) { Ok("A 建房成功", false, "未收到回复"); return; }
            var ra = new BitReader(resA);
            byte okA = ra.ReadByte(); ra.ReadU32();
            ushort idA = ra.ReadU16(); string code = ra.ReadStr();
            secA.MyPlayerId = idA;
            Ok("A 建房成功", okA == 0 && code.Length > 0, "code=" + code + " id=" + idA);
            WaitFor(sa, secA, NetProto.MsgPlayerList);

            // ---- B 加房 ----
            var cb = Connect(out var sb);
            if (!Handshake(sb, out var secB)) { Ok("B 握手", false, "握手失败"); return; }
            var jw = new BitWriter(); jw.WriteStr(code); jw.WriteStr("ClientB"); jw.WriteStr("");
            SendRaw(sb, NetProto.MsgRoomJoinReq, NetProto.EncLink,
                    secB.SealLink(NetProto.MsgRoomJoinReq, jw.ToArray()));

            var resB = WaitFor(sb, secB, NetProto.MsgRoomJoinRes);
            if (resB == null) { Ok("B 加房成功", false, "未收到回复"); return; }
            var rb = new BitReader(resB);
            byte okB = rb.ReadByte();
            ushort idB = rb.ReadU16();
            secB.MyPlayerId = idB;
            Ok("B 加房成功", okB == 0 && idB != 0, "id=" + idB);

            // ---- ★ 修复点：名单必须带公钥 ----
            var plA = WaitFor(sa, secA, NetProto.MsgPlayerList);
            byte[] pubB = null;
            if (plA != null)
                foreach (var p in PeerListCodec.Read(plA))
                    if (p.Id == idB) pubB = p.PublicKey;
            Ok("A 的名单含 B 的公钥（修复点）", pubB != null && pubB.Length == 32,
                pubB == null ? "PublicKey 缺失 → 密钥分发必断" : "");

            var plB = WaitFor(sb, secB, NetProto.MsgPlayerList);
            byte[] pubA = null;
            if (plB != null)
                foreach (var p in PeerListCodec.Read(plB))
                    if (p.IsHost) pubA = p.PublicKey;
            Ok("B 的名单含房主公钥", pubA != null && pubA.Length == 32);

            if (pubA == null || pubB == null) { try { ca.Close(); cb.Close(); } catch { } return; }

            // ---- A 包裹房间密钥并广播 ----
            secA.CreateRoomKey();
            var wrapped = secA.WrapRoomKeyFor(pubB);
            Ok("A 包裹房间密钥（60 字节）", wrapped != null && wrapped.Length == 60);
            SendRaw(sa, NetProto.MsgRoomKeyWrap, NetProto.EncLink,
                    secA.SealLink(NetProto.MsgRoomKeyWrap, wrapped));

            var gotWrap = WaitFor(sb, secB, NetProto.MsgRoomKeyWrap);
            Ok("B 收到密钥包裹", gotWrap != null && gotWrap.Length == 60);
            bool unwrapped = gotWrap != null && secB.UnwrapRoomKeyFrom(pubA, gotWrap);
            Ok("B 解包房间密钥成功", unwrapped);
            Ok("双方指纹一致", unwrapped && secA.RoomFingerprint == secB.RoomFingerprint,
                "A=" + secA.RoomFingerprint + " B=" + secB.RoomFingerprint);

            // ---- 开战（★ 客户端只发关卡字符串；battleId 由中继分配后回填）----
            var bs = new BitWriter(); bs.WriteStr("Level1_1");
            SendRaw(sa, NetProto.MsgBattleStart, NetProto.EncLink,
                    secA.SealLink(NetProto.MsgBattleStart, bs.ToArray()));
            var gotBattle = WaitFor(sb, secB, NetProto.MsgBattleStart);
            bool battleOk = false;
            string lv = "";
            if (gotBattle != null)
            {
                var br = new BitReader(gotBattle);
                uint bid = br.ReadU32();
                lv = br.ReadStr();
                battleOk = bid > 0 && lv == "Level1_1";
            }
            Ok("B 收到开战消息（battleId + 关卡标识）", battleOk, "关卡=" + lv);

            // ---- E2E 双向 ----
            var payload = Utf8("阳光=150 波次=3 僵尸=12");
            var e2e = secA.SealE2e(idA, NetProto.MsgStateSync, payload);
            Ok("A 能发 E2E 快照（房间密钥已就绪）", e2e != null, e2e == null ? "seal 返回 null" : "");
            if (e2e == null) { try { ca.Close(); cb.Close(); } catch { } return; }
            SendRaw(sa, NetProto.MsgStateSync, NetProto.EncE2e, e2e);
            var gotSync = WaitFor(sb, secB, NetProto.MsgStateSync);
            Ok("B 解密 A 的 E2E 快照", gotSync != null && NetSha256.Hex(gotSync) == NetSha256.Hex(payload),
                gotSync == null ? "解不开" : "");

            var cur = secB.SealE2e(idB, NetProto.MsgCursor, Utf8("B-cursor"));
            SendRaw(sb, NetProto.MsgCursor, NetProto.EncE2e, cur);
            var gotCur = WaitFor(sa, secA, NetProto.MsgCursor);
            Ok("A 解密 B 的 E2E 光标", gotCur != null && NetSha256.Hex(gotCur) == NetSha256.Hex(Utf8("B-cursor")));

            try { ca.Close(); } catch { }
            try { cb.Close(); } catch { }
        }
        catch (Exception ex) { Ok("双客户端全流程", false, ex.Message); }
    }

    /// <summary>
    /// 回归：进房但**未开战**时发游戏数据帧（光标会每 100ms 发一次）绝不能因此被断开。
    /// 曾经的 bug：中继把它计成“未开战即发游戏数据”违规 → 2 秒内累计 20 次
    /// → 断线 + IP 冷却 300s → 用户表现“房间建了一瞬间就没了”。
    /// </summary>
    static void TestPreBattleFrames()
    {
        Console.WriteLine("[回归 · 未开战发游戏数据不应断线]");
        try
        {
            var ca = Connect(out var sa);
            if (!Handshake(sa, out var secA)) { Ok("A 握手", false, "握手失败"); return; }
            var cw = new BitWriter(); cw.WriteByte(4); cw.WriteStr("PreA");
            SendRaw(sa, NetProto.MsgRoomCreateReq, NetProto.EncLink,
                    secA.SealLink(NetProto.MsgRoomCreateReq, cw.ToArray()));
            var resA = WaitFor(sa, secA, NetProto.MsgRoomCreateRes);
            if (resA == null) { Ok("建房", false, "未收到回复"); return; }
            var ra = new BitReader(resA); ra.ReadByte(); ra.ReadU32();
            ushort idA = ra.ReadU16();
            secA.MyPlayerId = idA;
            secA.CreateRoomKey();
            WaitFor(sa, secA, NetProto.MsgPlayerList);

            // 未开战：连发 30 个光标帧（旧逻辑会在第 20 个直接踢人）
            bool sealedOk = true;
            for (int i = 0; i < 30; i++)
            {
                var cur = secA.SealE2e(idA, NetProto.MsgCursor, Utf8("pre-" + i));
                if (cur == null) { sealedOk = false; break; }
                SendRaw(sa, NetProto.MsgCursor, NetProto.EncE2e, cur);
            }
            // 再用一次 Ping 探测连接是否还活着
            SendRaw(sa, NetProto.MsgPing, NetProto.EncLink, secA.SealLink(NetProto.MsgPing, new byte[0]));
            bool alive = WaitFor(sa, secA, NetProto.MsgPong) != null;
            Ok("未开战连发 30 帧后仍在连接中", sealedOk && alive, alive ? "" : "连接已被断开");
            try { ca.Close(); } catch { }
        }
        catch (Exception ex) { Ok("未开战发游戏数据不应断线", false, ex.Message); }
    }

    /// <summary>连接洪水：同 IP 每 60s 新建连接上限（默认 15）应拦截。</summary>
    static void TestConnFlood()
    {
        Console.WriteLine("[连接洪水 · 同 IP 新建频率]");
        int ok = 0, refused = 0;
        for (int i = 0; i < 40; i++)
        {
            try
            {
                var c = Connect(out var s, 1500);
                if (ReadFrame(s, out _, out _, out _)) ok++;
                else refused++;
                try { c.Close(); } catch { }
            }
            catch { refused++; }
        }
        Console.WriteLine("        建连成功 " + ok + " 次，被拒 " + refused + " 次（共 40 次尝试）");
        Ok("连接洪水被限流拦截", refused > 0 && ok <= 20, "ok=" + ok + " refused=" + refused);
    }
}
