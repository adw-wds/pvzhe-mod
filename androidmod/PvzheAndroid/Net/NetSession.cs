using System;
using System.Collections.Generic;
using Godot;

/// <summary>联机会话门面（唯一对 Godot 层的入口，由 GameCheats.OnFrame 每帧 Tick）。</summary>
public static class NetSession
{
    /// <summary>默认中继地址（域名，避免暴露服务器 IP；可在面板里改）。</summary>
    public static string RelayAddr = "localhost";
    public static int RelayPort = NetConstants.RelayDefaultPort;

    // ================= 服务器区域（界面只显示区域名，不出现域名/IP） =================
    // ★ 原本这张表只在电脑版的 RemoteServer 里，手机版拿不到 → 界面只能显示裸域名 localhost。
    //   提到共享层后两端显示一致（手机版 NetUI 的下拉、电脑版 NetStatus 的 regions= 都读这里）。
    public static readonly string[] RegionNames = { "香港1区", "香港2区", "日本1区" };
    public static readonly string[] RegionAddrs = { "localhost", "localhost", "localhost" };
    /// <summary>该区域是否已开放（未开放的在下拉里显示但置灰）。</summary>
    public static readonly bool[] RegionReady = { true, false, false };

    public static int RegionCount { get { return RegionNames.Length; } }

    /// <summary>当前地址对应的区域下标；不在表里返回 -1。</summary>
    public static int RegionIndex()
    {
        string a = RelayAddr ?? "";
        for (int i = 0; i < RegionAddrs.Length; i++) if (RegionAddrs[i] == a) return i;
        return -1;
    }

    /// <summary>当前地址 → 区域名（未知返回“自定义”/“未知”）。</summary>
    public static string CurrentRegionName()
    {
        int i = RegionIndex();
        if (i >= 0) return RegionNames[i];
        return string.IsNullOrEmpty(RelayAddr) ? "未知" : "自定义";
    }

    /// <summary>按区域名切换中继地址。返回是否命中已知区域（未开放的区域拒绝切换）。</summary>
    public static bool SetRegionByName(string nameOrAddr)
    {
        if (string.IsNullOrEmpty(nameOrAddr)) return false;
        string s = nameOrAddr.Trim();
        for (int i = 0; i < RegionNames.Length; i++)
        {
            if (RegionNames[i] != s) continue;
            if (!RegionReady[i]) return false;
            RelayAddr = RegionAddrs[i];
            return true;
        }
        RelayAddr = s;   // 不是已知区域名 → 当地址原样用（保留自定义服务器能力）
        return false;
    }

    /// <summary>按区域下标切换（未开放返回 false）。</summary>
    public static bool SetRegionByIndex(int i)
    {
        if (i < 0 || i >= RegionNames.Length) return false;
        if (!RegionReady[i]) return false;
        RelayAddr = RegionAddrs[i];
        return true;
    }

    /// <summary>区域列表单行文本（供外置修改器下拉）：名称:是否开放;名称:是否开放…</summary>
    public static string RegionListText()
    {
        string s = "";
        for (int i = 0; i < RegionNames.Length; i++)
        {
            if (i > 0) s += ";";
            s += RegionNames[i] + ":" + (RegionReady[i] ? 1 : 0);
        }
        return s;
    }

    public static NetState State { get; private set; } = NetState.Offline;
    public static bool IsHost { get; private set; }
    public static string RoomCode { get; private set; } = "";
    public static string MyNick { get; private set; } = "玩家";
    public static ushort MyPlayerId { get; private set; }
    /// <summary>房间内当前人数（来自中继下发的玩家名单，不再本地估算）。</summary>
    public static int PeerCount => Players.Count;
    /// <summary>房间内玩家名单（中继下发：id/昵称/颜色槽/是否房主）。</summary>
    public static readonly List<PeerInfo> Players = new List<PeerInfo>();
    /// <summary>大厅房间列表（最近一次刷新；仅公开信息，不含任何 IP）。</summary>
    public static readonly List<RoomInfo> RoomList = new List<RoomInfo>();
    /// <summary>大厅连接空闲超时（毫秒）：超时自动断开，省服务器连接数。</summary>
    public const int LobbyIdleMs = 30000;
    /// <summary>对局是否已开始（已开始则不能重复开）。</summary>
    public static bool Started { get; private set; }
    /// <summary>对局序号（中继分配）。</summary>
    public static uint BattleId { get; private set; }
    /// <summary>房主开战时的关卡标识（客户端据此自动进关）。</summary>
    public static string HostLevelKey { get; private set; } = "";
    /// <summary>房主当前设置（客机收到广播后同步）。</summary>
    public static RoomSettings Settings { get; private set; } = new RoomSettings();
    /// <summary>剩余存活时间（分钟，-1 = 未知）。</summary>
    public static int TtlMinutesLeft { get; private set; } = -1;

    // ================= 对战模式（v4 兼容字段：房主设置里的 BattleMode/BalanceTeams）=================
    /// <summary>本房间是否为对战模式。</summary>
    public static bool IsBattleMode { get { return Settings != null && Settings.BattleMode; } }
    /// <summary>对战模式下是否启用人数平衡。</summary>
    public static bool IsBalanceTeams { get { return Settings != null && Settings.BalanceTeams; } }

    /// <summary>我的阵营（从玩家名单里取；不在房间/未选 → NetFaction.None）。</summary>
    public static byte MyFaction
    {
        get
        {
            for (int i = 0; i < Players.Count; i++)
            {
                var p = Players[i];
                if (p != null && p.Id == MyPlayerId) return p.Faction;
            }
            return NetFaction.None;
        }
    }

    /// <summary>某阵营当前人数（用于分边 UI 显示与人数平衡提示）。</summary>
    public static int CountFaction(byte faction)
    {
        int n = 0;
        for (int i = 0; i < Players.Count; i++)
        {
            var p = Players[i];
            if (p != null && p.Faction == faction) n++;
        }
        return n;
    }

    /// <summary>分边：请求中继把自己改到指定阵营。
    /// 中继会根据人数平衡仲裁，拒绝时回 MsgError（ErrFactionBalance 等）→ 进 LastError。
    /// 已开战时中继直接拒（ErrFactionLocked），所以这里不做本地预判，以服务端为准。</summary>
    public static bool SetFaction(byte faction)
    {
        if (!InRoom) { LastError = "不在房间中"; Notice(LastError); return false; }
        if (_t == null) { LastError = "连接已断开"; Notice(LastError); return false; }
        if (!IsBattleMode) { LastError = "该房间不是对战模式"; Notice(LastError); return false; }
        _t.Send(NetProto.MsgSetFaction, new BitWriter().WriteByte(faction).ToArray());
        LastError = "";
        return true;
    }

    /// <summary>对战模式自动补边：未选阵营时落到人少的一边。
    /// 由 Tick 周期调用（含刚进房、房主刚切成对战、平衡开关刚打开几种情况）。
    /// 已开战则不补（中继会拒，而且此时阵营应已锁定）。</summary>
    static void EnsureFactionAssigned()
    {
        if (!InRoom || Started) return;
        if (!IsBattleMode) return;
        if (MyPlayerId == 0) return;
        if (MyFaction != NetFaction.None) return;
        int pc = CountFaction(NetFaction.Plant);
        int zc = CountFaction(NetFaction.Zombie);
        SetFaction(pc <= zc ? NetFaction.Plant : NetFaction.Zombie);
    }
    /// <summary>房主指定的关卡（联机选关）：空 = 用房主当前所在关卡。</summary>
    public static string LevelOverrideKey = "";
    /// <summary>房主指定关卡的中文显示名（界面用）。</summary>
    public static string LevelOverrideName = "";
    /// <summary>最近一次提示（销毁/踢出/到期等），供面板显示。</summary>
    public static string LastNotice = "";
    public static string LastError = "";
    public static string PeerDirectAddr = "";
    public const int DirectTimeoutMs = 3000;

    // ★ 只用中继（直连已移除）：字段用**具体类**而不是接口。
    //   游戏 AOT 运行时不支持 MOD 新类型的接口派发（会抛 EntryPointNotFoundException）。
    static RelayTransport _t;
    static DirectTransport _direct;
    static ulong _lastPingMs;
    static ulong _signalingStartMs;
    static bool _directTried;
    static uint _pingSeq;
    /// <summary>上一条 Ping 的发送时刻（用于算往返延迟）。</summary>
    static ulong _pingSentMs;
    /// <summary>最近一次往返延迟（毫秒，-1 = 未知）。</summary>
    public static int PingMs { get; private set; } = -1;

    // ---- M5 在线统计 ----
    /// <summary>统计查询间隔。中继有限流（60 帧/秒、20 次违规直接踢），别调太小。</summary>
    public const int StatsIntervalMs = 15_000;
    static ulong _lastStatsMs;
    /// <summary>中继当前在线连接数（含还没进房间的人）。
    /// 注：只在连上中继期间有效，断开后保留最后一次的值。</summary>
    public static int LiveCount { get; private set; }
    /// <summary>中继当日在线（唯一 IP 数）。同一 IP 一天只算一条。</summary>
    public static int TodayCount { get; private set; }
    static bool _wantRoomList;             // 大厅连接就绪后自动补发一次列表请求
    static int _factionTimer;              // 对战模式自动补边降频器（帧）
    /// <summary>掉线检测去抖：连接何时开始不可用的时刻（0 = 正常）。</summary>
    static ulong _lostMs;
    static ulong _lobbyLastActiveMs;       // 大厅最近活跃时刻（空闲超时自动断开）
    static int _pendingAction;          // 0=无 1=建房 2=加入（等鉴权完成后发送）
    static string _pendingCode = "", _pendingPass = "";

    public static event Action<string> OnChat;
    public static event Action<NetState> OnStateChanged;
    public static event Action OnSettingsChanged;
    public static event Action<string> OnNotice;
    /// <summary>大厅房间列表已刷新。</summary>
    public static event Action OnRoomList;

    static void SetState(NetState s)
    {
        if (!NetSessionCore.CanTransition(State, s)) { NetLog.Warn("非法状态转换 " + State + " -> " + s); return; }
        State = s;
        NetLog.Info("状态 -> " + s);
        try { OnStateChanged?.Invoke(s); } catch { }
    }

    /// <summary>
    /// 兜底进入会话状态：白名单允许时同 SetState；不允许时强行纠正（只用于“连接/建房已成功”这类
    /// 事实已经发生的情况，避免状态机残留导致“房间建好了但界面永远看不到房间”）。
    /// </summary>
    static void ForceRoomState(NetState s)
    {
        if (State == s) return;
        if (NetSessionCore.CanTransition(State, s)) { SetState(s); return; }
        var old = State;
        State = s;
        NetLog.Warn("状态强制纠错 " + old + " -> " + s);
        try { OnStateChanged?.Invoke(s); } catch { }
    }

    static void Notice(string s)
    {
        LastNotice = s;
        NetLog.Info(s);
        try { OnNotice?.Invoke(s); } catch { }
    }

    /// <summary>供其他联机模块推送提示（面板显示 + 日志）。</summary>
    public static void PostNotice(string s) { Notice(s); }

    // ================= 建房 / 加入 =================
    /// <summary>是否已在房间中（非 Offline/Ended）；用于禁止重复建房/重复加入。</summary>
    public static bool InRoom
    {
        get { return State == NetState.Signaling || State == NetState.Direct || State == NetState.Relay || State == NetState.Playing || State == NetState.Reconnecting; }
    }

    /// <summary>是否停留在大厅（已连中继但没进房间）。</summary>
    public static bool InLobby { get { return State == NetState.Lobby; } }

    /// <summary>确保大厅连接（未联机时连中继 + 鉴权；已在房间内则忽略）。</summary>
    public static bool EnsureLobby()
    {
        if (InRoom || State == NetState.Lobby) return true;
        if (State == NetState.Ended) SetState(NetState.Offline);
        if (State != NetState.Offline) return false;

        var rt = new RelayTransport(RelayAddr, RelayPort);
        if (!rt.Connect()) { LastError = "无法连接中继 " + RelayAddr + ":" + RelayPort; Notice(LastError); return false; }
        _t = rt;
        IsHost = false;
        _pendingAction = 0;
        _pendingCode = ""; _pendingPass = "";
        _wantRoomList = true;
        _directTried = false;
        _direct = null;
        _lobbyLastActiveMs = Time.GetTicksMsec();
        // ★ 新连接：重置统计/心跳计时，让上线后立刻拿一次在线人数与延迟
        //   （否则会沿用上个会话的时间戳，最长再等 15 秒才有人数）
        _lastStatsMs = 0;
        _lastPingMs = 0;
        LiveCount = 0;
        TodayCount = 0;
        SetState(NetState.Lobby);
        return true;
    }

    /// <summary>请求刷新大厅房间列表（自动确保大厅连接；在房间内则忽略）。</summary>
    public static void RequestRoomList()
    {
        _lobbyLastActiveMs = Time.GetTicksMsec();
        if (InRoom) return;
        if (State == NetState.Offline || State == NetState.Ended) { EnsureLobby(); return; }   // 连上后 Tick 会自动补发
        if (_t == null) return;
        // ★ 未鉴权时不能发：加密帧需要链路密钥，硬发只会被本地丢弃（并把日志刷满“密钥未就绪”）
        if (!Authenticated) { _wantRoomList = true; return; }   // 交给 Tick 在鉴权后补发
        _t.Send(NetProto.MsgRoomListReq, new byte[0]);
    }

    /// <summary>断开大厅连接（离开房间后回大厅时可主动调用；在房间内无效）。</summary>
    public static void CloseLobby()
    {
        if (InRoom) return;
        try { _t?.Close(); } catch { }
        _t = null;
        _direct = null;
        _wantRoomList = false;
        if (State != NetState.Offline) SetState(NetState.Offline);
    }

    public static bool CreateRoom(string nick, RoomSettings initial = null)
    {
        if (InRoom) { LastError = "已在房间中，请先离开房间"; Notice(LastError); return false; }
        IsHost = true;
        MyNick = PeerListCodec.Shorten(string.IsNullOrEmpty(nick) ? "房主" : nick);
        var want = initial ?? new RoomSettings();

        // 已在大厅（连接 + 鉴权完成）→ 直接在这条连接上建房，不重连
        if (State == NetState.Lobby && _t != null && Authenticated)
        {
            Settings = want;
            _directTried = false;
            _t.Send(NetProto.MsgRoomCreateReq, new BitWriter().WriteByte((byte)Settings.MaxPlayers).WriteStr(MyNick).ToArray());
            _signalingStartMs = Time.GetTicksMsec();
            SetState(NetState.Signaling);
            return true;
        }

        Leave();
        // ★ Leave() 会把状态置为 Ended；重连建房前必须先回 Offline，
        //   否则之后的 SetState(Signaling)/SetState(Relay) 会被状态机白名单拒绝 →
        //   连接与建房都成功、但客户端永远卡在 Ended（表现为：房间建好了却看不到房间）
        if (State == NetState.Ended) SetState(NetState.Offline);
        Settings = want;
        var rt = new RelayTransport(RelayAddr, RelayPort);
        if (!rt.Connect()) { LastError = "无法连接中继 " + RelayAddr + ":" + RelayPort; Notice(LastError); return false; }
        _t = rt;
        SetState(NetState.Signaling);
        _pendingAction = 1;
        _pendingCode = ""; _pendingPass = "";
        _directTried = false;
        return true;
    }

    public static bool JoinRoom(string code, string nick, string password = "")
    {
        if (InRoom) { LastError = "已在房间中，请先离开房间"; Notice(LastError); return false; }
        IsHost = false;
        MyNick = PeerListCodec.Shorten(string.IsNullOrEmpty(nick) ? "玩家" : nick);
        string wantCode = (code ?? "").Trim();
        string wantPass = password ?? "";

        // 已在大厅 → 直接加入，不重连
        if (State == NetState.Lobby && _t != null && Authenticated)
        {
            RoomCode = wantCode;
            _pendingCode = wantCode; _pendingPass = wantPass;
            _t.Send(NetProto.MsgRoomJoinReq, new BitWriter().WriteStr(wantCode).WriteStr(MyNick).WriteStr(wantPass).ToArray());
            _signalingStartMs = Time.GetTicksMsec();
            SetState(NetState.Signaling);
            return true;
        }

        Leave();
        if (State == NetState.Ended) SetState(NetState.Offline);   // 见 CreateRoom 里的说明
        RoomCode = wantCode;
        _pendingCode = wantCode;
        _pendingPass = wantPass;
        _directTried = false;
        var rt = new RelayTransport(RelayAddr, RelayPort);
        if (!rt.Connect()) { LastError = "无法连接中继 " + RelayAddr + ":" + RelayPort; Notice(LastError); return false; }
        _t = rt;
        SetState(NetState.Signaling);
        _pendingAction = 2;
        return true;
    }

    public static void Leave()
    {
        try { if (_t != null && State != NetState.Offline && State != NetState.Ended) _t.Send(NetProto.MsgRoomLeave, new byte[0]); } catch { }
        try { _t?.Close(); } catch { }
        try { _direct?.Close(); } catch { }
        _t = null;
        _direct = null;
        RoomCode = "";
        MyPlayerId = 0;
        Players.Clear();
        Started = false;
        BattleId = 0;
        HostLevelKey = "";
        _wantRoomList = false;
        NetCursor.Reset();
        _pendingAction = 0;
        TtlMinutesLeft = -1;
        Settings = new RoomSettings();
        LevelOverrideKey = "";
        LevelOverrideName = "";
        CheatPolicy.Reset();
        NetBattle.Reset();
        NetLevelShare.Reset();       // 关卡文件推送/等待（离开房间就别再等了）
        _lastKeyDistributeMask = 0;
        if (State != NetState.Offline && State != NetState.Ended) SetState(NetState.Ended);
    }

    /// <summary>回到可再次建房/加入的初始态。</summary>
    public static void Reset()
    {
        Leave();
        if (State == NetState.Ended) SetState(NetState.Offline);
    }

    // ================= 房主操作 =================

    /// <summary>上次已分发过密钥的成员集（位掩码），成员没变化就不重发。</summary>
    static int _lastKeyDistributeMask;

    /// <summary>房间内房主的公钥（客机用它解房间密钥包裹）。</summary>
    static byte[] HostPublicKey()
    {
        for (int i = 0; i < Players.Count; i++)
        {
            var p = Players[i];
            if (p != null && p.IsHost && p.PublicKey != null && p.PublicKey.Length == 32) return p.PublicKey;
        }
        return null;
    }

    /// <summary>当前房间密钥指纹（无则返回空串；UI 显示用）。</summary>
    public static string RoomKeyFingerprint
        => _t != null && _t.Secure != null ? _t.Secure.RoomFingerprint : "";

    /// <summary>本机是否已具备房间密钥（即端到端加密是否真已启用）。</summary>
    public static bool E2eReady => _t != null && _t.Secure != null && _t.Secure.HasRoomKey;

    /// <summary>
    /// 房主：为房内每个成员各包一份房间密钥并广播。
    /// 包裹是「房主私钥 × 成员公钥」的 ECDH 产物，非目标成员解不开（自己会忽略）。
    /// </summary>
    static void DistributeRoomKey()
    {
        if (!IsHost || _t == null || State == NetState.Offline) return;
        var sec = _t.Secure;
        if (sec == null) return;
        if (!sec.HasRoomKey) sec.CreateRoomKey();

        int mask = 0;
        for (int i = 0; i < Players.Count; i++)
        {
            var p = Players[i];
            if (p == null || p.Id == MyPlayerId) continue;
            if (p.PublicKey == null || p.PublicKey.Length != 32) continue;
            mask |= 1 << (p.Id & 31);
        }
        if (mask == 0 || mask == _lastKeyDistributeMask) return;
        _lastKeyDistributeMask = mask;

        int sent = 0;
        for (int i = 0; i < Players.Count; i++)
        {
            var p = Players[i];
            if (p == null || p.Id == MyPlayerId) continue;
            if (p.PublicKey == null || p.PublicKey.Length != 32) continue;
            var wrapped = sec.WrapRoomKeyFor(p.PublicKey);
            if (wrapped == null) continue;
            try { _t.Send(NetProto.MsgRoomKeyWrap, wrapped); sent++; } catch { }
        }
        if (sent > 0)
            NetLog.Info("房间密钥已分发 " + sent + " 份（指纹 " + sec.RoomFingerprint + "）");
    }
    public static void SendRoomSettings(RoomSettings s)
    {
        if (!IsHost || _t == null) return;
        Settings = s ?? new RoomSettings();
        _t.Send(NetProto.MsgSetRoomSettings, RoomSettingsCodec.Write(Settings));
        // 房主也要应用自己的房间设置：否则“房间禁用作弊”对房主无效（中继只广播给客机）
        CheatPolicy.Apply(Settings);
        try { OnSettingsChanged?.Invoke(); } catch { }
    }

    public static void KickPlayer(ushort id)
    {
        if (!IsHost || _t == null) return;
        _t.Send(NetProto.MsgKick, new BitWriter().WriteU16(id).ToArray());
    }

    public static void RequestCloseRoom()
    {
        if (!IsHost || _t == null) return;
        _t.Send(NetProto.MsgRoomClosed, new byte[0]);
    }

    /// <summary>房主：向全员广播对局状态（M2）。</summary>
    public static void BroadcastState(byte[] payload)
    {
        if (!IsHost || _t == null) return;
        _t.Send(NetProto.MsgStateSync, payload);
    }

    /// <summary>房主：广播实体快照（M3 镜像）。</summary>
    public static void BroadcastEntities(byte[] payload)
    {
        if (!IsHost || _t == null) return;
        _t.Send(NetProto.MsgEntitySnapshot, payload);
    }

    /// <summary>M4：广播一条实体同步消息（房主用；中继盲转发）。</summary>
    public static void BroadcastRelayData(byte[] payload)
    {
        if (!IsHost || _t == null) return;
        _t.Send(NetProto.MsgRelayData, payload);
    }

    /// <summary>M4：发送一条实体同步消息。任意成员都能发 —— 客机种的植物要同步给房主。
    /// 注意：中继盲转发不附带发送者 id，所以载荷里要自带 ownerId 才能拼出全局唯一 syncId。</summary>
    public static void SendSync(byte[] payload)
    {
        if (!InRoom) return;
        var t = Active();
        if (t == null) return;
        t.Send(NetProto.MsgRelayData, payload);
    }

    /// <summary>房主：开始对局（不能重复开）。返回 false 表示被本地规则拒绝。</summary>
    public static bool StartBattle(string levelKey)
    {
        if (!IsHost) { Notice("只有房主可以开始对局"); return false; }
        if (!InRoom || _t == null) { Notice("未在房间中，无法开始对局"); return false; }
        if (Started) { Notice("对局已开始，不能重复开始"); return false; }
        _t.Send(NetProto.MsgBattleStart, new BitWriter().WriteStr(levelKey ?? "").ToArray());
        return true;
    }

    /// <summary>房主：结束对局（Started 复位后方可再次开始）。</summary>
    public static void EndBattle()
    {
        if (!IsHost || _t == null || !Started) return;
        _t.Send(NetProto.MsgBattleEnd, new byte[0]);
    }

    /// <summary>发送自己的光标（世界坐标 ×8 定点，由 NetCursor 每 100ms 调用）。
    /// ★ 载荷必须自带发送者 id：MsgCursor 是 E2E 盲转发，中继只做原样广播、**不会补 id**。
    ///   唯一带发送者 id 的地方是密文外层，而 OpenFrame 会把它丢掉（`out ushort _`）。
    ///   旧版载荷只有 8 字节坐标 → 接收端把坐标低字节当成了玩家 id →
    ///   光标注册到一个不存在的玩家名下 → 下一秒被 SyncPeers 当“已离开”删掉 → **一个光标都看不到**。</summary>
    public static void SendCursor(int fx, int fy)
    {
        if (!InRoom) return;
        var t = Active();
        if (t == null) return;
        t.Send(NetProto.MsgCursor,
            new BitWriter().WriteU16(MyPlayerId)
                          .WriteU32(unchecked((uint)fx))
                          .WriteU32(unchecked((uint)fy)).ToArray());
    }

    public static void SendChat(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (text.Length > NetConstants.MaxChatBytes) text = text.Substring(0, NetConstants.MaxChatBytes);
        var t = Active();
        if (t == null) { NetLog.Warn("未联机，无法发送聊天"); return; }
        t.Send(NetProto.MsgChat, new BitWriter().WriteStr(text).ToArray());
    }

    /// <summary>当前实际使用的通道：只走中继。</summary>
    static RelayTransport Active()
    {
        return _t;
    }

    public static string ChannelName()
    {
        return _t != null ? "中继" : "无";
    }

    public static bool Authenticated
    {
        get { return _t != null && _t.Authenticated; }
    }

    // ================= 每帧驱动 =================
    public static void Tick(Node root)
    {
        if (_t == null) return;
        _t.Poll();

        // 等鉴权通过后再发送建房/加入
        if (_pendingAction != 0 && Authenticated)
        {
            int act = _pendingAction;
            _pendingAction = 0;
            if (act == 1)
                _t.Send(NetProto.MsgRoomCreateReq, new BitWriter().WriteByte((byte)Settings.MaxPlayers).WriteStr(MyNick).ToArray());
            else
                _t.Send(NetProto.MsgRoomJoinReq, new BitWriter().WriteStr(_pendingCode).WriteStr(MyNick).WriteStr(_pendingPass).ToArray());
            _signalingStartMs = Time.GetTicksMsec();
        }

        foreach (var kv in _t.Drain())
        {
            var r = new BitReader(kv.Value);
            switch (kv.Key)
            {
                case NetProto.MsgRoomCreateRes:
                {
                    byte ok = r.ReadByte();
                    uint roomId = r.ReadU32();
                    ushort pid = r.ReadU16();
                    RoomCode = r.ReadStr();
                    if (ok == 0)
                    {
                        MyPlayerId = pid;
                        LastError = "";
                        Notice("房间已创建 code=" + RoomCode + " roomId=" + roomId + " myId=" + pid);
                        ForceRoomState(NetState.Signaling);   // 兜底：建房成功就必须进入会话状态
                        // 房主即时应用本房间设置：房间禁用作弊时，房主自己同样受限
                        CheatPolicy.Apply(Settings ?? new RoomSettings());
                        // ★ 必须补发一次完整设置：MsgRoomCreateReq 里只带了 MaxPlayers + 昵称，
                        //   中继建房时其余字段全是默认值。不补发的话，紧接着中继广播回来的
                        //   MsgRoomSettings 会拿默认值覆盖房主的选择 —— 表现就是
                        //   「建房时勾了对战模式/人数平衡/口令，进房后全变回默认」。
                        SendRoomSettings(Settings ?? new RoomSettings());
                    }
                    else { LastError = "建房失败 code=" + ok; Notice(LastError); SetState(NetState.Ended); }
                    break;
                }
                case NetProto.MsgRoomJoinRes:
                {
                    byte ok = r.ReadByte();
                    ushort pid = r.ReadU16();
                    RoomCode = r.ReadStr();
                    if (ok == 0)
                    {
                        MyPlayerId = pid;
                        LastError = "";
                        // ★ 只报「加入成功」：指纹/内部 id 对玩家没意义（排查需要时看 mod_log）
                        Notice("加入成功：房间 " + RoomCode);
                        ForceRoomState(NetState.Signaling);   // 兜底：加入成功就必须进入会话状态
                    }
                    else
                    {
                        LastError = ok == NetProto.ErrBadPass ? "房间口令错误" : (ok == NetProto.ErrFull ? "房间已满或禁止中途加入" : "加入失败 code=" + ok);
                        Notice(LastError);
                        SetState(NetState.Ended);
                    }
                    break;
                }
                case NetProto.MsgRoomSettings:
                {
                    Settings = RoomSettingsCodec.Read(kv.Value);
                    TtlMinutesLeft = Settings.TtlMinutes;
                    CheatPolicy.Apply(Settings);
                    try { OnSettingsChanged?.Invoke(); } catch { }
                    break;
                }
                case NetProto.MsgPeerJoined:
                {
                    ushort id = r.ReadU16();
                    string nick = r.ReadStr();
                    Notice("玩家加入 id=" + id + " " + nick);
                    break;
                }
                case NetProto.MsgPeerLeft:
                {
                    ushort id = r.ReadU16();
                    Notice("玩家离开 id=" + id);
                    break;
                }
                case NetProto.MsgChat:
                {
                    string s = r.ReadStr();
                    NetLog.Info("[聊天] " + s);
                    try { OnChat?.Invoke(s); } catch { }
                    break;
                }
                case NetProto.MsgRoomClosingSoon:
                {
                    TtlMinutesLeft = r.ReadByte();
                    Notice("房主设定的存活时间即将结束：" + TtlMinutesLeft + " 分钟后房间自动销毁");
                    break;
                }
                case NetProto.MsgRoomClosed:
                {
                    byte reason = r.ReadByte();
                    string why = r.ReadStr();
                    Notice("房间已销毁：" + why);
                    CheatPolicy.Reset();
                    SetState(NetState.Ended);
                    break;
                }
                case NetProto.MsgKick:
                {
                    byte reason = r.ReadByte();
                    string why = r.ReadStr();
                    Notice("你已被移出房间：" + why);
                    CheatPolicy.Reset();
                    SetState(NetState.Ended);
                    break;
                }
                case NetProto.MsgStateSync:
                {
                    NetBattle.OnStateSync(kv.Value);
                    break;
                }
                case NetProto.MsgEntitySnapshot:
                {
                    NetMirror.OnSnapshot(kv.Value);
                    break;
                }
                case NetProto.MsgRelayData:
                {
                    // M4：真·共享战场实体同步；M4b：同一个通道里再分一个「准备状态」子类型
                    byte sub = (kv.Value != null && kv.Value.Length > 0) ? kv.Value[0] : (byte)0;
                    if (sub == NetProto.SyncReady)
                    {
                        var rr = new BitReader(kv.Value);
                        rr.ReadByte();
                        ushort rid = rr.ReadU16();
                        bool rdy = rr.ReadByte() != 0;
                        NetGate.OnReady(rid, rdy);
                    }
                    else if (sub == NetProto.SyncSun)
                    {
                        var sr = new BitReader(kv.Value);
                        sr.ReadByte();
                        NetSun.OnRemote(sr);
                    }
                    else if (sub == NetProto.SyncLevelFile)
                    {
                        var lr = new BitReader(kv.Value);
                        lr.ReadByte();
                        NetLevelShare.OnRemote(lr);
                    }
                    else if (sub == NetProto.SyncLevelAsk)
                    {
                        var ar = new BitReader(kv.Value);
                        ar.ReadByte();
                        NetLevelShare.OnRemoteAsk(ar);
                    }
                    else
                    {
                        NetMirror.OnSync(kv.Value);
                    }
                    break;
                }
                case NetProto.MsgPong:
                {
                    // 中继把 Ping 的 payload（序号）原样回显 → 用它算往返延迟
                    uint seq = r.ReadU32();
                    if (seq == _pingSeq && _pingSentMs > 0)
                    {
                        ulong nowMs = Time.GetTicksMsec();
                        if (nowMs >= _pingSentMs)
                        {
                            long rtt = (long)(nowMs - _pingSentMs);
                            if (rtt >= 0 && rtt < 60000) PingMs = (int)rtt;
                        }
                    }
                    break;
                }
                case NetProto.MsgStatsRes:
                {
                    // [u32 当前在线][u32 当日在线]
                    LiveCount = (int)r.ReadU32();
                    TodayCount = (int)r.ReadU32();
                    break;
                }
                case NetProto.MsgPlayerList:
                {
                    var list = PeerListCodec.Read(kv.Value);
                    int before = Players.Count;
                    Players.Clear();
                    Players.AddRange(list);
                    if (IsHost) DistributeRoomKey();
                    // ★ 有人新进房（含中途加入）：对局已经开始了就补推一次 user:// 关卡文件
                    //   —— 这类客机收不到开局那一刻的首次推送
                    if (IsHost && list.Count > before && Started && HostLevelKey.Length > 0)
                        NetLevelShare.StartPush(HostLevelKey);
                    break;
                }
                case NetProto.MsgRoomKeyWrap:
                {
                    // 房主发来的房间密钥包裹：只有目标成员能解开（中继同样解不开）
                    if (IsHost || !InRoom || _t == null) break;
                    byte[] hostPub = HostPublicKey();
                    if (hostPub == null) { NetLog.Warn("尚未拿到房主公钥，忽略房间密钥包裹"); break; }
                    if (_t.Secure.UnwrapRoomKeyFrom(hostPub, kv.Value))
                    {
                        // ★ 不再往 UI 报指纹（玩家只看“能联机”就行）
                        Notice("加入成功，已开启加密联机");
                    }
                    else
                    {
                        NetLog.Warn("房间密钥包裹解开失败（可能不是发给本机）");
                    }
                    break;
                }
                case NetProto.MsgRoomList:
                {
                    var list = RoomListCodec.Read(kv.Value);
                    RoomList.Clear();
                    RoomList.AddRange(list);
                    if (State == NetState.Lobby) _lobbyLastActiveMs = Time.GetTicksMsec();
                    try { OnRoomList?.Invoke(); } catch { }
                    break;
                }
                case NetProto.MsgBattleStart:
                {
                    BattleId = r.ReadU32();
                    HostLevelKey = r.ReadStr();
                    Started = true;
                    NetCursor.MarkBattleStarted();
                    NetBattle.OnBattleStart();
                    break;
                }
                case NetProto.MsgBattleEnd:
                {
                    BattleId = r.ReadU32();
                    Started = false;
                    NetBattle.OnBattleEnd();
                    Notice("房主已结束对局");
                    break;
                }
                case NetProto.MsgCursor:
                {
                    ushort cid = r.ReadU16();
                    int fx = unchecked((int)r.ReadU32());
                    int fy = unchecked((int)r.ReadU32());
                    NetCursor.OnRemoteCursor(cid, fx, fy);
                    break;
                }
                case NetProto.MsgSignalFrom:
                {
                    ushort from = r.ReadU16();
                    string addr = r.ReadStr();
                    PeerDirectAddr = addr;
                    NetLog.Info("收到信令 from=" + from + " addr=" + addr);
                    break;
                }
                case NetProto.MsgError:
                {
                    byte code = r.ReadByte();
                    string msg = r.ReadStr();
                    LastError = "中继错误 " + code + "：" + msg;
                    NetLog.Warn(LastError);
                    break;
                }
            }
        }

        // 直连优先：拿到对方地址就尝试直连，3 秒未建立则回落中继
        ulong now = Time.GetTicksMsec();
        if (State == NetState.Signaling && !_directTried && !string.IsNullOrEmpty(PeerDirectAddr))
        {
            _directTried = true;
            try { _direct?.Close(); } catch { }
            _direct = new DirectTransport();
            if (_direct.ConnectToHost(PeerDirectAddr))
                NetLog.Info("尝试直连 " + PeerDirectAddr + ":" + NetConstants.DirectPort);
            else { _direct = null; }
        }
        if (State == NetState.Signaling && _direct != null && _direct.IsConnected)
        {
            SetState(NetState.Direct);
            NetLog.Info("直连已建立，切换为直连通道");
        }
        else if (State == NetState.Signaling && now - _signalingStartMs >= DirectTimeoutMs)
        {
            ForceRoomState(NetSessionCore.NextOnDirectTimeout(State, relayAvailable: _t != null));
            if (State == NetState.Relay)
            {
                NetLog.Info("直连未建立，使用中继通道");
                try { _direct?.Close(); } catch { }
                _direct = null;
            }
        }

        // ★ 自愈：中继连接仍然存活却处于 Ended（旧版状态机漏洞会残留）→ 拉回大厅，
        //   否则界面会一直显示“未连接/已结束”，大厅也刷不出房间。
        if (State == NetState.Ended && _t != null && _pendingAction == 0)
        {
            bool alive = false;
            try { alive = _t.IsConnected; } catch { }
            if (alive)
            {
                NetLog.Warn("状态自愈：连接仍在线，Ended -> Lobby");
                ForceRoomState(NetState.Lobby);
            }
        }

        // 大厅：鉴权完成后自动补发一次房间列表请求；空闲超时断开连接（在房间内不受影响）
        if (_wantRoomList && Authenticated && !InRoom && _t != null)
        {
            _wantRoomList = false;
            _t.Send(NetProto.MsgRoomListReq, new byte[0]);
        }

        // 对战模式：未选阵营时自动落到人少的一边。
        // 降频到 5 秒：分边请求不是每帧的事，而中继有限流（且失败会回 MsgError）。
        if (State == NetState.Relay && _t != null && ++_factionTimer >= 300)
        {
            _factionTimer = 0;
            EnsureFactionAssigned();
        }
        if (State == NetState.Lobby && _t != null && now - _lobbyLastActiveMs > LobbyIdleMs)
        {
            NetLog.Info("大厅空闲超时，断开中继连接");
            CloseLobby();
        }

        // 心跳（1 秒）+ 延迟测量：中继会把 MsgPing 原样回显为 MsgPong
        // ★ 必须等鉴权完成再发：链路密钥未就绪时 Send 会被本地丢弃，
        //   若此时就刷新 _lastPingMs，会白白空转一整秒（表现为延迟一直是 --）。
        if (Authenticated && now - _lastPingMs >= 1000)
        {
            _lastPingMs = now;
            _pingSentMs = now;
            _t.Send(NetProto.MsgPing, new BitWriter().WriteU32(++_pingSeq).ToArray());
        }

        // 在线统计（默认 15 秒一次）：中继自己就能答，不需要已进房
        // ★ 同上：_lastStatsMs 初值为 0，连接后第一次 Tick 就会命中；
        //   若在鉴权前发送并被丢弃，却已刷新计时，玩家要再等满 15 秒才看到人数
        //   —— 这正是「联机人数统计不到人」的根因。
        if (Authenticated && now - _lastStatsMs >= StatsIntervalMs)
        {
            _lastStatsMs = now;
            _t.Send(NetProto.MsgStatsReq, new byte[0]);
        }

        // ★ 掉线检测（2 秒去抖）：中继踢人 / 网络断掉以前是**静默**的 ——
        //   玩家只看到“一进关就没了 / 完全没反应”，分不清是被踢、超限、还是网络断了。
        if (Authenticated && (State == NetState.Relay || State == NetState.Playing) && _t != null)
        {
            bool alive = false;
            try { alive = _t.IsConnected; } catch { }
            if (alive) _lostMs = 0;
            else if (_lostMs == 0) _lostMs = now;
            else if (now - _lostMs > 2000)
            {
                _lostMs = 0;
                NetLog.Warn("掉线：与中继的连接已断开 State=" + State + " " + NetBudget.Describe());
                Notice("与中继的连接已断开（网络中断，或被服务端限流踢出）");
                Leave();
            }
        }

        // 房主设置强制生效（客机每秒复核，被禁功能立即关闭）
        CheatPolicy.Tick(now);

        // M2：房主每秒广播对局状态（阳光/波次/僵尸数/关卡）
        NetBattle.HostTick(now);
    }
}
