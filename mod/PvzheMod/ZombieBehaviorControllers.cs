using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;

namespace PvzheMod;

/// <summary>
/// 僵尸行为自定义（子项目4 Task2）：移动/攻击/特殊技能 3 组控制器 + 挂载入口。
/// 全部 _Process 驱动 + 节流降频 + 反射 + try/catch，AOT 安全（禁 System.IO/STJ/PropertyInfo.SetValue）。
///
/// 运行期验证点（0.27 字段/方法名未完全确认，以 Bootstrap.Log 为准）：
///  ① 移动组件字段：僵尸本体 / groundMoveComponent / 类型名含 Move 子节点上的 moveSpeed/speed（CharReflect.SetMoveSpeedScale）。
///  ② 远程子弹：TowerDefenseCharacterEventProjectileCreate.Run(TowerDefenseCharacter, TowerDefenseProjectileCreateData, ...)
///     static + TowerDefenseProjectileCreateData(StringName) 构造——子弹名需为已注册（游戏或 CustomBulletManager）的子弹 key。
///  ③ 自爆：僵尸实例 CreateEffect（特效）+ 对半径内植物 ExplodeHurt/Hurt（范围伤害）+ QueueFree（参照 GameCheats.TryJackboxBomb）。
///  ④ 召唤/分裂 id：GetPacketIds(false) 匹配 "Imp"（小鬼）/ "ZombieNormal"（小僵尸）；种位用角色 gridPos 字段。
///  ⑤ 死亡检测：僵尸 die 字段为 true 触发分裂。
/// </summary>
public sealed class ZombieBehaviorDef
{
    public string Move = "walk";          // walk/flight/burrow/dash
    public string Attack = "bite";        // bite/ranged/selfdestruct
    public string RangedBullet = "Pea";   // ranged 用的子弹名
    public string Special = "none";       // none/summon/buff/split/freezeaura/heal
    public double SpecialValue = 0;       // summon/buff 间隔秒数
}

/// <summary>挂载入口：按 def.Behavior 挂对应控制器（默认 walk/bite/none 不挂）。</summary>
public static class ZombieBehaviorControllers
{
    public static void AttachZombie(Node2D node, CustomZombieDef def)
    {
        try
        {
            if (node == null || !GodotObject.IsInstanceValid(node) || def == null) return;
            var bh = def.Behavior;
            if (bh == null) return;
            // 移动：flight/burrow/dash（walk 默认不挂）
            if (!string.IsNullOrEmpty(bh.Move) && bh.Move != "walk")
            {
                if (node.GetNodeOrNull("ZombieMoveController") == null)
                {
                    var c = new ZombieMoveController { Mode = bh.Move };
                    node.AddChild(c); c.Name = "ZombieMoveController";
                }
            }
            // 攻击：ranged/selfdestruct（bite 默认不挂）
            if (!string.IsNullOrEmpty(bh.Attack) && bh.Attack != "bite")
            {
                if (node.GetNodeOrNull("ZombieAttackController") == null)
                {
                    var c = new ZombieAttackController
                    {
                        Mode = bh.Attack,
                        Bullet = string.IsNullOrEmpty(bh.RangedBullet) ? "Pea" : bh.RangedBullet,
                        Damage = bh.SpecialValue > 0 ? bh.SpecialValue : 100,
                    };
                    node.AddChild(c); c.Name = "ZombieAttackController";
                }
            }
            // 特殊：summon/buff/split/freezeaura/heal（none 默认不挂）
            if (!string.IsNullOrEmpty(bh.Special) && bh.Special != "none")
            {
                if (node.GetNodeOrNull("ZombieSpecialController") == null)
                {
                    var c = new ZombieSpecialController { Mode = bh.Special, Value = bh.SpecialValue };
                    node.AddChild(c); c.Name = "ZombieSpecialController";
                }
            }
        }
        catch (Exception ex) { Bootstrap.Log("僵尸行为挂载异常: " + ex.Message); }
    }
}

/// <summary>僵尸移动控制器：flight 悬浮 / burrow 遁地 / dash 冲刺 / walk 默认。</summary>
public sealed class ZombieMoveController : Node
{
    public string Mode = "walk";
    double _tick;
    bool _init;
    float _baseY;
    double _time;
    static bool _logged;

    public override void _Process(double delta)
    {
        try
        {
            var zombie = GetParent() as Node2D;
            if (zombie == null || !GodotObject.IsInstanceValid(zombie)) return;
            if (Mode == "flight") Flight(zombie, delta);
            else if (Mode == "burrow") Burrow(zombie);
            else if (Mode == "dash") Dash(zombie, delta);
        }
        catch (Exception ex) { Bootstrap.Log("僵尸移动异常: " + ex.Message); }
    }

    /// <summary>飞行：悬浮在基线之上（不落地），正弦微浮。只覆写 Y，不干扰游戏 X 移动。</summary>
    void Flight(Node2D z, double delta)
    {
        if (!_init) { _baseY = z.Position.Y; _init = true; }
        _time += delta;
        double bob = Math.Sin(_time * 2.0) * 6.0;
        z.Position = new Vector2(z.Position.X, (float)(_baseY - 46.0 + bob));
        if (!_logged) { _logged = true; Bootstrap.Log("僵尸飞行: 悬浮 y=-46px（不落地）"); }
    }

    /// <summary>遁地：y 下移（地下）+ 半透明。</summary>
    void Burrow(Node2D z)
    {
        if (!_init) { _baseY = z.Position.Y; _init = true; }
        z.Position = new Vector2(z.Position.X, _baseY + 16f);
        z.Modulate = new Color(z.Modulate.R, z.Modulate.G, z.Modulate.B, 0.4f);
        if (!_logged) { _logged = true; Bootstrap.Log("僵尸遁地: y 下移 + 半透明"); }
    }

    /// <summary>冲刺：每 0.5s 复写 moveSpeed×2.5（防游戏重置）。</summary>
    void Dash(Node2D z, double delta)
    {
        if (!_init) { _init = true; _tick = 0.5; Bootstrap.Log("僵尸冲刺: moveSpeed×2.5"); }
        _tick -= delta;
        if (_tick > 0) return;
        _tick = 0.5;
        CharReflect.SetMoveSpeedScale(z, 2.5);
    }
}

/// <summary>僵尸攻击控制器：bite 默认 / ranged 远程射子弹 / selfdestruct 自爆。</summary>
public sealed class ZombieAttackController : Node
{
    public string Mode = "bite";
    public string Bullet = "Pea";
    public double Damage = 100;
    double _tick;
    bool _triggered;
    static bool _projLogged;
    static bool _projFailLogged;

    public override void _Process(double delta)
    {
        try
        {
            var zombie = GetParent() as Node2D;
            if (zombie == null || !GodotObject.IsInstanceValid(zombie)) return;
            if (Mode == "selfdestruct")
            {
                if (_triggered) return;
                var plant = CharReflect.FindNearest(zombie, "Plant", 70.0);
                if (plant != null) { _triggered = true; SelfDestruct(zombie); }
            }
            else if (Mode == "ranged")
            {
                _tick -= delta;
                if (_tick > 0) return;
                _tick = 2.0;
                var plant = CharReflect.FindNearest(zombie, "Plant", 700.0);
                if (plant != null) TryFireProjectile(zombie, Bullet);
            }
        }
        catch (Exception ex) { Bootstrap.Log("僵尸攻击异常: " + ex.Message); }
    }

    /// <summary>自爆：接近植物 → CreateEffect 特效 + 半径范围伤害 + QueueFree（参照 GameCheats.TryJackboxBomb）。</summary>
    void SelfDestruct(Node2D zombie)
    {
        try
        {
            var ce = zombie.GetType().GetMethod("CreateEffect", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (ce != null) { try { ce.Invoke(zombie, null); } catch { } }
            var list = new List<Node2D>();
            CharReflect.WalkCollect(zombie, 90.0, "Plant", list);
            int hurt = 0;
            foreach (var p in list)
                if (p != null && GodotObject.IsInstanceValid(p) && (Node2D)p != zombie)
                    if (TryExplodeHurt(p, Damage)) hurt++;
            Bootstrap.Log("僵尸自爆: 范围伤害 " + Damage + " 命中植物 " + hurt + "/" + list.Count + " CreateEffect=" + (ce != null));
            try { zombie.QueueFree(); } catch { }
        }
        catch (Exception ex) { Bootstrap.Log("僵尸自爆异常: " + ex.Message); }
    }

    /// <summary>范围伤害：优先 ExplodeHurt(double,...)，兜底 Hurt(double,...)。探测式。</summary>
    static bool TryExplodeHurt(Node2D p, double dmg)
    {
        try
        {
            var em = FindMethodFirstParam(p.GetType(), "ExplodeHurt", typeof(double));
            if (em != null)
            {
                var ps = em.GetParameters();
                var args = new object[ps.Length];
                args[0] = dmg;
                for (int i = 1; i < ps.Length; i++) args[i] = Type.Missing;
                em.Invoke(p, args);
                return true;
            }
            var hm = FindMethodFirstParam(p.GetType(), "Hurt", typeof(double));
            if (hm != null)
            {
                var ps = hm.GetParameters();
                var args = new object[ps.Length];
                args[0] = dmg;
                for (int i = 1; i < ps.Length; i++) args[i] = Type.Missing;
                hm.Invoke(p, args);
                return true;
            }
        }
        catch { }
        return false;
    }

    static MethodInfo FindMethodFirstParam(Type t, string name, Type first)
    {
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (m.Name != name) continue;
            var ps = m.GetParameters();
            if (ps.Length >= 1 && ps[0].ParameterType == first) return m;
        }
        return null;
    }

    /// <summary>远程：用游戏官方事件反射创建子弹（TowerDefenseCharacterEventProjectileCreate.Run static）。
    /// 子弹按名解析（游戏自带或 CustomBulletManager 已注册的自制子弹）。探测式，未命中仅日志。</summary>
    static bool TryFireProjectile(Node2D zombie, string bullet)
    {
        try
        {
            if (string.IsNullOrEmpty(bullet)) bullet = "Pea";
            var pdataType = CharReflect.FindType("TowerDefenseProjectileCreateData");
            if (pdataType == null) { LogProjFail("无 TowerDefenseProjectileCreateData 类型"); return false; }
            var ctor = pdataType.GetConstructor(new Type[] { typeof(StringName) });
            if (ctor == null) { LogProjFail("无 TowerDefenseProjectileCreateData(StringName) 构造"); return false; }
            object pdata;
            try { pdata = ctor.Invoke(new object[] { new StringName(bullet) }); }
            catch { LogProjFail("创建子弹数据失败 " + bullet); return false; }
            var evType = CharReflect.FindType("TowerDefenseCharacterEventProjectileCreate");
            if (evType == null) { LogProjFail("无 TowerDefenseCharacterEventProjectileCreate"); return false; }
            MethodInfo run = null;
            foreach (var m in evType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "Run") continue;
                var ps = m.GetParameters();
                if (ps.Length >= 2 && ps[0].ParameterType.Name == "TowerDefenseCharacter") { run = m; break; }
            }
            if (run == null) { LogProjFail("无 Run 静态方法"); return false; }
            object camp = ResolveCampZombie();
            var rp = run.GetParameters();
            var args = new object[rp.Length];
            args[0] = zombie;      // TowerDefenseCharacter target（子弹从僵尸位置出）
            args[1] = pdata;       // TowerDefenseProjectileCreateData
            args[2] = 1;           // createNum
            for (int i = 3; i < rp.Length; i++) args[i] = Type.Missing;
            if (camp != null && rp.Length >= 8) args[7] = camp;   // CHARACTER_CAMP.ZOMBIE：子弹属僵尸阵营打植物
            run.Invoke(null, args);
            if (!_projLogged)
            {
                _projLogged = true;
                Bootstrap.Log("僵尸远程攻击: 发射子弹 " + bullet + " camp=" + (camp != null ? "ZOMBIE" : "默认(ALL,有误伤风险)") + " Run=" + run);
            }
            return true;
        }
        catch (Exception ex) { LogProjFail(ex.Message); return false; }
    }

    static void LogProjFail(string why)
    {
        if (_projFailLogged) return;
        _projFailLogged = true;
        Bootstrap.Log("僵尸远程攻击: 子弹创建未命中 → " + why);
    }

    static object _campZombie;
    static object ResolveCampZombie()
    {
        if (_campZombie != null) return _campZombie;
        try
        {
            var et = CharReflect.FindType("TowerDefenseEnum");
            if (et == null) return null;
            var nested = et.GetNestedType("CHARACTER_CAMP");
            if (nested == null) return null;
            _campZombie = Enum.Parse(nested, "ZOMBIE");
        }
        catch { _campZombie = null; }
        return _campZombie;
    }
}

/// <summary>僵尸特殊技能控制器：summon 召唤小鬼 / buff 增益周围僵尸 / split 死后分裂。</summary>
public sealed class ZombieSpecialController : Node
{
    public string Mode = "none";
    public double Value = 0;
    double _tick;
    double _splitTick;
    double _auraTick;
    bool _splitDone;
    string _summonId;
    static bool _summonLogged, _splitLogged, _buffLogged, _freezeLogged, _healLogged;
    static readonly Dictionary<ulong, double> _atkBase = new();
    static readonly HashSet<ulong> _hpBuffed = new();

    public override void _Process(double delta)
    {
        try
        {
            var zombie = GetParent() as Node2D;
            if (zombie == null || !GodotObject.IsInstanceValid(zombie)) return;
            if (Mode == "summon" || Mode == "buff") Tick(delta);
            else if (Mode == "split") CheckSplit(zombie, delta);
            else if (Mode == "freezeaura") DoFreezeAura(zombie, delta);
            else if (Mode == "heal") DoHeal(zombie, delta);
        }
        catch (Exception ex) { Bootstrap.Log("僵尸特殊异常: " + ex.Message); }
    }

    /// <summary>冰霜光环（SP6）：每 0.3s 收集半径 260 内植物 → 减速（探测植物移动/攻速字段：
    /// 有 moveSpeed/speed 字段则 ×0.5；同时 fireInterval×1.4 攻速降低）。探测式：无字段仅日志。</summary>
    void DoFreezeAura(Node2D zombie, double delta)
    {
        _auraTick -= delta;
        if (_auraTick > 0) return;
        _auraTick = 0.3;
        var list = new List<Node2D>();
        CharReflect.WalkCollect(zombie, 260.0, "Plant", list);
        int slowed = 0;
        foreach (var p in list)
        {
            if (p == null || !GodotObject.IsInstanceValid(p)) continue;
            if (CharReflect.GetNumField(p, "moveSpeed").HasValue || CharReflect.GetNumField(p, "speed").HasValue)
                CharReflect.SetMoveSpeedScale(p, 0.5);
            CharReflect.SetFireIntervalScale(p, 1.4);
            slowed++;
        }
        if (!_freezeLogged && slowed > 0)
        {
            _freezeLogged = true;
            Bootstrap.Log("冰霜光环: 半径260 内植物 " + slowed + " 减速（moveSpeed×0.5 / fireInterval×1.4）");
        }
    }

    /// <summary>治疗光环（SP6）：每 0.3s 收集半径 260 内僵尸 → 回血（hp=min(hp+Value, maxHp)，默认 20）。探测式。</summary>
    void DoHeal(Node2D zombie, double delta)
    {
        _auraTick -= delta;
        if (_auraTick > 0) return;
        _auraTick = 0.3;
        double heal = Value > 0 ? Value : 20.0;
        var list = new List<Node2D>();
        CharReflect.WalkCollect(zombie, 260.0, "Zombie", list);
        int healed = 0;
        foreach (var z in list)
        {
            if (z == null || !GodotObject.IsInstanceValid(z)) continue;
            var hp = CharReflect.GetNumField(z, "hp");
            if (!hp.HasValue) continue;
            var maxHp = CharReflect.GetNumField(z, "maxHp");
            double cap = maxHp.HasValue && maxHp.Value > 0 ? maxHp.Value : hp.Value + heal;
            CharReflect.SetNumField(z, "hp", Math.Min(hp.Value + heal, cap));
            healed++;
        }
        if (!_healLogged && healed > 0)
        {
            _healLogged = true;
            Bootstrap.Log("治疗光环: 半径260 内僵尸 " + healed + " 回血 +" + heal);
        }
    }

    /// <summary>summon/buff：每 Value 秒（默认 8s）触发一次。</summary>
    void Tick(double delta)
    {
        double interval = Value > 0 ? Value : 8.0;
        _tick -= delta;
        if (_tick > 0) return;
        _tick = interval;
        var zombie = GetParent() as Node2D;
        if (zombie == null || !GodotObject.IsInstanceValid(zombie)) return;
        if (Mode == "summon") TrySummon(zombie);
        else if (Mode == "buff") DoBuff(zombie);
    }

    /// <summary>召唤：SpawnCharacter 小鬼到僵尸所在格（GetPacketIds(false) 匹配 Imp，兜底普通僵尸）。</summary>
    void TrySummon(Node2D zombie)
    {
        try
        {
            if (string.IsNullOrEmpty(_summonId))
            {
                _summonId = FindZombieId("Imp") ?? "";
                if (string.IsNullOrEmpty(_summonId)) _summonId = FindZombieId("ZombieNormal") ?? "";
                if (string.IsNullOrEmpty(_summonId)) _summonId = FindZombieId("Zombie") ?? "";
            }
            if (string.IsNullOrEmpty(_summonId))
            {
                if (!_summonLogged) { _summonLogged = true; Bootstrap.Log("僵尸召唤: 未找到小鬼 id（GetPacketIds 无 Imp/Zombie 匹配）"); }
                return;
            }
            var grid = ReadGridPos(zombie);
            bool ok = GameCheats.SpawnCharacter(_summonId, grid);
            if (!_summonLogged) { _summonLogged = true; Bootstrap.Log("僵尸召唤: " + _summonId + " grid=" + grid + " ok=" + ok); }
        }
        catch (Exception ex) { Bootstrap.Log("僵尸召唤异常: " + ex.Message); }
    }

    /// <summary>增益：半径 260 内僵尸攻速×0.7（attackInterval 越低越快）+ 血量 +100（一次性）。</summary>
    void DoBuff(Node2D zombie)
    {
        try
        {
            var list = new List<Node2D>();
            CharReflect.WalkCollect(zombie, 260.0, "Zombie", list);
            foreach (var z in list)
            {
                if (z == null || !GodotObject.IsInstanceValid(z)) continue;
                ulong id = z.GetInstanceId();
                var atk = CharReflect.GetNumField(z, "attackInterval");
                if (atk.HasValue && atk.Value > 0)
                {
                    double b;
                    if (!_atkBase.TryGetValue(id, out b)) { b = atk.Value; _atkBase[id] = b; }
                    CharReflect.SetNumField(z, "attackInterval", b * 0.7);
                }
                if (!_hpBuffed.Contains(id))
                {
                    _hpBuffed.Add(id);
                    var hp = CharReflect.GetNumField(z, "hp");
                    if (hp.HasValue)
                    {
                        CharReflect.SetNumField(z, "hp", hp.Value + 100);
                        CharReflect.SetNumField(z, "maxHp", hp.Value + 100);
                    }
                }
            }
            if (!_buffLogged) { _buffLogged = true; Bootstrap.Log("僵尸buff: 半径260 内僵尸 " + list.Count + " 攻速×0.7 + 血量+100"); }
        }
        catch (Exception ex) { Bootstrap.Log("僵尸buff异常: " + ex.Message); }
    }

    /// <summary>分裂：_Process 检测僵尸 die==true → SpawnCharacter 2 个小僵尸（GetPacketIds 匹配 ZombieNormal，兜底 Imp）。</summary>
    void CheckSplit(Node2D zombie, double delta)
    {
        if (_splitDone) return;
        _splitTick -= delta;
        if (_splitTick > 0) return;
        _splitTick = 0.1;
        object die = ReadField(zombie, "die");
        if (die is bool b && b)
        {
            _splitDone = true;
            string id = FindZombieId("ZombieNormal") ?? "";
            if (string.IsNullOrEmpty(id)) id = FindZombieId("Imp") ?? "";
            if (string.IsNullOrEmpty(id))
            {
                if (!_splitLogged) { _splitLogged = true; Bootstrap.Log("僵尸分裂: 未找到小僵尸 id"); }
                return;
            }
            var grid = ReadGridPos(zombie);
            int n = 0;
            for (int i = 0; i < 2; i++)
                try { if (GameCheats.SpawnCharacter(id, grid)) n++; } catch { }
            if (!_splitLogged) { _splitLogged = true; Bootstrap.Log("僵尸分裂: " + id + " x" + n + " grid=" + grid); }
        }
    }

    /// <summary>GetPacketIds(false) 里模糊匹配关键词的第一个僵尸 id（探测小鬼/小僵尸 id）。</summary>
    static string FindZombieId(string kw)
    {
        try
        {
            var ids = GameCheats.GetPacketIds(false);
            if (ids == null) return null;
            foreach (var id in ids)
                if (!string.IsNullOrEmpty(id) && id.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0) return id;
        }
        catch { }
        return null;
    }

    /// <summary>读角色 gridPos 字段；失败按全局位置粗略估算格子（仅兜底）。</summary>
    static Vector2I ReadGridPos(Node2D zombie)
    {
        try
        {
            var f = zombie.GetType().GetField("gridPos", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null && f.GetValue(zombie) is Vector2I v) return v;
        }
        catch { }
        var g = zombie.GlobalPosition;
        return new Vector2I(1 + (int)(g.X / 80.0), 1 + (int)(g.Y / 100.0));
    }

    /// <summary>沿基类链读字段值。</summary>
    static object ReadField(object obj, string name)
    {
        try
        {
            var t = obj.GetType();
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            while (t != null)
            {
                var f = t.GetField(name, flags | BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(obj);
                t = t.BaseType;
            }
        }
        catch { }
        return null;
    }
}
