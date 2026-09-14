using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Godot;

namespace PvzheMod
{
    /// <summary>
    /// 实体属性调节：枚举当前场上所有植物/僵尸实例，动态反射每个实例的所有可改字段
    /// （血量/攻速/伤害/射程/大小/状态/组件等，覆盖基类+子类全部），可读可写。
    /// 由外置修改器通过 /entities /eprops /eset 使用。复用 GameCheats 的场上角色缓存。
    /// </summary>
    public static class EntityTools
    {
        static readonly BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        static bool IsSimple(Type t)
        {
            return t == typeof(float) || t == typeof(double) || t == typeof(int) || t == typeof(bool) ||
                   t == typeof(string) || t == typeof(uint) || t == typeof(long) || t == typeof(byte) || t == typeof(short);
        }

        // 只收集游戏自身（非 Godot/System 命名空间）声明的字段，避免 Node/Control 内部字段刷屏。
        // 注意：本游戏全部类型位于全局命名空间（Namespace 为空），必须视为游戏类型。
        static bool IsGameType(Type t)
        {
            if (t == null) return false;
            string ns = t.Namespace;
            if (string.IsNullOrEmpty(ns)) return true;          // 全局命名空间 → 游戏类型
            if (ns.StartsWith("Godot")) return false;
            if (ns.StartsWith("System")) return false;
            return true;
        }

        static string TypeName(Type t)
        {
            if (t == typeof(float)) return "float";
            if (t == typeof(double)) return "double";
            if (t == typeof(int)) return "int";
            if (t == typeof(bool)) return "bool";
            if (t == typeof(string)) return "string";
            if (t == typeof(uint)) return "uint";
            if (t == typeof(long)) return "long";
            if (t == typeof(byte)) return "byte";
            if (t == typeof(short)) return "short";
            return t.Name;
        }

        /// <summary>遍历场上所有实体（植物+僵尸缓存），返回 {id,name,camp,x,y} 的 JSON 数组。</summary>
        public static string EnumerateJson()
        {
            // 先强制刷新缓存：否则所有功能全关时缓存永不更新，列表刷不到实体
            GameCheats.ForceRefreshRoleCache();
            var sb = new StringBuilder("[");
            bool first = true;
            try
            {
                foreach (var n in GameCheats.GetPlantCache())
                    first = AppendEntity(sb, n, "Plant", ref first);
                foreach (var n in GameCheats.GetZombieCache())
                    first = AppendEntity(sb, n, "Zombie", ref first);
            }
            catch (Exception ex) { Bootstrap.Log("EntityTools 枚举异常: " + ex.Message); }
            sb.Append(']');
            return sb.ToString();
        }

        static bool AppendEntity(StringBuilder sb, Node2D n, string camp, ref bool first)
        {
            if (n == null || !GodotObject.IsInstanceValid(n)) return first;
            try
            {
                if (!first) sb.Append(',');
                first = false;
                string name = n.GetType().Name;
                if (name.StartsWith("TowerDefense", StringComparison.Ordinal)) name = name.Substring("TowerDefense".Length);
                sb.Append("{\"id\":").Append(n.GetInstanceId())
                  .Append(",\"name\":\"").Append(Esc(name))
                  .Append("\",\"camp\":\"").Append(camp)
                  .Append("\",\"x\":").Append(n.GlobalPosition.X.ToString("0", CultureInfo.InvariantCulture))
                  .Append(",\"y\":").Append(n.GlobalPosition.Y.ToString("0", CultureInfo.InvariantCulture))
                  .Append('}');
            }
            catch { }
            return first;
        }

        /// <summary>按 instanceId 在场上缓存中查找实体。</summary>
        static Node2D FindById(ulong id)
        {
            foreach (var n in GameCheats.GetPlantCache())
                if (n != null && GodotObject.IsInstanceValid(n) && n.GetInstanceId() == id) return n;
            foreach (var n in GameCheats.GetZombieCache())
                if (n != null && GodotObject.IsInstanceValid(n) && n.GetInstanceId() == id) return n;
            return null;
        }

        // ===== 实体语义字段（真实可调键 → 中文提示名）：改动直接落到真实对象（instance/FireComponent/transformPoint/config）=====
        static readonly (string Key, string Label)[] _plantSem = { ("hp","血量"), ("hitpointScale","血量倍率"), ("attack","攻速秒"), ("scale","大小"), ("cost","阳光"), ("cooldown","冷却秒") };
        static readonly (string Key, string Label)[] _zombieSem = { ("hp","血量"), ("hitpointScale","血量倍率"), ("scale","大小") };

        static bool IsZombieNode(Node2D n)
        {
            try { return n.GetType().Name.StartsWith("TowerDefenseZombie", StringComparison.Ordinal); }
            catch { return false; }
        }

        static void EmitSemantics(StringBuilder sb, Node2D n, ref bool first)
        {
            try
            {
                var arr = IsZombieNode(n) ? _zombieSem : _plantSem;
                foreach (var (k, lbl) in arr)
                {
                    double val;
                    if (!GameCheats.ReadEntityReal(n, k, out val)) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"f\":\"").Append(k)
                      .Append("\",\"t\":\"").Append(Esc(lbl))
                      .Append("\",\"v\":").Append(val.ToString("0.###", CultureInfo.InvariantCulture))
                      .Append('}');
                }
            }
            catch { }
        }

        /// <summary>反射读取实体所有可改字段 → JSON：[{f,name,t,value,v,...}]。前置真实可调语义字段（hp/scale/...）。</summary>
        public static string GetPropsJson(ulong id)
        {
            var n = FindById(id);
            if (n == null) return "{\"err\":\"not-found\"}";
            var sb = new StringBuilder("[");
            bool first = true;
            EmitSemantics(sb, n, ref first);   // 前置：真实可调项（其余裸字段多为无效配置缓存，改了不生效）
            var t = n.GetType();
            while (IsGameType(t))
            {
                foreach (var f in t.GetFields(Flags | BindingFlags.DeclaredOnly))
                {
                    if (!IsSimple(f.FieldType)) continue;
                    object v;
                    try { v = f.GetValue(n); } catch { continue; }
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"f\":\"").Append(Esc(f.Name))
                      .Append("\",\"t\":\"").Append(TypeName(f.FieldType))
                      .Append("\",\"v\":").Append(ValJson(f.FieldType, v))
                      .Append('}');
                }
                t = t.BaseType;
            }
            sb.Append(']');
            return sb.ToString();
        }

        /// <summary>设置实体字段。语义键（hp/scale/attack/cost/cooldown/hitpointScale）路由到真实对象
        /// （instance/FireComponent/transformPoint/config，否则改了不生效）；其余按裸字段沿继承链查找（保守兼容）。
        /// 返回 "ok" 或 "err:原因"。</summary>
        public static string SetProp(ulong id, string field, string val)
        {
            var n = FindById(id);
            if (n == null) return "err:实体不存在";
            if (string.IsNullOrEmpty(field)) return "err:缺少字段名";
            // ① 语义键 → 真实对象路由
            if (field == "hp" || field == "hitpointScale" || field == "scale" || field == "attack" || field == "cost" || field == "cooldown")
            {
                double dv;
                if (!double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out dv)) return "err:数值无效 " + val;
                return GameCheats.WriteEntityReal(n, field, dv);
            }
            // ② 裸字段沿继承链（保守：命中节点自身声明的字段才改）
            var t = n.GetType();
            while (IsGameType(t))
            {
                var f = t.GetField(field, Flags | BindingFlags.DeclaredOnly);
                if (f != null)
                {
                    try
                    {
                        object v = ParseVal(f.FieldType, val);
                        f.SetValue(n, v);
                        return "ok";
                    }
                    catch (Exception ex) { Bootstrap.Log("EntityTools 设置失败 " + field + ": " + ex.Message); return "err:" + ex.Message; }
                }
                t = t.BaseType;
            }
            return "err:找不到字段 " + field;
        }

        static object ParseVal(Type ft, string val)
        {
            if (ft == typeof(float)) return float.Parse(val, CultureInfo.InvariantCulture);
            if (ft == typeof(double)) return double.Parse(val, CultureInfo.InvariantCulture);
            if (ft == typeof(int)) return int.Parse(val, CultureInfo.InvariantCulture);
            if (ft == typeof(bool)) return val == "1" || val == "true" || val == "True" || val == "on";
            if (ft == typeof(uint)) return uint.Parse(val, CultureInfo.InvariantCulture);
            if (ft == typeof(long)) return long.Parse(val, CultureInfo.InvariantCulture);
            if (ft == typeof(byte)) return byte.Parse(val, CultureInfo.InvariantCulture);
            if (ft == typeof(short)) return short.Parse(val, CultureInfo.InvariantCulture);
            return val;
        }

        static string ValJson(Type ft, object v)
        {
            if (v == null) return "\"\"";
            if (ft == typeof(string)) return "\"" + Esc((string)v) + "\"";
            if (ft == typeof(bool)) return ((bool)v) ? "true" : "false";
            if (ft == typeof(float)) return ((float)v).ToString("0.###", CultureInfo.InvariantCulture);
            if (ft == typeof(double)) return ((double)v).ToString("0.###", CultureInfo.InvariantCulture);
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        static string Esc(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        // ===== 类型全局覆盖：固定列表选类型 → 调节该类所有实例（可关，移除时恢复原始值） =====
        static readonly Dictionary<string, Dictionary<string, double>> _gOver = new(StringComparer.Ordinal);  // type,prop → 覆盖值
        static readonly Dictionary<string, double> _gOrig = new(StringComparer.Ordinal);                       // type\u0000prop → 原始值

        public static string SetGlobalOverride(string type, string prop, double val)
        {
            if (!_gOver.TryGetValue(type, out var d)) { d = new Dictionary<string, double>(StringComparer.Ordinal); _gOver[type] = d; }
            d[prop] = val;
            RecordOrigOnce(type, prop);
            ApplyType(type, prop, val, false);
            return "ok";
        }

        public static string RemoveGlobalOverride(string type, string prop)
        {
            if (_gOver.TryGetValue(type, out var d) && d.Remove(prop))
            {
                double orig = _gOrig.TryGetValue(type + "\u0000" + prop, out var ov) ? ov : 1;
                ApplyType(type, prop, orig, true);
                if (d.Count == 0) _gOver.Remove(type);
                return "ok";
            }
            return "no-override";
        }

        public static string GetGlobalOverridesJson()
        {
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (var kv in _gOver)
                foreach (var p in kv.Value)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"type\":\"").Append(Esc(kv.Key))
                      .Append("\",\"p\":\"").Append(Esc(p.Key))
                      .Append("\",\"v\":").Append(p.Value.ToString("0.##", CultureInfo.InvariantCulture))
                      .Append('}');
                }
            sb.Append(']');
            return sb.ToString();
        }

        static void RecordOrigOnce(string type, string prop)
        {
            if (_gOrig.ContainsKey(type + "\u0000" + prop)) return;
            foreach (var n in AllEntities())
            {
                if (!TypeMatches(n, type)) continue;
                double? v = ReadPropValue(n, prop);
                if (v.HasValue) { _gOrig[type + "\u0000" + prop] = v.Value; return; }
            }
        }

        static void ApplyType(string type, string prop, double val, bool restore)
        {
            foreach (var n in AllEntities())
            {
                if (!TypeMatches(n, type)) continue;
                if (prop == "attack")
                {
                    var f = FindField(n, "_fireInterval"); if (f == null) f = FindField(n, "fireInterval");
                    if (f != null)
                    {
                        double orig = _gOrig.TryGetValue(type + "\u0000attack", out var ov) ? ov : 1;
                        SetFieldNum(f, n, restore ? orig : (val > 0.01 ? orig / val : orig));
                    }
                    continue;
                }
                ApplyProp(n, prop, val);
            }
        }

        static void ApplyProp(Node2D n, string prop, double val)
        {
            try
            {
                // ⓪ 语义键 → 真实对象（instance/FireComponent/transformPoint/config），否则全局覆盖改了不生效
                if (prop == "hp" || prop == "scale" || prop == "cost" || prop == "cooldown" || prop == "hitpointScale")
                { GameCheats.WriteEntityReal(n, prop, val); return; }
                // ① 精确字段名：任意字段直接按名设置（每个类型的专属字段走这里）
                var f = FindField(n, prop);
                if (f != null)
                {
                    if (f.FieldType == typeof(bool)) f.SetValue(n, val > 0.5);
                    else SetFieldNum(f, n, val);
                    return;
                }
                // ② 兜底：常用别名（大小/无敌/血量/伤害/攻速/射程/速度/价格/冷却/阳光）
                switch (prop)
                {
                    case "scale": n.Scale = new Vector2((float)val, (float)val); break;
                    case "invincible": { var fi = FindField(n, "invincible"); if (fi != null) fi.SetValue(n, val > 0.5); } break;
                    case "hp": { var fi = FindNumericField(n, "Hp", "Health"); if (fi != null) SetFieldNum(fi, n, val); } break;
                    case "damage": { var fi = FindNumericField(n, "Damage", "Attack"); if (fi != null) SetFieldNum(fi, n, val); } break;
                    case "range": { var fi = FindNumericField(n, "Range"); if (fi != null) SetFieldNum(fi, n, val); } break;
                    case "speed": { var fi = FindNumericField(n, "Speed"); if (fi != null) SetFieldNum(fi, n, val); } break;
                    case "cost": { var fi = FindNumericField(n, "Cost"); if (fi != null) SetFieldNum(fi, n, val); } break;
                    case "cooldown": { var fi = FindNumericField(n, "Cooldown"); if (fi != null) SetFieldNum(fi, n, val); } break;
                    case "sun": { var fi = FindNumericField(n, "SunNum", "Sun"); if (fi != null) SetFieldNum(fi, n, val); } break;
                }
            }
            catch { }
        }

        static void SetFieldNum(FieldInfo f, object o, double v)
        {
            if (f.FieldType == typeof(float)) f.SetValue(o, (float)v);
            else if (f.FieldType == typeof(double)) f.SetValue(o, v);
            else if (f.FieldType == typeof(int)) f.SetValue(o, (int)v);
        }

        static FieldInfo FindField(Node2D n, string name)
        {
            var t = n.GetType();
            while (IsGameType(t))
            {
                var f = t.GetField(name, Flags | BindingFlags.DeclaredOnly);
                if (f != null) return f;
                t = t.BaseType;
            }
            return null;
        }

        static FieldInfo FindHpField(Node2D n)
        {
            var t = n.GetType();
            while (IsGameType(t))
            {
                foreach (var f in t.GetFields(Flags | BindingFlags.DeclaredOnly))
                    if ((f.Name.IndexOf("Hp", StringComparison.OrdinalIgnoreCase) >= 0 || f.Name.IndexOf("Health", StringComparison.OrdinalIgnoreCase) >= 0) && IsSimple(f.FieldType))
                        return f;
                t = t.BaseType;
            }
            return null;
        }

        static double? ReadPropValue(Node2D n, string prop)
        {
            try
            {
                // ⓪ 语义键 → 真实对象当前值（供全局覆盖记录原始值，恢复时还原真实值）
                if (prop == "hp" || prop == "scale" || prop == "cost" || prop == "cooldown" || prop == "hitpointScale")
                {
                    double rv;
                    if (GameCheats.ReadEntityReal(n, prop, out rv)) return rv;
                }
                // ① 精确字段名：任意字段直接读值
                var f = FindField(n, prop);
                if (f != null)
                {
                    if (f.FieldType == typeof(bool)) return (bool)f.GetValue(n) ? 1 : 0;
                    return Convert.ToDouble(f.GetValue(n), CultureInfo.InvariantCulture);
                }
                // ② 兜底：常用别名
                switch (prop)
                {
                    case "scale": return n.Scale.X;
                    case "invincible": { var fi = FindField(n, "invincible"); if (fi != null && fi.GetValue(n) is bool b) return b ? 1 : 0; return null; }
                    case "hp": return ReadNumeric(n, "Hp", "Health");
                    case "damage": return ReadNumeric(n, "Damage", "Attack");
                    case "range": return ReadNumeric(n, "Range");
                    case "speed": return ReadNumeric(n, "Speed");
                    case "cost": return ReadNumeric(n, "Cost");
                    case "cooldown": return ReadNumeric(n, "Cooldown");
                    case "sun": return ReadNumeric(n, "SunNum", "Sun");
                    case "attack": { var fi = FindField(n, "_fireInterval"); if (fi == null) fi = FindField(n, "fireInterval"); if (fi != null) return Convert.ToDouble(fi.GetValue(n), CultureInfo.InvariantCulture); return null; }
                }
            }
            catch { }
            return null;
        }

        static double? ReadNumeric(Node2D n, params string[] keys)
        {
            var f = FindNumericField(n, keys);
            if (f == null) return null;
            try { return Convert.ToDouble(f.GetValue(n), CultureInfo.InvariantCulture); } catch { return null; }
        }

        static FieldInfo FindNumericField(Node2D n, params string[] keys)
        {
            var t = n.GetType();
            while (IsGameType(t))
            {
                foreach (var f in t.GetFields(Flags | BindingFlags.DeclaredOnly))
                {
                    if (!IsSimple(f.FieldType)) continue;
                    foreach (var k in keys)
                        if (f.Name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)
                            return f;
                }
                t = t.BaseType;
            }
            return null;
        }

        static bool TypeMatches(Node2D n, string type)
        {
            if (string.IsNullOrEmpty(type)) return false;
            try { return n.GetType().Name.IndexOf(type, StringComparison.OrdinalIgnoreCase) >= 0; }
            catch { return false; }
        }

        static IEnumerable<Node2D> AllEntities()
        {
            foreach (var n in GameCheats.GetPlantCache())
                if (n != null && GodotObject.IsInstanceValid(n)) yield return n;
            foreach (var n in GameCheats.GetZombieCache())
                if (n != null && GodotObject.IsInstanceValid(n)) yield return n;
        }

        // ===== 类型专属字段：动态反射该类型（如 TowerDefensePlantSunFlower）的所有可改字段 =====
        public static string GetTypePropsJson(string type)
        {
            var t = FindTypeInGame(type);
            if (t == null) { Bootstrap.Log("GetTypePropsJson(" + type + "): 未找到类型"); return "{\"err\":\"no-type\"}"; }
            Bootstrap.Log("GetTypePropsJson(" + type + "): 命中 " + t.FullName + " @" + t.Assembly.GetName().Name);
            var sb = new StringBuilder("[");
            bool first = true;
            // 只收集该类型自身声明的简单字段（真正的“专属字段”），不再沿基类链收集公共字段
            foreach (var f in t.GetFields(Flags | BindingFlags.DeclaredOnly))
            {
                if (!IsSimple(f.FieldType)) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"f\":\"").Append(Esc(f.Name))
                  .Append("\",\"t\":\"").Append(TypeName(f.FieldType)).Append("\"}");
            }
            sb.Append(']');
            Bootstrap.Log("GetTypePropsJson(" + type + "): 专属字段数=" + ((sb.Length > 1) ? (sb.Length - 1).ToString() : "0"));
            return sb.ToString();
        }

        // ===== 固定类型列表：反射游戏程序集所有植物/僵尸类型，不依赖卡牌数据加载（主菜单也有完整列表） =====
        public static string GetTypeListJson()
        {
            var sb = new StringBuilder("[");
            bool first = true;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { if (asm.GetName().Name != "PlantsVsZombies") continue; } catch { continue; }
                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }
                foreach (var t in types)
                {
                    string tn = t.Name;
                    string id = null;
                    if (tn.StartsWith("TowerDefensePlant", StringComparison.Ordinal)) id = "Plant" + tn.Substring("TowerDefensePlant".Length);
                    else if (tn.StartsWith("TowerDefenseZombie", StringComparison.Ordinal)) id = "Zombie" + tn.Substring("TowerDefenseZombie".Length);
                    else continue;
                    if (!seen.Add(id)) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    string name = id;
                    try { name = GameCheats.GetPacketDisplayNameZh(id); } catch { }
                    if (string.IsNullOrEmpty(name) || name == id) name = tn.Replace("TowerDefense", "");
                    sb.Append("{\"id\":\"").Append(Esc(id))
                      .Append("\",\"name\":\"").Append(Esc(name)).Append("\"}");
                }
            }
            sb.Append(']');
            return sb.ToString();
        }

        // ===== 卡牌/角色配置层：改 TowerDefensePacketConfig / TowerDefenseCharacterConfig 字段 → 影响该类新生成 =====
        static object GetConfigObj(string id)
        {
            try { return GameCheats.GetConfigPublic(id); } catch { return null; }
        }

        static object GetCharacterConfig(object packetCfg)
        {
            try
            {
                if (packetCfg == null) return null;
                var t = packetCfg.GetType();
                var f = t.GetField("_characterConfig", Flags);
                if (f != null) return f.GetValue(packetCfg);
                var p = t.GetProperty("characterConfig", BindingFlags.Public | BindingFlags.Instance);
                if (p != null) return p.GetValue(packetCfg);
                return null;
            }
            catch { return null; }
        }

        /// <summary>反射卡牌配置对象所有可改字段（packet 卡牌层 + char 角色底层）→ JSON。</summary>
        public static string GetPacketConfigPropsJson(string id)
        {
            var cfg = GetConfigObj(id);
            if (cfg == null) { Bootstrap.Log("GetPacketConfigPropsJson(" + id + "): no-cfg"); return "{\"err\":\"no-cfg\"}"; }
            Bootstrap.Log("GetPacketConfigPropsJson(" + id + "): cfg=" + cfg.GetType().FullName);
            var sb = new StringBuilder("[");
            bool first = true;
            AddSimpleFields(sb, cfg, cfg.GetType(), ref first, "packet");
            var cc = GetCharacterConfig(cfg);
            if (cc != null) AddSimpleFields(sb, cc, cc.GetType(), ref first, "char");
            sb.Append(']');
            return sb.ToString();
        }

        static void AddSimpleFields(StringBuilder sb, object o, Type t, ref bool first, string group)
        {
            while (IsGameType(t))
            {
                foreach (var f in t.GetFields(Flags | BindingFlags.DeclaredOnly))
                {
                    if (!IsSimple(f.FieldType)) continue;
                    object v;
                    try { v = f.GetValue(o); } catch { continue; }
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"f\":\"").Append(Esc(f.Name))
                      .Append("\",\"t\":\"").Append(TypeName(f.FieldType))
                      .Append("\",\"g\":\"").Append(group)
                      .Append("\",\"v\":").Append(ValJson(f.FieldType, v))
                      .Append('}');
                }
                t = t.BaseType;
            }
        }

        public static bool SetPacketConfigProp(string id, string field, string val)
        {
            var cfg = GetConfigObj(id);
            if (cfg == null) return false;
            var cc = GetCharacterConfig(cfg);
            if (TrySetField(cc, field, val)) return true;
            return TrySetField(cfg, field, val);
        }

        static bool TrySetField(object o, string field, string val)
        {
            if (o == null || string.IsNullOrEmpty(field)) return false;
            var t = o.GetType();
            while (IsGameType(t))
            {
                var f = t.GetField(field, Flags | BindingFlags.DeclaredOnly);
                if (f != null)
                {
                    try { f.SetValue(o, ParseVal(f.FieldType, val)); return true; }
                    catch { return false; }
                }
                t = t.BaseType;
            }
            return false;
        }

        static Type FindTypeInGame(string type)
        {
            if (string.IsNullOrEmpty(type)) return null;
            try
            {
                string tail = type;
                if (tail.StartsWith("Plant", StringComparison.Ordinal)) tail = tail.Substring(5);
                else if (tail.StartsWith("Zombie", StringComparison.Ordinal)) tail = tail.Substring(6);
                string[] cands = { "TowerDefense" + type, "TowerDefensePlant" + tail, "TowerDefenseZombie" + tail, type };
                // ① 按候选名在所有程序集 GetType（不限定程序集名，避免主程序集名不一致时找不到）
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    foreach (var c in cands)
                    {
                        try { var mt = asm.GetType(c); if (mt != null) return mt; } catch { }
                    }
                }
                // ② 兜底：遍历全部程序集的类型，按名称包含匹配
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = asm.GetTypes(); } catch { continue; }
                    foreach (var t in types)
                        if (t.Name.IndexOf(type, StringComparison.OrdinalIgnoreCase) >= 0)
                            return t;
                }
            }
            catch { }
            return null;
        }
    }
}
