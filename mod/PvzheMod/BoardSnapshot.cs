using System;
using System.Collections.Generic;
using System.Globalization;

namespace PvzheMod
{
    /// <summary>
    /// 阵型快照里的一个实体（一株植物 / 一只僵尸）。
    ///
    /// 【为什么状态用"可选键值袋"而不是固定字段】用户要求"状态尽量精确"，
    /// 而状态是**逐个补齐**的（血量→缩放→攻速→魅惑→成长…）。
    /// 固定字段每加一项都要改序列化 + 所有解析；可选袋加键零成本，
    /// 而且**键缺失 = 该项没采到**（与"值=0"语义不同），旧文件也能继续读。
    /// </summary>
    public sealed class BoardEntity
    {
        public int Col;
        public int Row;
        public string CardId = "";

        readonly List<string> _keys = new List<string>();
        readonly List<double> _vals = new List<double>();

        public int StateCount { get { return _keys.Count; } }
        public string KeyAt(int i) { return _keys[i]; }
        public double ValAt(int i) { return _vals[i]; }

        public bool Has(string k) { return _keys.IndexOf(k) >= 0; }

        public bool TryGet(string k, out double v)
        {
            var i = _keys.IndexOf(k);
            if (i < 0) { v = 0; return false; }
            v = _vals[i];
            return true;
        }

        /// <summary>写一个状态键。同名键覆盖（后采到的赢）。</summary>
        public void Set(string k, double v)
        {
            if (string.IsNullOrEmpty(k)) return;
            var i = _keys.IndexOf(k);
            if (i >= 0) { _vals[i] = v; return; }
            _keys.Add(k);
            _vals.Add(v);
        }
    }

    /// <summary>
    /// 战场快照：地图之外的全部运行时状态。
    ///
    /// 本类**完全不依赖游戏与 Godot**（只有 System + 集合），
    /// 所以可以脱离游戏环境单测：往返（写→读→逐字段相等）与坏输入容错都在这里验证。
    /// 文本格式见 docs/superpowers/specs/2026-10-12-board-snapshot-design.md §3。
    /// </summary>
    public sealed class BoardSnapshot
    {
        public int Version = 1;
        public string Name = "";
        public string Time = "";
        /// <summary>关卡标识（res:// 路径或 saveKey），关卡外加载用它进关。</summary>
        public string Level = "";
        /// <summary>本次实际写了文件的部分（map/plant/plantst/zombie/zombiest/sun/wave/mower）。</summary>
        public readonly List<string> Parts = new List<string>();

        /// <summary>-1 = 未采集（区别于 0）。</summary>
        public long Sun = -1;
        /// <summary>-1 = 未采集。</summary>
        public int WaveIdx = -1;
        /// <summary>每行小推车是否还在：1=还在 0=已用掉。null = 未采集。</summary>
        public int[] Mowers;

        public readonly List<BoardEntity> Plants = new List<BoardEntity>();
        public readonly List<BoardEntity> Zombies = new List<BoardEntity>();

        /// <summary>解析时跳过的坏行数（坏数据不抛异常，只计数）。</summary>
        public int ErrCount;

        public bool HasPart(string p) { return Parts.IndexOf(p) >= 0; }

        // ==================== 数值格式化 ====================
        // ★ 不用带格式串的 ToString（本项目 AOT 环境禁用它）→ 手写定点格式化。
        public static string Fmt(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "0";
            if (Math.Abs(v) > 1e12) return "0";                       // 异常值不写进文件
            var r = Math.Round(v, 4);
            var i = (long)r;
            var frac = (long)Math.Round((r - i) * 10000.0);
            if (frac == 0) return i.ToString(CultureInfo.InvariantCulture);
            var s = frac.ToString(CultureInfo.InvariantCulture);
            while (s.Length < 4) s = "0" + s;
            while (s.Length > 1 && s[s.Length - 1] == '0') s = s.Substring(0, s.Length - 1);
            return i.ToString(CultureInfo.InvariantCulture) + "." + s;
        }

        /// <summary>手写拆分（不用 Split(char[]) —— AOT 环境里少见的数组重载有被裁风险）。</summary>
        static List<string> SplitManual(string s, char sep)
        {
            var list = new List<string>();
            if (s == null) { list.Add(""); return list; }
            var start = 0;
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] != sep) continue;
                list.Add(s.Substring(start, i - start));
                start = i + 1;
            }
            list.Add(s.Substring(start));
            return list;
        }

        static bool ParseNum(string s, out double v)
        {
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        // ==================== 序列化 ====================

        /// <summary>只产出快照段（关卡定义段由既有 .pvzlevel 写入方负责，本类不碰）。</summary>
        public string Serialize()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("BOARD=").Append(Version).Append('\n');
            sb.Append("BOARDNAME=").Append(Name).Append('\n');
            sb.Append("BOARDTIME=").Append(Time).Append('\n');
            sb.Append("BOARDPARTS=").Append(Join(Parts, ',')).Append('\n');
            if (!string.IsNullOrEmpty(Level)) sb.Append("LEVEL=").Append(Level).Append('\n');
            if (Sun >= 0) sb.Append("SUN=").Append(Sun).Append('\n');
            if (WaveIdx >= 0) sb.Append("WAVEIDX=").Append(WaveIdx).Append('\n');
            if (Mowers != null)
            {
                sb.Append("MOWER=");
                for (var i = 0; i < Mowers.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(Mowers[i] != 0 ? "1" : "0");
                }
                sb.Append('\n');
            }
            AppendEntities(sb, "PLANT", Plants);
            AppendEntities(sb, "ZOMBIE", Zombies);
            return sb.ToString();
        }

        static string Join(List<string> l, char sep)
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

        static void AppendEntities(System.Text.StringBuilder sb, string tag, List<BoardEntity> list)
        {
            for (var i = 0; i < list.Count; i++)
            {
                var e = list[i];
                sb.Append(tag).Append('=');
                sb.Append(e.Col).Append(',').Append(e.Row).Append(';').Append(e.CardId);
                for (var k = 0; k < e.StateCount; k++)
                    sb.Append(';').Append(e.KeyAt(k)).Append('=').Append(Fmt(e.ValAt(k)));
                sb.Append('\n');
            }
        }

        // ==================== 解析 ====================

        /// <summary>
        /// 解析含快照的文本。**没有 `BOARD=` 行 → 返回 null**（那是纯关卡定义，不是快照文件）。
        /// 坏行只计数（ErrCount），不抛异常 —— 别人分享的文件可能来自别的版本。
        /// </summary>
        public static BoardSnapshot TryParse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var lines = SplitManual(text, '\n');

            // 两趟：先确认这是一份快照文件，再真正解析（避免半途才发现不是，白解析）
            var hasBoard = false;
            for (var i = 0; i < lines.Count; i++)
            {
                var ln = lines[i].TrimEnd('\r').Trim();
                if (ln.StartsWith("BOARD=", StringComparison.Ordinal)) { hasBoard = true; break; }
            }
            if (!hasBoard) return null;

            var s = new BoardSnapshot();
            for (var i = 0; i < lines.Count; i++)
            {
                var ln = lines[i].TrimEnd('\r').Trim();
                if (ln.Length == 0 || ln[0] == '#') continue;
                var eq = ln.IndexOf('=');
                if (eq <= 0) continue;
                var key = ln.Substring(0, eq).Trim().ToUpperInvariant();
                var val = ln.Substring(eq + 1).Trim();

                // 分片写法 PLANT#2=... 按主键归并（当前写入方不产生，但容忍别人手工分片）
                var hash = key.IndexOf('#');
                if (hash > 0) key = key.Substring(0, hash);

                try
                {
                    switch (key)
                    {
                        case "BOARD": { double v; if (ParseNum(val, out v)) s.Version = (int)v; break; }
                        case "BOARDNAME": s.Name = val; break;
                        case "BOARDTIME": s.Time = val; break;
                        case "BOARDPARTS":
                            s.Parts.Clear();
                            foreach (var p in SplitManual(val, ','))
                            {
                                var t = p.Trim();
                                if (t.Length > 0) s.Parts.Add(t);
                            }
                            break;
                        case "LEVEL": s.Level = val; break;
                        case "SUN": { double v; if (ParseNum(val, out v)) s.Sun = (long)v; break; }
                        case "WAVEIDX": { double v; if (ParseNum(val, out v)) s.WaveIdx = (int)v; break; }
                        case "MOWER":
                        {
                            var parts = SplitManual(val, ',');
                            var arr = new int[parts.Count];
                            for (var m = 0; m < parts.Count; m++)
                            {
                                double v;
                                arr[m] = ParseNum(parts[m].Trim(), out v) && v != 0 ? 1 : 0;
                            }
                            s.Mowers = arr;
                            break;
                        }
                        case "PLANT": s.Plants.Add(ParseEntity(val)); break;
                        case "ZOMBIE": s.Zombies.Add(ParseEntity(val)); break;
                        default: break;   // 未知键忽略（向前兼容）
                    }
                }
                catch { s.ErrCount++; }
            }
            return s;
        }

        /// <summary>解析 `col,row;卡id;k=v;k=v`。异常由调用方计入 ErrCount。</summary>
        static BoardEntity ParseEntity(string val)
        {
            var f = SplitManual(val, ';');
            var e = new BoardEntity();
            if (f.Count > 0)
            {
                var pos = SplitManual(f[0], ',');
                double c, r;
                if (pos.Count >= 2 && ParseNum(pos[0].Trim(), out c) && ParseNum(pos[1].Trim(), out r))
                {
                    e.Col = (int)c;
                    e.Row = (int)r;
                }
            }
            if (f.Count > 1) e.CardId = f[1].Trim();
            for (var i = 2; i < f.Count; i++)
            {
                var kv = f[i];
                if (kv.Length == 0) continue;
                var eq = kv.IndexOf('=');
                if (eq <= 0) continue;
                double v;
                if (!ParseNum(kv.Substring(eq + 1).Trim(), out v)) continue;   // 非数值键（如未来扩展）跳过
                e.Set(kv.Substring(0, eq).Trim(), v);
            }
            return e;
        }

        // ==================== 部分开关 ====================

        /// <summary>全部部分（UI 勾选项与它一一对应）。</summary>
        public static readonly string[] AllParts =
        {
            "map", "plant", "plantst", "zombie", "zombiest", "sun", "wave", "mower"
        };

        /// <summary>把 UI 的勾选串解析成 parts（空串 = 全选，方便"默认全都要"）。</summary>
        public static List<string> ParseParts(string csv)
        {
            var l = new List<string>();
            if (string.IsNullOrEmpty(csv))
            {
                for (var i = 0; i < AllParts.Length; i++) l.Add(AllParts[i]);
                return l;
            }
            foreach (var p in SplitManual(csv, ','))
            {
                var t = p.Trim();
                if (t.Length > 0 && l.IndexOf(t) < 0) l.Add(t);
            }
            return l;
        }
    }
}
