using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace PvzheMod
{
    /// <summary>
    /// 功能开关变化日志（供外置修改器弹「RTX 风格」卡片提示）。
    ///
    /// 采集点只有一个：每几次 Tick diff 一遍 ModSettings 的全部 public static 字段。
    /// 为什么不逐个挂钩子 —— ModSettings 字段被至少 5 处直接赋值
    /// （RemoteServer.ApplySetting / Net\CheatPolicy / GameCheats.ForceAllOff /
    ///   自制关卡配置 / ModUI 一键全关），逐个挂必然漏；diff 是唯一真相源，覆盖 100% 来源。
    ///
    /// 中文名/图标不在这里维护：事件只带字段名，外置端用它自己的 Feats 表翻译，
    /// 避免两端各维护一份名字表。
    /// </summary>
    public static class ChangeLog
    {
        public struct Evt
        {
            public int Seq;
            public string Name;   // ModSettings 字段名；"*" = 批量汇总
            public bool On;
            public string Val;    // 数值/字符串型新值；bool 型为 null
        }

        const int MaxEvents = 200;      // 环形队列容量（外置端每 300ms 拉一次，够用）
        const int DiffEveryTicks = 4;   // 每 4 次 Tick 检测一次（约 33~67ms，实测开销可忽略）
        const int BulkThreshold = 3;    // 同一轮变化超过这个数 → 合并成一条汇总（启动恢复配置不刷屏）

        static readonly List<Evt> _events = new List<Evt>();
        static FieldInfo[] _fields;
        static object[] _snap;
        static int _seq;
        static int _ticks;
        static bool _primed;

        /// <summary>最近事件的序号（外置端首次同步用，避免把历史事件全弹一遍）。</summary>
        public static int LastSeq { get { return _seq; } }

        /// <summary>取基线快照。必须在 ModSettings.Load() 之前调用 ——
        /// 这样「启动时恢复配置」才会被识别成变化并汇总成一条提示。</summary>
        public static void Prime()
        {
            try
            {
                var list = new List<FieldInfo>();
                foreach (FieldInfo f in typeof(ModSettings).GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    if (f.FieldType == typeof(bool) || f.FieldType == typeof(int) ||
                        f.FieldType == typeof(float) || f.FieldType == typeof(string))
                        list.Add(f);
                }
                _fields = list.ToArray();
                _snap = new object[_fields.Length];
                for (int i = 0; i < _fields.Length; i++) _snap[i] = _fields[i].GetValue(null);
                _primed = true;
                Bootstrap.Log("ChangeLog: 基线快照 " + _fields.Length + " 个字段");
            }
            catch (Exception ex) { Bootstrap.Log("ChangeLog.Prime 异常: " + ex.Message); }
        }

        /// <summary>由 FrameDriver 每帧调用（内部自动降频）。</summary>
        public static void Tick()
        {
            if (!_primed) { Prime(); return; }   // 没赶上 Init → 退化成「基线=当前值」，只是没有启动汇总提示
            if (++_ticks < DiffEveryTicks) return;
            _ticks = 0;
            try
            {
                List<Evt> changed = null;
                for (int i = 0; i < _fields.Length; i++)
                {
                    object cur;
                    try { cur = _fields[i].GetValue(null); }
                    catch { continue; }
                    if (Equals(cur, _snap[i])) continue;
                    _snap[i] = cur;
                    if (changed == null) changed = new List<Evt>(4);
                    changed.Add(Make(_fields[i], cur));
                }
                if (changed == null) return;
                if (changed.Count > BulkThreshold)
                {
                    // 一键全开 / 启动恢复配置：合并成一条「已恢复 N 项功能」
                    var bulk = new Evt { Name = "*", On = true, Val = changed.Count.ToString(CultureInfo.InvariantCulture) };
                    Push(bulk);
                    Bootstrap.Log("ChangeLog: 批量变化 " + changed.Count + " 项 → 已合并为一条汇总");
                }
                else
                {
                    foreach (var e in changed) Push(e);
                }
            }
            catch (Exception ex) { Bootstrap.Log("ChangeLog.Tick 异常: " + ex.Message); }
        }

        static Evt Make(FieldInfo f, object v)
        {
            var e = new Evt { Name = f.Name, On = true, Val = null };
            if (f.FieldType == typeof(bool))
            {
                e.On = (bool)v;
            }
            else
            {
                e.Val = v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture);
                // 数值/字符串型：空值或 0 视为「关闭」，其余视为「开启」（外置端据此选颜色）
                e.On = !(string.IsNullOrEmpty(e.Val) || e.Val == "0" || e.Val == "0.0");
            }
            return e;
        }

        static void Push(Evt e)
        {
            e.Seq = ++_seq;
            _events.Add(e);
            if (_events.Count > MaxEvents) _events.RemoveRange(0, _events.Count - MaxEvents);
        }

        /// <summary>外置端 GET /events?since=N 的响应体。
        /// since &lt; 0 → 只回当前序号、不回事件（客户端首次同步，不弹历史）。</summary>
        public static string BuildEventsBody(int since)
        {
            var sb = new StringBuilder(256);
            sb.Append("{\"ok\":true,\"seq\":").Append(_seq).Append(",\"events\":[");
            if (since >= 0)
            {
                bool first = true;
                int lo = since + 1;
                foreach (var e in _events)
                {
                    if (e.Seq < lo) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"seq\":").Append(e.Seq)
                      .Append(",\"name\":\"").Append(Esc(e.Name)).Append('"')
                      .Append(",\"on\":").Append(e.On ? "true" : "false")
                      .Append(",\"val\":");
                    if (e.Val == null) sb.Append("null");
                    else sb.Append('"').Append(Esc(e.Val)).Append('"');
                    sb.Append('}');
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }

        static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < ' ') sb.Append(' ');
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
