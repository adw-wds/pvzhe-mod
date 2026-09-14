using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;

namespace PvzheMod;

/// <summary>
/// 植物被动能力（子项目4 Task1 5 种 + SP6 扩充 4 种）：sun/shield/slowaura/coin/buffaura/reflect/lifesteal/crit/rangebonus。
/// 全部 _Process 驱动 + 节流降频 + try/catch 吞异常，AOT 安全（禁 System.IO/STJ/PropertyInfo.SetValue）。
///
/// 运行期验证点（0.27 字段名未完全确认，以 Bootstrap.Log 为准）：
///  ① GameCheats.CreateCurrency 仅支持 Silver/Gold/LuckyBag/Diamond——"Coin" 当前映射 COIN_DIAMOND（产金币被动需注意）；
///     产阳光 CreateCurrency 不支持 → 反射 TowerDefenseManager.SunCreate(Vector2, long, ...) 掉可见阳光，失败兜底 GameCheats.AddSun(long)。
///  ② 当前关卡判定：TowerDefenseManager.CurrentControl（0.27 public static 属性）或 currentControl 实例字段非空。
///  ③ 减速/增益光环：场景树递归 GetCamp 遍历 + 反射 moveSpeed/speed 与 fireInterval（FireComponent float / 植物节点 double 属性）。
///  ④ 僵尸移动速度字段：僵尸本体 / groundMoveComponent / 类型名含 Move 的子节点上的 moveSpeed/speed/_moveSpeed。
///  SP6 验证点：reflect 探测植物 hp 下降→TryHurt 最近敌人；lifesteal/crit 探测 FireComponent.timer 回落；
///  rangebonus 探测植物 range/attackRange/fireRange 字段——机制未命中仅日志，需游戏内实测确认字段名。
/// </summary>
public static class PassiveKind
{
    public const string None = "none";
    public const string Sun = "sun";
    public const string Shield = "shield";
    public const string SlowAura = "slowaura";
    public const string Coin = "coin";
    public const string BuffAura = "buffaura";
    // SP6: 反伤/吸血/暴击/射程加成（全部探测式，机制未命中仅日志）
    public const string Reflect = "reflect";
    public const string Lifesteal = "lifesteal";
    public const string Crit = "crit";
    public const string RangeBonus = "rangebonus";
}

/// <summary>植物被动定义（config.json 的 passive 子对象）。</summary>
public sealed class PassiveDef
{
    public string Kind = "none";
    public double Value = 0;
}

/// <summary>
/// 共享反射/场景辅助（PassiveControllers 与 ZombieBehaviorControllers 共用）：
/// 探测式 + 吞异常，命中即生效未命中仅返回失败。
/// </summary>
public static class CharReflect
{
    static readonly Dictionary<ulong, double> _moveBase = new();
    static readonly Dictionary<ulong, double> _fireBase = new();

    // ---------- 类型/实例 ----------

    /// <summary>按名字找游戏类型（优先主程序集）。</summary>
    public static Type FindType(string name)
    {
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (asm.GetName().Name != "PlantsVsZombies") continue;
                    var t = asm.GetType(name);
                    if (t != null) return t;
                }
                catch { }
            }
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType(name);
                    if (t != null) return t;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    /// <summary>取 TowerDefenseManager.Instance（public static 属性优先，兜底字段）。</summary>
    public static object FindTdm()
    {
        try
        {
            var t = FindType("TowerDefenseManager");
            if (t == null) return null;
            var prop = t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            if (prop != null)
            {
                try { var v = prop.GetValue(null); if (v != null) return v; } catch { }
            }
            var fld = t.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
            if (fld != null)
            {
                var v = fld.GetValue(null);
                if (v != null) return v;
            }
        }
        catch { }
        return null;
    }

    /// <summary>是否处于真实关卡内：TDM 实例存在且 currentControl 非空（参照 GameCheats.CreateCurrency 的检查）。</summary>
    public static bool InLevel()
    {
        try
        {
            var tdm = FindTdm();
            if (tdm == null) return false;
            var t = tdm.GetType();
            var cp = t.GetProperty("CurrentControl", BindingFlags.Public | BindingFlags.Static);
            object cc = null;
            if (cp != null) { try { cc = cp.GetValue(null); } catch { } }
            if (cc == null)
            {
                var f = t.GetField("currentControl", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null) cc = f.GetValue(tdm);
            }
            return cc != null;
        }
        catch { return false; }
    }

    // ---------- 数值字段（探测式，禁 PropertyInfo.SetValue） ----------

    static bool IsNum(Type t) => t == typeof(double) || t == typeof(float) || t == typeof(int);

    static FieldInfo FindNumField(object obj, string field)
    {
        try
        {
            var t = obj.GetType();
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var n in new[] { field, "_" + field })
            {
                var f = t.GetField(n, flags);
                if (f != null && IsNum(f.FieldType)) return f;
            }
        }
        catch { }
        return null;
    }

    public static bool SetNumField(object obj, string field, double val)
    {
        var f = FindNumField(obj, field);
        if (f == null) return false;
        try
        {
            if (f.FieldType == typeof(double)) { f.SetValue(obj, val); return true; }
            if (f.FieldType == typeof(float)) { f.SetValue(obj, (float)val); return true; }
            if (f.FieldType == typeof(int)) { f.SetValue(obj, (int)val); return true; }
        }
        catch { }
        return false;
    }

    public static double? GetNumField(object obj, string field)
    {
        var f = FindNumField(obj, field);
        if (f == null) return null;
        try
        {
            var v = f.GetValue(obj);
            if (v is double d) return d;
            if (v is float fl) return fl;
            if (v is int i) return i;
        }
        catch { }
        return null;
    }

    // ---------- 场景树遍历（参照 GameCheats CollectZombies 思路，GetCamp 递归） ----------

    /// <summary>从场景树根递归收集 center 半径 radius 内 camp 阵营的角色节点（含 internal 子节点）。</summary>
    public static void WalkCollect(Node2D center, double radius, string camp, List<Node2D> list)
    {
        try
        {
            if (center == null || !GodotObject.IsInstanceValid(center)) return;
            var tree = center.GetTree();
            var root = tree != null ? tree.Root : null;
            if (root == null) return;
            double rsq = radius * radius;
            WalkNode(root, center, rsq, camp, list);
        }
        catch { }
    }

    static void WalkNode(Node node, Node2D center, double rsq, string camp, List<Node2D> list)
    {
        try
        {
            foreach (var child in node.GetChildren(true))
            {
                if (child is Node2D n2 && GodotObject.IsInstanceValid(n2))
                {
                    try
                    {
                        string c = ESP.GetCamp(n2);
                        if (c != null && c.Equals(camp, StringComparison.OrdinalIgnoreCase))
                        {
                            var dv = n2.GlobalPosition - center.GlobalPosition;
                            if (dv.X * dv.X + dv.Y * dv.Y <= rsq) list.Add(n2);
                        }
                    }
                    catch { }
                }
                WalkNode(child, center, rsq, camp, list);
            }
        }
        catch { }
    }

    /// <summary>找 center 半径 radius 内最近的 camp 阵营角色；无则返回 null。</summary>
    public static Node2D FindNearest(Node2D center, string camp, double radius)
    {
        var list = new List<Node2D>();
        WalkCollect(center, radius, camp, list);
        Node2D best = null;
        double bestD = double.MaxValue;
        foreach (var n in list)
        {
            if (n == null || !GodotObject.IsInstanceValid(n)) continue;
            double d = (n.GlobalPosition - center.GlobalPosition).LengthSquared();
            if (d < bestD) { bestD = d; best = n; }
        }
        return best;
    }

    // ---------- 移速（减速光环 / 护盾反减速 / 僵尸冲刺共用） ----------

    /// <summary>把角色移速设为"初始值 × scale"（首见缓存初始值，之后按初始×scale 复写防游戏重置）。探测式。</summary>
    public static void SetMoveSpeedScale(Node2D z, double scale)
    {
        try
        {
            if (z == null || !GodotObject.IsInstanceValid(z)) return;
            ulong id = z.GetInstanceId();
            object owner = FindMoveSpeedOwner(z);
            if (owner == null) return;
            var f = FindMoveSpeedField(owner);
            if (f == null) return;
            double? cur = ReadNum(f, owner);
            if (!cur.HasValue) return;
            double baseV;
            if (!_moveBase.TryGetValue(id, out baseV)) { baseV = cur.Value; _moveBase[id] = baseV; }
            WriteNum(f, owner, baseV * scale);
        }
        catch { }
    }

    static object FindMoveSpeedOwner(Node2D z)
    {
        // ① 僵尸本体
        if (FindMoveSpeedField(z) != null) return z;
        // ② groundMoveComponent 字段
        var gmc = GetFieldVal(z, "groundMoveComponent");
        if (gmc != null && FindMoveSpeedField(gmc) != null) return gmc;
        // ③ 子节点中类型名含 Move 的组件
        var childComp = FindMoveCompChild(z);
        if (childComp != null && FindMoveSpeedField(childComp) != null) return childComp;
        return null;
    }

    static object FindMoveCompChild(Node node)
    {
        foreach (var child in node.GetChildren(true))
        {
            if (child == null || !GodotObject.IsInstanceValid(child)) continue;
            if (child.GetType().Name.IndexOf("Move", StringComparison.Ordinal) >= 0)
            {
                if (FindMoveSpeedField(child) != null) return child;
            }
            var deeper = FindMoveCompChild(child);
            if (deeper != null) return deeper;
        }
        return null;
    }

    /// <summary>沿类型基类链找数值字段 moveSpeed/_moveSpeed（优先），兜底 speed/_speed。探测式。</summary>
    static FieldInfo FindMoveSpeedField(object obj)
    {
        try
        {
            foreach (var primary in new[] { true, false })
            {
                var t = obj.GetType();
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                while (t != null)
                {
                    foreach (var f in t.GetFields(flags | BindingFlags.DeclaredOnly))
                    {
                        if (!IsNum(f.FieldType)) continue;
                        bool isMove = f.Name == "moveSpeed" || f.Name == "_moveSpeed" || f.Name == "moveSpeedBase";
                        bool isSpeed = f.Name == "speed" || f.Name == "_speed";
                        if (primary ? isMove : isSpeed) return f;
                    }
                    t = t.BaseType;
                }
            }
        }
        catch { }
        return null;
    }

    // ---------- 攻速（增益光环 / 僵尸攻速 buff 共用） ----------

    /// <summary>把植物攻速设为"初始值 × scale"（fireInterval：FireComponent float 字段 + 植物节点 double 属性双写）。探测式。</summary>
    public static void SetFireIntervalScale(Node2D n, double scale)
    {
        try
        {
            if (n == null || !GodotObject.IsInstanceValid(n) || scale <= 0) return;
            ulong id = n.GetInstanceId();
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var comp = FindNodeByType(n, "FireComponent");
            FieldInfo fi = null;
            if (comp != null) fi = comp.GetType().GetField("fireInterval", flags);
            var nfp = n.GetType().GetProperty("fireInterval", flags);
            bool hasF = fi != null && fi.FieldType == typeof(float);
            bool hasD = nfp != null && nfp.PropertyType == typeof(double);
            if (!hasF && !hasD) return;
            double baseV;
            if (!_fireBase.TryGetValue(id, out baseV))
            {
                baseV = hasF ? (double)(float)fi.GetValue(comp) : (double)nfp.GetValue(n);
                if (baseV <= 0) baseV = 1.5;
                _fireBase[id] = baseV;
            }
            double target = baseV * scale;
            if (hasF) fi.SetValue(comp, (float)target);
            if (hasD)
            {
                var st = nfp.GetSetMethod(true);
                if (st != null) st.Invoke(n, new object[] { target });
            }
        }
        catch { }
    }

    static Node FindNodeByType(Node node, string typeName)
    {
        foreach (var child in node.GetChildren(true))
        {
            if (child == null || !GodotObject.IsInstanceValid(child)) continue;
            if (child.GetType().Name.IndexOf(typeName, StringComparison.Ordinal) >= 0) return child;
            var deeper = FindNodeByType(child, typeName);
            if (deeper != null) return deeper;
        }
        return null;
    }

    // ---------- 血量（反伤 / 吸血 / 治疗共用） ----------

    /// <summary>读角色当前 hp：本体 hp 字段优先，兜底 characterConfig 层。失败返回 null。</summary>
    public static double? GetHp(object obj)
    {
        try
        {
            var hp = GetNumField(obj, "hp");
            if (hp.HasValue) return hp;
            var cc = GetFieldVal(obj, "characterConfig");
            if (cc != null)
            {
                hp = GetNumField(cc, "hp");
                if (hp.HasValue) return hp;
            }
        }
        catch { }
        return null;
    }

    /// <summary>写角色 hp（本体优先，兜底 characterConfig 层）。命中返回 true。</summary>
    public static bool SetHp(object obj, double val)
    {
        try
        {
            if (SetNumField(obj, "hp", val)) return true;
            var cc = GetFieldVal(obj, "characterConfig");
            if (cc != null) return SetNumField(cc, "hp", val);
        }
        catch { }
        return false;
    }

    // ---------- 伤害（反伤 / 暴击共用） ----------

    /// <summary>对角色造成伤害：优先 ExplodeHurt(double,...)，兜底 Hurt(double,...)。探测式。</summary>
    public static bool TryHurt(Node2D n, double dmg)
    {
        try
        {
            var em = FindMethodFirstParam(n.GetType(), "ExplodeHurt", typeof(double));
            if (em != null)
            {
                var ps = em.GetParameters();
                var args = new object[ps.Length];
                args[0] = dmg;
                for (int i = 1; i < ps.Length; i++) args[i] = Type.Missing;
                em.Invoke(n, args);
                return true;
            }
            var hm = FindMethodFirstParam(n.GetType(), "Hurt", typeof(double));
            if (hm != null)
            {
                var ps = hm.GetParameters();
                var args = new object[ps.Length];
                args[0] = dmg;
                for (int i = 1; i < ps.Length; i++) args[i] = Type.Missing;
                hm.Invoke(n, args);
                return true;
            }
        }
        catch { }
        return false;
    }

    static MethodInfo FindMethodFirstParam(Type t, string name, Type first)
    {
        try
        {
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (m.Name != name) continue;
                var ps = m.GetParameters();
                if (ps.Length >= 1 && ps[0].ParameterType == first) return m;
            }
        }
        catch { }
        return null;
    }

    // ---------- 射程（射程加成光环） ----------

    static readonly Dictionary<ulong, double> _rangeBase = new();

    /// <summary>把植物射程设为"初始值 × scale"（range/attackRange/fireRange 数值字段；首见缓存初始值复写防游戏重置）。探测式。</summary>
    public static void SetRangeScale(Node2D n, double scale)
    {
        try
        {
            if (n == null || !GodotObject.IsInstanceValid(n) || scale <= 0) return;
            ulong id = n.GetInstanceId();
            var f = FindRangeField(n);
            if (f == null) return;
            double? cur = ReadNum(f, n);
            if (!cur.HasValue) return;
            double baseV;
            if (!_rangeBase.TryGetValue(id, out baseV)) { baseV = cur.Value; _rangeBase[id] = baseV; }
            WriteNum(f, n, baseV * scale);
        }
        catch { }
    }

    /// <summary>沿类型基类链找数值射程字段（range/_range/attackRange/fireRange）。探测式。</summary>
    static FieldInfo FindRangeField(object obj)
    {
        try
        {
            var t = obj.GetType();
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            while (t != null)
            {
                foreach (var f in t.GetFields(flags | BindingFlags.DeclaredOnly))
                {
                    if (!IsNum(f.FieldType)) continue;
                    if (f.Name == "range" || f.Name == "_range" || f.Name == "attackRange"
                        || f.Name == "_attackRange" || f.Name == "fireRange" || f.Name == "_fireRange")
                        return f;
                }
                t = t.BaseType;
            }
        }
        catch { }
        return null;
    }

    // ---------- 通用小工具 ----------

    static object GetFieldVal(object obj, string name)
    {
        try
        {
            var t = obj.GetType();
            var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) return f.GetValue(obj);
        }
        catch { }
        return null;
    }

    static double? ReadNum(FieldInfo f, object obj)
    {
        try
        {
            var v = f.GetValue(obj);
            if (v is float fv) return fv;
            if (v is double dv) return dv;
            if (v is int iv) return iv;
        }
        catch { }
        return null;
    }

    static void WriteNum(FieldInfo f, object obj, double val)
    {
        try
        {
            if (f.FieldType == typeof(float)) f.SetValue(obj, (float)val);
            else if (f.FieldType == typeof(double)) f.SetValue(obj, val);
            else if (f.FieldType == typeof(int)) f.SetValue(obj, (int)val);
        }
        catch { }
    }
}

/// <summary>挂载入口：按 def.Passive.Kind 挂对应被动控制器（GetNodeOrNull 防重，无匹配返回）。</summary>
public static class PassiveControllers
{
    public static void AttachPlant(Node2D node, CustomPlantDef def)
    {
        try
        {
            if (node == null || !GodotObject.IsInstanceValid(node) || def == null) return;
            var pd = def.Passive;
            if (pd == null) return;
            string kind = string.IsNullOrEmpty(pd.Kind) ? "none" : pd.Kind;
            switch (kind)
            {
                case PassiveKind.Sun:
                    if (node.GetNodeOrNull("SunProduceController") != null) return;
                    var sun = new SunProduceController { Interval = pd.Value > 0 ? pd.Value : 10.0 };
                    node.AddChild(sun); sun.Name = "SunProduceController";
                    break;
                case PassiveKind.Coin:
                    if (node.GetNodeOrNull("CoinProduceController") != null) return;
                    var coin = new CoinProduceController { Interval = pd.Value > 0 ? pd.Value : 10.0 };
                    node.AddChild(coin); coin.Name = "CoinProduceController";
                    break;
                case PassiveKind.SlowAura:
                    if (node.GetNodeOrNull("SlowAuraController") != null) return;
                    var slow = new SlowAuraController { Radius = pd.Value > 0 ? pd.Value : 300.0 };
                    node.AddChild(slow); slow.Name = "SlowAuraController";
                    break;
                case PassiveKind.Shield:
                    if (node.GetNodeOrNull("ShieldController") != null) return;
                    var shield = new ShieldController { Radius = pd.Value > 0 ? pd.Value : 300.0 };
                    node.AddChild(shield); shield.Name = "ShieldController";
                    break;
                case PassiveKind.BuffAura:
                    if (node.GetNodeOrNull("BuffAuraController") != null) return;
                    var buff = new BuffAuraController { Radius = pd.Value > 0 ? pd.Value : 300.0 };
                    node.AddChild(buff); buff.Name = "BuffAuraController";
                    break;
                case PassiveKind.Reflect:
                    if (node.GetNodeOrNull("ReflectController") != null) return;
                    var reflect = new ReflectController { Radius = pd.Value > 0 ? pd.Value : 300.0, Value = pd.Value > 0 ? pd.Value : 1.0 };
                    node.AddChild(reflect); reflect.Name = "ReflectController";
                    break;
                case PassiveKind.Lifesteal:
                    if (node.GetNodeOrNull("LifestealController") != null) return;
                    var ls = new LifestealController { Value = pd.Value > 0 ? pd.Value : 20.0 };
                    node.AddChild(ls); ls.Name = "LifestealController";
                    break;
                case PassiveKind.Crit:
                    if (node.GetNodeOrNull("CritController") != null) return;
                    var crit = new CritController
                    {
                        Chance = pd.Value > 0 ? pd.Value : 0.2,
                        Damage = def.Attack != null && def.Attack.Damage > 0 ? def.Attack.Damage : 40,
                    };
                    node.AddChild(crit); crit.Name = "CritController";
                    break;
                case PassiveKind.RangeBonus:
                    if (node.GetNodeOrNull("RangeBonusController") != null) return;
                    var rb = new RangeBonusController { Radius = pd.Value > 0 ? pd.Value : 300.0 };
                    node.AddChild(rb); rb.Name = "RangeBonusController";
                    break;
                default:
                    return;   // none / 未知 → 不挂载
            }
        }
        catch (Exception ex) { Bootstrap.Log("被动挂载异常: " + ex.Message); }
    }
}

/// <summary>产阳光被动：每 Interval 秒产 25 阳光。优先反射 TowerDefenseManager.SunCreate 掉可见阳光，
/// 失败兜底 GameCheats.AddSun(25) 直接加计数。需当前关卡内（currentControl 非空）。</summary>
public sealed class SunProduceController : Node
{
    public double Interval = 0;
    double _timer;
    static bool _logged;
    static MethodInfo _sunCreate;

    public override void _Process(double delta)
    {
        try
        {
            if (Interval <= 0) return;
            _timer -= delta;
            if (_timer > 0) return;
            _timer = Interval;
            Produce();
        }
        catch (Exception ex) { Bootstrap.Log("产阳光被动异常: " + ex.Message); }
    }

    void Produce()
    {
        var parent = GetParent() as Node2D;
        if (parent == null || !GodotObject.IsInstanceValid(parent)) return;
        if (!CharReflect.InLevel()) return;
        const long amount = 25;
        bool visual = TrySunCreate(parent, amount);
        if (!visual)
        {
            long n = GameCheats.AddSun(amount);
            if (!_logged)
            {
                _logged = true;
                Bootstrap.Log("产阳光被动: SunCreate 未命中，兜底 AddSun(25)=" + (n >= 0 ? n.ToString() : "失败"));
            }
            return;
        }
        if (!_logged)
        {
            _logged = true;
            Bootstrap.Log("产阳光被动: SunCreate 命中（落阳光" + amount + "，收集后入账）");
        }
    }

    /// <summary>反射 TowerDefenseManager.SunCreate(Vector2 pos, long sunNum, ...) 掉一颗可见阳光到植物处。</summary>
    static bool TrySunCreate(Node2D plant, long amount)
    {
        try
        {
            var tdm = CharReflect.FindTdm();
            if (tdm == null) return false;
            if (_sunCreate == null)
                _sunCreate = FindSunCreateMethod(tdm.GetType());
            if (_sunCreate == null) return false;
            var ps = _sunCreate.GetParameters();
            var args = new object[ps.Length];
            args[0] = plant.GlobalPosition;
            args[1] = amount;
            for (int i = 2; i < ps.Length; i++) args[i] = Type.Missing;   // 可选参数用默认值
            var ret = _sunCreate.Invoke(tdm, args);
            return ret != null;
        }
        catch { return false; }
    }

    static MethodInfo FindSunCreateMethod(Type t)
    {
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (m.Name != "SunCreate") continue;
            var ps = m.GetParameters();
            if (ps.Length >= 2 && ps[0].ParameterType == typeof(Vector2) && ps[1].ParameterType == typeof(long)) return m;
        }
        return null;
    }
}

/// <summary>产金币被动：每 Interval 秒 GameCheats.CreateCurrency("Coin", (int)Interval)。
/// 注意：CreateCurrency 仅 Silver/Gold/LuckyBag/Diamond——"Coin" 当前映射 COIN_DIAMOND（钻石），
/// 若要产普通金币应改传 "Gold"（运行期验证点，日志已标注）。</summary>
public sealed class CoinProduceController : Node
{
    public double Interval = 0;
    double _timer;
    static bool _logged;

    public override void _Process(double delta)
    {
        try
        {
            if (Interval <= 0) return;
            _timer -= delta;
            if (_timer > 0) return;
            _timer = Interval;
            Produce();
        }
        catch (Exception ex) { Bootstrap.Log("产金币被动异常: " + ex.Message); }
    }

    void Produce()
    {
        if (!CharReflect.InLevel()) return;
        int amount = (int)Interval;
        if (amount <= 0) amount = 1;
        GameCheats.CreateCurrency("Coin", amount);
        if (!_logged)
        {
            _logged = true;
            Bootstrap.Log("产金币被动: CreateCurrency(\"Coin\"," + amount + ")（注意 \"Coin\" 当前按钻石处理，需金币可改传 \"Gold\"）");
        }
    }
}

/// <summary>减速光环被动：每 0.3s 收集半径内僵尸 → moveSpeed×0.5（首见缓存初始值，按初始×0.5 复写）。</summary>
public class SlowAuraController : Node
{
    public double Radius = 300;
    double _tick;
    static bool _logged;

    public override void _Process(double delta)
    {
        try
        {
            if (Radius <= 0) return;
            _tick -= delta;
            if (_tick > 0) return;
            _tick = 0.3;
            var parent = GetParent() as Node2D;
            if (parent == null || !GodotObject.IsInstanceValid(parent)) return;
            var list = new List<Node2D>();
            CharReflect.WalkCollect(parent, Radius, "Zombie", list);
            foreach (var z in list)
                if (z != null && GodotObject.IsInstanceValid(z))
                    CharReflect.SetMoveSpeedScale(z, 0.5);
            if (!_logged && list.Count > 0)
            {
                _logged = true;
                Bootstrap.Log("减速光环: 半径 " + Radius + " 检测到 " + list.Count + " 僵尸 → moveSpeed×0.5");
            }
        }
        catch (Exception ex) { Bootstrap.Log("减速光环异常: " + ex.Message); }
    }
}

/// <summary>护盾被动：MVP = 高血量（Load 已设 hp）+ 反减速光环（复用 SlowAura 逻辑，靠近僵尸减速）。</summary>
public sealed class ShieldController : SlowAuraController
{
}

/// <summary>增益光环被动：每 0.3s 收集半径内植物 → fireInterval×0.7（攻速增益，首见缓存初始值复写）。</summary>
public sealed class BuffAuraController : Node
{
    public double Radius = 300;
    double _tick;
    static bool _logged;

    public override void _Process(double delta)
    {
        try
        {
            if (Radius <= 0) return;
            _tick -= delta;
            if (_tick > 0) return;
            _tick = 0.3;
            var parent = GetParent() as Node2D;
            if (parent == null || !GodotObject.IsInstanceValid(parent)) return;
            var list = new List<Node2D>();
            CharReflect.WalkCollect(parent, Radius, "Plant", list);
            foreach (var p in list)
                if (p != null && GodotObject.IsInstanceValid(p))
                    CharReflect.SetFireIntervalScale(p, 0.7);
            if (!_logged && list.Count > 0)
            {
                _logged = true;
                Bootstrap.Log("增益光环: 半径 " + Radius + " 检测到 " + list.Count + " 植物 → fireInterval×0.7");
            }
        }
        catch (Exception ex) { Bootstrap.Log("增益光环异常: " + ex.Message); }
    }
}

/// <summary>反伤被动（SP6）：探测植物 hp 下降（受击）→ 对半径内最近敌人反伤（伤害=掉血量×Value）。探测式：
/// 植物无 hp 字段 / 无 Hurt/ExplodeHurt 方法 / 无敌人 → 仅日志不抛。</summary>
public sealed class ReflectController : Node
{
    public double Radius = 300;
    public double Value = 1.0;   // 反伤倍率（掉血量×Value）
    double _tick;
    double? _lastHp;
    static bool _logged;

    public override void _Process(double delta)
    {
        try
        {
            var plant = GetParent() as Node2D;
            if (plant == null || !GodotObject.IsInstanceValid(plant)) return;
            _tick -= delta;
            if (_tick > 0) return;
            _tick = 0.15;
            var hp = CharReflect.GetHp(plant);
            if (!hp.HasValue)
            {
                if (!_logged) { _logged = true; Bootstrap.Log("反伤被动: 植物无 hp 字段（机制未命中，仅日志）"); }
                return;
            }
            if (_lastHp.HasValue && hp.Value < _lastHp.Value - 0.5)
            {
                double taken = _lastHp.Value - hp.Value;
                var enemy = CharReflect.FindNearest(plant, "Zombie", Radius);
                if (enemy != null)
                {
                    double dmg = taken * (Value > 0 ? Value : 1.0);
                    if (CharReflect.TryHurt(enemy, dmg))
                    {
                        if (!_logged) { _logged = true; Bootstrap.Log("反伤被动: 受击反伤 " + dmg + " → 最近敌人"); }
                    }
                }
            }
            _lastHp = hp.Value;
        }
        catch (Exception ex) { Bootstrap.Log("反伤被动异常: " + ex.Message); }
    }
}

/// <summary>吸血被动（SP6）：检测植物攻击（FireComponent.timer 回落，复用 AttackFxController.DetectFire 思路）
/// → 回复自身 hp（上限 maxHp）。边沿检测防同一次攻击重复回血。探测式：无 hp/无 FireComponent → 仅日志。</summary>
public sealed class LifestealController : Node
{
    public double Value = 20;   // 每次攻击回复量
    double _tick;
    bool _lastFired;
    static bool _logged;
    static bool _hpMissLogged;

    public override void _Process(double delta)
    {
        try
        {
            var plant = GetParent() as Node2D;
            if (plant == null || !GodotObject.IsInstanceValid(plant)) return;
            _tick -= delta;
            if (_tick > 0) return;
            _tick = 0.05;
            bool fired = DetectFire(plant);
            if (fired && !_lastFired)
            {
                _lastFired = true;
                var hp = CharReflect.GetHp(plant);
                if (!hp.HasValue)
                {
                    if (!_hpMissLogged) { _hpMissLogged = true; Bootstrap.Log("吸血被动: 植物无 hp 字段（机制未命中，仅日志）"); }
                    return;
                }
                var maxHp = CharReflect.GetNumField(plant, "maxHp");
                double cap = maxHp.HasValue && maxHp.Value > 0 ? maxHp.Value : hp.Value + Value;
                CharReflect.SetHp(plant, Math.Min(hp.Value + Value, cap));
                if (!_logged) { _logged = true; Bootstrap.Log("吸血被动: 攻击回复 +" + Value); }
            }
            else if (!fired) _lastFired = false;
        }
        catch (Exception ex) { Bootstrap.Log("吸血被动异常: " + ex.Message); }
    }

    /// <summary>探测攻击：子节点类型名含 FireComponent 且 timer≤0.01（与 CustomPlantManager.AttackFxController 同思路）。</summary>
    static bool DetectFire(Node2D plant)
    {
        try
        {
            foreach (var child in plant.GetChildren())
            {
                var t = child.GetType();
                if (!t.Name.Contains("FireComponent")) continue;
                var f = t.GetField("timer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f == null) continue;
                var v = f.GetValue(child);
                double tv = v is float fv ? fv : (v is double dv ? dv : 0);
                if (tv <= 0.01) return true;
            }
        }
        catch { }
        return false;
    }
}

/// <summary>暴击被动（SP6）：检测植物攻击（FireComponent 回落）→ 概率 Chance 触发，对半径内最近敌人追加一次伤害
/// （伤害=植物攻击 damage，等效 2 倍）。探测式：无 FireComponent / 无 Hurt 方法 / 无敌人 → 仅日志。</summary>
public sealed class CritController : Node
{
    public double Chance = 0.2;    // 暴击概率 0..1
    public double Damage = 40;     // 暴击追加伤害（植物攻击伤害）
    public double Radius = 700;
    double _tick;
    bool _lastFired;
    static bool _logged;
    static readonly Random _rng = new();

    public override void _Process(double delta)
    {
        try
        {
            var plant = GetParent() as Node2D;
            if (plant == null || !GodotObject.IsInstanceValid(plant)) return;
            _tick -= delta;
            if (_tick > 0) return;
            _tick = 0.05;
            bool fired = DetectFire(plant);
            if (fired && !_lastFired)
            {
                _lastFired = true;
                double chance = Chance > 0 ? Math.Min(Chance, 1.0) : 0.0;
                if (_rng.NextDouble() < chance)
                {
                    var enemy = CharReflect.FindNearest(plant, "Zombie", Radius);
                    if (enemy != null && CharReflect.TryHurt(enemy, Damage))
                    {
                        if (!_logged) { _logged = true; Bootstrap.Log("暴击被动: 触发暴击 追加伤害 +" + Damage + " → 最近敌人"); }
                    }
                }
            }
            else if (!fired) _lastFired = false;
        }
        catch (Exception ex) { Bootstrap.Log("暴击被动异常: " + ex.Message); }
    }

    /// <summary>探测攻击：子节点类型名含 FireComponent 且 timer≤0.01。</summary>
    static bool DetectFire(Node2D plant)
    {
        try
        {
            foreach (var child in plant.GetChildren())
            {
                var t = child.GetType();
                if (!t.Name.Contains("FireComponent")) continue;
                var f = t.GetField("timer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f == null) continue;
                var v = f.GetValue(child);
                double tv = v is float fv ? fv : (v is double dv ? dv : 0);
                if (tv <= 0.01) return true;
            }
        }
        catch { }
        return false;
    }
}

/// <summary>射程加成被动（SP6，光环）：每 0.3s 收集半径内植物 → range×1.3（复用 buffaura 遍历，首见缓存初始值复写）。
/// 探测式：植物无 range 字段 → 仅日志。</summary>
public sealed class RangeBonusController : Node
{
    public double Radius = 300;
    double _tick;
    static bool _logged;

    public override void _Process(double delta)
    {
        try
        {
            if (Radius <= 0) return;
            _tick -= delta;
            if (_tick > 0) return;
            _tick = 0.3;
            var parent = GetParent() as Node2D;
            if (parent == null || !GodotObject.IsInstanceValid(parent)) return;
            var list = new List<Node2D>();
            CharReflect.WalkCollect(parent, Radius, "Plant", list);
            foreach (var p in list)
                if (p != null && GodotObject.IsInstanceValid(p))
                    CharReflect.SetRangeScale(p, 1.3);
            if (!_logged && list.Count > 0)
            {
                _logged = true;
                Bootstrap.Log("射程加成光环: 半径 " + Radius + " 检测到 " + list.Count + " 植物 → range×1.3");
            }
        }
        catch (Exception ex) { Bootstrap.Log("射程加成光环异常: " + ex.Message); }
    }
}
