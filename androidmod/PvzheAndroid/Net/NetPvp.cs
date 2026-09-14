using System;
using System.Collections.Generic;
using Godot;

namespace PvzheMod
{
    /// <summary>
    /// 对战模式（PvP）的战斗侧规则：标靶僵尸、免疫、僵尸方经济、胜负判定。
    ///
    /// 与 Net/ 下那些协议类（NetSession/NetPeers…）的分工：
    ///   · Net/ 负责「谁在哪一边」——阵营字段、分边仲裁、房间设置；
    ///   · 本文件负责「分好边之后仗怎么打」——实体规则与结算。
    ///
    /// 设计原则（与既有 MOD 一致）：规则判定一律走「方法头注入 + 这里返回 true/false」，
    /// 不去改写游戏的状态机；AOT 环境下禁 LINQ/Convert，全部手写循环与拼接。
    /// </summary>
    public static class NetPvp
    {
        // ================= 常量 =================

        /// <summary>标靶僵尸血量（用户指定）。它是对战里植物方的进攻目标，血量拉高才扛得住消耗。</summary>
        public const double TargetZombieHp = 6666.0;

        /// <summary>僵尸方的「老家」实体：右端后排站着不动的标靶僵尸。</summary>
        public const string TargetZombieKey = "ZombieTarget";

        // ================= 禁卡表 =================

        /// <summary>禁卡表文件：每行一个卡牌 id，# 开头为注释；分 [plant] / [zombie] 两节。
        /// 存在则用文件内容覆盖内置默认表，改完在游戏里执行 NetPvpReload 即可生效（不用重新注入）。</summary>
        public const string BanListPath = @"mod\pvp_ban.txt";

        /// <summary>内置默认禁卡表 —— 灰烬植物（一次性/爆炸，使用后自身消失）。
        ///
        /// ★ 刻意用**精确 id 枚举**而不是关键词匹配：实测按关键词筛会把持续型植物一起误伤，
        ///   例如 Jala 会命中 JalaTorch（火爆火炬，常驻）、Corn/Cob 会命中玉米投手与玉米加农炮
        ///   —— 那些都是正常卡，不该禁。所以这里逐个列，宁可少禁也不要误禁。
        ///   名单来源：游戏 PacketBankResource.json 的真实卡牌 id。</summary>
        static readonly string[] DefaultBannedPlants = {
            // 毁灭菇系
            "PlantDoomShroom", "PlantDoomShroomF", "PlantDoomShroomGar", "PlantDoomTanglekelp",
            "PlantHypnoDoomShroom", "PlantTabooDoomShroom",
            // 樱桃系
            "PlantCherryBomb", "PlantCherryBean", "PlantCherryMine", "PlantCherryPea",
            "PlantDisguiserCherry", "PlantEMPCherry", "PlantJalaCherryBomb",
            // 辣椒系
            "PlantJalapeno", "PlantJalapenopepe", "PlantGarlicJalapeno", "PlantJalaVase",
            // 窝瓜系
            "PlantSquash", "PlantSquashCandy", "PlantSquashKing", "PlantSquashVase",
            "PlantBungiSquash", "PlantGloomSquash", "PlantIceSquash", "PlantPotatoSquash",
            "PlantWallnutSquash", "PlantWallnutSquashBowling",
            // 雷系
            "PlantPotatoMine", "PlantSunMine", "PlantIceSunMine", "PlantGraveMine", "PlantMagnetMine",
            // 寒冰菇 / 炸弹系
            "PlantIceShroom", "PlantIceBomb", "PlantDiceBomb", "PlantJewelBomb",
            "PlantSunBomb", "PlantMagnetBomb", "PlantGarlicBomb", "PlantGravebomber",
        };

        /// <summary>内置默认禁卡表 —— 灰烬僵尸（携带一次性/爆炸效果，落地即炸或自爆）。</summary>
        static readonly string[] DefaultBannedZombies = {
            "ZombieBalloonBomb", "ZombieDiggerPotatoMine", "ZombieNormalDoomShroom",
            "ZombieNormalDoomShroomBlackHelmet", "ZombieNormalJalapeno", "ZombieNormalSquash",
            "ZombieSnorkleDoomTanglekelp", "ZombieZamboniIceShroom",
        };

        static List<string> _banPlants;
        static List<string> _banZombies;
        static bool _banLoaded;

        /// <summary>读禁卡表（失败回退内置默认）。首次访问时加载。</summary>
        static void EnsureBanList()
        {
            if (_banLoaded) return;
            _banLoaded = true;
            _banPlants = new List<string>();
            _banZombies = new List<string>();
            bool fromFile = false;
            try
            {
                if (Godot.FileAccess.FileExists(BanListPath))
                {
                    using (var fa = Godot.FileAccess.Open(BanListPath, Godot.FileAccess.ModeFlags.Read))
                    {
                        if (fa != null)
                        {
                            string text = fa.GetAsText();
                            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                            bool zombie = false;
                            for (int i = 0; i < lines.Length; i++)
                            {
                                string L = lines[i].Trim();
                                if (L.Length == 0 || L[0] == '#') continue;
                                if (L == "[zombie]") { zombie = true; continue; }
                                if (L == "[plant]") { zombie = false; continue; }
                                if (zombie) _banZombies.Add(L); else _banPlants.Add(L);
                            }
                            fromFile = _banPlants.Count > 0 || _banZombies.Count > 0;
                        }
                    }
                }
            }
            catch { fromFile = false; }

            if (!fromFile)
            {
                for (int i = 0; i < DefaultBannedPlants.Length; i++) _banPlants.Add(DefaultBannedPlants[i]);
                for (int i = 0; i < DefaultBannedZombies.Length; i++) _banZombies.Add(DefaultBannedZombies[i]);
            }
            Bootstrap.Log("对战禁卡表已加载（" + (fromFile ? "来自文件" : "内置默认") +
                          "）：植物 " + _banPlants.Count + " 张、僵尸 " + _banZombies.Count + " 张");
        }

        /// <summary>僵尸方卡槽默认带的僵尸卡。
        /// ★ 只放**确认存在**的 id（_types.txt 里能查到的）；
        ///   拿不到的会被上层 GetConfig 过滤掉，不会把卡槽弄坏。</summary>
        public static readonly string[] DefaultTrayIds = new string[]
        {
            "ZombieNormal", "ZombiePolevaulter", "ZombieFootball", "ZombieGargantuar",
            "ZombieImp", "ZombieBalloon", "ZombieBackup", "ZombieAxe",
            "ZombieAlien", "ZombieSkeletuar",
        };

        /// <summary>僵尸方卡槽该放哪些卡（已剔除对战禁卡）。
        /// 想调整卡槽内容改上面的 DefaultTrayIds 即可。</summary>
        public static List<string> ZombieTrayIds()
        {
            var outIds = new List<string>();
            for (int i = 0; i < DefaultTrayIds.Length; i++)
            {
                string id = DefaultTrayIds[i];
                if (string.IsNullOrEmpty(id)) continue;
                if (IsBannedCard(id, true)) continue;
                outIds.Add(id);
            }
            return outIds;
        }

        /// <summary>重新读取禁卡表（供 HTTP 命令 / 改完文件后热更）。</summary>
        public static void ReloadBanList() { _banLoaded = false; EnsureBanList(); }

        /// <summary>这个卡牌 id 是否在对战禁卡表里。
        ///
        /// ★ 匹配要容错：禁卡表里写的是**卡包 id**（如 `PlantDoomShroom`），
        ///   而运行时从卡牌 config 上读到的是 `saveKey`，两者前缀未必一致
        ///   （游戏自己的 NetGetNodePacketId 就是依次试 saveKey/id/name 再验证的，
        ///   说明 saveKey 常常不是合法卡包 id）。所以先精确比，再去掉 Plant/Zombie 前缀比。</summary>
        public static bool IsBannedCard(string packetId, bool zombie)
        {
            if (string.IsNullOrEmpty(packetId)) return false;
            EnsureBanList();
            var list = zombie ? _banZombies : _banPlants;
            string n = StripPrefix(packetId);
            for (int i = 0; i < list.Count; i++)
            {
                string b = list[i];
                if (string.Equals(b, packetId, StringComparison.Ordinal)) return true;
                if (n.Length > 0 && string.Equals(StripPrefix(b), n, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>去掉 Plant/Zombie 前缀（用于跨命名口径比较）。</summary>
        public static string StripPrefix(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Length > 5 && s.StartsWith("Plant", StringComparison.Ordinal)) return s.Substring(5);
            if (s.Length > 6 && s.StartsWith("Zombie", StringComparison.Ordinal)) return s.Substring(6);
            return s;
        }

        /// <summary>禁卡表文本（诊断/导出示意用）。</summary>
        public static string BanListText()
        {
            EnsureBanList();
            string s = "plant=" + _banPlants.Count + "|zombie=" + _banZombies.Count + "\n[plant]\n";
            for (int i = 0; i < _banPlants.Count; i++) s += _banPlants[i] + "\n";
            s += "[zombie]\n";
            for (int i = 0; i < _banZombies.Count; i++) s += _banZombies[i] + "\n";
            return s;
        }


        /// <summary>僵尸方的产阳光实体（阳光墓碑）。</summary>
        public const string SunTombKey = "GraveStoneTargetSun";

        /// <summary>标靶僵尸（僵尸方老家）生成在哪一列 —— 草坪最右端，与植物方的脑子（最左端）对称。
        /// 自适应地图：= 本关总列数。</summary>
        public static int TargetColumn { get { return MapCols(); } }
        /// <summary>标靶僵尸生成在哪一行（5 行草坪取中间）。</summary>
        public const int TargetRow = 3;

        // ---- 场地分区：僵尸方只占最右 ZombieColumns 列，其余归植物方 ----
        /// <summary>僵尸方占几列（用户要求：僵尸只留 3 列就够）。</summary>
        public const int ZombieColumns = 3;

        static int _mapColsCache, _mapColsAt;
        /// <summary>本关地图共有多少列（拿不到时回退 9）。缓存 3 秒 —— 种植校验是热路径，不想每次反射。</summary>
        public static int MapCols()
        {
            try
            {
                int now = System.Environment.TickCount;
                if (_mapColsCache > 0 && unchecked(now - _mapColsAt) < 3000) return _mapColsCache;
                int n = GameCheats.NetMapGridNumX();
                if (n > 0) { _mapColsCache = n; _mapColsAt = now; return n; }
            }
            catch { }
            return _mapColsCache > 0 ? _mapColsCache : 9;
        }

        /// <summary>植物最多能种到第几列（含）= 总列数 - 僵尸占列数。
        /// ★ 原来写死 4，导致分界线画到第 5 列左边（用户反馈“红线比僵尸可放置范围偏左一列”）。</summary>
        public static int PlantMaxColumn
        {
            get { int n = MapCols(); return n > ZombieColumns + 1 ? n - ZombieColumns : 6; }
        }

        /// <summary>僵尸最少能放到第几列（含）= 植物最大列 + 1。</summary>
        public static int ZombieMinColumn { get { return PlantMaxColumn + 1; } }

        /// <summary>每座阳光墓碑每次产出的阳光量。</summary>
        public const int SunPerTomb = 25;
        /// <summary>阳光墓碑的产出间隔（帧）。按 60fps 计约 6 秒一座一次。</summary>
        public const int SunIntervalFrames = 360;
        /// <summary>僵尸方开局阳光。</summary>
        public const int ZombieStartSun = 150;

        // ================= 状态 =================

        /// <summary>僵尸方专属阳光（对战模式下与植物方完全独立，不走 NetSun 权威同步）。
        ///
        /// ★ 惰性初始化：不能只在 Tick 里赋值 —— 主菜单下 GameCheats.OnFrame 会跳过整个战斗段，
        ///   而玩家是在房间里（而非关卡里）看卡槽面板的，那时 Tick 根本不跑。
        ///   所以首次读取且对战生效时就补上开局阳光。开新局时 Reset() 会重新置位。</summary>
        public static int ZombieSun
        {
            get { EnsureSunInit(); return _zombieSun; }
            private set { _zombieSun = value; _sunInit = true; }
        }
        static int _zombieSun;
        static bool _sunInit;

        /// <summary>首次读取阳光时补上开局值（仅在对战房间内）。</summary>
        static void EnsureSunInit()
        {
            if (_sunInit) return;
            if (!Active) return;
            _sunInit = true;
            _zombieSun = ZombieStartSun;
        }
        /// <summary>本局是否已给僵尸方发过开局阳光 / 是否已判定胜负。</summary>
        static bool _started, _resolved;
        static int _sunTimer;
        static bool _hadTarget;
        /// <summary>本局是否已经生成过标靶僵尸（每局只生成一次）。</summary>
        static bool _targetSpawned;
        /// <summary>「迟迟进不了战斗关卡」的诊断只报一次（1800 帧 ≈ 30 秒）。</summary>
        static int _targetDiagTimer;
        static bool _targetDiagLogged;
        /// <summary>本局是否已经清零过波次刷怪（对战中僵尸只能由玩家放置）。</summary>
        static bool _spawnsZeroed;
        static int _logCount;
        /// <summary>上一次生效的对局号：变了就说明是新一局，自动重置（不用反向依赖 NetSession 去调 Reset）。</summary>
        static uint _lastBattleId;

        /// <summary>是否处于对战对局中（在房间里 + 房主开了对战模式）。</summary>
        public static bool Active { get { return NetSession.InRoom && NetSession.IsBattleMode; } }

        /// <summary>我是不是僵尸方。</summary>
        public static bool IAmZombie { get { return NetSession.MyFaction == NetFaction.Zombie; } }

        // ================= 局势重置 =================

        /// <summary>每局开局重置（NetBattle 开战时调用）。</summary>
        public static void Reset()
        {
            ZombieSun = ZombieStartSun;
            _started = false;
            _resolved = false;
            _sunTimer = 0;
            _hadTarget = false;
            _targetSpawned = false;
            _targetNode = null;
            _targetRetry = 0;
            _targetRetryTimer = 0;
            _targetDiagTimer = 0;
            _targetDiagLogged = false;
            _scanDiagLogged = 0;
            _spawnsZeroed = false;
            _logCount = 0;
        }

        // ================= 僵尸方买卡与放置 =================

        /// <summary>上一条操作结果（供 UI / 诊断显示）。</summary>
        public static string LastMsg { get; private set; } = "";
        /// <summary>僵尸方手上选中的卡（UI 选中态用；放置时可显式传 id，不依赖它）。</summary>
        public static string HeldCard { get; private set; } = "";

        /// <summary>取一张僵尸卡的官方价格（读不到按 0 算 —— 僵尸卡在原版不是买来的，
        /// 官方数据可能没给价，按 0 处理免得整张卡不可用）。</summary>
        public static int ZombieCardCost(string id)
        {
            int c = GameCheats.PacketCost(id);
            return c < 0 ? 0 : c;
        }

        /// <summary>选一张僵尸卡到手上。
        ///
        /// ★ 这里**不扣**阳光 —— 阳光在 TryPlaceZombie 放置成功时才扣。
        ///   原实现买卡和放置各扣一次，买一张放一次会被扣两遍（重复计费）。
        ///   改成"放置时结算"也更贴近原版：买而不放不该白扣。
        ///   仍然在这里预检阳光，好让 UI 能立刻提示"阳光不足"而不必等到放置。</summary>
        public static bool TryBuyZombie(string id)
        {
            if (!Active) { LastMsg = "不在对战房间"; return false; }
            if (!IAmZombie) { LastMsg = "只有僵尸方能买僵尸卡"; return false; }
            if (string.IsNullOrEmpty(id)) { LastMsg = "卡 id 为空"; return false; }
            // ★ 阵营锁定：僵尸方只能拿僵尸卡，植物卡一律拒绝
            if (!GameCheats.IsZombieCardId(id)) { LastMsg = "僵尸方只能选僵尸卡"; return false; }
            if (IsBannedCard(id, true)) { LastMsg = "该卡在对战中被禁用"; return false; }
            int cost = ZombieCardCost(id);
            if (ZombieSun < cost) { LastMsg = "阳光不足：需要 " + cost + "，现有 " + ZombieSun; return false; }
            HeldCard = id;
            LastMsg = "已选中 " + GameCheats.PacketBrief(id) + "，点落点格放置（放置时扣 " + cost + "）";
            return true;
        }

        /// <summary>取消手上选中的卡。</summary>
        public static void ClearHeldCard()
        {
            HeldCard = "";
            LastMsg = "已取消选中";
        }

        /// <summary>僵尸方放置僵尸到指定格子（放置成功才扣阳光）。
        /// 分区校验：只能放右半场（第 <see cref="ZombieMinColumn"/> 列及以右）。
        /// 格子占用校验：同格已有僵尸则拒绝（避免叠怪）。</summary>
        public static bool TryPlaceZombie(string id, int gx, int gy)
        {
            if (!Active) { LastMsg = "不在对战房间"; return false; }
            if (!IAmZombie) { LastMsg = "只有僵尸方能放僵尸"; return false; }
            if (string.IsNullOrEmpty(id)) { LastMsg = "卡 id 为空"; return false; }
            // ★ 阵营锁定：僵尸方只能放僵尸卡
            if (!GameCheats.IsZombieCardId(id)) { LastMsg = "僵尸方只能放僵尸卡"; return false; }
            if (IsBannedCard(id, true)) { LastMsg = "该卡在对战中被禁用"; return false; }
            if (gx < ZombieMinColumn) { LastMsg = "僵尸只能放在右半场（第 " + ZombieMinColumn + " 列及以右）"; return false; }
            if (gy < 1 || gy > 8) { LastMsg = "行号非法：" + gy; return false; }
            if (IsCellOccupied(gx, gy)) { LastMsg = "该格已有僵尸"; return false; }
            int cost = ZombieCardCost(id);
            if (ZombieSun < cost) { LastMsg = "阳光不足：需要 " + cost + "，现有 " + ZombieSun; return false; }

            var pos = new Vector2I(gx, gy);
            if (!GameCheats.NetSpawnEntity(id, pos, false)) { LastMsg = "放置失败（" + id + "）"; return false; }
            ZombieSun -= cost;
            LastMsg = "已放置 " + GameCheats.PacketBrief(id) + " 于 (" + gx + "," + gy + ")，剩余阳光 " + ZombieSun;
            Log("僵尸方放置 " + id + " @(" + gx + "," + gy + ") 花费 " + cost + " 剩余 " + ZombieSun);
            return true;
        }

        /// <summary>给僵尸方直接加阳光（调试/补偿用）。</summary>
        public static void AddZombieSun(int v) { if (v != 0) ZombieSun = System.Math.Max(0, ZombieSun + v); }

        /// <summary>该格是否已有僵尸（用镜像层的格子坐标反查）。</summary>
        static bool IsCellOccupied(int gx, int gy)
        {
            try
            {
                var tree = Engine.GetMainLoop() as SceneTree;
                var root = tree != null ? tree.Root : null;
                if (root == null) return false;
                var zombies = new System.Collections.Generic.List<object>();
                var tombs = new System.Collections.Generic.List<object>();
                Collect(root, zombies, tombs);
                for (int i = 0; i < zombies.Count; i++)
                {
                    var n = zombies[i] as Node2D;
                    if (n == null) continue;
                    var p = GameCheats.NetGetNodeGridPos(n);
                    if (p.X == gx && p.Y == gy) return true;
                }
            }
            catch { }
            return false;
        }

        // ================= 实体识别 =================

        /// <summary>取角色的卡牌 id（config.saveKey 优先；与 PacketShowId 同思路但作用于角色实例）。</summary>
        static string CharKey(object node)
        {
            try
            {
                if (node == null) return null;
                object cfg = GameCheats.PvpReadField(node, "config");
                if (cfg == null) cfg = GameCheats.PvpReadField(node, "characterConfig");
                if (cfg != null)
                {
                    string v = GameCheats.PvpReadField(cfg, "saveKey") as string;
                    if (!string.IsNullOrEmpty(v)) return v;
                    v = GameCheats.PvpReadField(cfg, "name") as string;
                    if (!string.IsNullOrEmpty(v)) return v;
                }
                // 兜底：节点自己的 saveKey / 类名（ZombieTarget 的场景类名通常就含 ZombieTarget）
                string s = GameCheats.PvpReadField(node, "saveKey") as string;
                if (!string.IsNullOrEmpty(s)) return s;
                var n = node as Node;
                return n != null ? n.GetType().Name : null;
            }
            catch { return null; }
        }

        /// <summary>这个角色是不是僵尸方的「老家」标靶僵尸。
        ///
        /// ★ 顺序很重要：先比**引用**（最可靠），再退到字符串比对。
        ///   免疫注入（Hypnoses / IsHardControlImmune）跑在游戏自己的调用点上，
        ///   那时传进来的可能是子节点或包装对象，CharKey 读不到 config 就会当成"不是标靶"
        ///   → 免疫整灶失效（用户反馈：不免疫魅惑等等控制）。
        ///   标靶是我们自己生成的，引用一比就准，不依赖任何字符串。</summary>
        public static bool IsTargetZombie(object node)
        {
            try
            {
                if (node == null) return false;
                if (_targetNode != null && GodotObject.IsInstanceValid(_targetNode))
                {
                    var n = node as Node;
                    if (n != null && ReferenceEquals(n, _targetNode)) return true;
                    if (n != null && _targetNode.IsAncestorOf(n)) return true;
                }
                return IsTargetByKey(node);
            }
            catch { return false; }
        }

        /// <summary>按卡牌 key / 类型名认标靶（不依赖 _targetNode）。</summary>
        static bool IsTargetByKey(object node)
        {
            try
            {
                string k = CharKey(node);
                if (!string.IsNullOrEmpty(k) && k.IndexOf(TargetZombieKey, StringComparison.Ordinal) >= 0) return true;
                string tn = node.GetType().Name;
                return tn.IndexOf(TargetZombieKey, StringComparison.Ordinal) >= 0;
            }
            catch { return false; }
        }

        /// <summary>这个角色是不是僵尸方的产阳光实体（阳光墓碑）。</summary>
        public static bool IsSunTomb(object node)
        {
            try
            {
                string k = CharKey(node);
                return !string.IsNullOrEmpty(k) && k.IndexOf(SunTombKey, StringComparison.Ordinal) >= 0;
            }
            catch { return false; }
        }

        // ================= 免疫（patcher 注入判定入口）=================

        /// <summary>魅惑免疫：patcher 注入 TowerDefenseCharacter.Hypnoses 方法头
        ///   if (NetPvp.ShouldBlockCharm(this)) return;
        /// 标靶僵尸是植物方的进攻目标，若能魅惑就等于植物方白嫖一个 6666 血的肉盾，
        /// 而且它会掉头打僵尸 —— 对战立刻失衡，所以必须免疫。</summary>
        public static bool ShouldBlockCharm(object node)
        {
            try
            {
                if (!Active) return false;
                if (!IsTargetZombie(node)) return false;
                if (_charmLogged < 2) { _charmLogged++; Log("标靶僵尸免疫魅惑（命中）"); }
                return true;
            }
            catch { return false; }
        }
        static int _charmLogged;

        /// <summary>硬控免疫：patcher 注入 TowerDefenseCharacter.get_IsHardControlImmune 方法头
        ///   if (NetPvp.ShouldImmuneHardControl(this)) return true;
        /// base 实现恒为 false、由子类覆盖，所以只有没覆盖的子类会走到这里 —— 正好是标靶僵尸。</summary>
        public static bool ShouldImmuneHardControl(object node)
        {
            try
            {
                if (!Active) return false;
                return IsTargetZombie(node);
            }
            catch { return false; }
        }

        // ================= 每帧 =================

        /// <summary>每帧（未在对战中时零开销）。</summary>
        public static void Tick(Node root)
        {
            if (!Active || root == null) return;
            try
            {
                // 新一局：对局号变了就重置局势（避免依赖其它模块主动调 Reset）
                if (NetSession.BattleId != _lastBattleId)
                {
                    _lastBattleId = NetSession.BattleId;
                    Reset();
                }

                if (!_started)
                {
                    _started = true;
                    ZombieSun = ZombieStartSun;
                    Log("对战开始：僵尸方开局阳光 " + ZombieStartSun + "，标靶僵尸血量 " + (int)TargetZombieHp);
                }

                // ① 标靶僵尸（僵尸方老家）：进关后在草坪最右端生成一次。
                //    ★ 这一步是必须的 —— 普通关卡天生不会有这个实体，不生成就没有「植物方要打的目标」，
                //      胜负判定也就永远触发不了。与植物方最左端的脑子对称。
                //    ★ 不要求已开战（NetSession.Started）：玩家的心智是“选了对战进关就是对战”，
                //      再要求房主额外点一次“开始对局”才行的话，表现就是“没有标靶僵尸”。
                // ★ 标靶生成：**统一成一个重试循环**，不再用 _targetSpawned 做门闩。
                //
                //   上一版的 bug：第一次尝试一进来就把 _targetSpawned 置 true，
                //   而补生成写的是 `else if (!_targetSpawned && ...)` —— 永远进不去。
                //   结果第一次太早（关卡还没就绪，SpawnTargetZombie 拿不到草坪/容器）失败后
                //   就再也不会重试，表现就是“标靶第一次不刷新，要重开一把才有”。
                //
                //   现在的判据是“标靶是否真的在场上”：不在就每 30 帧试一次，最多 20 次。
                //   这样无论关卡就绪得早还是晚，最终都会生成一次；生成成功就自然停止(不会乱刷)。
                {
                    bool alive = _targetNode != null && GodotObject.IsInstanceValid(_targetNode) && _targetNode.IsInsideTree();
                    if (!alive && GameCheats.NetIsInBattleLevel())
                    {
                        if (TargetPlacedByLevel)
                        {
                            if (!_targetDiagLogged) { _targetDiagLogged = true; Log("标靶由关卡 PreSpawn 摆放，跳过手动生成"); }
                        }
                        else if (_targetRetry < 20 && ++_targetRetryTimer >= 30)
                        {
                            _targetRetryTimer = 0;
                            _targetRetry++;
                            Log("标靶不在场上，尝试生成第 " + _targetRetry + " 次");
                            SpawnTargetZombie();
                        }
                    }
                }
                // 诊断：迟迟进不了战斗关卡就说一声，否则只能看到“没标靶”而不知道卡在哪一步
                // 诊断：只有**20 次重试都失败**才报，否则会误报。
                //   （原来写成 `_targetRetry == 0` —— 标靶活着时 retry 就是 0，
                //     结果每次都打“标靶僵尸未生成”，把我自己也误导了。）
                if (!_targetDiagLogged && _targetRetry >= 20)
                {
                    _targetDiagLogged = true;
                    Log("标靶僵尸未生成：在战斗关=" + GameCheats.NetIsInBattleLevel() +
                        " Active=" + Active + " 阵营=" + NetSession.MyFaction);
                    Bootstrap.FlushLog();
                }

                // ② 禁止系统自动出僵尸：对战中僵尸只能由僵尸方玩家放置。
                //   客机本来就靠 NetMirror 清零了，但**房主也得清** —— 否则房主那边波次
                //   还在源源不断刷怪，与玩家放的重叠，完全不是对战的节奏。
                //   NetZeroLocalSpawns 内部会快照原值，退房/换局时能完整还原。
                // ② 禁止系统自动出僵尸：对战中僵尸只能由僵尸方玩家放置（房主客机都要）。
                //   ★ 这里原来是 `_spawnsZeroed = true;` 写在调用**之前** —— 一次性门闩。
                //     只要第一次调用时关卡还没就绪（NetZeroLocalSpawns 返回 false），
                //     就永远不会重试，波次照常刷怪（用户反馈“又开始乱刷怪了”）。
                //     改成“成功才置位”，失败就每帧重试。
                if (!_spawnsZeroed && GameCheats.NetIsInBattleLevel())
                {
                    if (GameCheats.NetZeroLocalSpawns())
                    {
                        _spawnsZeroed = true;
                        Log("已禁止系统自动出僵尸（对战中僵尸只由玩家放置）");
                    }
                }

                // 每 30 帧扫一次场景（与 MOD 其它功能同频，避免每帧全树递归）
                if (++_sunTimer % 30 != 0) return;

                // ★ 扫描根用**整个场景树**，不用传进来的 root。
                //   “僵尸数=0”从第一天起就是这个原因：传进来的 root 不一定包含角色节点，
                //   递归自然扫不到任何僵尸，于是标靶、血量、免疫、结算全跟着失效。
                //   （GetTreeRoot() 是项目里取树根的现有写法。）
                Node scanRoot = root;
                try { var tr = GameCheats.NetTreeRoot(); if (tr != null) scanRoot = tr; } catch { }
                var zombies = new List<object>();
                var tombs = new List<object>();
                Collect(scanRoot, zombies, tombs);

                // 诊断：标靶生成成功却扫不到它 —— 血量/免疫都靠这个查找，扫不到就整灶失效。
                // 把实际扫到的僵尸类型名与卡牌 key 打出来，一眼就能看出命名到底长什么样。
                if (_targetNode == null && _scanDiagLogged < 2)
                {
                    _scanDiagLogged++;
                    string d = "";
                    for (int i = 0; i < zombies.Count && i < 4; i++)
                    {
                        try { d += "[" + zombies[i].GetType().Name + " key=" + CharKey(zombies[i]) + "]"; }
                        catch { }
                    }
                    Bootstrap.Log("对战扫描: 僵尸数=" + zombies.Count + " 未认到标靶 " + d);
                    Bootstrap.FlushLog();
                }

                // ① 标靶僵尸：扫描只用来补记引用，**不再刷血量**。
                //    ★ 原来这里有一句 EnforceHp(z)，每 30 帧把血量拉回 6666 ——
                //      结果就是玩家打掉一点、半秒后又满血，表现为“标靶无限回血”。
                //      血量只在生成时设一次（SpawnTargetZombie 里已经设了）。
                for (int i = 0; i < zombies.Count; i++)
                {
                    var z = zombies[i];
                    if (!IsTargetByKey(z)) continue;
                    if (_targetNode == null || !GodotObject.IsInstanceValid(_targetNode)) _targetNode = z as Node2D;
                }

                // ② 阳光墓碑产阳光（只有僵尸方需要，其它阵营不累积）
                if (IAmZombie && ++_sunTimer % SunIntervalFrames == 0)
                {
                    int gain = tombs.Count * SunPerTomb;
                    if (gain > 0) ZombieSun += gain;
                }

                // ③ 胜负：暂时**不结算**。
                //   用户明确要求“标靶死了不结算” —— 现在打死标靶不该弹获胜提示、不该结束对局。
                //   只留一行日志方便观察，胜负规则等玩法定下来再补。
                bool hasTarget = false;
                for (int i = 0; i < zombies.Count; i++) if (IsTargetZombie(zombies[i])) { hasTarget = true; break; }
                if (_hadTarget && !hasTarget && !_resolved)
                {
                    _resolved = true;
                    Log("标靶僵尸被击毁（按当前设定不结算）");
                }
                else if (hasTarget && !_hadTarget)
                {
                    _hadTarget = true;
                }
            }
            catch (Exception ex) { LogErr(ex); }
        }

        /// <summary>收集场上的僵尸与阳光墓碑（一次遍历，两个结果）。</summary>
        static void Collect(Node n, List<object> zombies, List<object> tombs)
        {
            if (n == null) return;
            try
            {
                var children = n.GetChildren(true);
                for (int i = 0; i < children.Count; i++)
                {
                    var c = children[i];
                    if (c == null) continue;
                    // 只用类型名粗筛，避免对每个节点做反射取值（场景树很大）
                    string tn = c.GetType().Name;
                    if (tn.IndexOf("Zombie", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        zombies.Add(c);
                        if (IsSunTomb(c)) tombs.Add(c);
                    }
                    else if (IsSunTomb(c))
                    {
                        tombs.Add(c);
                    }
                    var cc = c.GetChildren(true);
                    if (cc != null && cc.Count > 0) Collect(c, zombies, tombs);
                }
            }
            catch { }
        }

        /// <summary>在草坪最右端生成僵尸方的「老家」标靶僵尸（每局一次）。
        ///
        /// 用 GameCheats.NetSpawnEntity —— 它内部走游戏自己的 SpawnCharacter，
        /// 与联机镜像同步用的是同一条已验证路径；生成的实体会自然进入场景树，
        /// 随后被本类每 30 帧的扫描找到并强制血量。
        ///
        /// ★ 坐标会被 NetSpawnEntity 夹到合法草坪范围（X≥1、Y∈[1,8]），
        ///   所以不同尺寸的关卡不会因为越界而把它生成到卡槽那一带。</summary>
        static void SpawnTargetZombie()
        {
            try
            {
                int row = ResolveTargetRow();

                // ★★ 用游戏自己的预置生成类，而不是我自己拼 Plant 参数。
                //    反编译 TowerDefenseLevelPreSpawnConfig.SpawnCharacter 看到游戏的实际调用是：
                //      packetConfig.Plant(_gridPos, playAudio:false, noLimit:true,
                //                         default(EconomyAccountId), skipPlacementCheck:false, editorPreviewMode)
                //    而我原来用 FillArgs 拼的是 (gridPos, true, true) —— 参数完全对不上，
                //    结果就是诊断里那句“返回=TowerDefenseZombieTarget 不在场景树”：节点造出来了但进不了场景树。
                //    现在直接让游戏自己走它的预置路径。
                var t = GameCheats.NetFindType("TowerDefenseLevelPreSpawnConfig");
                if (t == null) { Log("标靶：找不到 TowerDefenseLevelPreSpawnConfig"); return; }

                object cfg = Activator.CreateInstance(t);
                var dict = new Godot.Collections.Dictionary();
                dict["Name"] = TargetZombieKey;
                dict["GridPos"] = new Godot.Collections.Array { TargetColumn, row };
                dict["CharacterOverride"] = new Godot.Collections.Dictionary();
                var init = t.GetMethod("Init", new Type[] { typeof(Godot.Collections.Dictionary) });
                if (init == null) { Log("标靶：TowerDefenseLevelPreSpawnConfig.Init 签名不符"); return; }
                init.Invoke(cfg, new object[] { dict });

                var sc = t.GetMethod("SpawnCharacter", new Type[] { typeof(Vector2I), typeof(bool) });
                if (sc == null) { Log("标靶：找不到 SpawnCharacter"); return; }
                object ch = sc.Invoke(cfg, new object[] { new Vector2I(TargetColumn, row), false });

                // ★ 补插入：SpawnCharacter 会造出角色，但实测它**不进场景树**
                //   （上一轮日志：僵尸总数=10，里面就是没有标靶）。
                //   挂载位置有源码依据：AddWarningColumn 里用的是
                //   TowerDefenseManager.GetCharacterNode() —— 那才是角色节点容器。
                if (ch is Node2D nd && GodotObject.IsInstanceValid(nd) && !nd.IsInsideTree())
                {
                    var cn = GameCheats.NetCharacterNode();
                    if (cn != null)
                    {
                        cn.AddChild(nd);
                        Log("标靶补插入 → " + cn.GetType().Name + " 节点类型=" + nd.GetType().Name +
                            "（在树里=" + nd.IsInsideTree() + "）");
                    }
                    else Log("标靶补插入失败：拿不到 GetCharacterNode()");
                }

                // ★★ 生成时直接把引用记下来 —— 不再依赖“扫描去认”。
                //   扫描要按类型名/卡牌 key 匹配，标靶的类名或 key 只要有一点不同就认不出来
                //   （上一轮就是：扫到 10 个僵尸，一个都没认成标靶）。
                //   这是我们自己造出来的对象，比引用最直接，血量与免疫都靠它。
                if (ch is Node2D t2 && GodotObject.IsInstanceValid(t2))
                {
                    _targetNode = t2;
                    EnforceHp(ch);
                    Log("标靶引用已记录（类=" + t2.GetType().Name + "，在树里=" + t2.IsInsideTree() + "）");
                }

                Log(ch != null
                    ? ("标靶僵尸已生成（游戏预置路径） (" + TargetColumn + "," + row + ")")
                    : ("标靶僵尸生成失败：Plant 返回空（" + TargetZombieKey + "）"));
                Bootstrap.FlushLog();
            }
            catch (Exception ex) { LogErr(ex); }
        }

        /// <summary>标靶该放哪一行：取**草坪的实际中间行**。
        ///
        /// ★ 原来是写死 TargetRow=3。不同关卡草坪行数不一样，写死就会“只有某一行能出标靶、
        ///   换一行就不行”（用户反馈）。这里用已验证可行的做法（NetWorldToGrid 扫世界坐标）
        ///   探出草坪的最小/最大行号，取中间 —— 与关卡尺寸无关。
        ///   （不能用 NetGridToWorld：实测它恒返回 0，不可用。）</summary>
        static int ResolveTargetRow()
        {
            try
            {
                int minR = 99, maxR = 0;
                for (float y = -1000; y <= 2400; y += 20)
                {
                    for (float x = 0; x <= 3600; x += 400)
                    {
                        var g = GameCheats.NetWorldToGrid(new Vector2(x, y));
                        if (g.X <= 0 || g.Y < 1 || g.Y > 8) continue;
                        if (g.Y < minR) minR = g.Y;
                        if (g.Y > maxR) maxR = g.Y;
                    }
                }
                if (maxR >= minR && minR <= 8)
                {
                    int mid = (minR + maxR) / 2;
                    if (mid != TargetRow) Log("标靶行号按草坪调整：行 " + minR + "~" + maxR + " → 取中 " + mid);
                    return mid;
                }
            }
            catch { }
            return TargetRow;
        }

        /// <summary>把血量与血量上限一起设成 TargetZombieHp。</summary>
        ///
        /// ★ 血不在**节点**上，在 node.instance 上（hitpoints / hitpointsBase）——
        ///   上一版直接往节点上写 hp/maxHp，那些字段根本不存在，PvpWriteField 全部返回 false，
        ///   于是血量一直是原版的（用户反馈：标靶正常血）。日志可证：从没出现过“血量已设为”。
        ///   （同一套字段在“直接设置植物血量”的 ApplyPlantHpDirect 里已经验证过。）
        static void EnforceHp(object z)
        {
            try
            {
                object inst = null;
                try { inst = GameCheats.PvpReadField(z, "instance"); } catch { }
                if (inst == null)
                {
                    if (_hpLogged < 3) { _hpLogged++; Log("标靶血量设置失败：拿不到 instance"); }
                    return;
                }
                bool a = GameCheats.PvpWriteField(inst, "hitpoints", TargetZombieHp);
                bool b = GameCheats.PvpWriteField(inst, "hitpointsBase", TargetZombieHp);
                if ((a || b) && _hpLogged < 3) { _hpLogged++; Log("标靶血量已设为 " + (int)TargetZombieHp + "（inst=" + inst.GetType().Name + "）"); }
                else if (!a && !b && _hpLogged < 3)
                {
                    _hpLogged++;
                    Log("标靶血量字段没找到（inst=" + inst.GetType().Name + "，hitpoints=" + a + " hitpointsBase=" + b + "）");
                }
            }
            catch { }
        }
        static int _hpLogged;
        /// <summary>标靶补生成计数（最多 5 次，避免“乱刷”）。</summary>
        static int _targetRetry, _targetRetryTimer;
        /// <summary>“扫到僵尸但认不出标靶”的诊断只报两次。</summary>
        static int _scanDiagLogged;

        /// <summary>标靶已由关卡自己的 PreSpawn 摆好（那就不用手动生成）。</summary>
        public static bool TargetPlacedByLevel;

        /// <summary>本局标靶僵尸的节点引用。
        /// ★ 靠“自己生成的那个就是标靶”来认，比字符串比对可靠得多：
        ///   CharKey 读的是卡牌 config 的 saveKey，层级/时机不对就可能拿到空串，
        ///   免疫与血量就整灶失效（用户反馈：不免疫魅惑）。引用比对不依赖任何字符串。</summary>
        static Node2D _targetNode;

        // ================= 诊断 =================

        /// <summary>一行文本（供外置修改器 / NetStatus 展示）。</summary>
        public static string Describe()
        {
            return "pvp=" + (Active ? 1 : 0)
                 + "|faction=" + (int)NetSession.MyFaction
                 + "|zsun=" + ZombieSun
                 + "|targethp=" + (int)TargetZombieHp
                 + "|resolved=" + (_resolved ? 1 : 0);
        }

        static void Log(string m)
        {
            if (_logCount++ > 40) return;   // 防刷日志
            Bootstrap.Log("对战: " + m);
        }

        static void LogErr(Exception ex)
        {
            if (_logCount++ > 40) return;
            Bootstrap.Log("对战异常: " + ex.GetType().Name + " " + ex.Message);
        }
    }
}
