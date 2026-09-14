using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PvzheRemote
{
    /// <summary>外部/内部资源引用标记（对应 Python 的 ("@ext",i) / ("@int",i)）。</summary>
    public sealed class ExtRef { public int Kind; public int Index; }

    /// <summary>Godot 4.7 RSRC 二进制资源（save.res）数据结构。</summary>
    public class SaveResource
    {
        public uint BigEndian, UseReal64, Major, Minor, Format;
        public string Type = "";
        public ulong ImportmdOfs;
        public uint Flags;
        public ulong Uid;
        public string ScriptClass = "";
        public List<string> StringTable = new();
        public List<(string Type, string Path)> ExtResources = new();
        public List<(string Path, ulong Offset)> InternalResources = new();
        public Dictionary<object, object> Properties = new();
        public List<string> PropOrder = new();
        public string ResType = "";
    }

    // ---------- 二进制读取 ----------
    sealed class Sr
    {
        readonly byte[] d; int p;
        public Sr(byte[] data) { d = data; }
        public int Pos { get => p; set => p = value; }
        public void Skip(int n) => p += n;
        public byte[] Take(int n)
        {
            var b = new byte[n];
            Array.Copy(d, p, b, 0, n);
            p += n;
            return b;
        }
        public uint U32()
        {
            uint v = (uint)(d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24));
            p += 4;
            return v;
        }
        public int I32() => unchecked((int)U32());
        public ulong U64()
        {
            ulong v = 0;
            for (int i = 0; i < 8; i++) v |= (ulong)d[p + i] << (8 * i);
            p += 8;
            return v;
        }
        public float F32() { float v = BitConverter.ToSingle(d, p); p += 4; return v; }
        public double F64() { double v = BitConverter.ToDouble(d, p); p += 8; return v; }
        public string Str()
        {
            uint n = U32();
            string s = Encoding.UTF8.GetString(d, p, (int)n);
            p += (int)n;
            int nul = s.IndexOf('\0');
            if (nul >= 0) s = s.Substring(0, nul);
            return s;
        }
    }

    // ---------- 二进制写入 ----------
    sealed class Sw
    {
        readonly MemoryStream ms = new();
        public long Length => ms.Length;
        public void U32(uint v)
        {
            ms.WriteByte((byte)v);
            ms.WriteByte((byte)(v >> 8));
            ms.WriteByte((byte)(v >> 16));
            ms.WriteByte((byte)(v >> 24));
        }
        public void I32(int v) => U32(unchecked((uint)v));
        public void U64(ulong v)
        {
            for (int i = 0; i < 8; i++) ms.WriteByte((byte)(v >> (8 * i)));
        }
        public void F32(float v) => U32(BitConverter.SingleToUInt32Bits(v));
        public void F64(double v) => U64(BitConverter.DoubleToUInt64Bits(v));
        public void Str(string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s);
            var withNul = new byte[b.Length + 1];
            Array.Copy(b, withNul, b.Length);
            U32((uint)withNul.Length);
            ms.Write(withNul, 0, withNul.Length);
        }
        public void Raw(byte[] b) => ms.Write(b, 0, b.Length);
        public void PatchU64(long pos, ulong v)
        {
            long cur = ms.Position;
            ms.Position = pos;
            for (int i = 0; i < 8; i++) ms.WriteByte((byte)(v >> (8 * i)));
            ms.Position = cur;
        }
        public byte[] ToArray() => ms.ToArray();
    }

    // ---------- 解析 Variant ----------
    sealed class Sp
    {
        const uint T_NIL = 1, T_BOOL = 2, T_INT = 3, T_FLOAT = 4, T_STRING = 5, T_STRING_NAME = 21;
        const uint T_VECTOR2 = 6, T_VECTOR2I = 7, T_COLOR = 20, T_DICTIONARY = 26, T_ARRAY = 30;
        const uint T_PACKED_BYTE_ARRAY = 31, T_PACKED_INT32_ARRAY = 32, T_PACKED_FLOAT32_ARRAY = 34, T_PACKED_FLOAT64_ARRAY = 35, T_PACKED_STRING_ARRAY = 36;
        const uint T_OBJECT = 24;
        const uint OBJ_EMPTY = 0, OBJ_EXTERNAL_RESOURCE_INDEX = 3, OBJ_INTERNAL_RESOURCE_INDEX = 4;
        readonly Sr r;
        public List<string> Map = new();
        public Sp(Sr rr) { r = rr; }
        public string UString() => r.Str();
        public string GetString()
        {
            uint id = r.U32();
            if ((id & 0x80000000u) != 0)
            {
                int ln = (int)(id & 0x7FFFFFFFu);
                string s = Encoding.UTF8.GetString(r.Take(ln), 0, ln);
                return s;
            }
            return Map[(int)id];
        }
        public object? Variant()
        {
            uint t = r.U32();
            switch (t)
            {
                case T_NIL: return null;
                case T_BOOL: return r.U32() != 0;
                case T_INT: return r.I32();
                case T_FLOAT: return r.F32();
                case T_STRING:
                case T_STRING_NAME: return UString();
                case T_VECTOR2: return new List<object> { r.F32(), r.F32() };
                case T_VECTOR2I: return new List<object> { r.I32(), r.I32() };
                case T_COLOR: return new List<object> { r.F32(), r.F32(), r.F32(), r.F32() };
                case T_DICTIONARY:
                {
                    uint n = r.U32();
                    var d = new Dictionary<object, object>();
                    for (uint i = 0; i < n; i++)
                    {
                        var k = Variant();
                        var v = Variant();
                        if (k is List<object> || k is Dictionary<object, object>) k = k.ToString();
                        d[k!] = v;
                    }
                    return d;
                }
                case T_ARRAY:
                {
                    uint n = r.U32();
                    var l = new List<object>();
                    for (uint i = 0; i < n; i++) l.Add(Variant());
                    return l;
                }
                case T_PACKED_BYTE_ARRAY: { uint n = r.U32(); return r.Take((int)n); }
                case T_PACKED_INT32_ARRAY:
                {
                    uint n = r.U32();
                    var a = new int[n];
                    for (uint i = 0; i < n; i++) a[i] = r.I32();
                    return a;
                }
                case T_PACKED_FLOAT32_ARRAY:
                {
                    uint n = r.U32();
                    var a = new float[n];
                    for (uint i = 0; i < n; i++) a[i] = r.F32();
                    return a;
                }
                case T_PACKED_FLOAT64_ARRAY:
                {
                    uint n = r.U32();
                    var a = new double[n];
                    for (uint i = 0; i < n; i++) a[i] = r.F64();
                    return a;
                }
                case T_PACKED_STRING_ARRAY:
                {
                    uint n = r.U32();
                    var l = new List<string>();
                    for (uint i = 0; i < n; i++) l.Add(UString());
                    return l;
                }
                case T_OBJECT:
                {
                    uint ot = r.U32();
                    if (ot == OBJ_EMPTY) return null;
                    if (ot == OBJ_EXTERNAL_RESOURCE_INDEX) return new ExtRef { Kind = 3, Index = r.I32() };
                    if (ot == OBJ_INTERNAL_RESOURCE_INDEX) return new ExtRef { Kind = 4, Index = r.I32() };
                    throw new NotSupportedException("OBJECT 子类型 " + ot + " 未支持");
                }
                default: throw new NotSupportedException("Variant 类型码 " + t + " 未支持");
            }
        }
    }

    // ---------- 写入 Variant ----------
    sealed class Vw
    {
        readonly Sw w;
        public Vw(Sw sw) { w = sw; }
        void UString(string s) => w.Str(s);
        public void Variant(object? v)
        {
            if (v == null) w.U32(1);
            else if (v is bool b) { w.U32(2); w.U32(b ? 1u : 0u); }
            else if (v is int i) { w.U32(3); w.I32(i); }
            else if (v is float f) { w.U32(4); w.F32(f); }
            else if (v is string s) { w.U32(5); UString(s); }
            else if (v is Dictionary<object, object> d)
            {
                w.U32(26); w.U32((uint)d.Count);
                foreach (var kv in d) { Variant(kv.Key); Variant(kv.Value); }
            }
            else if (v is List<object> l)
            {
                w.U32(30); w.U32((uint)l.Count);
                foreach (var it in l) Variant(it);
            }
            else if (v is byte[] ba) { w.U32(31); w.U32((uint)ba.Length); w.Raw(ba); }
            else if (v is int[] ia) { w.U32(32); w.U32((uint)ia.Length); foreach (var x in ia) w.I32(x); }
            else if (v is float[] fa) { w.U32(34); w.U32((uint)fa.Length); foreach (var x in fa) w.F32(x); }
            else if (v is double[] da) { w.U32(35); w.U32((uint)da.Length); foreach (var x in da) w.F64(x); }
            else if (v is List<string> sa) { w.U32(36); w.U32((uint)sa.Count); foreach (var x in sa) UString(x); }
            else if (v is ExtRef x) { w.U32(24); w.U32((uint)x.Kind); w.U32((uint)x.Index); }
            else throw new NotSupportedException("无法写回 " + v.GetType());
        }
    }

    public static class SaveTools
    {
        const int MAX_NUM = 999999999;
        static readonly byte[] MAGIC = { (byte)'R', (byte)'S', (byte)'R', (byte)'C' };

        public static string DefaultSavePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Godot", "app_userdata", "植物大战僵尸杂交版", "Csharp", "save.res");

        public static string FindSavePath()
        {
            if (File.Exists(DefaultSavePath)) return DefaultSavePath;
            var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Godot", "app_userdata", "植物大战僵尸杂交版");
            if (Directory.Exists(baseDir))
                foreach (var f in Directory.EnumerateFiles(baseDir, "save.res", SearchOption.AllDirectories))
                    return f;
            return DefaultSavePath;
        }

        public static SaveResource Parse(byte[] data)
        {
            var r = new Sr(data);
            if (data.Length < 8 || data[0] != MAGIC[0] || data[1] != MAGIC[1] || data[2] != MAGIC[2] || data[3] != MAGIC[3])
                throw new InvalidDataException("不是 RSRC 文件");
            r.Skip(4);
            var res = new SaveResource();
            res.BigEndian = r.U32();
            res.UseReal64 = r.U32();
            res.Major = r.U32();
            res.Minor = r.U32();
            res.Format = r.U32();
            if (res.Format > 6) throw new InvalidDataException("format 版本 " + res.Format + " 过高");
            var sp = new Sp(r);
            res.Type = sp.UString();
            res.ImportmdOfs = r.U64();
            res.Flags = r.U32();
            if ((res.Flags & 2) != 0) res.Uid = r.U64();
            if ((res.Flags & 8) != 0) res.ScriptClass = sp.UString();
            for (int i = 0; i < 11; i++) r.U32();
            uint st = r.U32();
            for (uint i = 0; i < st; i++) res.StringTable.Add(sp.UString());
            sp.Map = res.StringTable;
            uint ext = r.U32();
            for (uint i = 0; i < ext; i++)
            {
                string t = sp.UString();
                string pth = sp.UString();
                if ((res.Flags & 2) != 0) r.U64();
                res.ExtResources.Add((t, pth));
            }
            uint inn = r.U32();
            for (uint i = 0; i < inn; i++) res.InternalResources.Add((sp.UString(), r.U64()));
            for (int idx = 0; idx < res.InternalResources.Count; idx++)
            {
                r.Pos = (int)res.InternalResources[idx].Offset;
                string typ = sp.UString();
                uint pc = r.U32();
                var props = new Dictionary<object, object>();
                var order = new List<string>();
                for (uint i = 0; i < pc; i++)
                {
                    string name = sp.GetString();
                    object? val = sp.Variant();
                    props[name] = val;
                    order.Add(name);
                }
                if (idx == res.InternalResources.Count - 1)
                {
                    res.ResType = typ;
                    res.Properties = props;
                    res.PropOrder = order;
                }
            }
            return res;
        }

        static List<string> BuildStrings(SaveResource res)
        {
            var strs = new List<string>();
            void Add(string s) { if (!strs.Contains(s)) strs.Add(s); }
            void Walk(object? v)
            {
                switch (v)
                {
                    case string s: Add(s); break;
                    case Dictionary<object, object> d:
                        foreach (var kv in d) { Walk(kv.Key); Walk(kv.Value); }
                        break;
                    case List<object> l:
                        foreach (var it in l) Walk(it);
                        break;
                    case List<string> sl:
                        foreach (var it in sl) Add(it);
                        break;
                }
            }
            foreach (var n in res.PropOrder) { Add(n); Walk(res.Properties[n]); }
            return strs;
        }

        public static byte[] ToBytes(SaveResource res)
        {
            var w = new Sw();
            w.Raw(MAGIC);
            w.U32(res.BigEndian);
            w.U32(res.UseReal64);
            w.U32(res.Major);
            w.U32(res.Minor);
            w.U32(res.Format);
            w.Str(res.Type);
            w.U64(res.ImportmdOfs);
            w.U32(res.Flags);
            if ((res.Flags & 2) != 0) w.U64(res.Uid);
            if ((res.Flags & 8) != 0) w.Str(res.ScriptClass);
            for (int i = 0; i < 11; i++) w.U32(0);
            var strs = BuildStrings(res);
            w.U32((uint)strs.Count);
            foreach (var s in strs) w.Str(s);
            var map = new Dictionary<string, int>();
            for (int i = 0; i < strs.Count; i++) map[strs[i]] = i;
            var vw = new Vw(w);
            w.U32((uint)res.ExtResources.Count);
            foreach (var (t, pth) in res.ExtResources)
            {
                w.Str(t);
                w.Str(pth);
                if ((res.Flags & 2) != 0) w.U64(0xFFFFFFFFFFFFFFFF);
            }
            w.U32(1);
            string mainPath = res.InternalResources.Count > 0 ? res.InternalResources[0].Path : "";
            w.Str(mainPath);
            long offPos = w.Length;
            w.U64(0);
            long dataStart = w.Length;
            w.Str(res.ResType);
            w.U32((uint)res.PropOrder.Count);
            foreach (var name in res.PropOrder)
            {
                w.U32((uint)map[name]);
                vw.Variant(res.Properties[name]);
            }
            w.PatchU64(offPos, (ulong)dataStart);
            w.Raw(MAGIC);
            return w.ToArray();
        }

        public static Dictionary<object, object>? GetUser(SaveResource res, out string? cur)
        {
            cur = null;
            if (res.Properties.TryGetValue("saveDictionary", out var sd) && sd is Dictionary<object, object> sdd
                && res.Properties.TryGetValue("userCurrent", out var uc) && uc is string us)
            {
                cur = us;
                if (sdd.TryGetValue(us, out var u) && u is Dictionary<object, object> ud) return ud;
            }
            return null;
        }

        // ---------- 满级规则 ----------

        /// <summary>一键全满：货币(所有 int Key→10亿) / 无限火力 / 章节进度 / 全卡解锁 / 全功能 / 全教程 / 全关卡通关。
        /// 0.27 新增货币（钻石/水晶等任何 Key int 值）也一并拉满。</summary>
        public static int ApplyFull(Dictionary<object, object> user)
        {
            int ops = 0;
            if (user.TryGetValue("Key", out var k) && k is Dictionary<object, object> key)
            {
                // 所有 int 数值键 → 10 亿（金币/钻石/水晶等任何货币/计数）
                var intKeys = key.Where(kv => kv.Value is int).Select(kv => kv.Key).ToList();
                foreach (var n in intKeys) { key[n] = MAX_NUM; ops++; }
                if (key.ContainsKey("UnlimitedFire")) { key["UnlimitedFire"] = true; ops++; }
                var chaps = key.Keys.OfType<string>().Where(s => s.StartsWith("AdventureChapter") && s.EndsWith("Index")).ToList();
                foreach (var s in chaps) { key[s] = 9; ops++; }
            }
            if (user.TryGetValue("TowerDefensePacket", out var pk) && pk is Dictionary<object, object> pkd)
            {
                var booleans = pkd.Where(kv => kv.Value is bool b && !b).Select(kv => kv.Key).ToList();
                foreach (var kv in pkd)
                {
                    if (kv.Value is Dictionary<object, object> val)
                    {
                        if (val.ContainsKey("Unlock")) { val["Unlock"] = true; ops++; }
                        if (val.ContainsKey("Love")) { val["Love"] = true; ops++; }
                    }
                }
                foreach (var kk in booleans) { pkd[kk] = true; ops++; }
            }
            if (user.TryGetValue("Feature", out var ft) && ft is Dictionary<object, object> ftd)
            {
                var offs = ftd.Where(kv => kv.Value is bool b && !b).Select(kv => kv.Key).ToList();
                foreach (var kk in offs) { ftd[kk] = true; ops++; }
            }
            if (user.TryGetValue("Tutorial", out var tu) && tu is Dictionary<object, object> tud)
            {
                var offs = tud.Where(kv => kv.Value is bool b && !b).Select(kv => kv.Key).ToList();
                foreach (var kk in offs) { tud[kk] = true; ops++; }
            }
            if (user.TryGetValue("Level", out var lv) && lv is Dictionary<object, object> lvd)
            {
                foreach (var kv in lvd)
                {
                    if (!(kv.Value is Dictionary<object, object> val)) continue;
                    val["Normal"] = true; val["Difficult"] = true; val["Ultimate"] = true;
                    val["Mower"] = true;
                    if (val.ContainsKey("Reward")) val["Reward"] = true;
                    if (val.TryGetValue("Key", out var lk) && lk is Dictionary<object, object> lkd)
                    {
                        lkd["Finish"] = 1;
                        if (lkd.ContainsKey("Play")) lkd["Play"] = 3;
                        if (lkd.ContainsKey("Played")) lkd["Played"] = 1;
                    }
                    ops++;
                }
            }
            return ops;
        }

        public static int ApplySelected(Dictionary<object, object> user,
            IEnumerable<string> cards, IEnumerable<string> levels, IEnumerable<string> keys,
            IEnumerable<string> features = null, IEnumerable<string> tutorials = null)
        {
            int ops = 0;
            if (user.TryGetValue("TowerDefensePacket", out var pk) && pk is Dictionary<object, object> pkd)
                foreach (var c in cards)
                    if (pkd.TryGetValue(c, out var val))
                    {
                        if (val is Dictionary<object, object> vd)
                        {
                            vd["Unlock"] = true;
                            if (vd.ContainsKey("Love")) vd["Love"] = true;
                        }
                        else pkd[c] = true;
                        ops++;
                    }
            if (user.TryGetValue("Level", out var lv) && lv is Dictionary<object, object> lvd)
                foreach (var c in levels)
                    if (lvd.TryGetValue(c, out var val) && val is Dictionary<object, object> vd)
                    {
                        vd["Normal"] = true; vd["Difficult"] = true; vd["Ultimate"] = true;
                        vd["Mower"] = true;
                        if (vd.ContainsKey("Reward")) vd["Reward"] = true;
                        if (vd.TryGetValue("Key", out var lk) && lk is Dictionary<object, object> lkd)
                        {
                            lkd["Finish"] = 1;
                            if (lkd.ContainsKey("Play")) lkd["Play"] = 3;
                        }
                        ops++;
                    }
            if (user.TryGetValue("Key", out var kk) && kk is Dictionary<object, object> key)
                foreach (var c in keys)
                    if (key.ContainsKey(c))
                    {
                        key[c] = key[c] is int ? MAX_NUM : true;
                        ops++;
                    }
            // 全功能 / 全教程解锁
            if (features != null && user.TryGetValue("Feature", out var ft) && ft is Dictionary<object, object> ftd)
                foreach (var c in features)
                    if (ftd.ContainsKey(c)) { ftd[c] = true; ops++; }
            if (tutorials != null && user.TryGetValue("Tutorial", out var tu) && tu is Dictionary<object, object> tud)
                foreach (var c in tutorials)
                    if (tud.ContainsKey(c)) { tud[c] = true; ops++; }
            return ops;
        }

        // ---------- 多用户管理 ----------

        /// <summary>列出所有用户（saveDictionary 的 key）。</summary>
        public static List<string> GetUsers(SaveResource res)
        {
            var list = new List<string>();
            if (res.Properties.TryGetValue("saveDictionary", out var sd) && sd is Dictionary<object, object> sdd)
                foreach (var k in sdd.Keys)
                    if (k is string s) list.Add(s);
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        /// <summary>新增用户：复制当前用户存档结构，缺省则建空用户。返回是否成功。</summary>
        public static bool AddUser(SaveResource res, string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (!(res.Properties.TryGetValue("saveDictionary", out var sd) && sd is Dictionary<object, object> sdd))
            {
                sdd = new Dictionary<object, object>();
                res.Properties["saveDictionary"] = sdd;
            }
            if (sdd.ContainsKey(name)) return false;
            // 以当前用户为模板复制（保结构），无则建空
            Dictionary<object, object> template = new();
            if (res.Properties.TryGetValue("userCurrent", out var uc) && uc is string us && sdd.TryGetValue(us, out var t) && t is Dictionary<object, object> td)
                template = td;
            sdd[name] = template;
            // userList 追加
            var list = new List<object>();
            if (res.Properties.TryGetValue("userList", out var ul) && ul is List<object> l) list = l;
            if (!list.Contains(name)) list.Add(name);
            res.Properties["userList"] = list;
            return true;
        }

        /// <summary>切换当前用户。</summary>
        public static void SwitchUser(SaveResource res, string name)
        {
            res.Properties["userCurrent"] = name;
        }

        /// <summary>删除用户（不允许删当前用户）。返回是否成功。</summary>
        public static bool DeleteUser(SaveResource res, string name)
        {
            if (!(res.Properties.TryGetValue("saveDictionary", out var sd) && sd is Dictionary<object, object> sdd)) return false;
            if (res.Properties.TryGetValue("userCurrent", out var uc) && uc is string us && us == name) return false;
            if (!sdd.Remove(name)) return false;
            if (res.Properties.TryGetValue("userList", out var ul) && ul is List<object> l) l.Remove(name);
            return true;
        }

        /// <summary>直接设置 Key 数值（货币/计数等）。value 传字符串自动按当前类型解析。</summary>
        public static bool SetKeyValue(Dictionary<object, object> user, string key, string value)
        {
            if (!(user.TryGetValue("Key", out var k) && k is Dictionary<object, object> keyd)) return false;
            if (!keyd.TryGetValue(key, out var cur)) return false;
            if (cur is int) { if (!int.TryParse(value, out int v)) return false; keyd[key] = v; return true; }
            if (cur is bool) { keyd[key] = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase); return true; }
            keyd[key] = value;
            return true;
        }

        public static string Backup(string path)
        {
            var bak = path + ".bak_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            File.Copy(path, bak, true);
            return bak;
        }

        public static void WriteSave(string path, SaveResource res)
        {
            byte[] data = ToBytes(res);
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, path, true);
        }

        // ---------- 显示辅助（翻译） ----------

        static readonly Dictionary<string, string> CardClass = new()
        {
            { "Plant", "植物" }, { "Zombie", "僵尸" }, { "Item", "道具" }, { "Grave", "墓碑" },
            { "Crater", "坑" }, { "Rune", "符文" }, { "Vase", "花瓶" }, { "Mower", "割草机" },
            { "Chest", "宝箱" }, { "Time", "时间" }, { "Bungi", "蹦极" }, { "Clone", "克隆" },
            { "Duckytube", "鸭子泳圈" }, { "Floor", "地板" }, { "Skeleton", "骷髅" },
            { "Trash", "垃圾" }, { "Zongzi", "粽子" },
        };

        public static string CardDisplay(string name)
        {
            var m = System.Text.RegularExpressions.Regex.Match(name, @"^([A-Z][a-z]*)");
            return m.Success && CardClass.TryGetValue(m.Groups[1].Value, out var cls)
                ? "[" + cls + "] " + name : name;
        }

        public static string LevelDisplay(string name)
        {
            var m = System.Text.RegularExpressions.Regex.Match(name, @"^Level(\d+)_(\d+)$");
            if (m.Success) return "第" + m.Groups[1].Value + "章 第" + m.Groups[2].Value + "关";
            m = System.Text.RegularExpressions.Regex.Match(name, @"^Challenge_Level_Diamond(\d+)_(\d+)$");
            if (m.Success) return "钻石挑战·第" + m.Groups[1].Value + "章 第" + m.Groups[2].Value + "关";
            m = System.Text.RegularExpressions.Regex.Match(name, @"^Challenge_Level(\d+)_(\d+)$");
            if (m.Success) return "挑战·第" + m.Groups[1].Value + "章 第" + m.Groups[2].Value + "关";
            m = System.Text.RegularExpressions.Regex.Match(name, @"^MiniGames_Level(\d+)_(\d+)$");
            if (m.Success) return "小游戏·第" + m.Groups[1].Value + "章 第" + m.Groups[2].Value + "关";
            m = System.Text.RegularExpressions.Regex.Match(name, @"^Shooting_Level(\d+)_(\d+)$");
            if (m.Success) return "射击·第" + m.Groups[1].Value + "章 第" + m.Groups[2].Value + "关";
            m = System.Text.RegularExpressions.Regex.Match(name, @"^Survival_Level(\d+)_(\d+)$");
            if (m.Success) return "生存·第" + m.Groups[1].Value + "章 第" + m.Groups[2].Value + "关";
            m = System.Text.RegularExpressions.Regex.Match(name, @"^Survival_Level_Entertainment(\d+)_(\d+)$");
            if (m.Success) return "生存·娱乐 第" + m.Groups[1].Value + "章 第" + m.Groups[2].Value + "关";
            m = System.Text.RegularExpressions.Regex.Match(name, @"^Vase_Level(\d+)$");
            if (m.Success) return "花瓶模式·第" + m.Groups[1].Value + "关";
            m = System.Text.RegularExpressions.Regex.Match(name, @"^IZM2_Level(\d+)_(\d+)$");
            if (m.Success) return "IZM2·第" + m.Groups[1].Value + "章 第" + m.Groups[2].Value + "关";
            m = System.Text.RegularExpressions.Regex.Match(name, @"^IZM_Level(\d+)$");
            if (m.Success) return "IZM·第" + m.Groups[1].Value + "关";
            m = System.Text.RegularExpressions.Regex.Match(name, @"^OnlineLevel-(\d+)$");
            if (m.Success) return "联机关卡 (" + m.Groups[1].Value + ")";
            m = System.Text.RegularExpressions.Regex.Match(name, @"^DailyLevel-(\d{4}-\d{2}-\d{2})-\d+$");
            if (m.Success) return "每日关卡 (" + m.Groups[1].Value + ")";
            return name;
        }

        public static string FeatureDisplay(string name)
        {
            if (FeatureZh.TryGetValue(name, out var zh)) return zh + "（" + name + "）";
            if (name.StartsWith("Shovel")) return "铲子·" + name.Substring("Shovel".Length) + "（" + name + "）";
            if (name.StartsWith("Mower")) return "割草机·" + name.Substring("Mower".Length) + "（" + name + "）";
            return name;
        }

        static readonly Dictionary<string, string> FeatureZh = new()
        {
            { "TowerDefenseWorldMap", "世界地图" }, { "TowerDefensePlantfood", "植物能量豆" },
            { "Coins", "金币系统" }, { "Shop", "商店" }, { "Shovel", "铲子系统" },
            { "ShovelDefault", "铲子·普通" }, { "ShovelGold", "铲子·黄金" }, { "ShovelCold", "铲子·寒冰" },
            { "ShovelDiamond", "铲子·钻石" }, { "ShovelJalapeno", "铲子·辣椒" }, { "ShovelMagnet", "铲子·磁铁" },
            { "ShovelWatermelon", "铲子·西瓜" }, { "ShovelPumpkin", "铲子·南瓜" }, { "ShovelCaptain", "铲子·船长" },
            { "ShovelSkeleton", "铲子·骷髅" }, { "ShovelExplode", "铲子·爆炸" },
            { "MowerDefault", "割草机·普通" }, { "MowerSun", "割草机·阳光" },
            { "Adventure", "冒险模式" }, { "Challenge", "挑战模式" }, { "Survival", "生存模式" },
            { "MiniGame", "小游戏" }, { "Puzzle", "拼图" }, { "IZM2", "IZM2 模式" },
            { "Glove", "手套" }, { "ScreenEffect", "屏幕特效" }, { "RainMode", "种子雨" },
            { "ConveyorBelt", "传送带" }, { "SeedBank", "卡槽" }, { "PacketBank", "选卡" },
            { "Portal", "传送门" }, { "Sun", "阳光" }, { "Map", "地图" }, { "Mower", "割草机" },
            { "Brain", "脑子" }, { "Arena", "竞技场" }, { "Garden", "花园" }, { "Trade", "交易" },
        };

        static readonly Dictionary<string, string> KeyZh = new()
        {
            { "CoinNum", "金币数量" }, { "CrystalNum", "钻石数量" }, { "UnlimitedFire", "无限火力" },
            { "CurrentShovel", "当前铲子" }, { "CurrentMower", "当前割草机" }, { "CurrentDifficult", "当前难度" },
            { "LevelMenuFlatten", "关卡菜单扁平化" }, { "LevelEditorBattleCurrentLevel", "关卡编辑器·当前关" },
            { "LevelEditorBattleFinishNum", "关卡编辑器·通关数" }, { "LevelEditorBattleFailNum", "关卡编辑器·失败数" },
            { "PacketReSlect", "上次选卡" }, { "PacketGroup1", "选卡分组 1" }, { "PacketGroup2", "选卡分组 2" },
            { "PacketGroup3", "选卡分组 3" }, { "PacketGroup4", "选卡分组 4" }, { "PacketGroup5", "选卡分组 5" },
            { "PacketGroup6", "选卡分组 6" }, { "PacketGroupName1", "选卡分组名 1" }, { "PacketGroupName2", "选卡分组名 2" },
            { "PacketGroupName3", "选卡分组名 3" }, { "PacketGroupName4", "选卡分组名 4" }, { "PacketGroupName5", "选卡分组名 5" },
            { "PacketGroupName6", "选卡分组名 6" },
        };

        public static string KeyDisplay(string name)
        {
            if (KeyZh.TryGetValue(name, out var zh)) return zh + "（" + name + "）";
            if (name.StartsWith("AdventureChapter") && name.EndsWith("Index"))
            {
                var ch = name.Substring("AdventureChapter".Length, name.Length - "AdventureChapter".Length - "Index".Length);
                return "第" + ch + "章 进度（" + name + "）";
            }
            if (name.StartsWith("PacketGroupName")) return "选卡分组名 " + name.Substring("PacketGroupName".Length) + "（" + name + "）";
            if (name.StartsWith("PacketGroup")) return "选卡分组 " + name.Substring("PacketGroup".Length) + "（" + name + "）";
            return name;
        }
    }
}
