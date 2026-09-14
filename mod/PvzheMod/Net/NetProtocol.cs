/// <summary>联机协议常量。纯 System，中继与 MOD 共用。</summary>
public static class NetConstants
{
    // ★ 对战模式的改动刻意【不】提升版本号：它们向后兼容，提升反而有害。
    //   · RoomSettings 新增的「模式位」追加在报文末尾 —— 旧端读到 IpWhitelist 就停，多余字节自然忽略；
    //     新端读旧中继的包时 BitReader 越界返回 0 → BattleMode/BalanceTeams=false，自动降级为合作模式。
    //   · 阵营塞进 MsgPlayerList 的 flags bit1..2 —— 旧端只检查 bit0（房主位），不受影响。
    //   · MsgSetFaction 只由客户端主动发起，旧端永远收不到。
    //   若提升版本号，现网手机版（v3）会被中继直接拒绝 → PC 与手机彻底无法联机。
    public const ushort ProtoVer = 3;   // v3 = 帧加密（AES-256-GCM）。旧版客户端会因版本不符被拒
    public const int DirectPort = 22999;
    public const int RelayDefaultPort = 8231;
    /// <summary>单帧上限（含类型字节）。正常帧 &lt; 1KB；M3 的大快照需分批发送。</summary>
    public const int MaxFrame = 4096;
    public const int MaxChatBytes = 200;
    /// <summary>房间人数上限（中继与客户端一致）。</summary>
    public const int MaxPlayers = 8;
    /// <summary>昵称最大字符数。</summary>
    public const int MaxNickChars = 12;

    /// <summary>服务器访问 Token（随 MOD 版本轮换，用于连接后的挑战响应鉴权；不在网络上明文传输）。</summary>
    public const string ServerToken = "CHANGE_ME_RELAY_TOKEN";

    // ---- 防刷量与心跳参数 ----
    public const int RateLimitPerSec = 60;      // 每连接每秒允许帧数
    public const int RateBurst = 120;           // 令牌桶容量
    public const int MaxConnPerIp = 4;          // 同 IP 并发连接上限
    // ---- 2026-09-12 新增：补三类 IP 限流 ----
    public const int MaxConnPerMinPerIp = 15;   // 同 IP 每 60s 新建连接上限（≈ 即触发 IP 冷却）
    public const int MaxTotalConn = 400;        // 全服连接上限（2GB 内存 + 每连接一线程的安全线）
    public const int MaxRoomOpsPerMinPerIp = 5; // 同 IP 每 60s 建房/加房次数上限
    public const int ConnRateWindowSec = 60;    // 上述频率窗口（秒）
    public const int ViolationLimit = 20;       // 违规阈值（次/窗口）→ 断开 + IP 冷却
    public const int ViolationWindowSec = 60;
    public const int IpCooldownSec = 300;       // IP 冷却时长
    public const int HeartbeatTimeoutSec = 15;  // 该时长内无任何帧 → 断开（客户端 1s 一发 Ping）
    public const int DefaultRoomTtlMinutes = 120;
    public const int RoomClosingSoonSec = 300;  // 到期前提醒
    public const int ScanIntervalMs = 5000;     // 中继巡检间隔（心跳/TTL/冷却）
}

/// <summary>帧格式 [len:uint32 LE][type:byte][payload]；中继控制层与直连层共用同一编解码。</summary>
public static class NetProto
{
    // ---- 帧载荷加密级别（Frame 的 enc 字节） ----
    public const byte EncPlain = 0;   // 明文：仅握手阶段
    public const byte EncLink = 1;    // 链路加密：中继可解（信令/房间设置/名单/大厅列表）
    public const byte EncE2e = 2;     // 端到端加密：中继不可解（状态/实体/光标/聊天）

    // 中继控制层
    public const byte MsgAuthChallenge = 0x00;
    public const byte MsgRoomCreateReq = 0x01;
    public const byte MsgRoomCreateRes = 0x02;
    public const byte MsgRoomJoinReq = 0x03;
    public const byte MsgRoomJoinRes = 0x04;
    public const byte MsgRoomLeave = 0x05;
    public const byte MsgPeerJoined = 0x06;
    public const byte MsgPeerLeft = 0x07;
    public const byte MsgSetRoomSettings = 0x08;   // 房主 -> 中继
    public const byte MsgRoomSettings = 0x09;      // 中继 -> 全员（含加入时同步）
    public const byte MsgRoomClosed = 0x0A;        // 房间销毁（原因见 payload 首字节）
    public const byte MsgRoomClosingSoon = 0x0B;   // 到期提醒
    public const byte MsgKick = 0x0C;              // 房主踢人 / 违规踢出
    public const byte MsgKeyExchange = 0x0D;       // 双向：握手公钥交换（客户端公钥）
    public const byte MsgRoomKeyWrap = 0x0E;       // 房主 -> 指定成员：房间密钥包裹（中继盲转发）
    public const byte MsgAuthResponse = 0x0F;
    public const byte MsgSignalTo = 0x10;
    public const byte MsgSignalFrom = 0x11;
    public const byte MsgClientReport = 0x12;      // 客机 -> 房主（权威校验用，M3 起）
    public const byte MsgCorrectState = 0x13;      // 房主 -> 客机（回正状态）
    public const byte MsgStateSync = 0x18;         // 房主 -> 全员：对局状态（关卡/阳光/波次/僵尸数），每秒 1 次
    public const byte MsgBattleStart = 0x19;       // 房主 -> 全员：对局开始（battleId + 关卡标识）；重复发起被中继拒绝
    public const byte MsgPlayerList = 0x1A;        // 中继 -> 全员：玩家名单（id/昵称/颜色槽/是否房主）
    public const byte MsgBattleEnd = 0x1B;         // 房主 -> 全员：对局结束（battleId）
    public const byte MsgEntitySnapshot = 0x1C;    // 房主 -> 全员：实体快照分片（M3：僵尸/植物镜像）
    public const byte MsgCursor = 0x1D;            // 玩家 -> 中继 -> 其他玩家：光标世界坐标（x:i32,y:i32，×8 定点）
    public const byte MsgRoomListReq = 0x1E;       // 大厅 -> 中继：请求房间列表（无需在房间内）
    public const byte MsgRoomList = 0x1F;          // 中继 -> 大厅：房间列表（房间码/房主昵称/人数/是否已开始/是否需口令）
    public const byte MsgRelayData = 0x20;
    public const byte MsgRelayDataTo = 0x21;
    // ---- 在线统计（M5）：中继自己就能答，无需在房间内 ----
    // 当前在线 = 中继的 TCP 连接数（含还没进房间的人）；当日在线 = 今天出现过的【唯一 IP】数，
    // 同一 IP 一天只累计一条（字典天然去重）。
    public const byte MsgStatsReq = 0x22;          // 客户端 -> 中继：请求在线统计
    public const byte MsgStatsRes = 0x23;          // 中继 -> 客户端：[u32 当前在线][u32 当日在线]
    // ---- 对战模式分边（v4）----
    public const byte MsgSetFaction = 0x24;        // 客户端 -> 中继：[u8 faction(0未选/1植物/2僵尸)]
    // 中继不另发应答：成功则广播新名单（MsgPlayerList），失败则回 MsgError。

    // ---- MsgRelayData 载荷首字节：M4 真·共享战场实体同步 ----
    // 复用 MsgRelayData（已是中继盲转发的 E2E 类型）→ 服务端零改动。
    //   Spawn: [0x01][u32 syncId低][u32 syncId高][u8 kind(0僵尸/1植物)][u8 flags(b0=魅惑,b1=带世界坐标)][str packetId][i16 gx][i16 gy][i16 wx][i16 wy]
    //          syncId = ((ulong)发送者playerId << 32) | Godot实例id低32位
    //          —— 用实例 id 作稳定标识，重发幂等，双方可周期性全量重发给对方补事件
    //          ★ gx/gy 必须是草坪格子（≥1）。(0,0) 不是草坪，传下去会生成在卡槽那一带。
    //          wx/wy = 世界坐标（×1 像素）；僵尸的格子只是反推出来的近似值，实际位置靠 wx/wy 摆正。
    //   Dead : [0x02][u32 syncId低][u32 syncId高]      —— 实体消失（铲掉/被吃/清场）
    //   Ready: [0x03][u16 playerId][u8 ready]            —— 选卡阶段准备信号（M4b 门禁用）
    //   Sun  : [0x04][u8 mode(0=增量/1=绝对值)][u32 值]     —— 阳光共享（M4c）
    //   File : [0x05][str key][str jsonText]                  —— 每日/在线关卡文件互传（M4f）
    //   Ask  : [0x06][str key]                                 —— 客机索要关卡文件（自愈）
    public const byte SyncSpawn = 0x01;
    public const byte SyncDead = 0x02;
    public const byte SyncReady = 0x03;
    public const byte SyncSun = 0x04;
    public const byte SyncLevelFile = 0x05;
    public const byte SyncLevelAsk = 0x06;
    public const byte MsgPing = 0x30;
    public const byte MsgPong = 0x31;
    public const byte MsgChat = 0x40;
    public const byte MsgError = 0x7F;

    // 中继错误码（MsgError payload 首字节）
    public const byte ErrProto = 1;
    public const byte ErrNoRoom = 2;
    public const byte ErrFull = 3;
    public const byte ErrBadCode = 4;
    public const byte ErrNotInRoom = 5;
    public const byte ErrAuth = 6;
    public const byte ErrBadPass = 7;
    public const byte ErrRateLimited = 8;
    public const byte ErrIpLimit = 9;
    public const byte ErrBattleStarted = 10;   // 对局已开始（不能重复开）
    public const byte ErrNotHost = 11;         // 只有房主可以执行该操作
    // ---- 对战模式（v4）----
    public const byte ErrNotBattleMode = 12;   // 该房间不是对战模式，不能分边
    public const byte ErrFactionBalance = 13;  // 人数平衡：该方人已满，换一边
    public const byte ErrFactionLocked = 14;   // 已开战，阵营锁定

    // 房间销毁原因
    public const byte CloseExpired = 1;      // 到达 TTL
    public const byte CloseHostClosed = 2;   // 房主解散
    public const byte CloseEmpty = 3;        // 无人在线
    public const byte CloseShutdown = 4;     // 服务器维护

    // 踢出原因
    public const byte KickByHost = 1;
    public const byte KickCheat = 2;
    public const byte KickViolation = 3;

    /// <summary>打包成 [len:uint32 LE][type:byte][enc:byte][payload]（len = 2 + payload 长度）。</summary>
    public static byte[] Frame(byte type, byte enc, byte[] payload)
    {
        int total = 2 + (payload?.Length ?? 0);
        var o = new byte[4 + total];
        o[0] = (byte)total; o[1] = (byte)(total >> 8); o[2] = (byte)(total >> 16); o[3] = (byte)(total >> 24);
        o[4] = type;
        o[5] = enc;
        if (payload != null && payload.Length > 0) System.Array.Copy(payload, 0, o, 6, payload.Length);
        return o;
    }

    /// <summary>解析一帧（v3：[len][type][enc][payload]）；缓冲不足返回 false 且 consumed=0。</summary>
    public static bool TryParseFrame(byte[] buf, out byte type, out byte enc, out byte[] payload, out int consumed)
    {
        type = 0; enc = 0; payload = null; consumed = 0;
        if (buf == null || buf.Length < 6) return false;
        uint total = (uint)(buf[0] | (buf[1] << 8) | (buf[2] << 16) | (buf[3] << 24));
        if (total < 2 || total > NetConstants.MaxFrame) return false;
        if (buf.Length < 4 + total) return false;
        type = buf[4];
        enc = buf[5];
        payload = new byte[total - 2];
        if (payload.Length > 0) System.Array.Copy(buf, 6, payload, 0, payload.Length);
        consumed = (int)(4 + total);
        return true;
    }
}
