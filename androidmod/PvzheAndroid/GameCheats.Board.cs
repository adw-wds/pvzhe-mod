using System;
using System.Collections.Generic;
using Godot;

namespace PvzheMod
{
    /// <summary>
    /// 布阵器：战场快照的采集 / 恢复（P1–P3）。
    ///
    /// 【设计要点】场上几百种角色**共享同一套组件**，所以状态走**语义键 → 真实字段路径**的
    /// 白名单表（复用已存在的 ReadEntityReal / WriteEntityReal），而不是逐类型写序列化器。
    /// 好处：加一个状态 = 加一行；读不到的键**不写进文件**（= "没采到"，不是 0）；
    /// 写不回的键**逐条计进"未还原"**，绝不假装成功。
    ///
    /// 数据模型与文本格式在 BoardSnapshot.cs（纯逻辑、不依赖游戏，可单测）。
    /// 设计见 docs/superpowers/specs/2026-10-12-board-snapshot-design.md
    /// </summary>
    public static partial class GameCheats
    {
        // ==================== 通用小工具 ====================

        /// <summary>读一个成员并转成字符串（string / Godot.StringName 都认）。</summary>
        static string BoardStrVal(object o, string key)
        {
            try
            {
                var v = FindPropOrFieldVal(o, key);
                if (v == null) return "";
                var s = v as string;
                if (!string.IsNullOrEmpty(s)) return s;
                var sn = v as Godot.StringName;
                if (sn != null) return sn.ToString();
            }
            catch { }
            return "";
        }

        static bool _boardDiagLogged;

        /// <summary>探针：把节点上各层字段、以及各候选配置对象的字段名打到日志（只打一次）。
        /// 为什么必须留着它：0.28 把关键字段改名+分层，字段名猜错的话失败长得一模一样，
        /// 而失败又被 try/catch 吞成“写入 0 个”一行。让运行时自己说出真名，是唯一可靠取证。</summary>
        static void BoardDiagNode(Node2D n, string why)
        {
            if (_boardDiagLogged) return;
            _boardDiagLogged = true;
            try
            {
                var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var sb = new System.Text.StringBuilder("布阵探针: ");
                sb.Append(why).Append(" | 节点类型=").Append(n.GetType().FullName);
                // 沿基类链列字段（DeclaredOnly，最多 4 层）——TowerDefenseCharacter 的 _characterConfig 在父层
                var t = n.GetType();
                var depth = 0;
                while (t != null && depth < 4)
                {
                    sb.Append(" || 层").Append(depth).Append('(').Append(t.Name).Append("): ");
                    var fs = t.GetFields(flags | System.Reflection.BindingFlags.DeclaredOnly);
                    for (var i = 0; i < fs.Length; i++)
                        sb.Append(fs[i].Name).Append('(').Append(fs[i].FieldType.Name).Append(") ");
                    t = t.BaseType;
                    depth++;
                }
                // 把候选配置对象自己的字段名也列出来
                var cands = new string[] { "config", "packetConfig", "_characterConfig", "characterConfig", "packet", "_packet" };
                for (var i = 0; i < cands.Length; i++)
                {
                    var cv = FindPropOrFieldVal(n, cands[i]);
                    if (cv == null) continue;
                    sb.Append(" || ").Append(cands[i]).Append(" 类型=").Append(cv.GetType().FullName).Append(" 字段: ");
                    var cfs = cv.GetType().GetFields(flags);
                    for (var j = 0; j < cfs.Length; j++)
                        sb.Append(cfs[j].Name).Append('(').Append(cfs[j].FieldType.Name).Append(") ");
                }
                Bootstrap.Log(sb.ToString());
            }
            catch (System.Exception ex) { Bootstrap.Log("布阵探针异常 " + ex.GetType().Name + ": " + ex.Message); }
        }

        /// <summary>
        /// 节点的卡 id。★ 必须同时满足**两个**条件才算有效：
        ///   ① 节点上能读出来（config/packetConfig 的 saveKey/id/name…）
        ///   ② GetPacketConfig(它) 非 null —— 因为还原时就是用它调 SpawnPacketAt(id,…)，
        ///      存一个游戏不认的 id 进文件，加载时必然铺不出来。
        ///
        /// 2026-10-12 事故复盘：旧实现只读 config.saveKey / originalSaveKey 两个键，
        /// 9 个实体全部被丢弃 —— 日志“场上植物=7 僵尸=2 写入植物=0 僵尸=0”，
        /// 保存出的文件只有 SUN/WAVEIDX/MOWER，加载时“只还原了阳光”。
        /// 而既有的 GetPlantPacketId（已在用）顺序是 id → name → saveKey，并有 packetConfig 兜底，
        /// 说明 saveKey 本来就不是第一顺位。
        /// </summary>
        static string BoardCardId(Node2D n)
        {
            try
            {
                var cfg = FindPropOrFieldVal(n, "config");
                if (cfg == null) cfg = FindPropOrFieldVal(n, "packetConfig");
                if (cfg == null) { BoardDiagNode(n, "无 config/packetConfig"); return ""; }

                // 候选按「最可能是 GetPacketConfig 能认的键」排序
                var keys = new string[] { "saveKey", "SaveKey", "id", "name", "packetId", "originalSaveKey", "originalId" };
                var firstNonEmpty = "";
                for (var i = 0; i < keys.Length; i++)
                {
                    var v = BoardStrVal(cfg, keys[i]);
                    if (string.IsNullOrEmpty(v)) continue;
                    if (string.IsNullOrEmpty(firstNonEmpty)) firstNonEmpty = keys[i] + "=" + v;
                    if (GetConfig(v) != null) return v;      // ★ ② 游戏认，才算数
                }
                BoardDiagNode(n, "cfg 里没有可用的键（首个非空 " + firstNonEmpty + "）");
                return "";
            }
            catch (System.Exception ex)
            {
                Bootstrap.Log("布阵卡id异常 " + ex.GetType().Name + ": " + ex.Message);
                return "";
            }
        }

        static Vector2I BoardGrid(Node2D n)
        {
            try { if (FindPropOrFieldVal(n, "gridPos") is Vector2I g) return g; } catch { }
            return new Vector2I(-1, -1);
        }

        /// <summary>遍历场景树，按阵营收集植物与僵尸。空节点/已失效节点跳过。</summary>
        static void BoardWalk(Node node, List<Node2D> plants, List<Node2D> zombies)
        {
            if (node == null) return;
            var kids = node.GetChildren(true);
            for (var i = 0; i < kids.Count; i++)
            {
                var child = kids[i];
                if (child is Node2D n2 && GodotObject.IsInstanceValid(n2))
                {
                    var camp = ESP.GetCamp(n2);
                    if (camp == "Plant") plants.Add(n2);
                    else if (camp == "Zombie") zombies.Add(n2);
                }
                BoardWalk(child, plants, zombies);
            }
        }

        // ==================== P1 采集 ====================

        /// <summary>
        /// 采集一个实体的状态袋。**只写读得到的键**——
        /// 读不到就少一个键，加载时表现为"这项没还原"，而不是被误当成 0。
        /// </summary>
        static void BoardCaptureState(Node2D n, BoardEntity e)
        {
            try
            {
                double v;
                if (ReadEntityReal(n, "hp", out v)) e.Set("hp", v);
                if (ReadEntityReal(n, "hitpointScale", out v)) e.Set("hps", v);
                if (ReadEntityReal(n, "scale", out v)) e.Set("sc", v);
                if (ReadEntityReal(n, "attack", out v)) e.Set("atk", v);
                // 魅惑：游戏原生 Hypnoses buff（不是 camp 反转 —— 移动组件只认 buff）
                if (BuffHas(n, "Hypnoses")) e.Set("charm", 1);
                // 僵尸死亡标志（采集时通常为 0，但"濒死/已死"状态值得带上）
                var die = FindPropOrFieldVal(n, "die");
                if (die is bool db) e.Set("die", db ? 1 : 0);
            }
            catch (System.Exception ex) { Bootstrap.Log("布阵采集: 读状态异常 " + ex.GetType().Name + ": " + ex.Message); }
        }

        static void BoardAppendEntities(List<BoardEntity> dst, List<Node2D> src)
        {
            for (var i = 0; i < src.Count; i++)
            {
                var n = src[i];
                var id = BoardCardId(n);
                if (string.IsNullOrEmpty(id)) continue;          // 拿不到卡 id 就还原不了，不写进文件
                var g = BoardGrid(n);
                var e = new BoardEntity { Col = g.X, Row = g.Y, CardId = id };
                BoardCaptureState(n, e);
                dst.Add(e);
            }
        }

        /// <summary>把当前战场采集成快照对象（不落盘）。失败返回 null 并打日志。</summary>
        public static BoardSnapshot BoardCapture(Node root, string name, string partsCsv)
        {
            try
            {
                if (root == null || !GodotObject.IsInstanceValid(root)) { Bootstrap.Log("布阵采集: 节点无效（不在游戏里？）"); return null; }
                var parts = BoardSnapshot.ParseParts(partsCsv);
                var s = new BoardSnapshot { Name = name ?? "", Time = NowStamp() };
                for (var i = 0; i < parts.Count; i++) s.Parts.Add(parts[i]);

                var plants = new List<Node2D>();
                var zombies = new List<Node2D>();
                BoardWalk(root, plants, zombies);

                if (s.HasPart("plant")) BoardAppendEntities(s.Plants, plants);
                if (s.HasPart("zombie")) BoardAppendEntities(s.Zombies, zombies);

                // 植物/僵尸没勾时也把数量记进日志，便于对照"为什么文件里是 0 个"
                if (s.HasPart("sun")) s.Sun = GetSunCount();
                if (s.HasPart("wave")) s.WaveIdx = NetGetWave();
                if (s.HasPart("mower")) s.Mowers = BoardCaptureMowers();
                s.Level = NetGetLevelRef();
                if (string.IsNullOrEmpty(s.Level)) s.Level = NetGetLevelKey() ?? "";

                Bootstrap.Log("布阵采集: 场上植物=" + plants.Count + " 僵尸=" + zombies.Count +
                              " 写入植物=" + s.Plants.Count + " 僵尸=" + s.Zombies.Count +
                              " 阳光=" + s.Sun + " 波次=" + s.WaveIdx +
                              " 部分=" + JoinStr(s.Parts, ","));
                return s;
            }
            catch (System.Exception ex)
            {
                // 绝不空 catch —— 类型与原文都要打出来
                Bootstrap.Log("布阵采集异常 " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>每行小推车是否还在（1/0）。读不到返回 null（= 未采集，与"全是 0"区分）。</summary>
        static int[] BoardCaptureMowers()
        {
            try
            {
                var tdm = GetTdmInstance();
                if (tdm == null) return null;
                var ctrl = FindFieldVal(tdm, "currentControl");
                if (ctrl == null) return null;
                var gfm = FindMethodExact(ctrl.GetType(), "GetFeature", new Type[] { typeof(Godot.StringName) });
                if (gfm == null) return null;
                var feat = gfm.Invoke(ctrl, new object[] { new Godot.StringName("Mower") });
                if (feat == null) return null;
                var line = FindPropOrFieldVal(feat, "mowerLine");
                var count = GetCollectionCount(line);
                if (count <= 0) return null;

                var arr = new int[count];
                for (var i = 0; i < count; i++)
                {
                    object item = null;
                    try
                    {
                        var il = line as System.Collections.IList;
                        if (il != null) item = il[i];
                        else
                        {
                            var idxP = line.GetType().GetProperty("Item", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                            if (idxP != null) item = idxP.GetValue(line, new object[] { i });
                        }
                    }
                    catch { }
                    var alive = item is Godot.GodotObject go && GodotObject.IsInstanceValid(go);
                    arr[i] = alive ? 1 : 0;
                }
                return arr;
            }
            catch (System.Exception ex) { Bootstrap.Log("布阵采集小推车异常: " + ex.Message); return null; }
        }

        // ==================== P2 恢复 · 清场 ====================

        /// <summary>清空场上指定阵营。返回清掉的数量。</summary>
        public static int BoardClearField(Node root, bool clearPlants, bool clearZombies)
        {
            var n = 0;
            try
            {
                if (root == null) return 0;
                var plants = new List<Node2D>();
                var zombies = new List<Node2D>();
                BoardWalk(root, plants, zombies);
                // ★ 必须先收集完再删 —— 边遍历边 QueueFree 会让 GetChildren 的列表变化
                if (clearPlants) { for (var i = 0; i < plants.Count; i++) { try { if (GodotObject.IsInstanceValid(plants[i])) { plants[i].QueueFree(); n++; } } catch { } } }
                if (clearZombies) { for (var i = 0; i < zombies.Count; i++) { try { if (GodotObject.IsInstanceValid(zombies[i])) { zombies[i].QueueFree(); n++; } } catch { } } }
            }
            catch (System.Exception ex) { Bootstrap.Log("布阵清场异常: " + ex.Message); }
            return n;
        }

        // ==================== P2 恢复 · 铺满 ====================

        /// <summary>
        /// 写回一个实体的状态袋。写不回的键**逐条记进 notRestored**（带实体标识），
        /// 由调用方汇总成"未还原"回报 —— 不许静默吞掉。
        /// </summary>
        static void BoardWriteState(Node2D n, BoardEntity e, List<string> notRestored)
        {
            for (var i = 0; i < e.StateCount; i++)
            {
                var k = e.KeyAt(i);
                var v = e.ValAt(i);
                var who = e.CardId + "(" + e.Col + "," + e.Row + ")";
                try
                {
                    switch (k)
                    {
                        case "hp":
                            // ReadEntityReal 的 hp 走 ApplyPlantHpDirect（植物专用）——僵尸另走 instance.hitpoints
                            if (IsPlantEntity(n)) { var r = WriteEntityReal(n, "hp", v); if (r != "ok") notRestored.Add(who + ":hp"); }
                            else if (!BoardSetZombieHp(n, v)) notRestored.Add(who + ":hp");
                            break;
                        case "hps":
                            { var r = WriteEntityReal(n, "hitpointScale", v); if (r != "ok") notRestored.Add(who + ":hps"); }
                            break;
                        case "sc":
                            { var r = WriteEntityReal(n, "scale", v); if (r != "ok") notRestored.Add(who + ":sc"); }
                            break;
                        case "atk":
                            if (IsPlantEntity(n)) { var r = WriteEntityReal(n, "attack", v); if (r != "ok") notRestored.Add(who + ":atk"); }
                            else notRestored.Add(who + ":atk");      // 僵尸攻速由 AccelerateTimer 每帧驱动，不持久写入
                            break;
                        case "charm":
                            {
                                var want = v != 0;
                                var has = BuffHas(n, "Hypnoses");
                                if (want != has) InvokeHypnoses(n);       // Hypnoses() 有 buff 则删、无则加
                            }
                            break;
                        case "die":
                            // 死亡标志是采集时的瞬时值，刻意不回写（回写会把僵尸直接写死）
                            break;
                        default:
                            // 未知键（来自更新的版本）不报"未还原"——那不是我们写的，报出来只会刷屏
                            break;
                    }
                }
                catch (System.Exception ex)
                {
                    notRestored.Add(who + ":" + k);
                    Bootstrap.Log("布阵写状态失败 " + who + " " + k + " = " + BoardSnapshot.Fmt(v) + " : " + ex.Message);
                }
            }
        }

        /// <summary>僵尸血量：instance.hitpoints 字段。植物那边由 ApplyPlantHpDirect 处理。</summary>
        static bool BoardSetZombieHp(Node2D n, double v)
        {
            try
            {
                var inst = FindFieldVal(n, "instance");
                if (inst == null) return false;
                var f = FindFieldInfo(inst, "hitpoints");
                if (f == null) return false;
                if (f.FieldType == typeof(int)) f.SetValue(inst, (int)v);
                else if (f.FieldType == typeof(float)) f.SetValue(inst, (float)v);
                else if (f.FieldType == typeof(double)) f.SetValue(inst, v);
                else return false;
                return true;
            }
            catch (System.Exception ex) { Bootstrap.Log("布阵写僵尸血量异常: " + ex.Message); return false; }
        }

        /// <summary>
        /// 铺满：按快照生成植物/僵尸，再把状态写回。
        ///
        /// 【为什么生成后要重新遍历树找实体】SpawnPacketAt 不返回新建的节点，
        /// 而状态必须写到**真正建出来**的那个节点上。所以按 (阵营, 格子, 卡id) 匹配，
        /// 每个快照项只消费一次（支持同格堆叠多个）。
        /// </summary>
        static void BoardPlaceEntities(Node root, List<BoardEntity> ents, bool isPlant, string partsCsv,
                                      ref int spawned, ref int failed, List<string> notRestored)
        {
            var parts = BoardSnapshot.ParseParts(partsCsv);
            var writeState = isPlant ? parts.IndexOf("plantst") >= 0 : parts.IndexOf("zombiest") >= 0;
            var pending = new List<BoardEntity>();
            for (var i = 0; i < ents.Count; i++)
            {
                var e = ents[i];
                try
                {
                    if (GetConfig(e.CardId) == null) { failed++; notRestored.Add(e.CardId + "(" + e.Col + "," + e.Row + "):卡不存在"); continue; }
                    if (SpawnPacketAt(e.CardId, e.Row, e.Col)) { spawned++; if (writeState) pending.Add(e); }
                    else { failed++; notRestored.Add(e.CardId + "(" + e.Col + "," + e.Row + "):生成失败"); }
                }
                catch (System.Exception ex) { failed++; Bootstrap.Log("布阵生成异常 " + e.CardId + ": " + ex.Message); }
            }

            if (pending.Count == 0) return;

            // 重新遍历，按 格子+卡id 匹配刚生成的节点，写回状态
            var plants = new List<Node2D>();
            var zombies = new List<Node2D>();
            BoardWalk(root, plants, zombies);
            var live = isPlant ? plants : zombies;
            for (var i = 0; i < pending.Count; i++)
            {
                var e = pending[i];
                for (var j = 0; j < live.Count; j++)
                {
                    var n = live[j];
                    if (n == null || !GodotObject.IsInstanceValid(n)) continue;
                    var g = BoardGrid(n);
                    if (g.X != e.Col || g.Y != e.Row) continue;
                    if (BoardCardId(n) != e.CardId) continue;
                    BoardWriteState(n, e, notRestored);
                    live[j] = null;          // 已被消费，防同格堆叠时重复匹配
                    break;
                }
            }
        }

        /// <summary>
        /// 恢复战场（关卡内）。返回回报串（UI/日志直接显示）。
        /// 语义 = **先清空当前战场，再按文件铺满**（用户明确选定）。
        /// </summary>
        public static string BoardRestoreInPlace(Node root, BoardSnapshot s, string partsCsv)
        {
            try
            {
                if (root == null || s == null) return "err:参数为空";
                var parts = BoardSnapshot.ParseParts(partsCsv);
                var hasPlant = parts.IndexOf("plant") >= 0;
                var hasZombie = parts.IndexOf("zombie") >= 0;
                var notRestored = new List<string>();

                // ① 清场（只清我们要覆盖的阵营；没勾的阵营绝不动）
                // ★ 安全闸：文件里那个阵营本来就是空的 → 绝不清场。
                //   否则一旦采集侧出问题（如卡 id 读不到导致植物=0），加载会“把战场清干净、
                //   却什么都铺不回来”，这是破坏性的。宁可保留原战场并如实报告。
                var clearPlants = hasPlant && s.Plants.Count > 0;
                var clearZombies = hasZombie && s.Zombies.Count > 0;
                var skippedClear = (hasPlant && s.Plants.Count == 0) || (hasZombie && s.Zombies.Count == 0);
                var cleared = BoardClearField(root, clearPlants, clearZombies);

                // ② 铺满
                var sp = 0; var spFail = 0; var zp = 0; var zFail = 0;
                if (hasPlant) BoardPlaceEntities(root, s.Plants, true, partsCsv, ref sp, ref spFail, notRestored);
                if (hasZombie) BoardPlaceEntities(root, s.Zombies, false, partsCsv, ref zp, ref zFail, notRestored);

                // ③ 场面：阳光 / 波次
                if (parts.IndexOf("sun") >= 0 && s.Sun >= 0)
                {
                    var cur = GetSunCount();
                    if (cur < 0) notRestored.Add("sun:读不到当前阳光");
                    else if (cur != s.Sun) AddSun(s.Sun - cur);
                }
                if (parts.IndexOf("wave") >= 0 && s.WaveIdx >= 0)
                {
                    if (!NetSetWaveIndex(s.WaveIdx)) notRestored.Add("wave:设置失败");
                }
                if (parts.IndexOf("mower") >= 0 && s.Mowers != null)
                {
                    // 只能"报告"不能"还原"——已用掉的小推车无法凭空复现（要复现得重建，属于另一个功能）
                    // ★ 实测：5 行的地图 mowerLine 有 51 项，长度 != 行数。
                    //   所以只说"项"，不按"行"报数 —— 否则会报出"46 行已用掉"这种假数据。
                    var used = 0;
                    for (var i = 0; i < s.Mowers.Length; i++) if (s.Mowers[i] == 0) used++;
                    if (used > 0) notRestored.Add("mower:" + used + " 项不在场上（小推车无法凭空还原）");
                }

                var msg = "清场=" + cleared + " 植物=" + sp + " 僵尸=" + zp;
                if (skippedClear) msg += " （快照里该阵营为空，已跳过清场）";
                if (spFail > 0) msg += " 植物失败=" + spFail;
                if (zFail > 0) msg += " 僵尸失败=" + zFail;
                if (notRestored.Count > 0)
                {
                    msg += " 未还原=" + notRestored.Count + "(" + JoinStr(notRestored, " ") + ")";
                }
                Bootstrap.Log("布阵恢复: " + msg);
                return "ok " + msg;
            }
            catch (System.Exception ex)
            {
                Bootstrap.Log("布阵恢复异常 " + ex.GetType().Name + ": " + ex.Message);
                return "err:" + ex.Message;
            }
        }

        // ==================== 小工具 ====================

        static string JoinStr(List<string> l, string sep)
        {
            if (l == null || l.Count == 0) return "";
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < l.Count; i++)
            {
                if (i > 0) sb.Append(sep);
                sb.Append(l[i]);
            }
            return sb.ToString();
        }

        /// <summary>时间戳（不用 System.DateTime 的带格式串 ToString —— AOT 环境避开格式串重载）。</summary>
        static string NowStamp()
        {
            try
            {
                var t = System.DateTime.Now;
                return t.Year + "-" + Pad2(t.Month) + "-" + Pad2(t.Day) + " " + Pad2(t.Hour) + ":" + Pad2(t.Minute);
            }
            catch { return ""; }
        }

        static string Pad2(int v) { return v < 10 ? "0" + v : v.ToString(System.Globalization.CultureInfo.InvariantCulture); }

        // ==================== 文件读写（Godot FileAccess，禁用 System.IO） ====================

        static string BoardReadText(string path)
        {
            try
            {
                using (var f = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read))
                {
                    if (f == null) { Bootstrap.Log("布阵读文件失败: " + path + " err=" + Godot.FileAccess.GetOpenError()); return null; }
                    return f.GetAsText();
                }
            }
            catch (System.Exception ex) { Bootstrap.Log("布阵读文件异常: " + ex.GetType().Name + ": " + ex.Message); return null; }
        }

        static bool BoardWriteText(string path, string text)
        {
            try
            {
                using (var f = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write))
                {
                    if (f == null) { Bootstrap.Log("布阵写文件失败: " + path + " err=" + Godot.FileAccess.GetOpenError()); return false; }
                    f.StoreString(text);
                    return true;
                }
            }
            catch (System.Exception ex) { Bootstrap.Log("布阵写文件异常: " + ex.GetType().Name + ": " + ex.Message); return false; }
        }

        /// <summary>从文件文本里取一个 `KEY=值` 行的值（用于拿关卡定义段的 MAP/TEMPLATE）。</summary>
        static string BoardDefValue(string text, string key)
        {
            try
            {
                if (string.IsNullOrEmpty(text)) return "";
                var lines = text.Split('\n');
                var want = key + "=";
                for (var i = 0; i < lines.Length; i++)
                {
                    var ln = lines[i].TrimEnd('\r').Trim();
                    if (ln.StartsWith("BOARD", StringComparison.OrdinalIgnoreCase)) break;   // 到快照段就停，别抓同名键
                    if (!ln.StartsWith(want, StringComparison.OrdinalIgnoreCase)) continue;
                    return ln.Substring(want.Length).Trim();
                }
            }
            catch { }
            return "";
        }

        /// <summary>关卡库目录（外置修改器导入时把文件拷到这里，再调 BoardList 刷新）。</summary>
        public static string BoardLibraryDir()
        {
            try { return GetLevelsDir() ?? ""; } catch { return ""; }
        }

        /// <summary>快照文件在关卡库里的完整路径（关卡库 = exe 目录\levels\，与自制关卡共用）。</summary>
        public static string BoardLibraryPath(string fileName)
        {
            try
            {
                var dir = GetLevelsDir();
                if (string.IsNullOrEmpty(dir)) return "";
                var f = fileName;
                if (f.IndexOf('\\') >= 0 || f.IndexOf('/') >= 0) f = FileNameNoExt(f);   // 防目录穿越：只取文件名
                if (!f.EndsWith(".pvzlevel", StringComparison.OrdinalIgnoreCase)) f += ".pvzlevel";
                return dir + "\\" + f;
            }
            catch { return ""; }
        }

        // ==================== P1+P3 保存到关卡库 ====================

        /// <summary>
        /// 采集当前战场并写入关卡库。返回 "ok 详情" 或 "err:原因"。
        /// ★ 若当前关卡来自关卡库里的某个 .pvzlevel，会把它的**关卡定义段原样保留**在文件开头 ——
        ///   这样这份文件既能"关卡内恢复战场"，也能"关卡外自动进同一关"。
        /// </summary>
        public static string BoardSave(Node root, string name, string partsCsv)
        {
            try
            {
                if (string.IsNullOrEmpty(name)) name = "阵型_" + NowStamp().Replace(':', '-').Replace(' ', '_');
                var s = BoardCapture(root, name, partsCsv);
                if (s == null) return "err:采集失败（看 mod_log.txt）";

                // ★ 安全闸：勾了植物/僵尸却采到 0 个 —— 十有八九是拿不到卡 id。
                //   这种快照加载时会“清场了但铺不回来”，所以绝不静默当成功。
                var warn = "";
                if (s.HasPart("plant") && s.Plants.Count == 0) warn += "【植物采到 0 个】";
                if (s.HasPart("zombie") && s.Zombies.Count == 0) warn += "【僵尸采到 0 个】";

                var path = BoardLibraryPath(name);
                if (string.IsNullOrEmpty(path)) return "err:拿不到关卡库目录";

                var text = s.Serialize();
                // 保留原关卡定义段（若当前关卡是从关卡库加载的）
                if (!string.IsNullOrEmpty(_lastBoardLevelText))
                    text = _lastBoardLevelText + "\n" + text;

                if (!BoardWriteText(path, text)) return "err:写文件失败 " + path;
                Bootstrap.Log("布阵保存: " + path + " 植物=" + s.Plants.Count + " 僵尸=" + s.Zombies.Count + (warn.Length > 0 ? " 警告=" + warn : ""));
                return "ok 已保存 " + FileNameNoExt(path) + "（植物 " + s.Plants.Count + " 僵尸 " + s.Zombies.Count + "）"
                       + (warn.Length > 0 ? " " + warn + " 详见 mod_log.txt" : "");
            }
            catch (System.Exception ex)
            {
                Bootstrap.Log("布阵保存异常 " + ex.GetType().Name + ": " + ex.Message);
                return "err:" + ex.Message;
            }
        }

        /// <summary>关卡库里的快照文件清单（JSON，供外置修改器渲染列表）。</summary>
        public static string BoardListJson()
        {
            try
            {
                var all = ListCustomLevels();
                var sb = new System.Text.StringBuilder();
                sb.Append('[');
                var n = 0;
                for (var i = 0; i < all.Count; i++)
                {
                    var txt = BoardReadText(all[i]);
                    if (string.IsNullOrEmpty(txt)) continue;
                    var s = BoardSnapshot.TryParse(txt);
                    if (s == null) continue;                       // 纯关卡定义文件，不是快照 → 不列
                    if (n > 0) sb.Append(',');
                    // 手写 JSON 转义（不引 System.Text.Json —— AOT 裁剪）
                    sb.Append("{\"file\":\"").Append(JsonEsc(FileNameNoExt(all[i]))).Append('"');
                    sb.Append(",\"name\":\"").Append(JsonEsc(s.Name)).Append('"');
                    sb.Append(",\"time\":\"").Append(JsonEsc(s.Time)).Append('"');
                    sb.Append(",\"parts\":\"").Append(JsonEsc(JoinStr(s.Parts, ","))).Append('"');
                    sb.Append(",\"plants\":").Append(s.Plants.Count);
                    sb.Append(",\"zombies\":").Append(s.Zombies.Count);
                    sb.Append(",\"sun\":").Append(s.Sun >= 0 ? s.Sun.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-1");
                    sb.Append('}');
                    n++;
                }
                sb.Append(']');
                return sb.ToString();
            }
            catch (System.Exception ex) { Bootstrap.Log("布阵列清单异常: " + ex.Message); return "[]"; }
        }

        static string JsonEsc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') { }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        // ==================== P4 加载（关卡内直接恢复 / 关卡外先进关） ====================

        /// <summary>最近一次从关卡库加载的关卡定义段（保存快照时原样保留，让文件自描述“来自哪关”）。</summary>
        static string _lastBoardLevelText = "";

        /// <summary>
        /// 加载一份快照。
        /// · 已经在关卡里 → 直接清场恢复（用户选定的语义：完全覆盖）
        /// · 不在关卡里 → 自动进关，登记为 pending，等控制权/玩法资源就绪后再铺
        /// </summary>
        public static string BoardLoad(Node root, string fileOrName, string partsCsv)
        {
            try
            {
                var path = BoardLibraryPath(fileOrName);
                if (string.IsNullOrEmpty(path)) return "err:拿不到关卡库目录";
                var text = BoardReadText(path);
                if (string.IsNullOrEmpty(text)) return "err:读不到文件 " + path;

                var s = BoardSnapshot.TryParse(text);
                if (s == null) return "err:该文件不含阵型快照（只有关卡定义）";

                var parts = string.IsNullOrEmpty(partsCsv) ? JoinStr(s.Parts, ",") : partsCsv;
                // 生效的部分 = 文件里实际存了的部分 ∩ 调用方勾选的部分（避免"勾了但文件里没有"导致误清场）
                var eff = BoardIntersectParts(s, parts);

                if (NetIsInBattleLevel())
                {
                    var r = BoardRestoreInPlace(root, s, eff);
                    return r;
                }

                // —— 不在关卡内 ——
                // ★ 用户 2026-10-12 明确要求：**删掉「关卡外自动进关」**，布阵器只在关卡内用。
                //   旧实现（先试 LEVEL= 进对应关卡 → 失败退回 TEMPLATE / 内置测试关卡）实测结果是
                //   「进的全都是测试关卡」；而「我是僵尸」关卡的**脑子 / 卡槽 / 小推车都写在关卡配置里**，
                //   进了测试关卡自然全没了，且会直接把玩家从当前界面掳进另一个关卡。
                //   与其猜关卡载体，不如只在关卡内用 —— 语义干脆、不会弄乱玩家的游戏状态。
                return "err:请在关卡内使用（布阵器只在关卡中加载，不会替你进关）";
            }
            catch (System.Exception ex)
            {
                Bootstrap.Log("布阵加载异常 " + ex.GetType().Name + ": " + ex.Message);
                return "err:" + ex.Message;
            }
        }

        /// <summary>
        /// 取文件里的关卡定义段：BOARD 段之前的全部内容。
        /// ★ **没有 BOARD 段时返回整份文本** —— 那本身就是一份纯关卡定义文件，
        ///   保存阵型时要把它原样保留，否则存出来的文件进不了同一关。
        /// </summary>
        static string BoardDefinitionSection(string text)
        {
            try
            {
                if (string.IsNullOrEmpty(text)) return "";
                var i = text.IndexOf("BOARD=", StringComparison.Ordinal);
                if (i < 0) return text.TrimEnd('\r', '\n');
                if (i == 0) return "";
                return text.Substring(0, i).TrimEnd('\r', '\n');
            }
            catch { return ""; }
        }

        /// <summary>生效部分 = 文件里存了的部分 ∩ 调用方勾选的部分。文件里没有的部分绝不参与清场。</summary>
        static string BoardIntersectParts(BoardSnapshot s, string partsCsv)
        {
            var want = BoardSnapshot.ParseParts(partsCsv);
            var outl = new List<string>();
            for (var i = 0; i < want.Count; i++)
                if (s.HasPart(want[i])) outl.Add(want[i]);
            return JoinStr(outl, ",");
        }

        /// <summary>删除关卡库里的一份快照。只允许删关卡库目录下的文件。</summary>
        public static string BoardDelete(string fileOrName)
        {
            try
            {
                var path = BoardLibraryPath(fileOrName);
                if (string.IsNullOrEmpty(path)) return "err:拿不到路径";
                if (!GodotFileExists(path)) return "err:文件不存在";
                var err = Godot.DirAccess.RemoveAbsolute(path);
                if (err != Godot.Error.Ok) return "err:删除失败 " + err;
                Bootstrap.Log("布阵删除: " + path);
                return "ok";
            }
            catch (System.Exception ex) { return "err:" + ex.Message; }
        }

        /// <summary>把快照复制到导出目录（供分享）。返回导出后的完整路径或 err。</summary>
        public static string BoardExport(string fileOrName)
        {
            try
            {
                var path = BoardLibraryPath(fileOrName);
                if (string.IsNullOrEmpty(path)) return "err:拿不到路径";
                var text = BoardReadText(path);
                if (string.IsNullOrEmpty(text)) return "err:读不到文件";

                var dir = GetExeDir() + "\\board_export";
                Godot.DirAccess.MakeDirAbsolute(dir);
                var dst = dir + "\\" + FileNameNoExt(path) + ".pvzlevel";
                if (!BoardWriteText(dst, text)) return "err:写导出文件失败";
                Bootstrap.Log("布阵导出: " + dst);
                return "ok " + dst;
            }
            catch (System.Exception ex) { return "err:" + ex.Message; }
        }
    }
}
