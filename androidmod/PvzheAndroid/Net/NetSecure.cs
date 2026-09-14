using System;
using System.Collections.Generic;

/// <summary>
/// 安全会话：连接握手（X25519 → 链路密钥）+ 房间密钥分发（E2E）+ 帧加解密包装。
/// ★ 两端共用同一份源码（MOD 与中继），纯托管、不依赖 Godot / BCL 密码学。
///
/// 三层密钥：
///  - linkKey  连接级，由临时 X25519 + 握手 nonce 派生 → 具备前向安全；用于信令帧（enc=1）
///  - roomKey  房间级，房主随机生成，经「房主↔成员」的 X25519 点对点包裹分发；
///             中继无法解密 → 游戏数据帧（enc=2）是真正的端到端加密
///
/// 帧体格式（NetProto 组装外层长度与 type）：
///  enc=0：明文
///  enc=1：payload = [seq:u64 BE][密文 + tag16]
///  enc=2：payload = [senderId:u16 BE][seq:u64 BE][密文 + tag16]
///
/// 防重放：每发送方 seq 严格递增，接收方只接受更大的 seq（TCP 有序，无需窗口）。
/// nonce 唯一性：链路帧用方向前缀区分两个方向；E2E 帧用 senderId 区分发送者，
/// 因此同一房间内不会出现 (key, nonce) 复用。
/// </summary>
public sealed class NetSecureSession
{
    // 方向前缀：保证链路密钥下两个方向的 nonce 空间不重叠
    public const uint DirClientToServer = 0x43325331;   // "C2S1"
    public const uint DirServerToClient = 0x53324331;   // "S2C1"
    /// <summary>本端发送方向前缀：MOD 侧固定客户端，中继侧固定服务端。</summary>
    public uint SendPrefix = DirClientToServer;
    /// <summary>本端接收方向前缀（对端发送用的前缀），必须与 SendPrefix 相反。</summary>
    public uint RecvPrefix = DirServerToClient;

    const string InfoLink = "pvzhe-link-v3";
    const string InfoRoomKey = "pvzhe-roomkey-v3";
    const string InfoAuth = "pvzhe-auth-v3";
    static readonly byte[] AadWrap = System.Text.Encoding.ASCII.GetBytes("pvzhe-w");

    byte[] _myPriv;
    byte[] _myPub;
    byte[] _peerPub;
    byte[] _linkKey;

    byte[] _roomKey;

    ulong _txSeq;
    ushort _myPlayerId;
    readonly Dictionary<ushort, ulong> _maxSeq = new Dictionary<ushort, ulong>();

    // ---------- 状态查询 ----------

    /// <summary>握手是否已完成（链路密钥可用）。</summary>
    public bool Handshaken => _linkKey != null;
    public byte[] MyPublicKey => _myPub;
    public byte[] PeerPublicKey => _peerPub;
    public byte[] LinkKey => _linkKey;

    public bool HasRoomKey => _roomKey != null;

    /// <summary>本端在房间内的玩家 id（E2E 加密时作为 nonce 前缀）。</summary>
    public ushort MyPlayerId
    {
        get => _myPlayerId;
        set { _myPlayerId = value; }
    }

    /// <summary>房间密钥指纹（前 4 字节 hex，形如 A3F1-9C02），供玩家带外核对以防中继 MITM。</summary>
    public string RoomFingerprint
    {
        get
        {
            if (_roomKey == null) return "";
            var h = NetSha256.Hash(_roomKey);
            string s = NetSha256.Hex(h).Substring(0, 8).ToUpperInvariant();
            return s.Substring(0, 4) + "-" + s.Substring(4, 4);
        }
    }

    /// <summary>链路密钥指纹（诊断用）。</summary>
    public string LinkFingerprint
    {
        get
        {
            if (_linkKey == null) return "";
            return NetSha256.Hex(NetSha256.Hash(_linkKey)).Substring(0, 8).ToUpperInvariant();
        }
    }

    public void Reset()
    {
        _myPriv = null; _myPub = null; _peerPub = null; _linkKey = null;
        _roomKey = null; _txSeq = 0; _maxSeq.Clear();
    }

    /// <summary>离开房间：清掉房间密钥与重放表，保留链路密钥（连接还在）。</summary>
    public void ResetRoom()
    {
        _roomKey = null;
        _maxSeq.Clear();
    }

    // ================= 握手 =================

    /// <summary>
    /// 生成临时密钥对并返回本端公钥。每次连接调用一次（临时密钥 = 前向安全）。
    /// </summary>
    public byte[] BeginHandshake()
    {
        _myPriv = NetX25519.GeneratePrivate();
        _myPub = NetX25519.PublicFrom(_myPriv);
        return _myPub;
    }

    /// <summary>
    /// 完成握手：用对端公钥 + 挑战 nonce 派生出链路密钥（两端调用同一次，结果一致）。
    /// </summary>
    public bool CompleteHandshake(byte[] peerPub, byte[] challengeNonce)
    {
        if (peerPub == null || peerPub.Length != 32) return false;
        if (_myPriv == null) BeginHandshake();

        var shared = NetX25519.SharedSecret(_myPriv, peerPub);
        // 全零共享密钥意味着对端公钥非法（低阶点），必须拒绝
        if (NetSha256.Hex(shared) == NetSha256.Hex(new byte[32])) return false;

        _peerPub = peerPub;
        _linkKey = NetSha256.HkdfStr(shared, challengeNonce, InfoLink, 32);
        return true;
    }

    /// <summary>握手鉴权 MAC（客户端与中继用同一算法验证）。</summary>
    public byte[] AuthMac(byte[] challengeNonce)
    {
        if (_linkKey == null) return null;
        var info = System.Text.Encoding.UTF8.GetBytes(InfoAuth);
        var buf = new byte[info.Length + (challengeNonce != null ? challengeNonce.Length : 0)];
        Array.Copy(info, 0, buf, 0, info.Length);
        if (challengeNonce != null && challengeNonce.Length > 0)
            Array.Copy(challengeNonce, 0, buf, info.Length, challengeNonce.Length);
        return NetSha256.Hmac(_linkKey, buf);
    }

    /// <summary>校验对端 MAC（常量时间比较）。</summary>
    public bool VerifyAuthMac(byte[] challengeNonce, byte[] mac)
    {
        var want = AuthMac(challengeNonce);
        if (want == null || mac == null) return false;
        return NetSha256.FixedEquals(want, mac);
    }

    // ================= 房间密钥（E2E） =================

    /// <summary>房主：生成并持有房间密钥。</summary>
    public byte[] CreateRoomKey()
    {
        _roomKey = NetX25519.RandomBytes(32);
        _maxSeq.Clear();
        return _roomKey;
    }

    /// <summary>加入方：设置从房主收到的房间密钥。</summary>
    public void SetRoomKey(byte[] key)
    {
        _roomKey = key;
        _maxSeq.Clear();
    }

    /// <summary>
    /// 房主：把房间密钥包裹给指定成员（用 房主私钥 × 成员公钥 的 ECDH 派生 KEK）。
    /// 返回 60 字节 = [nonce12][密文32 + tag16]；中继无法解开。
    /// </summary>
    public byte[] WrapRoomKeyFor(byte[] memberPub)
    {
        if (_roomKey == null || memberPub == null || memberPub.Length != 32) return null;
        if (_myPriv == null) return null;

        var shared = NetX25519.SharedSecret(_myPriv, memberPub);
        var kek = NetSha256.HkdfStr(shared, null, InfoRoomKey, 32);

        var nonce = NetX25519.RandomBytes(12);
        var sealedKey = new NetAesGcm(kek).Seal(nonce, AadWrap, _roomKey);

        var outb = new byte[12 + sealedKey.Length];
        Array.Copy(nonce, 0, outb, 0, 12);
        Array.Copy(sealedKey, 0, outb, 12, sealedKey.Length);
        return outb;
    }

    /// <summary>成员：用 成员私钥 × 房主公钥 解出房间密钥。</summary>
    public bool UnwrapRoomKeyFrom(byte[] hostPub, byte[] wrapped)
    {
        if (wrapped == null || wrapped.Length != 60) return false;
        if (hostPub == null || hostPub.Length != 32) return false;
        if (_myPriv == null) return false;

        var shared = NetX25519.SharedSecret(_myPriv, hostPub);
        var kek = NetSha256.HkdfStr(shared, null, InfoRoomKey, 32);

        var nonce = new byte[12];
        Array.Copy(wrapped, 0, nonce, 0, 12);
        var body = new byte[wrapped.Length - 12];
        Array.Copy(wrapped, 12, body, 0, body.Length);

        var key = new NetAesGcm(kek).Open(nonce, AadWrap, body);
        if (key == null || key.Length != 32) return false;
        _roomKey = key;
        _maxSeq.Clear();
        return true;
    }

    // ================= 帧加解密 =================

    static byte[] MakeLinkNonce(uint prefix, ulong seq)
    {
        var n = new byte[12];
        n[0] = (byte)(prefix >> 24); n[1] = (byte)(prefix >> 16);
        n[2] = (byte)(prefix >> 8); n[3] = (byte)prefix;
        for (int i = 0; i < 8; i++) n[4 + i] = (byte)(seq >> (56 - 8 * i));
        return n;
    }

    static byte[] MakeE2eNonce(ushort senderId, ulong seq)
    {
        var n = new byte[12];
        n[0] = (byte)(senderId >> 8); n[1] = (byte)senderId;
        n[2] = 0; n[3] = 0;
        for (int i = 0; i < 8; i++) n[4 + i] = (byte)(seq >> (56 - 8 * i));
        return n;
    }

    static void WriteU64(byte[] b, int off, ulong v)
    {
        for (int i = 0; i < 8; i++) b[off + i] = (byte)(v >> (56 - 8 * i));
    }

    static ulong ReadU64(byte[] b, int off)
    {
        ulong v = 0;
        for (int i = 0; i < 8; i++) v = (v << 8) | b[off + i];
        return v;
    }

    /// <summary>链路加密帧体（enc=1）：[seq8][密文+tag]。type 进 AAD 防类型混淆。</summary>
    public byte[] SealLink(byte type, byte[] plain)
    {
        if (_linkKey == null) return null;
        ulong seq = ++_txSeq;
        var nonce = MakeLinkNonce(SendPrefix, seq);
        var aad = new byte[2];
        aad[0] = type; aad[1] = NetProto.EncLink;
        var sealedBody = new NetAesGcm(_linkKey).Seal(nonce, aad, plain ?? new byte[0]);

        var outb = new byte[8 + sealedBody.Length];
        WriteU64(outb, 0, seq);
        Array.Copy(sealedBody, 0, outb, 8, sealedBody.Length);
        return outb;
    }

    /// <summary>解开链路加密帧体；失败返回 null。</summary>
    public byte[] OpenLink(byte type, byte[] body)
    {
        if (_linkKey == null || body == null || body.Length < 8 + 16) return null;
        ulong seq = ReadU64(body, 0);
        if (seq == 0) return null;
        var nonce = MakeLinkNonce(RecvPrefix, seq);
        var aad = new byte[2];
        aad[0] = type; aad[1] = NetProto.EncLink;

        var cipher = new byte[body.Length - 8];
        Array.Copy(body, 8, cipher, 0, cipher.Length);
        return new NetAesGcm(_linkKey).Open(nonce, aad, cipher);
    }

    /// <summary>E2E 加密帧体（enc=2）：[senderId2][seq8][密文+tag]。</summary>
    public byte[] SealE2e(ushort senderId, byte type, byte[] plain)
    {
        if (_roomKey == null) return null;
        // 玩家 id 未分配（=0）时不能用：0 与“未进房”冲突，会破坏 nonce 唯一性
        if (senderId == 0) return null;
        ulong seq = ++_txSeq;
        var nonce = MakeE2eNonce(senderId, seq);
        var aad = new byte[2];
        aad[0] = type; aad[1] = NetProto.EncE2e;
        var sealedBody = new NetAesGcm(_roomKey).Seal(nonce, aad, plain ?? new byte[0]);

        var outb = new byte[10 + sealedBody.Length];
        outb[0] = (byte)(senderId >> 8);
        outb[1] = (byte)senderId;
        WriteU64(outb, 2, seq);
        Array.Copy(sealedBody, 0, outb, 10, sealedBody.Length);
        return outb;
    }

    /// <summary>解开 E2E 帧体；同时做按发送者防重放。失败返回 null。</summary>
    public byte[] OpenE2e(byte type, byte[] body, out ushort senderId)
    {
        senderId = 0;
        if (_roomKey == null || body == null || body.Length < 10 + 16) return null;

        senderId = (ushort)((body[0] << 8) | body[1]);
        ulong seq = ReadU64(body, 2);
        if (seq == 0) return null;

        // 防重放：同一发送者的 seq 必须严格递增
        if (_maxSeq.TryGetValue(senderId, out ulong seen) && seq <= seen) return null;

        var nonce = MakeE2eNonce(senderId, seq);
        var aad = new byte[2];
        aad[0] = type; aad[1] = NetProto.EncE2e;

        var cipher = new byte[body.Length - 10];
        Array.Copy(body, 10, cipher, 0, cipher.Length);
        var plain = new NetAesGcm(_roomKey).Open(nonce, aad, cipher);
        if (plain == null) return null;

        _maxSeq[senderId] = seq;
        return plain;
    }

    /// <summary>中继转发时使用：不改动密文，只解析出 senderId（用于按发送者隔离重放窗口）。</summary>
    public static ushort PeekSenderId(byte[] body)
        => (body != null && body.Length >= 10) ? (ushort)((body[0] << 8) | body[1]) : (ushort)0;

    /// <summary>
    /// 按消息类型决定加密级别。
    /// ★ 只有「纯广播、中继无需理解内容」的游戏数据才走 E2E（中继盲转发）。
    ///   需要中继定向投递或读字段的（ClientReport / RelayDataTo / BattleStart / BattleEnd /
    ///   玩家名单 / 房间设置）必须走链路加密，否则中继无法处理。
    /// </summary>
    public static byte EncFor(byte type)
    {
        switch (type)
        {
            case NetProto.MsgStateSync:
            case NetProto.MsgEntitySnapshot:
            case NetProto.MsgCursor:
            case NetProto.MsgChat:
            case NetProto.MsgRelayData:
                return NetProto.EncE2e;
            default:
                return NetProto.EncLink;
        }
    }

    /// <summary>是否是中继可盲转发的 E2E 帧（中继不解密、不读字段、只按 type 广播）。</summary>
    public static bool IsBlindForwardType(byte type)
    {
        switch (type)
        {
            case NetProto.MsgStateSync:
            case NetProto.MsgEntitySnapshot:
            case NetProto.MsgCursor:
            case NetProto.MsgChat:
            case NetProto.MsgRelayData:
                return true;
            default:
                return false;
        }
    }

    /// <summary>统一发送包装：按类型自动选密钥并加密（返回 null = 密钥未就绪，应拒发）。</summary>
    public byte[] SealFrame(byte type, byte[] payload)
    {
        byte enc = EncFor(type);
        return enc == NetProto.EncE2e
            ? SealE2e(_myPlayerId, type, payload)
            : SealLink(type, payload);
    }

    /// <summary>统一接收包装：按 enc 解密；失败返回 null（绝不返回未认证明文）。</summary>
    public byte[] OpenFrame(byte type, byte enc, byte[] body)
    {
        if (enc == NetProto.EncPlain) return body;
        if (enc == NetProto.EncE2e) return OpenE2e(type, body, out ushort _);
        if (enc == NetProto.EncLink) return OpenLink(type, body);
        return null;
    }
}
