using System;
using System.Collections.Generic;
using System.Text;

namespace PvzheMod;

public sealed class CustomPlantManager
{
    public static readonly Dictionary<string, CustomPlantDef> Dict = new();

    // 植物名 → 克隆后的游戏 config（供 Spawn 种植用），与 Dict 同步注册
    private static readonly Dictionary<string, object> _cfgs = new();

    public static string Load(string configPath)
    {
        try
        {
            if (!Godot.FileAccess.FileExists(configPath)) return "err:no-file";
            var fa = Godot.FileAccess.Open(configPath, Godot.FileAccess.ModeFlags.Read);
            if (fa == null) { Bootstrap.Log("自定义植物 Load: 无法打开 " + configPath); return "err:bad-config"; }
            string text = fa.GetAsText();
            fa.Close();
            var def = ParseDef(text);
            if (def == null || string.IsNullOrWhiteSpace(def.Name)) return "err:bad-config";
            // SP6 全量化：模板不再限制在 TEMPLATES 13 个——任意游戏植物 id/关键词均可（先精确、后模糊探测）。
            if (ResolveTemplateConfig(def.Template) == null) return "err:bad-template";
            def.Dir = GetDirOf(configPath);
            try { ProbeLog(def); } catch { }
        // ---- Task 4: 克隆模板 config + 应用参数/贴图 + 注册 ----------------
        var cfg = DuplicateTemplate(def);
        if (cfg == null) return "err:bad-template";
        ApplyParams(cfg, def);
        ApplyAttack(cfg, def);   // I2: 应用攻击参数（攻速/子弹）——玩家填的攻速/子弹此前从未生效
        ApplyTextures(cfg, def);
        // 真二创：把 config 关联的 PackedScene 换成用户自己的场景（未填/失败 = 什么都不改）
        ApplyCustomScene(cfg, def);
        // I1: 覆盖语义——同名重载视为"更新"（重注册）。先校验后注册：解析/模板校验/克隆/覆盖/换图全在注册前，失败不污染 Dict
        bool existed = Dict.ContainsKey(def.Name);
        Dict[def.Name] = def;
        _cfgs[def.Name] = cfg;
        Bootstrap.Log("自定义植物" + (existed ? "已更新: " : "已加载: ") + def.Name + " 模板=" + def.Template);
        // SP8: 卡牌图标：项目目录有 icon.png 则替换卡牌图标（探测式，失败仅日志）
        CustomProjectManager.ApplyCardIcon(def.Dir, cfg);
        // Task 2: 反向注册选卡栏（进选卡列表）。探测式，失败不阻塞 Load。
        try { if (RegisterToBank(def.Name)) Bootstrap.Log("自定义植物 卡包注册成功"); } catch (Exception ex) { Bootstrap.Log("自定义植物 卡包注册异常: " + ex.Message); }
        // 尝试进物品栏（若游戏按名识别则成功；失败不阻塞，可走 Spawn 直接刷出）
        try { if (GameCheats.AddPacketToInventory(def.Name)) Bootstrap.Log("自定义植物已进卡栏: " + def.Name); else Bootstrap.Log("自定义植物未进卡栏(按名不可用,可刷出): " + def.Name); } catch (Exception ex) { Bootstrap.Log("自定义植物进卡栏异常: " + ex.Message); }
        return "ok";
    }
    catch (Exception ex) { Bootstrap.Log("自定义植物 Load 异常: " + ex.Message); return "err:bad-config"; }
    }

    // ---- 手写 JSON 解析（Godot.Json.ParseString → Variant，规避 System.Text.Json 在 AOT 下不可靠）----
    private static CustomPlantDef ParseDef(string text)
    {
        try
        {
            var v = Godot.Json.ParseString(text);
            if (v.VariantType != Godot.Variant.Type.Dictionary) return null;
            var d = v.AsGodotDictionary();
            if (!TryGetStr(d, "name", out string name)) return null;
            var def = new CustomPlantDef
            {
                Name = name,
                Template = GetStr(d, "template", ""),
                Cost = (int)GetNum(d, "cost", 125),
                Cooldown = GetNum(d, "cooldown", 7.5),
                Hp = GetNum(d, "hp", 300),
                Scale = GetNum(d, "scale", 1.0),
                // 真二创（可选）：项目目录内的场景文件名，如 "scene.tscn"。
                // 不填 = 完全维持原行为（克隆原生模板 + 换贴图）。
                Scene = GetStr(d, "scene", ""),
            };
            var ad = GetDict(d, "attack");
            if (ad != null)
            {
                def.Attack.Damage = GetNum(ad, "damage", 40);
                def.Attack.Interval = GetNum(ad, "interval", 1.5);
                def.Attack.Range = GetNum(ad, "range", 2000);
                def.Attack.Bullet = GetStr(ad, "bullet", "Pea");
                def.Attack.Targets = GetStr(ad, "targets", "single");
                def.Attack.SunAmount = (int)GetNum(ad, "sunAmount", 25);
            }
            var fd = GetDict(d, "frame");
            if (fd != null)
            {
                def.Frame.Idle = GetStr(fd, "idle", "idle.png");
                def.Frame.Attack = GetStr(fd, "attack", "attack.png");
            }
            // SP4: 被动能力（passive 子对象：kind= none/sun/shield/slowaura/coin/buffaura, value）
            var pd = GetDict(d, "passive");
            if (pd != null)
            {
                def.Passive.Kind = GetStr(pd, "kind", "none");
                def.Passive.Value = GetNum(pd, "value", 0);
            }
            return def;
        }
        catch { return null; }
    }

    // ==================== 真二创：用户自己的 Godot 场景 ====================

    /// <summary>
    /// **真二创**：把原生植物 config 关联的 PackedScene 换成用户自己的场景。
    ///
    /// 【为什么用"换 PackedScene"而不是自己 Instantiate 再挂节点】
    /// 游戏本来就通过 characterConfig → PackedScene.Instantiate() 建植物实体。
    /// 只要把那个字段指到用户的场景，游戏的实例化 / 挂载 / 动画 / 生命周期**全部照旧** ——
    /// 这是**改数据、不改流程**，是风险最小的接入方式。
    ///
    /// 【已实测的两个前提】
    ///   ① 游戏能从盘上散装加载 <c>res://</c> 资源（ResourceLoader.Load 一个散装 .tscn 成功拿到 PackedScene）；
    ///   ② <c>res://</c> == 游戏主程序目录，所以内容必须发布在游戏目录下（PvzModContent\）才看得见。
    ///      —— 实测同时发现 DirAccess **枚举不到**散装文件，所以必须靠 config.json 显式给路径，
    ///         不能指望游戏自己扫目录（这也正是本字段存在的原因）。
    ///
    /// 【失败策略】未填 / 项目不在游戏目录下 / 加载不到 / 类型不对
    /// → **什么都不改**（保持原生场景），并把**具体是哪一步**写进日志。
    /// 真二创是增益，绝不能让它拖垮原本能用的"克隆模板 + 换贴图"路线。
    /// </summary>
    private static void ApplyCustomScene(object cfg, CustomPlantDef def)
    {
        try
        {
            if (def == null || string.IsNullOrWhiteSpace(def.Scene)) return;   // 未填 → 现有行为完全不变

            var resDir = ToResPath(def);
            if (resDir == null)
            {
                Bootstrap.Log("自定义植物 真二创 失败(" + def.Name + "): 项目不在游戏目录下，res:// 看不见 → " + def.Dir
                            + "（把内容放到 <游戏根>\\PvzModContent 下即可）");
                return;
            }

            var full = resDir + "/" + def.Scene;
            var loaded = Godot.ResourceLoader.Load(full);
            if (loaded == null)
            {
                Bootstrap.Log("自定义植物 真二创 失败(" + def.Name + "): 加载不到 " + full + "（保持原生场景）");
                return;
            }

            var packed = loaded as Godot.PackedScene;
            if (packed == null)
            {
                Bootstrap.Log("自定义植物 真二创 失败(" + def.Name + "): " + full
                            + " 不是 PackedScene，而是 " + loaded.GetType().Name);
                return;
            }

            var hit = RegisterCustomScene(def, packed);
            Bootstrap.Log("自定义植物 真二创 生效(" + def.Name + "): 自建场景 " + full
                        + " 已登记 → 键=" + hit + "（游戏查 TOWERDEFENSE_CHARCATERS 前自动写入）");
            // 即时自检：直接调游戏真正的 GetCharacterScene(name)，看拿到的到底是不是这份场景。
            // GetCharacterScene 是纯字典查表，所以注册完就能验，不必等玩家进关卡种下植物。
            GameCheats.SelfTestCustomScene(def.Name);
        }
        catch (Exception ex)
        {
            Bootstrap.Log("自定义植物 真二创 异常(" + (def != null ? def.Name : "?") + "): "
                        + ex.GetType().Name + " " + ex.Message);
        }
    }

    /// <summary>
    /// 项目绝对目录 → <c>res://</c> 路径。
    /// <c>res://</c> 只覆盖**游戏主程序目录下**的那棵树，所以靠 "PvzModContent" 这一段把它剪出来；
    /// 不在该树下返回 null（调用方会写清原因，不静默）。
    /// </summary>
    private static string ToResPath(CustomPlantDef def)
    {
        if (def == null || string.IsNullOrEmpty(def.Dir)) return null;

        var abs = def.Dir.Replace('\\', '/').TrimEnd('/');
        var marker = "/" + CustomProjectManager.RootFolderName + "/";   // "/PvzModContent/"

        var i = abs.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;

        return "res://" + abs.Substring(i + 1);   // "res://PvzModContent/custom_projects/plant/xxx"
    }

    private static Godot.Collections.Dictionary GetDict(Godot.Collections.Dictionary d, string key)    {
        foreach (var k in d.Keys)
            if (k.AsString().Equals(key, StringComparison.OrdinalIgnoreCase))
                return d[k].VariantType == Godot.Variant.Type.Dictionary ? d[k].AsGodotDictionary() : null;
        return null;
    }

    private static bool TryGetStr(Godot.Collections.Dictionary d, string key, out string val)
    {
        foreach (var k in d.Keys)
        {
            if (!k.AsString().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            if (d[k].VariantType == Godot.Variant.Type.String) { val = d[k].AsString(); return true; }
            val = null;
            return false;
        }
        val = null;
        return false;
    }

    private static string GetStr(Godot.Collections.Dictionary d, string key, string def)
    {
        return TryGetStr(d, key, out string val) ? val : def;
    }

    private static double GetNum(Godot.Collections.Dictionary d, string key, double def)
    {
        foreach (var k in d.Keys)
        {
            if (!k.AsString().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            var v = d[k];
            if (v.VariantType == Godot.Variant.Type.Int) return v.AsInt64();
            if (v.VariantType == Godot.Variant.Type.Float) return v.AsDouble();
            return def;
        }
        return def;
    }

    private static string GetDirOf(string path)
    {
        int idx = -1;
        for (int i = 0; i < path.Length; i++)
            if (path[i] == '/' || path[i] == '\\') idx = i;
        return idx < 0 ? "" : path.Substring(0, idx);
    }

    public static string Remove(string name)
    {
        _cfgs.Remove(name);
        return Dict.Remove(name) ? "ok" : "err:not-found";
    }

    public static string ListJson()
    {
        var sb = new StringBuilder("[");
        bool first = true;
        foreach (var kv in Dict)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"name\":\"").Append(kv.Key)
              .Append("\",\"template\":\"").Append(kv.Value.Template)
              .Append("\",\"cost\":").Append(kv.Value.Cost).Append('}');
        }
        sb.Append(']');
        return sb.ToString();
    }

    // ---- Task 4: 种植（Spawn）--------------------------------------------

    /// <summary>种植自定义植物到指定格子。cfg 来自 _cfgs（Load 时克隆+应用过参数/贴图）。</summary>
    public static bool Spawn(string name, Godot.Vector2I gridPos)
    {
        try
        {
            if (!_cfgs.TryGetValue(name, out var cfg)) { Bootstrap.Log("自定义植物 Spawn: 未注册 " + name); return false; }
            var plant = FindPlantMethod(cfg.GetType());
            if (plant == null) { Bootstrap.Log("自定义植物 Spawn: 找不到 Plant 方法"); return false; }
            object ret = null;
            try { ret = plant.Invoke(cfg, FillArgs(plant, new object[] { gridPos, true, true })); } catch (Exception ex) { Bootstrap.Log("自定义植物 Spawn 异常: " + ex.Message); return false; }
            // 兜底换图：若返回的是节点，对种植实例应用双纹（idle 打底 + attack 加载记录）
            if (ret is Godot.Node2D n2 && Godot.GodotObject.IsInstanceValid(n2) && Dict.TryGetValue(name, out var def))
            {
                ApplyDualTexture(n2, def);
                AttachAttackFx(n2, def);
                // SP4: 被动能力挂载（产阳光/护盾/减速光环/产金币/增益光环）
                PassiveControllers.AttachPlant(n2, def);
            }
            return true;
        }
        catch (Exception ex) { Bootstrap.Log("自定义植物 Spawn 异常: " + ex.Message); return false; }
    }

    /// <summary>在 config 类型上查找首参为 Vector2I 的 Plant 方法（跳过泛型重载）。</summary>
    private static System.Reflection.MethodInfo FindPlantMethod(Type t)
    {
        foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
        {
            if (m.Name != "Plant" || m.IsGenericMethodDefinition) continue;
            var ps = m.GetParameters();
            if (ps.Length >= 1 && ps[0].ParameterType == typeof(Godot.Vector2I)) return m;
        }
        return null;
    }

    /// <summary>用头部实参补齐完整参数列表：可选参数按类型填默认（bool/int/double/float），否则用声明默认值或 null。</summary>
    private static object[] FillArgs(System.Reflection.MethodInfo m, object[] given)
    {
        var ps = m.GetParameters();
        var args = new object[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            if (i < given.Length) args[i] = given[i];
            else if (ps[i].ParameterType == typeof(bool)) args[i] = true;
            else if (ps[i].ParameterType == typeof(int)) args[i] = 0;
            else if (ps[i].ParameterType == typeof(double)) args[i] = 0.0;
            else if (ps[i].ParameterType == typeof(float)) args[i] = 0f;
            else args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : null;
        }
        return args;
    }

    public static bool SetNumField(object obj, string field, double val)
    {
        try
        {
            var f = FindNumField(obj, field);
            if (f == null) return false;
            if (f.FieldType == typeof(double)) { f.SetValue(obj, val); return true; }
            if (f.FieldType == typeof(float))  { f.SetValue(obj, (float)val); return true; }
            if (f.FieldType == typeof(int))    { f.SetValue(obj, (int)val); return true; }
        }
        catch { }
        return false;
    }

    private static System.Reflection.FieldInfo FindNumField(object obj, string field)
    {
        var t = obj.GetType();
        foreach (var name in new[] { field, "_" + field })
        {
            var f = t.GetField(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (f == null) continue;
            if (f.FieldType == typeof(double) || f.FieldType == typeof(float) || f.FieldType == typeof(int)) return f;
        }
        return null;
    }

    static bool _ccNameLogged;   // 角色层字段名适配日志（只打一次）
    static bool _ccFailLogged;   // 角色层取不到日志（只打一次，附带实际字段名）
    static bool _rmFieldsLogged; // 注册失败时列出 ResourceManager 候选成员（只打一次）

    // ---- 真二创：自定义角色场景登记表（键 = 角色名，值 = 玩家自建的 PackedScene）----
    static readonly Dictionary<string, Godot.PackedScene> _customScenes =
        new Dictionary<string, Godot.PackedScene>(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> _sceneInjectLogged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    static bool _sceneNameProbed;

    internal static int CustomSceneCount => _customScenes.Count;

    /// <summary>把玩家自建场景登记为「角色名 → PackedScene」，供 GameCheats.EnsureCharacterSceneLoaded
    /// 在游戏查 TOWERDEFENSE_CHARCATERS 之前写入（GetCharacterScene 本身只查字典、不加载）。
    /// 登记名一律用 def.Name —— 也就是我们注册进卡栏的那个名字，即游戏将要传递的 characterName。
    /// **绝不登记成模板角色名**，否则会连带把原版植物的场景也换掉。返回登记的键便于日志核对。</summary>
    internal static string RegisterCustomScene(CustomPlantDef def, Godot.PackedScene scene)
    {
        var key = def.Name;
        _customScenes[key] = scene;
        return key;
    }

    /// <summary>命中返回 true 并给出自建场景。</summary>
    internal static bool TryGetCustomScene(string characterName, out Godot.PackedScene scene)
    {
        scene = null;
        if (string.IsNullOrEmpty(characterName)) return false;
        return _customScenes.TryGetValue(characterName, out scene) && scene != null;
    }

    /// <summary>同一角色名只打一次"已注入"日志（避免每放置一次刷屏）。</summary>
    internal static bool MarkSceneInjected(string characterName)
        => _sceneInjectLogged.Add(characterName ?? "");

    /// <summary>游戏请求了一个**它自己字典里也没有**的角色名 → 极可能就是我们的自定义植物，
    /// 但名字与登记名对不上。只打一次，用来实测游戏到底把我们的植物叫什么（不猜）。</summary>
    internal static void ProbeUnknownCharacter(string characterName)
    {
        if (_sceneNameProbed || string.IsNullOrEmpty(characterName)) return;
        _sceneNameProbed = true;
        var names = new StringBuilder();
        foreach (var k in _customScenes.Keys) { if (names.Length > 0) names.Append(','); names.Append(k); }
        Bootstrap.Log("自定义植物 真二创 探测: 游戏请求角色名 \"" + characterName
                    + "\" 既不在游戏字典也不在自建表[" + names + "] → 注册名与游戏请求名不一致");
    }

    /// <summary>取角色层（TowerDefensePlantConfig：真实 cost/packetCooldown/hp/maxHp 与角色场景都在这一层）。
    /// 0.28 实测字段名为 **_characterConfig**（本 mod 的"自定义植物探测"日志已列出顶层字段），
    /// 而 0.26/0.27 为 characterConfig → 按候选名逐个探测，全找不到才返回 null 并打印实际字段名，
    /// 绝不静默失败（历史上正是这里恒返回 null，导致 cost/攻击/场景替换/注册全部静默失效）。</summary>
    private static object GetCharacterConfig(object cfg)
    {
        if (cfg == null) return null;
        const System.Reflection.BindingFlags BF = System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        try
        {
            var t = cfg.GetType();
            // ① 明确候选名（新→旧），字段存在但值为空时继续找，不提前放弃
            foreach (var nm in new[] { "_characterConfig", "characterConfig", "_CharacterConfig", "CharacterConfig", "m_characterConfig" })
            {
                var f = t.GetField(nm, BF);
                if (f == null) continue;
                var v = f.GetValue(cfg);
                if (v != null)
                {
                    if (nm != "characterConfig" && !_ccNameLogged)
                    { _ccNameLogged = true; Bootstrap.Log("自定义植物: 角色层字段名 = " + nm + "（本版本与内置默认名不同，已自动适配）"); }
                    return v;
                }
            }
            // ② 模糊：忽略下划线/大小写后等于 characterConfig
            foreach (var f in t.GetFields(BF))
            {
                if (!f.Name.Replace("_", "").Equals("characterConfig", StringComparison.OrdinalIgnoreCase)) continue;
                var v = f.GetValue(cfg);
                if (v != null)
                {
                    if (!_ccNameLogged) { _ccNameLogged = true; Bootstrap.Log("自定义植物: 角色层字段名 = " + f.Name + "（模糊匹配命中）"); }
                    return v;
                }
            }
            // ③ 属性兜底
            var p = t.GetProperty("CharacterConfig", BF) ?? t.GetProperty("characterConfig", BF);
            if (p != null) { var v = p.GetValue(cfg); if (v != null) return v; }
            // ④ 全失败：把实际顶层字段名打出来，便于对症（只打一次，避免刷屏）
            if (!_ccFailLogged)
            {
                _ccFailLogged = true;
                var names = new StringBuilder();
                foreach (var f in t.GetFields(BF)) { if (names.Length > 0) names.Append(','); names.Append(f.Name); }
                Bootstrap.Log("自定义植物: 取不到角色层（含 characterConfig 的字段/属性均不存在或为空）顶层字段=[" + names + "]");
            }
        }
        catch { }
        return null;
    }

    private static object DuplicateConfig(object cfg)
    {
        try
        {
            var dm = cfg.GetType().GetMethod("Duplicate", new Type[] { typeof(bool) });
            if (dm != null) return dm.Invoke(cfg, new object[] { true });
        }
        catch { }
        return null;
    }

    private static object DuplicateTemplate(CustomPlantDef def)
    {
        var eff = ResolveEffectiveTemplate(def);
        if (eff != def.Template)
            Bootstrap.Log("自定义植物: cannon 寒冰语义 → 模板升级 " + def.Template + " → " + eff);
        var src = ResolveTemplateConfig(eff);
        if (src == null) { Bootstrap.Log("自定义植物: 模板 config 为空 " + eff); return null; }
        return DuplicateConfig(src);
    }

    /// <summary>模板语义修正（2026-09-09）：玉米加农炮(cannon) 的炮弹由 CannonDefinition 决定
    /// （PlantCobCannon → 普通 CobCannonCob），不受 projectileName/子弹字段控制 → 玩家选"寒冰"子弹
    /// 实际仍发普通玉米炮弹（历史 bug）。对 cannon+寒冰语义 → 升级为游戏原生寒冰加农炮
    /// TowerDefensePlantIceCobCannon（CannonDefinition.projectileName=IceCobCannonCob，冰炮弹+冰爆减速）。
    /// 非寒冰 bullet（Pea/FirePea/自制等）保持原模板，不改变既有行为。</summary>
    static string ResolveEffectiveTemplate(CustomPlantDef def)
    {
        try
        {
            var t = def.Template ?? "";
            bool isCannon = TEMPLATES.TryGetValue(t, out var mapped)
                ? mapped == "PlantCobCannon"
                : t.IndexOf("CobCannon", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!isCannon) return t;
            var b = def.Attack.Bullet ?? "";
            bool ice = b.IndexOf("Snow", StringComparison.OrdinalIgnoreCase) >= 0
                       || b.IndexOf("Ice", StringComparison.OrdinalIgnoreCase) >= 0
                       || b.IndexOf("寒", StringComparison.OrdinalIgnoreCase) >= 0;
            return ice ? "PlantIceCobCannon" : t;
        }
        catch { return def.Template; }
    }

    private static void ApplyParams(object cfg, CustomPlantDef def)
    {
        // 优先 characterConfig（TowerDefensePlantConfig）层：真实 cost/packetCooldown/hp/maxHp
        var cc = GetCharacterConfig(cfg);
        if (cc != null)
        {
            SetNumField(cc, "cost", def.Cost);
            SetNumField(cc, "packetCooldown", def.Cooldown);
            // 血量：0.28 实测角色层的真实字段名是 hitpoints —— 探测日志列出的 29 个字段里**没有** hp/maxHp，
            // 所以按候选名逐个试、命中即止（SetNumField 未命中不抛、只是不生效）。
            foreach (var hpName in new[] { "hitpoints", "hp", "maxHp", "maxHitpoints", "health", "maxHealth" })
                SetNumField(cc, hpName, def.Hp);
        }
        // 顶层兜底：overrideCost(Int)/overridePacketCooldown(Single)——SetNumField 按字段类型自动转 int/float
        SetNumField(cfg, "overrideCost", def.Cost);
        SetNumField(cfg, "overridePacketCooldown", def.Cooldown);
    }

    // ---- I2: 攻击参数应用（攻速 + 子弹类型）------------------------------------------
    // 参考 GameCheats.ApplyAttackSpeed / TryChangeBulletViaApi 的思路，但全部自包含在 CustomPlantManager
    // （不改 GameCheats）。对场景实例做探测式反射：命中即生效，未命中仅日志不抛。
    //
    // 伤害语义：伤害由子弹 config 决定（自制子弹自带 baseDamage；游戏子弹不改，避免误伤）。
    // 玩家填的 damage 不做强制覆盖，仅作为兜底：若探测发现植物节点有 fireDamage/damage 数值字段则补设。
    //
    // 运行期验证点（0.27 字段名未完全确认，以 ProbeLog 日志为准）：
    //  ① FireComponent 是否作为场景子节点存在（类型名含 "FireComponent"）；
    //  ② 植物节点 / FireComponent 是否有 fireInterval/fireIntervalBase 控制点（Single/Double）；
    //  ③ 植物节点是否有 projectileName 字段，MOD 是否已按名注册自制子弹（CustomBulletManager.RegisterBullet →
    //     GameCheats.RegisterProjectileKey + _bullets[name]=cfg，游戏按名解析即生效）。

    /// <summary>应用攻击参数：攻速（fireInterval 双写 FireComponent+植物节点）+ 子弹类型（projectileName/fireProjectileList）。</summary>
    private static void ApplyAttack(object cfg, CustomPlantDef def)
    {
        try
        {
            // 场景实例（characterConfig 关联 PackedScene Instantiate，与 ApplyTextures 同路径）
            var cc = GetCharacterConfig(cfg);
            if (cc == null) return;
            var sf = FindSceneField(cc.GetType());
            if (sf == null) return;
            var packed = sf.GetValue(cc) as Godot.PackedScene;
            if (packed == null) return;
            var root = packed.Instantiate();

            bool ivHit = SetNodeInterval(root, def.Attack.Interval);
            bool pjHit = SetNodeProjectile(root, def.Attack.Bullet);
            // FireComponent 子节点（若运行时作为子节点挂在场景里）：攻速/子弹也对其设置
            var fireNode = FindFireNode(root);
            if (fireNode != null && fireNode != root)
            {
                ivHit |= SetNodeInterval(fireNode, def.Attack.Interval);
                pjHit |= SetNodeProjectile(fireNode, def.Attack.Bullet);
            }
            // 伤害兜底（探测式）：仅当植物自身有 fireDamage/damage 数值字段才补设，避免误伤游戏子弹伤害
            bool dmgHit = def.Attack.Damage > 0 &&
                (SetNumField(root, "fireDamage", def.Attack.Damage) | SetNumField(root, "damage", def.Attack.Damage));

            Bootstrap.Log("自定义植物 攻击应用: " + def.Name + " 攻速=" + def.Attack.Interval
                + "(" + (ivHit ? "命中" : "未命中") + ") 子弹=" + def.Attack.Bullet
                + "(" + (pjHit ? "命中" : "未命中") + ") 伤害兜底=" + (dmgHit ? "命中" : "无字段")
                + " fireNode=" + (fireNode != null ? fireNode.GetType().Name : "无"));
            root.QueueFree();
        }
        catch (Exception ex) { Bootstrap.Log("自定义植物 应用攻击异常: " + ex.Message); }
    }

    /// <summary>递归找类型名含 "FireComponent" 的子节点；找不到返回 null（调用方兜底用植物根节点）。</summary>
    private static Godot.Node FindFireNode(Godot.Node root)
    {
        try
        {
            foreach (var child in root.GetChildren())
            {
                if (child == null || !Godot.GodotObject.IsInstanceValid(child)) continue;
                if (child.GetType().Name.Contains("FireComponent")) return child;
                var deeper = FindFireNode(child);
                if (deeper != null) return deeper;
            }
        }
        catch { }
        return null;
    }

    /// <summary>反射设 fireInterval/fireIntervalBase（Single/Double 字段或属性，属性用 setter Invoke）。命中任一控制点返回 true。</summary>
    private static bool SetNodeInterval(Godot.Node node, double interval)
    {
        bool hit = false;
        try
        {
            if (node == null || interval <= 0) return false;
            var t = node.GetType();
            var bf = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            foreach (var fn in new[] { "fireInterval", "fireIntervalBase" })
            {
                var f = t.GetField(fn, bf);
                if (f == null) continue;
                if (f.FieldType == typeof(float)) { f.SetValue(node, (float)interval); hit = true; }
                else if (f.FieldType == typeof(double)) { f.SetValue(node, interval); hit = true; }
            }
            // 属性（Double）：AOT/.NET9 下 PropertyInfo.SetValue 抛 MissingMethod → 用 GetSetMethod(true).Invoke
            var p = t.GetProperty("fireInterval", bf);
            if (p != null && p.PropertyType == typeof(double))
            {
                var st = p.GetSetMethod(true);
                if (st != null) { st.Invoke(node, new object[] { (double)interval }); hit = true; }
            }
        }
        catch (Exception ex) { Bootstrap.Log("自定义植物 攻速应用异常: " + ex.Message); }
        return hit;
    }

    /// <summary>反射设 projectileName 字段（string）→ 游戏按名解析（MOD 已注册自制子弹）则生效；找不到仅日志。返回是否命中任一控制点。</summary>
    private static bool SetNodeProjectile(Godot.Node node, string bullet)
    {
        bool hit = false;
        try
        {
            if (node == null || string.IsNullOrWhiteSpace(bullet)) return false;
            var t = node.GetType();
            var bf = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var f = t.GetField("projectileName", bf);
            if (f != null && f.FieldType == typeof(string)) { f.SetValue(node, bullet); hit = true; return hit; }
            var p = t.GetProperty("projectileName", bf);
            if (p != null && p.PropertyType == typeof(string))
            {
                var st = p.GetSetMethod(true);
                if (st != null) { st.Invoke(node, new object[] { bullet }); hit = true; return hit; }
            }
            // FireComponent.fireProjectileList（IList<string>）反射设置：命中即生效
            var fl = t.GetField("fireProjectileList", bf);
            if (fl != null && fl.GetValue(node) is System.Collections.IList il)
            {
                il.Clear();
                il.Add(bullet);
                hit = true;
            }
        }
        catch (Exception ex) { Bootstrap.Log("自定义植物 子弹应用异常: " + ex.Message); }
        return hit;
    }

    private static void ProbeLog(CustomPlantDef def)
    {
        var dcfg = DuplicateTemplate(def);
        if (dcfg == null) return;
        var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var topNames = new List<string>();
        foreach (var f in dcfg.GetType().GetFields(flags))
        {
            if (topNames.Count >= 30) break;
            topNames.Add(f.Name);
        }
        var cc = GetCharacterConfig(dcfg);
        var ccNames = new List<string>();
        if (cc != null)
        {
            foreach (var f in cc.GetType().GetFields(flags))
            {
                if (ccNames.Count >= 30) break;
                ccNames.Add(f.Name);
            }
        }
        var sb = new StringBuilder("自定义植物探测: [" + def.Template + "]");
        sb.Append(" 顶=[").Append(string.Join(",", topNames)).Append(']');
        sb.Append(" 字=[").Append(cc != null ? string.Join(",", ccNames) : "无").Append(']');
        foreach (var k in new[] { "cost", "packetCooldown", "hitpoints", "hp", "maxHp", "overrideCost", "overridePacketCooldown" })
        {
            bool top = FindNumField(dcfg, k) != null;
            bool ccHit = cc != null && FindNumField(cc, k) != null;
            sb.Append(' ').Append(k).Append('=').Append(top || ccHit ? "命中" : "未命中")
              .Append(top ? "(顶)" : (ccHit ? "(字)" : ""));
        }
        Bootstrap.Log(sb.ToString());
    }

    // ---- Task 3: 贴图替换（PNG → ImageTexture → Sprite）----

    /// <summary>加载 PNG 为 ImageTexture。dir 是绝对目录（含盘符，来自 def.Dir），file 是相对文件名。
    /// public：CustomZombieManager 复用（子项目3 换图）。</summary>
    public static Godot.Texture2D LoadTex(string dir, string file)
    {
        try
        {
            var p = dir.Length == 0 ? file : dir + "/" + file;
            if (!Godot.FileAccess.FileExists(p)) return null;
            var img = new Godot.Image();
            var err = img.Load(p);   // Godot 4 Image.Load 支持 OS 绝对路径
            if (err != Godot.Error.Ok) { Bootstrap.Log("自定义植物: 图片加载失败 " + p + " " + err); return null; }
            return Godot.ImageTexture.CreateFromImage(img);
        }
        catch (Exception ex) { Bootstrap.Log("自定义植物: 贴图异常 " + ex.Message); return null; }
    }

    /// <summary>实例级替换：递归遍历节点树，Sprite2D 直接设纹理，非 Sprite2D 用反射兜底。</summary>
    private static void ReplaceSpriteTextures(Godot.Node node, Godot.Texture2D idle, Godot.Texture2D attack, CustomPlantDef def)
    {
        foreach (var child in node.GetChildren())
        {
            if (child is Godot.Sprite2D sp)
            {
                sp.Texture = idle;
                sp.Scale = new Godot.Vector2((float)def.Scale, (float)def.Scale);
            }
            else
            {
                // 游戏角色 Sprite 可能是自定义类型（AdobeAnimateSprite/SpriteGroup 等），反射兜底
                TrySetTextureByReflection(child, idle, new Godot.Vector2((float)def.Scale, (float)def.Scale));
            }
            ReplaceSpriteTextures(child, idle, attack, def);
        }
    }

    /// <summary>反射兜底：类型名含 "Sprite" 且带 Texture 的节点，直接设字段或经 Setter 方法设属性。
    /// public：CustomZombieManager 复用（子项目3 换图）。</summary>
    public static void TrySetTextureByReflection(object node, Godot.Texture2D tex, Godot.Vector2 scale)
    {
        try
        {
            var t = node.GetType();
            if (!t.Name.Contains("Sprite")) return;
            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

            // Texture：字段优先（SetValue），否则属性经 GetSetMethod(true).Invoke（避免 PropertyInfo.SetValue 在 AOT/.NET9 MissingMethod）
            var tf = t.GetField("Texture", flags) ?? t.GetField("_texture", flags);
            if (tf != null)
            {
                if (typeof(Godot.Texture2D).IsAssignableFrom(tf.FieldType)) tf.SetValue(node, tex);
            }
            else
            {
                var tp = t.GetProperty("Texture", flags) ?? t.GetProperty("_texture", flags);
                if (tp != null && typeof(Godot.Texture2D).IsAssignableFrom(tp.PropertyType))
                    tp.GetSetMethod(true)?.Invoke(node, new object[] { tex });
            }

            // Scale：字段优先，否则属性经 Setter
            var sf = t.GetField("Scale", flags) ?? t.GetField("_scale", flags);
            if (sf != null)
            {
                if (sf.FieldType == typeof(Godot.Vector2) || sf.FieldType == typeof(Godot.Vector2?)) sf.SetValue(node, scale);
            }
            else
            {
                var sp = t.GetProperty("Scale", flags) ?? t.GetProperty("_scale", flags);
                if (sp != null && sp.PropertyType == typeof(Godot.Vector2))
                    sp.GetSetMethod(true)?.Invoke(node, new object[] { scale });
            }
        }
        catch { /* 该节点跳过，不抛异常 */ }
    }

    // ---- I2 基础版：种植实例上的双纹应用（idle 打底；attack 加载并记录，运行时切换为已知限制）----

    /// <summary>
    /// 种后双纹应用：idle 打底（ReplaceSpriteTextures）；attack 存在则加载并探测可写 Sprite。
    /// MVP 决策：不做 0.5s 攻击登场切换——Godot 定时/异步（SceneTreeTimer/回调）在反射 + AOT 上下文不可靠，
    /// 种植实例是否已在场景树内、是否在超时前被释放均不可验证，引入定时器有崩溃风险。
    /// 故本任务仅加载 attack 并在 ProbeLog 记录是否成功，攻击帧运行时切换标注为已知限制（留给后续）。
    /// </summary>
    private static void ApplyDualTexture(Godot.Node2D node, CustomPlantDef def)
    {
        try
        {
            var idle = LoadTex(def.Dir, def.Frame.Idle);
            if (idle == null) { Bootstrap.Log("自定义植物: 待机图缺失(种后) " + def.Frame.Idle); return; }
            ReplaceSpriteTextures(node, idle, idle, def);
            bool hasAttackFile = !string.IsNullOrEmpty(def.Frame.Attack) && def.Frame.Attack != def.Frame.Idle;
            if (!hasAttackFile) return;   // 未配置独立攻击帧
            var attack = LoadTex(def.Dir, def.Frame.Attack);
            if (attack == null) { Bootstrap.Log("自定义植物: 攻击帧加载失败(种后) " + def.Name + " " + def.Frame.Attack); return; }
            bool writable = FindFirstTextureSprite(node) != null;
            Bootstrap.Log("自定义植物: 攻击帧已加载(" + def.Name + ") 可写Sprite=" + (writable ? "是" : "否") + " 运行时攻击切换=已知限制(未启用)");
        }
        catch (Exception ex) { Bootstrap.Log("自定义植物: 双纹应用异常 " + ex.Message); }
    }

    /// <summary>探测：递归找首个"类型名含 Sprite 且 Texture 可写(Texture2D)"的节点，与 TrySetTextureByReflection 前置条件一致（不实际写入）。</summary>
    private static Godot.Node FindFirstTextureSprite(Godot.Node node)
    {
        var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        foreach (var child in node.GetChildren())
        {
            var t = child.GetType();
            if (t.Name.Contains("Sprite"))
            {
                var tf = t.GetField("Texture", flags) ?? t.GetField("_texture", flags);
                if (tf != null && typeof(Godot.Texture2D).IsAssignableFrom(tf.FieldType)) return child;
                var tp = tf == null ? (t.GetProperty("Texture", flags) ?? t.GetProperty("_texture", flags)) : null;
                if (tp != null && typeof(Godot.Texture2D).IsAssignableFrom(tp.PropertyType) && tp.GetSetMethod(true) != null) return child;
            }
            var deeper = FindFirstTextureSprite(child);
            if (deeper != null) return deeper;
        }
        return null;
    }

    /// <summary>从 characterConfig 取场景实例做替换探测：实例化后改贴图再释放，验证路径可用。</summary>
    private static void ApplyTextures(object cfg, CustomPlantDef def)
    {
        var idle = LoadTex(def.Dir, def.Frame.Idle);
        var attack = LoadTex(def.Dir, string.IsNullOrEmpty(def.Frame.Attack) ? def.Frame.Idle : def.Frame.Attack);
        if (idle == null) { Bootstrap.Log("自定义植物: 待机图缺失 " + def.Frame.Idle); return; }
        try
        {
            var cc = cfg.GetType().GetField("characterConfig", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var cco = cc?.GetValue(cfg);
            if (cco == null) return;
            var sf = FindSceneField(cco.GetType());
            if (sf == null) return;
            var packed = sf.GetValue(cco) as Godot.PackedScene;
            if (packed == null) return;
            var root = packed.Instantiate();
            ReplaceSpriteTextures(root, idle, attack, def);
            root.QueueFree();
        }
        catch (Exception ex) { Bootstrap.Log("自定义植物: 换图异常 " + ex.Message); }
    }

    /// <summary>在 config 类型上查找 PackedScene 场景字段。</summary>
    private static System.Reflection.FieldInfo FindSceneField(Type t)
    {
        foreach (var n in new[] { "scene", "characterScene", "prefabScene", "_scene", "_characterScene" })
        {
            var f = t.GetField(n, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (f != null && (f.FieldType == typeof(Godot.PackedScene) || f.FieldType.Name.Contains("PackedScene"))) return f;
        }
        return null;
    }

    // ---- Task 1: 攻击帧动画（AttackFxController 挂接）----

    /// <summary>种植实例挂攻击帧控制器；收集可写 Texture 的 Sprite 节点。</summary>
    public static void AttachAttackFx(Godot.Node2D node, CustomPlantDef def)
    {
        try
        {
            if (node.GetNodeOrNull("AttackFxController") != null) return;
            var idle = LoadTex(def.Dir, def.Frame.Idle);
            var attack = LoadTex(def.Dir, string.IsNullOrEmpty(def.Frame.Attack) ? def.Frame.Idle : def.Frame.Attack);
            if (idle == null) return;
            var ctrl = new AttackFxController
            {
                IdleTex = idle,
                AttackTex = attack,
                Def = def,
                HasAttack = !string.IsNullOrEmpty(def.Frame.Attack) && attack != null,
            };
            CollectSpriteNodes(node, ctrl.Sprites);
            if (ctrl.Sprites.Count == 0) { ctrl.QueueFree(); return; }
            node.AddChild(ctrl);
            ctrl.Name = "AttackFxController";
            Bootstrap.Log("自定义植物 攻击帧控制器: 挂接 " + def.Name + " 精灵=" + ctrl.Sprites.Count + " 有攻击帧=" + ctrl.HasAttack);
        }
        catch (Exception ex) { Bootstrap.Log("自定义植物 挂接攻击帧异常: " + ex.Message); }
    }

    static void CollectSpriteNodes(Godot.Node node, System.Collections.Generic.List<Godot.Sprite2D> list)
    {
        foreach (var child in node.GetChildren())
        {
            if (child is Godot.Sprite2D sp) list.Add(sp);
            CollectSpriteNodes(child, list);
        }
    }

    // ---- Task 2: 反向注册选卡栏（RegisterToBank）----
    // 把自定义植物 cfg 的 name 加进 GeneralPlant 卡包列表 + 注册 config 按名映射。
    // 路径与 GameCheats.GetPacketIds 植物分支同构（I-1）：FindTdmInstance → GetPacketBankData("GeneralPlant") → GetPlantList()（静态路径，不依赖 TOWERDEFENSE_PACKETBANKS dict）。
    // I-2 顺序保证：先 RegisterConfigLookup（config 查找注册），成功才追加列表；config 失败不污染列表。
    // 全部辅助方法探测式：失败返回 null/false，不抛。

    public static bool RegisterToBank(string name)
    {
        try
        {
            if (!_cfgs.TryGetValue(name, out var cfg)) { Bootstrap.Log("自定义植物 注册: 未找到 " + name); return false; }
            var tdm = FindTdmInstance();
            if (tdm == null) { Bootstrap.Log("自定义植物 注册: TDM 不可用"); return false; }
            var bankData = InvokeGetPacketBankData(tdm, "GeneralPlant");
            if (bankData == null) { Bootstrap.Log("自定义植物 注册: GetPacketBankData(GeneralPlant) 返回 null"); return false; }
            var list = InvokeGetPlantList(bankData);
            if (list == null) { Bootstrap.Log("自定义植物 注册: GetPlantList 返回 null"); return false; }
            // I-2：先注册 config 查找（失败则不污染列表）
            if (!RegisterConfigLookup(cfg, name)) { Bootstrap.Log("自定义植物 注册: config 查找注册失败，不追加列表"); return false; }
            if (!AddNameToContainer(list, name)) { Bootstrap.Log("自定义植物 注册: 列表追加失败"); return false; }
            Bootstrap.Log("自定义植物 已注册卡包: " + name);
            return true;
        }
        catch (Exception ex) { Bootstrap.Log("自定义植物 注册异常: " + ex.Message); return false; }
    }

    static Type FindType(string name)
    {
        try
        {
            // 优先搜索主程序集（目标类型都在主程序集），避免其他程序集 GetType 抛异常
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
            // 兜底：搜索全部程序集
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

    /// <summary>取 ResourceManager.Instance（public static 字段优先，兜底属性）。</summary>
    static object FindResourceManagerInstance()
    {
        try
        {
            var rmType = FindType("ResourceManager");
            if (rmType == null) return null;
            var fld = rmType.GetField("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (fld != null)
            {
                var inst = fld.GetValue(null);
                if (inst != null) return inst;
            }
            var prop = rmType.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (prop != null)
            {
                try { return prop.GetValue(null); } catch { }
            }
        }
        catch { }
        return null;
    }

    /// <summary>取 TowerDefenseManager.Instance（GameCheats.GetTdmInstance 是 private，跨类不可用，此处自实现；public static 属性优先，兜底字段）。</summary>
    static object FindTdmInstance()
    {
        try
        {
            var t = FindType("TowerDefenseManager");
            if (t == null) return null;
            var prop = t.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (prop != null)
            {
                try { var v = prop.GetValue(null); if (v != null) return v; } catch { }
            }
            var fld = t.GetField("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (fld != null)
            {
                var v = fld.GetValue(null);
                if (v != null) return v;
            }
        }
        catch { }
        return null;
    }

    /// <summary>静态调用 GetPacketBankData(string key)，返回卡包数据；失败返回 null。</summary>
    static object InvokeGetPacketBankData(object tdm, string key)
    {
        try
        {
            var t = tdm.GetType();
            var m = t.GetMethod("GetPacketBankData", new Type[] { typeof(string) });
            if (m == null) return null;
            return m.Invoke(null, new object[] { key });
        }
        catch { return null; }
    }

    /// <summary>调用卡包的 GetPlantList() 返回植物列表容器；失败返回 null。</summary>
    static object InvokeGetPlantList(object bank)
    {
        try
        {
            var m = bank.GetType().GetMethod("GetPlantList", Type.EmptyTypes);
            if (m == null) return null;
            return m.Invoke(bank, null);
        }
        catch { return null; }
    }

    /// <summary>把 name 加进列表容器（IList.Add 优先；否则扫描 Add/Append/PushBack(string|Variant)）。
    /// ★ 0.28 实测教训：原先只认 Add(string)，而 Godot 的 <c>Godot.Collections.Array</c> 用的方法是
    ///   <c>Append(Variant)</c> → 走到最后 return false，且异常被 catch 吞掉，只看到"列表追加失败"四个字。
    ///   现在：① 覆盖 Godot 容器方法名；② 每一次失败都把容器真实类型/异常原因写进日志（不再静默）。
    /// 已存在返回 false。</summary>
    static bool AddNameToContainer(object container, string name)
    {
        if (container == null) { Bootstrap.Log("自定义植物 注册: 列表容器为 null"); return false; }
        var t = container.GetType();

        // ★ 0.28 真机实测（2026-09-19）：容器真实类型是 Godot.Collections.Array（非泛型），
        //   其 Add/Contains 只接受 Godot.Variant，而反射 Invoke **不会**把 string 隐式转成 Variant，
        //   会抛 "Object of type 'System.String' cannot be converted to type 'Godot.Variant'"。
        //   所以这里必须用**强类型调用**，让编译器插入 string→Variant 的隐式转换（反射做不到）。
        if (container is Godot.Collections.Array garr)
        {
            try
            {
                if (garr.Contains(name))
                {
                    Bootstrap.Log("自定义植物 注册: 列表已含 " + name + "（Godot.Collections.Array），不重复追加");
                    return false;
                }
                garr.Add(name);
                Bootstrap.Log("自定义植物 注册: 列表追加命中 Godot.Collections.Array.Add(Variant)");
                return true;
            }
            catch (Exception ex)
            {
                Bootstrap.Log("自定义植物 注册: Godot.Collections.Array.Add 抛 " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        try
        {
            if (container is System.Collections.IList il)
            {
                if (il.Contains(name))
                {
                    Bootstrap.Log("自定义植物 注册: 列表已含 " + name + "（容器 " + t.FullName + "），不重复追加");
                    return false;
                }
                il.Add(name);
                Bootstrap.Log("自定义植物 注册: 列表追加命中 " + t.FullName + ".Add(IList)");
                return true;
            }
        }
        catch (Exception ex)
        {
            Bootstrap.Log("自定义植物 注册: IList.Add 抛 " + ex.GetType().Name + ": " + ex.Message
                        + "（容器 " + t.FullName + "）→ 继续尝试 Godot 风格方法名");
        }

        // 扫描 Add/Append/PushBack（含 Godot Variant 形参）
        // ★ 必须用 GetMethods(flags)：IL2CPP 裁剪掉了无参 GetMethods() 重载，调用会抛
        //   "Method not found: 'MethodInfo[] Type.GetMethods()'"（本 mod 已在真机上踩过一次）。
        const System.Reflection.BindingFlags MF = System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var tried = new StringBuilder();
        foreach (var m in t.GetMethods(MF))
        {
            if (m.Name != "Add" && m.Name != "Append" && m.Name != "PushBack" && m.Name != "push_back") continue;
            var ps = m.GetParameters();
            if (ps.Length != 1) continue;
            var pt = ps[0].ParameterType;
            if (pt != typeof(string) && pt != typeof(object) && pt.Name != "Variant") continue;
            if (tried.Length > 0) tried.Append(',');
            tried.Append(m.Name).Append('(').Append(pt.Name).Append(')');
            try
            {
                m.Invoke(container, new object[] { name });
                Bootstrap.Log("自定义植物 注册: 列表追加命中 " + t.FullName + "." + m.Name + "(" + pt.Name + ")");
                return true;
            }
            catch (Exception ex)
            {
                Bootstrap.Log("自定义植物 注册: " + m.Name + "(" + pt.Name + ") 抛 " + ex.GetType().Name + ": " + ex.Message);
            }
        }
        Bootstrap.Log("自定义植物 注册: 列表追加失败——容器 " + t.FullName + " 无可用追加方法（试过: "
                    + (tried.Length > 0 ? tried.ToString() : "无") + "）");
        return false;
    }

    /// <summary>注册 name→cfg 到游戏的 packet 配置字典，并把 packet→角色名 映射指向 name。
    /// 0.28 实测（2026-09-19 ResourceManager 成员清单）：packet 配置字典是 TOWERDEFENSE_PACKETS，
    /// 角色名映射是 _characterNameByPacket；旧候选名在本版本全部不存在。命中返回 true。
    /// ★ 这个映射就是真二创闭环的关键：游戏按角色名查 TOWERDEFENSE_CHARCATERS 拿场景，
    ///   指向 name 才能命中我们登记的自建场景。
    /// 失败返回 false（I-2：调用方不得追加列表，失败安全）。</summary>
    static bool RegisterConfigLookup(object cfg, string name)
    {
        try
        {
            // ① 0.28 正确路径（新）：packet 配置字典 + packet→角色名 映射成对写入
            if (GameCheats.RegisterCustomPacket(name, cfg, name)) return true;

            // ② 老版本兜底：探测旧的 config 缓存字段名
            var rm = FindResourceManagerInstance();
            if (rm == null) { Bootstrap.Log("自定义植物 注册: ResourceManager 不可用"); return false; }
            var t = rm.GetType();
            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            foreach (var fn in new[] { "_packetConfigCache", "packetConfigs", "_packetConfigs", "PacketConfigs", "_packetConfigDict" })
            {
                var f = t.GetField(fn, flags);
                if (f == null) continue;
                var v = f.GetValue(rm);
                if (v is System.Collections.IDictionary id)
                {
                    id[name] = cfg;
                    Bootstrap.Log("自定义植物 注册: config缓存字典命中 " + fn);
                    return true;
                }
            }
            // 失败必须能看出该往哪写：把 ResourceManager 上像「配置/缓存/包」的成员名列出来（只打一次），
            // 下一轮即可据实测名补进候选列表，而不是继续猜（0.28 已证旧名 _packetConfigCache 全不存在）。
            if (!_rmFieldsLogged)
            {
                _rmFieldsLogged = true;
                var cand = new StringBuilder();
                foreach (var f in t.GetFields(flags))
                {
                    var n = f.Name;
                    if (n.IndexOf("config", StringComparison.OrdinalIgnoreCase) < 0
                        && n.IndexOf("cache", StringComparison.OrdinalIgnoreCase) < 0
                        && n.IndexOf("packet", StringComparison.OrdinalIgnoreCase) < 0
                        && n.IndexOf("character", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (cand.Length > 0) cand.Append(',');
                    cand.Append(n).Append(':').Append(f.FieldType.Name);
                }
                Bootstrap.Log("自定义植物 注册: 未找到config缓存字典；ResourceManager 候选成员=[" + cand + "]");
            }
            return false;
        }
        catch { return false; }
    }

    public static readonly Dictionary<string, string> TEMPLATES = new()
    {
        ["shooter"]  = "PlantPeaShooter",
        ["catapult"] = "PlantCabbagepult",
        ["cannon"]   = "PlantCobCannon",
        ["melee"]    = "PlantChomper",
        ["passive"]  = "PlantSunFlower",
        // SP6 更多攻击模板（0.27 部分 id 可能不存在/变种——DuplicateTemplate 对 null 返回 false → Load 返回
        // "err:bad-template" 优雅失败，ProbeLog 打印探测字段供核对）
        ["spike"]    = "PlantCaltrop",        // 地刺
        ["umbrella"] = "PlantUmbrellaleaf",   // 保护伞
        ["magnet"]   = "PlantMagnetShroom",   // 磁力菇
        ["squash"]   = "PlantSquash",         // 倭瓜
        ["iceberg"]  = "PlantIcePea",         // 寒冰射手
        ["doom"]     = "PlantDoomShroom",     // 毁灭菇
        ["fume"]     = "PlantFumeShroom",     // 喷气菇
        ["puff"]     = "PlantPuffShroom",     // 小喷菇
    };

    /// <summary>模板解析（SP6 全量化）：① 精确——template 本身即游戏植物 config id；② 候选——TEMPLATES 映射 id；③ 模糊——GetPacketIds(true) 里 id 含 template。失败返回 null。</summary>
    static object ResolveTemplateConfig(string template)
    {
        try
        {
            if (string.IsNullOrEmpty(template)) return null;
            // ① 精确：template 直接作为游戏 id（任意游戏植物模板）
            var direct = GameCheats.GetConfigPublic(template);
            if (direct != null) return direct;
            // ② 候选：TEMPLATES 映射 id
            if (TEMPLATES.TryGetValue(template, out var id) && !string.IsNullOrEmpty(id))
            {
                var cfg = GameCheats.GetConfigPublic(id);
                if (cfg != null) return cfg;
            }
            // ③ 模糊：GetPacketIds(true) 里 id 含 template
            var ids = GameCheats.GetPacketIds(true);
            if (ids != null)
            {
                foreach (var pid in ids)
                {
                    if (string.IsNullOrEmpty(pid)) continue;
                    if (pid.IndexOf(template, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var cfg2 = GameCheats.GetConfigPublic(pid);
                        if (cfg2 != null) { Bootstrap.Log("自定义植物: 模板探测命中 " + template + " → " + pid); return cfg2; }
                    }
                }
            }

            // 失败必须能看出**为什么**。这里最容易踩的坑不是 id 写错，而是
            // 首帧时游戏的 config 系统还没就绪 → 所有 id 都查不到，表现与"模板名写错"完全一样。
            // 所以把关键事实打出来：映射有没有、游戏此刻给回多少 id。
            var mapped = TEMPLATES.TryGetValue(template, out var mid) ? mid : "(无映射)";
            var n = -2;
            try { var ids2 = GameCheats.GetPacketIds(true); n = ids2 == null ? -1 : ids2.Count; } catch { }
            Bootstrap.Log("自定义植物 模板解析失败: " + template
                        + " | 直接查 id 未命中"
                        + " | TEMPLATES 映射=" + mapped
                        + " | 游戏可用 id 数=" + n
                        + (n == 0 ? " ★ config 系统可能还没就绪（首帧调用太早）" : ""));
        }
        catch (Exception ex)
        {
            Bootstrap.Log("自定义植物 模板解析异常(" + template + "): " + ex.GetType().Name + " " + ex.Message);
        }
        return null;
    }

    /// <summary>PlantTemplates：返回 GetPacketIds(true) 全量植物 [{key,name}]，中文名 GetPacketDisplayNameZh。
    /// 供 WPF 植物模板下拉全量化（失败回退内置 13 个）。</summary>
    public static string PlantTemplates()
    {
        var sb = new StringBuilder("[");
        bool first = true;
        try
        {
            var ids = GameCheats.GetPacketIds(true);
            if (ids != null)
            {
                foreach (var id in ids)
                {
                    if (string.IsNullOrEmpty(id)) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"key\":\"").Append(EscapeJson(id))
                      .Append("\",\"name\":\"").Append(EscapeJson(GameCheats.GetPacketDisplayNameZh(id))).Append("\"}");
                }
            }
        }
        catch (Exception ex) { Bootstrap.Log("植物模板枚举异常: " + ex.Message); }
        sb.Append(']');
        return sb.ToString();
    }

    static string EscapeJson(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

public sealed class CustomPlantDef
{
    public string Name = "";
    public string Template = "";
    public int Cost = 125;
    public double Cooldown = 7.5;
    public double Hp = 300;
    public CustomAttackDef Attack = new();
    public CustomFrameDef Frame = new();
    public PassiveDef Passive = new();   // SP4: 被动能力（kind/value）
    public double Scale = 1.0;
    public string Dir = "";

    /// <summary>
    /// 真二创（可选）：项目目录内的场景文件名，如 <c>scene.tscn</c>。
    /// 空 = 走原有"克隆原生模板 + 换贴图"路线。
    /// </summary>
    public string Scene = "";
}

public sealed class CustomAttackDef
{
    public double Damage = 40;
    public double Interval = 1.5;
    public double Range = 2000;
    public string Bullet = "Pea";
    public string Targets = "single";
    public int SunAmount = 25;
}

public sealed class CustomFrameDef
{
    public string Idle = "idle.png";
    public string Attack = "attack.png";
}

/// 攻击帧控制器：挂种植实例上，攻击瞬间切 attack 帧 0.15s；缺帧用 idle 抖动 0.2s。AOT 安全（无 SceneTreeTimer）。
public sealed class AttackFxController : Godot.Node
{
    public Godot.Texture2D IdleTex;
    public Godot.Texture2D AttackTex;
    public CustomPlantDef Def;
    public System.Collections.Generic.List<Godot.Sprite2D> Sprites = new();
    public double AttackTimer;
    public double JitterTimer;
    public bool HasAttack;
    public double AttackCheckCooldown;

    public override void _Process(double delta)
    {
        if (Def == null || Sprites.Count == 0) return;
        AttackCheckCooldown -= delta;
        if (AttackCheckCooldown <= 0)
        {
            AttackCheckCooldown = 0.05;
            if (DetectFire())
            {
                if (HasAttack) { AttackTimer = 0.15; JitterTimer = 0.0; }
                else JitterTimer = 0.2;
            }
        }
        if (AttackTimer > 0)
        {
            AttackTimer -= delta;
            SetAllTex(AttackTex ?? IdleTex);
            if (AttackTimer <= 0) SetAllTex(IdleTex);
        }
        if (JitterTimer > 0)
        {
            JitterTimer -= delta;
            double ph = JitterTimer * 40.0;
            foreach (var s in Sprites)
                s.Scale = new Godot.Vector2((float)(Def.Scale * (1.0 + 0.15 * System.Math.Sin(ph))), (float)(Def.Scale * (1.0 - 0.15 * System.Math.Sin(ph))));
            if (JitterTimer <= 0)
                foreach (var s in Sprites) s.Scale = new Godot.Vector2((float)Def.Scale, (float)Def.Scale);
        }
    }

    bool DetectFire()
    {
        try
        {
            var parent = GetParent();
            if (parent == null) return false;
            foreach (var child in parent.GetChildren())
            {
                var t = child.GetType();
                if (!t.Name.Contains("FireComponent")) continue;
                var f = t.GetField("timer", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (f == null) continue;
                var v = f.GetValue(child);
                double tv = v is float fv ? fv : (v is double dv ? dv : 0);
                if (tv <= 0.01) return true;
            }
        }
        catch { }
        return false;
    }

    void SetAllTex(Godot.Texture2D tex)
    {
        if (tex == null) return;
        // Sprites 均为 Godot.Sprite2D，直接赋值公开 Texture/Scale 属性，避免依赖 CustomPlantManager 的 private 反射方法
        foreach (var s in Sprites)
        {
            s.Texture = tex;
            s.Scale = new Godot.Vector2((float)Def.Scale, (float)Def.Scale);
        }
    }

    public override void _ExitTree()
    {
        if (IdleTex != null) SetAllTex(IdleTex);
        base._ExitTree();
    }
}
