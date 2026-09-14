using System;
using System.Collections.Generic;
using Godot;
using PvzheMod;

/// <summary>
/// M4：共享战场实体同步（**真实体**，不再是幽灵方块）。
///
/// 设计要点：
/// 1. 每个实体有一个**稳定的全局 syncId** = (发送者 playerId &lt;&lt; 32) | (Godot 实例 id 低 32 位)。
///    id 稳定 ⇒ 重复广播是幂等的（收到已见过的 syncId 直接忽略），补发/重传都安全。
/// 2. 双方都上报自己场上"新出现的"实体（植物双向、僵尸房主单向：客机刷怪已被清零）。
/// 3. 双方都上报自己场上"消失的"实体（铲子铲掉 / 被吃掉 / 清场）→ SyncDead。
///    只报自己**认得**的（种过的 / 镜像过来的），不会误删对方的。
/// 4. 载入期丢事件有兜底：房主每 2 秒把当场僵尸重播一遍。
///
/// 传输复用已有的 MsgRelayData(0x20)（中继本来就是盲转发，服务端零改动），
/// 载荷首字节是子类型：0x01=Spawn，0x02=Dead，0x03=Ready（Ready 归 NetGate）。
/// </summary>
public static class NetMirror
{
    const int TickMs = 120;          // 差分周期
    const int ResyncMs = 3000;       // 双方定期重播自己场上实体（给丢包/加载慢的一方补齐）
    const int ResyncMax = 24;        // ★ 单次补发上限。以前是 100/2s≈50帧/秒，叠加光标+阳光直接超中继限流 → 被踢
    const int SeenCap = 4096;        // 去重表上限（超了整体清空，宁可重放也不要无限涨）
    const int MaxPendingTries = 25;  // 生成重试上限（约 3 秒；再不行就放弃这条）

    sealed class Pending
    {
        public byte Kind;            // 0=僵尸 1=植物
        public string PacketId;
        public int Gx, Gy;
        public int Tries;
        public ulong SyncId;         // 远端那条事件的 syncId
        public bool HasWorld;        // 发送方带了世界坐标（僵尸用）
        public float WX, WY;
    }

    // ---- 去重 / 记账 ----
    static readonly Dictionary<ulong, byte> _seen = new();        // 收到过的远端 syncId（幂等去重）
    static readonly Dictionary<ulong, byte> _deadSeen = new();    // 收到过的 Dead syncId（避免回声）
    static readonly Dictionary<ulong, Node2D> _bySyncId = new();  // 远端 syncId → 本地克隆节点
    static readonly Dictionary<ulong, ulong> _syncOf = new();     // 本地节点 instanceId → syncId
    static readonly Dictionary<ulong, Node2D> _reported = new();  // 我们登记过的节点（检测消失用）
    static readonly Dictionary<ulong, byte> _mirrored = new();    // 由远端生成的节点（别回发）
    static readonly Dictionary<ulong, byte> _kindOf = new();      // 本地节点 instanceId → 种类（补发时要选对缓存）
    /// <summary>镜像节点的诞生时刻（instanceId → ms）。
    /// ★ 刚认领的镜像不能马上被当成“已消失”上报 —— 见 ReportRemovals。</summary>
    static readonly Dictionary<ulong, int> _born = new();
    static readonly List<Pending> _pending = new();

    static int _lastTickMs;
    static int _lastResyncMs;
    static int _lastZeroMs;
    static int _lastLogMs;
    static bool _stoppedLocalWaves;
    static string _stoppedLevel = "";

    /// <summary>发送/接收计数（面板与日志用）。</summary>
    public static int SentSpawns, RecvSpawns, SentDeads, RecvDeads, EchoSkips;
    /// <summary>房主拒收的“客机上报僵尸”次数（应该恒为 0；不为 0 说明上游还有漏洞）。</summary>
    public static int RejectedZombieSpawns;

    /// <summary>客机是否处于"本地刷怪已清零"状态（真正做过清零且对局进行中）。</summary>
    public static bool FreezeSpawnActive { get; private set; }

    // =====================================================================
    //  对外
    // =====================================================================

    public static void Reset()
    {
        _seen.Clear();
        _deadSeen.Clear();
        _bySyncId.Clear();
        _syncOf.Clear();
        _reported.Clear();
        _mirrored.Clear();
        _kindOf.Clear();
        _born.Clear();
        _pending.Clear();
        _lastTickMs = 0;
        _lastResyncMs = 0;
        _lastZeroMs = 0;
        FreezeSpawnActive = false;
        _stoppedLocalWaves = false;
        _stoppedLevel = "";
        NetBudget.Reset();
        GameCheats.NetWaveSpawnsRestore();   // 把客机被清零的刷怪还原（下一局还得正常刷）
    }

    /// <summary>主循环调用（OnFrame 里紧跟 NetSession.Tick 之后）。</summary>
    public static void Tick(Node root)
    {
        try
        {
            if (!NetSession.InRoom || NetSession.State == NetState.Offline || !NetSession.Started)
            {
                if (FreezeSpawnActive) StopFreeze();
                return;
            }
            if (root == null) return;

            int now = (int)(Time.GetTicksMsec() & 0x7FFFFFFF);
            if (now - _lastTickMs < TickMs) return;
            _lastTickMs = now;
            TickCore(now);
        }
        catch (Exception ex)
        {
            NetLog.Warn("镜像 Tick 异常：" + ex.GetType().Name + " " + ex.Message);
        }
    }

    static void TickCore(int now)
    {
        bool isHost = NetSession.IsHost;

        // 客机：完全不跑自己的波次（僵尸只认房主同步过来的）。
        // ★ 只在「进/换关卡」时做一次，**绝不周期性重写** ——
        //   之前每 2 秒重扫一遍波次配置，是在和游戏抢状态（一堆毛病的根源）。
        if (!isHost)
        {
            FreezeSpawnActive = GameCheats.NetInBattle();
            if (FreezeSpawnActive)
            {
                string lv = GameCheats.NetGetLevelKey();
                if (lv == null) lv = "";
                if (!_stoppedLocalWaves || lv != _stoppedLevel)
                {
                    GameCheats.NetNoLocalWaves(false);      // 换关卡后配置对象是新的，先解除再重做
                    if (GameCheats.NetNoLocalWaves(true))
                    {
                        _stoppedLocalWaves = true;
                        _stoppedLevel = lv;
                    }
                }
            }
        }
        else if (FreezeSpawnActive)
        {
            FreezeSpawnActive = false;
        }

        GameCheats.NetRefreshRoleCache();    // 差分必须在同一帧看到最新实体

        ResolvePending();
        ReportLocal();
        ReportRemovals();

        // 补发：双方对称（房主补僵尸+植物，客机补自己的植物）。
        if (now - _lastResyncMs >= ResyncMs)
        {
            _lastResyncMs = now;
            ResyncMine();
        }

        if (now - _lastLogMs >= 5000)
        {
            _lastLogMs = now;
            NetLog.Info(Describe());
        }
    }

    /// <summary>一行状态（面板/日志）。</summary>
    public static string Describe()
    {
        return "联机同步[" + (NetSession.IsHost ? "房主" : "客机") + "]: 发出=" + SentSpawns +
               " 收到=" + RecvSpawns + " 消失发出=" + SentDeads + " 消失收到=" + RecvDeads +
               " 拒收客机僵尸=" + RejectedZombieSpawns +
               " 待确认=" + _pending.Count + " 已镜像=" + _mirrored.Count + " 去重回声=" + EchoSkips +
               " 客机波次=" + (GameCheats.NetNoLocalWavesOn ? "已停用" : "正常") +
               " " + NetBudget.Describe();
    }

    /// <summary>节点是不是"从远端镜像过来的"。
    /// ★ 除了已认领的，**正在认领中的也要算**：
    ///   节点是 CallDeferred 延迟出现的，会先出现在缓存里，而 _mirrored 要等 ResolvePending 才写。
    ///   旧版只查 _mirrored → 刚生成还没认领的镜像会被 "清理本地刷怪" 当成自己的怪删掉，
    ///   然后 ReportRemovals 又把 Dead 发给房主 → **房主的真僵尸被删** → 表现就是“乱出怪 / 乱没怪”。</summary>
    public static bool IsMirroredEntity(Node n)
    {
        try
        {
            if (n == null) return false;
            ulong iid = n.GetInstanceId();
            if (_mirrored.ContainsKey(iid)) return true;
            ulong sid;
            if (_syncOf.TryGetValue(iid, out sid) && sid != 0) return true;
            // 认领中的镜像：按卡 id 比（比“有任意 pending”精确：
            // 否则只要有一条卡住的 pending，本地刷怪清理就永远不跑）
            if (n is Node2D n2)
            {
                string pid = GameCheats.NetGetNodePacketId(n2);
                if (!string.IsNullOrEmpty(pid) && MatchesPendingAny(pid)) return true;
            }
            return false;
        }
        catch { return false; }
    }

    /// <summary>有没有一条待认领的 Spawn 用的是这张卡（不分种类）。</summary>
    static bool MatchesPendingAny(string packetId)
    {
        for (int i = 0; i < _pending.Count; i++)
            if (string.Equals(_pending[i].PacketId, packetId, StringComparison.Ordinal)) return true;
        return false;
    }

    static int NowMs() { return (int)(Time.GetTicksMsec() & 0x7FFFFFFF); }

    /// <summary>节点是不是我们已经登记过的同步实体。</summary>
    public static bool IsSyncedEntity(Node n)
    {
        try { return n != null && _reported.ContainsKey(n.GetInstanceId()); }
        catch { return false; }
    }

    /// <summary>M2 遗留通道：老的幽灵快照。现在实体都是真同步了，这里只计数不回放。</summary>
    public static void OnSnapshot(byte[] payload)
    {
        EchoSkips++;
    }

    /// <summary>MsgRelayData 里非 Ready 的载荷都进这里，按首字节再分子类型。</summary>
    public static void OnSync(byte[] payload)
    {
        try
        {
            if (payload == null || payload.Length < 1) return;
            var r = new BitReader(payload);
            byte sub = r.ReadByte();
            if (sub == NetProto.SyncSpawn) OnRemoteSpawn(r);
            else if (sub == NetProto.SyncDead) OnRemoteDead(r);
        }
        catch (Exception ex)
        {
            NetLog.Warn("镜像收包异常：" + ex.GetType().Name + " " + ex.Message);
        }
    }

    // =====================================================================
    //  发送
    // =====================================================================

    static bool SendSpawn(ulong syncId, byte kind, bool charm, string packetId, Vector2I grid, bool hasWorld, float wx, float wy)
    {
        if (syncId == 0) return false;
        if (!NetBudget.TryTake()) return false;   // 超预算：这一帧不发，差分下一帧会重试
        try
        {
            var w = new BitWriter();
            w.WriteByte(NetProto.SyncSpawn);
            w.WriteU32((uint)(syncId & 0xFFFFFFFFUL));
            w.WriteU32((uint)(syncId >> 32));
            w.WriteByte(kind);
            w.WriteByte((byte)((charm ? 1 : 0) | (hasWorld ? 2 : 0)));
            w.WriteStr(packetId);
            w.WriteI16((short)grid.X);
            w.WriteI16((short)grid.Y);
            // 世界坐标（×1 像素，夹到 i16）：僵尸实际 x/y 靠它，格子只保证“在哪一排”
            w.WriteI16((short)ClampI16(wx));
            w.WriteI16((short)ClampI16(wy));
            NetSession.SendSync(w.ToArray());
            SentSpawns++;
            return true;
        }
        catch { return false; }
    }

    static int ClampI16(float v)
    {
        if (v < -32000f) return -32000;
        if (v > 32000f) return 32000;
        return (int)v;
    }

    /// <summary>取一个实体的"可同步坐标"：格子必须在草坪上（≥1），否则返回 false。
    /// ★ 绝不能退化成 (0,0) —— 那不是草坪格子，会让对方把僵尸生成在卡槽那一带。</summary>
    static bool TryGetSyncPos(Node2D n, out string pid, out Vector2I grid, out float wx, out float wy)
    {
        pid = null; grid = new Vector2I(-1, -1); wx = 0f; wy = 0f;
        try
        {
            pid = GameCheats.NetGetNodePacketId(n);
            if (string.IsNullOrEmpty(pid)) return false;
            grid = GameCheats.NetGetNodeGridPos(n);
            if (!GameCheats.NetIsGridOnLawn(grid)) return false;
            var w = GameCheats.NetGetNodeWorldPos(n);
            if (w.X > -1e6f) { wx = w.X; wy = w.Y; }
            return true;
        }
        catch { return false; }
    }

    static bool SendDead(ulong syncId)
    {
        if (syncId == 0) return false;
        if (!NetBudget.TryTake()) return false;   // 超预算：下次 Tick 再报
        try
        {
            var w = new BitWriter();
            w.WriteByte(NetProto.SyncDead);
            w.WriteU32((uint)(syncId & 0xFFFFFFFFUL));
            w.WriteU32((uint)(syncId >> 32));
            NetSession.SendSync(w.ToArray());
            SentDeads++;
            return true;
        }
        catch { return false; }
    }

    // =====================================================================
    //  上报：新出现的实体
    // =====================================================================

    static void ReportLocal()
    {
        // ★★ 僵尸**只有房主有权上报**！
        //   客机把自己关卡里“开局就带的僵尸”（prespawn / 波次停用前那几帧刷出来的）
        //   当成“我自己的”上报给房主 → 房主照着又生成一份
        //   → **房主场上凭空多出一堆僵尸**（用户反馈的“房主乱出怪”）。
        //   实测：客机一条状态行里 `发出=27` 全是这些幽灵。
        //   所以植物是双向的（谁种的谁上报），**僵尸是单向的：房主 → 客机**。
        ReportList(GameCheats.GetPlantCache(), 1);
        if (NetSession.IsHost) ReportList(GameCheats.GetZombieCache(), 0);
    }

    static void ReportList(List<Node2D> list, byte kind)
    {
        if (list == null) return;
        for (int i = 0; i < list.Count; i++)
        {
            var n = list[i];
            if (n == null) continue;
            ulong iid;
            try { iid = n.GetInstanceId(); } catch { continue; }
            if (_mirrored.ContainsKey(iid)) continue;   // 远端生成的，别回发
            if (_reported.ContainsKey(iid)) continue;   // 已经报过

            // 回声防护：刚收到 Spawn、节点是 CallDeferred 延迟出现的，
            // 在 ResolvePending 认领它之前会出现在缓存里 —— 认出来就跳过。
            if (MatchesPending(kind, GameCheats.NetGetNodePacketId(n), GameCheats.NetGetNodeGridPos(n)))
            {
                EchoSkips++;
                continue;
            }

            string pid; Vector2I g; float wx, wy;
            if (!TryGetSyncPos(n, out pid, out g, out wx, out wy))
            {
                // 解析不出卡 id / 坐标（投射物、还没落地的、格子不在草坪上的）——
                // 记下节点但不上报；★ 绝不能用 (0,0) 充数（那是卡槽一带）
                _reported[iid] = n;
                _syncOf[iid] = 0;
                continue;
            }

            // 我自己生成的实体：syncId 由「我的 playerId + 本地实例 id」拼出来，全局唯一且稳定
            ulong sid = ((ulong)NetSession.MyPlayerId << 32) | (iid & 0xFFFFFFFFUL);
            // ★ 发成功才记账：预算不足时先不记，下一帧自然会重试（否则这一条就永远丢了）
            if (!SendSpawn(sid, kind, GameCheats.NetGetNodeCharmed(n), pid, g, true, wx, wy)) continue;
            _reported[iid] = n;
            _syncOf[iid] = sid;
            _kindOf[iid] = kind;
        }
    }

    /// <summary>补发：把我方当前还活着的实体按原 syncId 再播一遍，给丢包/加载慢的一方补齐。
    /// syncId 稳定 ⇒ 对方收到重复包会自动忽略；没收到的就补上了。</summary>
    static void ResyncMine()
    {
        int sent = 0;
        sent += ResyncList(GameCheats.GetPlantCache(), 1, sent);
        // 僵尸只有房主会补发（客机场上的僵尸全是镜像，不需要也不应该补发）
        if (NetSession.IsHost && sent < ResyncMax) sent += ResyncList(GameCheats.GetZombieCache(), 0, sent);
    }

    static int ResyncList(List<Node2D> list, byte kind, int already)
    {
        if (list == null) return 0;
        int sent = 0;
        for (int i = 0; i < list.Count && already + sent < ResyncMax; i++)
        {
            var n = list[i];
            if (n == null) continue;
            ulong iid;
            try { iid = n.GetInstanceId(); } catch { continue; }
            if (_mirrored.ContainsKey(iid)) continue;       // 远端镜像过来的，不是我的

            ulong sid;
            if (!_syncOf.TryGetValue(iid, out sid) || sid == 0) continue;

            string pid; Vector2I g; float wx, wy;
            if (!TryGetSyncPos(n, out pid, out g, out wx, out wy)) continue;

            // 预算用完就收手，剩下的下一次补发（不记账，自然会重试）
            if (!SendSpawn(sid, kind, GameCheats.NetGetNodeCharmed(n), pid, g, true, wx, wy)) break;
            sent++;
        }
        return sent;
    }

    /// <summary>兼容旧的调用点。</summary>
    static void ResyncZombies() { ResyncMine(); }

    // =====================================================================
    //  上报：消失的实体
    // =====================================================================

    /// <summary>
    /// 本地已经消失的实体 → 广播 Dead。
    /// 这是「用铲子铲掉，对面还立着」的修复：
    /// 只同步了生成、没同步消失的话，对面那份副本永远不会消失。
    /// </summary>
    static void ReportRemovals()
    {
        if (_reported.Count == 0) return;

        List<ulong> gone = null;
        int nowMs = NowMs();
        foreach (var kv in _reported)
        {
            bool alive = false;
            try { alive = kv.Value != null && GodotObject.IsInstanceValid(kv.Value); } catch { }
            if (alive) continue;
            // ★ 刚认领不到 1.2 秒的镜像不算“消失”：
            //   这一小段时间里节点可能只是还没挂到场景树/还没完成初始化，
            //   误报 Dead 会把对面那份真实体删掉（两边一起乱）。
            int born;
            if (_born.TryGetValue(kv.Key, out born) && nowMs - born < 1200) continue;
            if (gone == null) gone = new List<ulong>();
            gone.Add(kv.Key);
        }
        if (gone == null) return;

        for (int i = 0; i < gone.Count; i++)
        {
            ulong iid = gone[i];
            ulong sid = 0;
            bool hasSid = _syncOf.TryGetValue(iid, out sid);
            // ★ 僵尸的“死”两个方向都报：客机的植物把它那边那份镜像打死了，就应该让房主也把它消掉
            //   （共享战场本来就是要一起打）。防的只是"误判成死了" —— 靠上面的 _born 保护。
            //   而“生”是单向的：客机永远不上报僵尸 Spawn（僵尸只可能由房主产生）。
            bool needNotify = hasSid && sid != 0 && !_deadSeen.ContainsKey(sid);

            // ★ 先确保能把 Dead 发出去再“忘记”这个实体，
            //   否则预算不足时这一条就永远不会再报了（对面那份副本永久残留：铲掉不消失）
            if (needNotify && !SendDead(sid)) continue;

            _reported.Remove(iid);
            _mirrored.Remove(iid);
            _kindOf.Remove(iid);
            _born.Remove(iid);
            if (hasSid) _syncOf.Remove(iid);
            if (sid != 0) _bySyncId.Remove(sid);
        }
    }

    // =====================================================================
    //  接收
    // =====================================================================

    static void OnRemoteSpawn(BitReader r)
    {
        uint lo = r.ReadU32();
        uint hi = r.ReadU32();
        ulong syncId = ((ulong)hi << 32) | lo;
        if (syncId == 0) return;

        byte kind = r.ReadByte();
        byte flags = r.ReadByte();
        string packetId = r.ReadStr();
        int gx = r.ReadI16();
        int gy = r.ReadI16();
        int wx = r.ReadI16();
        int wy = r.ReadI16();

        // 幂等：同一条 Spawn 重播（房主的周期补发、网络重传）只处理一次
        if (_seen.ContainsKey(syncId)) { EchoSkips++; return; }
        if (_deadSeen.ContainsKey(syncId)) return;          // 已经删了就别再生成

        if (string.IsNullOrEmpty(packetId)) return;

        // ★★ 房主不接客机的僵尸 Spawn：僵尸只可能由房主产生。
        //   就算客机那边出了什么绕不过的时序漏洞（遗漏上报），这里也能兜住 ——
        //   用户报的「房主乱出怪」根本不会再发生。
        if (kind == 0 && NetSession.IsHost) { RejectedZombieSpawns++; return; }

        // ★ 格子必须落在草坪上（≥1）。
        //   旧版给僵尸用了 (0,0) 兜底 —— 那不是草坪格子，会生成在卡槽那一带（用户实际报过）。
        //   宁可这一条不生成（等对方补发时会带上合法坐标），也不要生成到卡槽里。
        {
            var g = new Vector2I(gx, gy);
            if (!GameCheats.NetIsGridOnLawn(g)) return;
        }

        // ★ 先记账再生成：万一生成要等几帧，去重和回声防护都已经生效
        if (_seen.Count >= SeenCap) _seen.Clear();
        _seen[syncId] = 1;
        RecvSpawns++;

        bool charm = (flags & 1) != 0;
        bool hasWorld = (flags & 2) != 0;
        bool ok = GameCheats.NetSpawnEntity(packetId, new Vector2I(gx, gy), charm);
        if (!ok)
        {
            // 生成失败（例如关卡还在载入）→ 撤销记账，等对方补发
            _seen.Remove(syncId);
            RecvSpawns--;
            return;
        }
        _pending.Add(new Pending
        {
            Kind = kind, PacketId = packetId, Gx = gx, Gy = gy, Tries = 0, SyncId = syncId,
            HasWorld = hasWorld, WX = wx, WY = wy
        });
    }

    static void OnRemoteDead(BitReader r)
    {
        uint lo = r.ReadU32();
        uint hi = r.ReadU32();
        ulong syncId = ((ulong)hi << 32) | lo;
        if (syncId == 0) return;

        if (_deadSeen.Count >= SeenCap) _deadSeen.Clear();
        _deadSeen[syncId] = 1;
        RecvDeads++;

        Node2D n;
        if (!_bySyncId.TryGetValue(syncId, out n)) return;   // 不是我们镜像的（可能是我自己种的，本地已处理）
        _bySyncId.Remove(syncId);
        if (n == null) return;

        ulong iid;
        try { iid = n.GetInstanceId(); } catch { return; }
        _reported.Remove(iid);
        _mirrored.Remove(iid);
        _syncOf.Remove(iid);
        _kindOf.Remove(iid);
        _born.Remove(iid);
        GameCheats.NetFreeEntity(n);
    }

    // =====================================================================
    //  认领 / 兜底
    // =====================================================================

    /// <summary>把"已经生成出来"的节点认领为镜像副本（延迟一帧左右）。
    /// 匹配策略分两种：
    ///   僵尸（kind=0）：只比卡 id —— 僵尸不在格子里、而且一直在移动，比格子比不准。
    ///                   客机的本地刷怪已经被清零，场上任何"没认领过"的僵尸就只能是镜像。
    ///   植物（kind=1）：先比卡 id + 格子（容差 1）；重试多次后放宽成只比卡 id。</summary>
    static void ResolvePending()
    {
        if (_pending.Count == 0) return;
        var plants = GameCheats.GetPlantCache();
        var zombies = GameCheats.GetZombieCache();

        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            var p = _pending[i];
            var list = p.Kind == 1 ? plants : zombies;
            bool strict = p.Kind == 1 && p.Tries <= 8;
            Node2D found = null;

            if (list != null)
            {
                for (int k = 0; k < list.Count; k++)
                {
                    var n = list[k];
                    if (n == null) continue;
                    ulong iid;
                    try { iid = n.GetInstanceId(); } catch { continue; }
                    if (_mirrored.ContainsKey(iid) || _reported.ContainsKey(iid)) continue;
                    if (!string.Equals(GameCheats.NetGetNodePacketId(n), p.PacketId, StringComparison.Ordinal)) continue;
                    if (strict)
                    {
                        var g = GameCheats.NetGetNodeGridPos(n);
                        if (Math.Abs(g.X - p.Gx) > 1 || Math.Abs(g.Y - p.Gy) > 1) continue;
                    }
                    found = n;
                    break;
                }
            }

            if (found == null)
            {
                p.Tries++;
                if (p.Tries > MaxPendingTries) _pending.RemoveAt(i);
                continue;
            }

            ulong fid;
            try { fid = found.GetInstanceId(); } catch { _pending.RemoveAt(i); continue; }
            _mirrored[fid] = 1;
            _reported[fid] = found;
            _syncOf[fid] = p.SyncId;
            _kindOf[fid] = p.Kind;
            _bySyncId[p.SyncId] = found;
            _pending.RemoveAt(i);

            // 僵尸：格子只决定"吃哪一排"，实际 x/y 用发送方的世界坐标摆正。
            // 不摆的话镜像僵尸会全挤在格子中心（而且格子本身是反推出来的，只是近似）。
            if (p.Kind == 0 && p.HasWorld)
                GameCheats.NetPlaceEntity(found, p.WX, p.WY, p.Gx, p.Gy);

            _born[fid] = NowMs();   // 刚认领：短时间内不把它当成“已消失”上报
        }
    }

    /// <summary>是否有一条待认领的 Spawn 跟这个（种类/卡 id）吻合 —— 避免回声把自己生成的镜像又发回去。</summary>
    static bool MatchesPending(byte kind, string packetId, Vector2I g)
    {
        for (int i = 0; i < _pending.Count; i++)
        {
            var p = _pending[i];
            if (p.Kind != kind) continue;
            if (!string.Equals(p.PacketId, packetId, StringComparison.Ordinal)) continue;
            // 僵尸不比格子（僵尸不在格子里、还会移动，比了反而认不出来）
            if (kind == 1 && (Math.Abs(p.Gx - g.X) > 1 || Math.Abs(p.Gy - g.Y) > 1)) continue;
            return true;
        }
        return false;
    }

    static void StopFreeze()
    {
        FreezeSpawnActive = false;
        _lastZeroMs = 0;
        _stoppedLocalWaves = false;
        _stoppedLevel = "";
        GameCheats.NetNoLocalWaves(false);
        GameCheats.NetWaveSpawnsRestore();
    }
}
