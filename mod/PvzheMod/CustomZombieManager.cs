using System;
using System.Collections.Generic;
using System.Text;
using System.Reflection;

namespace PvzheMod;

/// <summary>
/// 自定义僵尸（子项目3）：克隆模板僵尸 config → 覆盖（血量/移速/伤害）+ 换图（idle 帧）
/// → 登记字典 + 进僵尸卡包。僵尸行为全自定义（移动/攻击/特殊技能）留子项目4（SP4），本任务仅"注册"。
///
/// AOT 纪律：禁 System.IO/Path/System.Text.Json/PropertyInfo.SetValue；
/// 文件用 Godot.FileAccess，JSON 用 Godot.Json.ParseString → AsGodotDictionary 手写映射。
/// 字段全部"探测式"（复用 CustomPlantManager.SetNumField 模式）：命中即生效，未命中仅日志。
/// 运行期验证点（0.27 字段名未完全确认，以 Bootstrap.Log 为准）：
///  ① 僵尸 config 结构（characterConfig 层 hp/maxHp、moveSpeed/speed、damage/attackDamage 命中情况）
///  ② 僵尸模板 id（TEMPLATES 映射 id 经 GetConfigPublic 命中，或 GetPacketIds(false) 模糊探测）
///  ③ 僵尸卡包注册路径（GetPacketBankData("GeneralZombie") → GetZombieList，或枚举 TOWERDEFENSE_PACKETBANKS 卡包）
/// </summary>
public static class CustomZombieManager
{
    /// <summary>僵尸名 → 克隆后的游戏 config（与 _zombieDefs 同步；进卡包/Spawn 用）。</summary>
    static readonly Dictionary<string, object> _zombies = new();

    /// <summary>僵尸名 → 原始 def（List 元数据用，Load 时缓存）。</summary>
    static readonly Dictionary<string, CustomZombieDef> _zombieDefs = new();

    // 模板映射：key=config.json 里用户填的模板名，value=游戏真实僵尸 config id（GetPacketIds(false) 能拿到的候选）。
    // 值若未命中（0.27 id 可能带前后缀），运行期用 GetPacketIds(false) 模糊匹配（id 含模板 key）兜底。
    public static readonly Dictionary<string, string> TEMPLATES = new()
    {
        ["ZombieNormal"]     = "ZombieNormal",      // 普通僵尸
        ["ZombieConehead"]   = "ZombieConehead",    // 路障僵尸
        ["ZombieBuckethead"] = "ZombieBuckethead",  // 铁桶僵尸
        ["ZombieGiant"]      = "ZombieGiant",       // 巨人僵尸
        ["ZombieRunner"]     = "ZombieRunner",      // 舞王僵尸
        ["ZombieBungi"]      = "ZombieBungi",       // 飞贼
    };

    // ==================== 命令入口 ====================

    /// <summary>CustomZombieLoad：读 config.json → 解析 → 注册 → 缓存 def。返回 ok/err。</summary>
    public static string Load(string file)
    {
        try
        {
            if (!Godot.FileAccess.FileExists(file)) return "err:no-file";
            var fa = Godot.FileAccess.Open(file, Godot.FileAccess.ModeFlags.Read);
            if (fa == null) { Bootstrap.Log("自定义僵尸 Load: 无法打开 " + file); return "err:bad-config"; }
            string text = fa.GetAsText();
            fa.Close();
            var def = ParseZombieDef(text);
            if (def == null || string.IsNullOrWhiteSpace(def.Name)) return "err:bad-config";
            // SP6 全量化：模板不再限制在 TEMPLATES 6 个——任意僵尸 id/关键词均可（先精确、后模糊探测）。
            // 保留 "err:bad-template" 语义：完全解析不到才报错。
            if (ResolveTemplateConfig(def.Template) == null) return "err:bad-template";
            def.Dir = GetDirOf(file);
            if (!RegisterZombie(def)) return "err:register-fail";
            _zombieDefs[def.Name] = def;
            return "ok";
        }
        catch (Exception ex) { Bootstrap.Log("自定义僵尸 Load 异常: " + ex.Message); return "err:bad-config"; }
    }

    /// <summary>CustomZombieList：JSON [{name,template,hp,speed,damage}]。</summary>
    public static string List()
    {
        var sb = new StringBuilder("[");
        bool first = true;
        foreach (var kv in _zombieDefs)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"name\":\"").Append(kv.Key)
              .Append("\",\"template\":\"").Append(kv.Value.Template)
              .Append("\",\"hp\":").Append(kv.Value.Hp)
              .Append(",\"speed\":").Append(kv.Value.Speed)
              .Append(",\"damage\":").Append(kv.Value.Damage).Append('}');
        }
        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>CustomZombieTemplates（SP6）：返回 GetPacketIds(false) 全量僵尸 [{key,name}]，中文名 GetPacketDisplayNameZh。
    /// 供 WPF 僵尸模板下拉全量化（失败回退内置 6 个）。</summary>
    public static string ZombieTemplates()
    {
        var sb = new StringBuilder("[");
        bool first = true;
        try
        {
            var ids = GameCheats.GetPacketIds(false);
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
        catch (Exception ex) { Bootstrap.Log("僵尸模板枚举异常: " + ex.Message); }
        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>JSON 字符串转义（与 GameCheats.EscapeJson 同实现，private 跨类不可见）。</summary>
    static string EscapeJson(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    /// <summary>CustomZombieRemove：清 _zombies + _zombieDefs。返回 ok/err:not-found。</summary>
    public static string Remove(string name)
    {
        bool removed = _zombies.Remove(name) | _zombieDefs.Remove(name);   // |：两处都执行
        return removed ? "ok" : "err:not-found";
    }

    /// <summary>已加载僵尸项目名（游戏内面板列出用，_zombieDefs 与 _zombies 同键）。</summary>
    public static System.Collections.Generic.List<string> LoadedNames()
    {
        return new System.Collections.Generic.List<string>(_zombieDefs.Keys);
    }

    // ==================== 注册核心 ====================

    /// <summary>克隆模板 config → 覆盖 hp/moveSpeed/damage → 换图（idle）→ 登记 _zombies + 进僵尸卡包（失败仅日志不阻塞）。</summary>
    public static bool RegisterZombie(CustomZombieDef def)
    {
        try
        {
            if (def == null || string.IsNullOrWhiteSpace(def.Name)) return false;
            var src = ResolveTemplateConfig(def.Template);
            if (src == null) { Bootstrap.Log("自定义僵尸: 模板 config 为空 " + def.Template); return false; }
            var cfg = DuplicateConfig(src);
            if (cfg == null) { Bootstrap.Log("自定义僵尸: 克隆失败 " + def.Template); return false; }
            ApplyParams(cfg, def);
            ApplyTextures(cfg, def);
            // 先登记字典（Spawn/List 用），再探测式进卡包（失败仅日志，不阻塞 Load）
            _zombies[def.Name] = cfg;
            // SP8: 卡牌图标：项目目录有 icon.png 则替换卡牌图标（探测式，失败仅日志）
            CustomProjectManager.ApplyCardIcon(def.Dir, cfg);
            Bootstrap.Log("自定义僵尸已注册: " + def.Name + " 模板=" + def.Template
                + " hp=" + def.Hp + " 移速=" + def.Speed + " 伤害=" + def.Damage);
            try { if (RegisterZombieToBank(def.Name)) Bootstrap.Log("自定义僵尸 卡包注册成功"); }
            catch (Exception ex) { Bootstrap.Log("自定义僵尸 卡包注册异常: " + ex.Message); }
            return true;
        }
        catch (Exception ex) { Bootstrap.Log("自定义僵尸注册异常: " + ex.Message); return false; }
    }

    // ==================== 种植（CustomZombieSpawn，SP4） ====================

    /// <summary>种植自定义僵尸到指定格子（参照 CustomPlantManager.Spawn 的 FindPlantMethod/FillArgs 模式）。
    /// 克隆 config 副本种植（避免共享 config 内部状态导致重复种植问题）→ 返回节点后挂行为控制器。</summary>
    public static bool Spawn(string name, Godot.Vector2I gridPos)
    {
        try
        {
            if (!_zombies.TryGetValue(name, out var cfg)) { Bootstrap.Log("自定义僵尸 Spawn: 未注册 " + name); return false; }
            // 每次种植用独立 config 副本（同 GameCheats.SpawnCharacter：共享 config 内部状态会记录已种位置）
            try
            {
                var dm = cfg.GetType().GetMethod("Duplicate", new Type[] { typeof(bool) });
                if (dm != null) { var dup = dm.Invoke(cfg, new object[] { true }); if (dup != null) cfg = dup; }
            }
            catch { }
            var plant = FindPlantMethod(cfg.GetType());
            if (plant == null) { Bootstrap.Log("自定义僵尸 Spawn: 找不到 Plant 方法"); return false; }
            object ret = null;
            try { ret = plant.Invoke(cfg, FillArgs(plant, new object[] { gridPos, true, true })); } catch (Exception ex) { Bootstrap.Log("自定义僵尸 Spawn 异常: " + ex.Message); return false; }
            // 种后挂行为控制器（移动/攻击/特殊技能）
            if (ret is Godot.Node2D n2 && Godot.GodotObject.IsInstanceValid(n2) && _zombieDefs.TryGetValue(name, out var def))
                ZombieBehaviorControllers.AttachZombie(n2, def);
            return true;
        }
        catch (Exception ex) { Bootstrap.Log("自定义僵尸 Spawn 异常: " + ex.Message); return false; }
    }

    /// <summary>在 config 类型上查找首参为 Vector2I 的 Plant 方法（跳过泛型重载）。</summary>
    static System.Reflection.MethodInfo FindPlantMethod(Type t)
    {
        foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
        {
            if (m.Name != "Plant" || m.IsGenericMethodDefinition) continue;
            var ps = m.GetParameters();
            if (ps.Length >= 1 && ps[0].ParameterType == typeof(Godot.Vector2I)) return m;
        }
        return null;
    }

    /// <summary>用头部实参补齐完整参数列表：可选参数按类型填默认，否则用声明默认值或 null。</summary>
    static object[] FillArgs(System.Reflection.MethodInfo m, object[] given)
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

    /// <summary>模板解析（SP6 全量化）：① 精确——template 本身即游戏僵尸 config id（GetConfigPublic 直查）；
    /// ② 候选——TEMPLATES 映射 id；③ 模糊——GetPacketIds(false) 里 id 含 template（OrdinalIgnoreCase）。
    /// 失败返回 null。</summary>
    static object ResolveTemplateConfig(string template)
    {
        try
        {
            if (string.IsNullOrEmpty(template)) return null;
            // ① 精确：template 直接作为游戏 id（任意僵尸模板）
            var direct = GameCheats.GetConfigPublic(template);
            if (direct != null) return direct;
            // ② 候选：TEMPLATES 映射 id
            if (TEMPLATES.TryGetValue(template, out var id) && !string.IsNullOrEmpty(id))
            {
                var cfg = GameCheats.GetConfigPublic(id);
                if (cfg != null) return cfg;
            }
            // ③ 模糊：GetPacketIds(false) 里 id 含 template
            var ids = GameCheats.GetPacketIds(false);
            if (ids != null)
            {
                foreach (var zid in ids)
                {
                    if (string.IsNullOrEmpty(zid)) continue;
                    if (zid.IndexOf(template, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var cfg2 = GameCheats.GetConfigPublic(zid);
                        if (cfg2 != null) { Bootstrap.Log("自定义僵尸: 模板探测命中 " + template + " → " + zid); return cfg2; }
                    }
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>克隆 config（Resource.Duplicate(true)），失败返回 null。</summary>
    static object DuplicateConfig(object cfg)
    {
        try
        {
            var dm = cfg.GetType().GetMethod("Duplicate", new Type[] { typeof(bool) });
            if (dm != null) return dm.Invoke(cfg, new object[] { true });
        }
        catch { }
        return null;
    }

    /// <summary>覆盖数值（探测式）：hp/maxHp、moveSpeed/speed、damage/attackDamage——characterConfig 层 + 顶层兜底。</summary>
    static void ApplyParams(object cfg, CustomZombieDef def)
    {
        var cc = GetCharacterConfig(cfg);
        if (cc != null)
        {
            CustomPlantManager.SetNumField(cc, "hp", def.Hp);
            CustomPlantManager.SetNumField(cc, "maxHp", def.Hp);
            CustomPlantManager.SetNumField(cc, "moveSpeed", def.Speed);
            CustomPlantManager.SetNumField(cc, "speed", def.Speed);
            CustomPlantManager.SetNumField(cc, "damage", def.Damage);
            CustomPlantManager.SetNumField(cc, "attackDamage", def.Damage);
        }
        CustomPlantManager.SetNumField(cfg, "hp", def.Hp);
        CustomPlantManager.SetNumField(cfg, "maxHp", def.Hp);
        CustomPlantManager.SetNumField(cfg, "moveSpeed", def.Speed);
        CustomPlantManager.SetNumField(cfg, "speed", def.Speed);
        CustomPlantManager.SetNumField(cfg, "damage", def.Damage);
        CustomPlantManager.SetNumField(cfg, "attackDamage", def.Damage);
    }

    static object GetCharacterConfig(object cfg)
    {
        try
        {
            var f = cfg.GetType().GetField("characterConfig", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null) return null;
            return f.GetValue(cfg);
        }
        catch { return null; }
    }

    // ==================== 换图（idle 帧替换僵尸 Sprite，实例级探测） ====================

    static void ApplyTextures(object cfg, CustomZombieDef def)
    {
        try
        {
            if (string.IsNullOrEmpty(def.Dir)) { Bootstrap.Log("自定义僵尸: 无 Dir（换图跳过）"); return; }
            var idle = CustomPlantManager.LoadTex(def.Dir, def.Frame.Idle);
            if (idle == null) { Bootstrap.Log("自定义僵尸: 待机图缺失 " + def.Dir + "/" + def.Frame.Idle); return; }
            var cc = GetCharacterConfig(cfg);
            if (cc == null) { Bootstrap.Log("自定义僵尸: 无 characterConfig（换图跳过）"); return; }
            var sf = FindSceneField(cc.GetType());
            if (sf == null) { Bootstrap.Log("自定义僵尸: 无场景字段（换图跳过）"); return; }
            var packed = sf.GetValue(cc) as Godot.PackedScene;
            if (packed == null) { Bootstrap.Log("自定义僵尸: 场景为空（换图跳过）"); return; }
            var root = packed.Instantiate();
            ReplaceSpriteTextures(root, idle, (float)def.Scale);
            root.QueueFree();
            Bootstrap.Log("自定义僵尸 换图完成: " + def.Name);
        }
        catch (Exception ex) { Bootstrap.Log("自定义僵尸 换图异常: " + ex.Message); }
    }

    /// <summary>实例级递归替换：Sprite2D 直设纹理，非 Sprite2D 反射兜底（复用 CustomPlantManager.TrySetTextureByReflection）。</summary>
    static void ReplaceSpriteTextures(Godot.Node node, Godot.Texture2D tex, float scale)
    {
        foreach (var child in node.GetChildren())
        {
            if (child is Godot.Sprite2D sp)
            {
                sp.Texture = tex;
                sp.Scale = new Godot.Vector2(scale, scale);
            }
            else
            {
                CustomPlantManager.TrySetTextureByReflection(child, tex, new Godot.Vector2(scale, scale));
            }
            ReplaceSpriteTextures(child, tex, scale);
        }
    }

    static FieldInfo FindSceneField(Type t)
    {
        foreach (var n in new[] { "scene", "characterScene", "prefabScene", "_scene", "_characterScene" })
        {
            var f = t.GetField(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null && (f.FieldType == typeof(Godot.PackedScene) || f.FieldType.Name.Contains("PackedScene"))) return f;
        }
        return null;
    }

    // ==================== 僵尸卡包注册（探测式，失败仅日志不阻塞） ====================
    // 参照 CustomPlantManager.RegisterToBank 思路：植物=GetPacketBankData("GeneralPlant")+GetPlantList()。
    // 僵尸卡包 key 不可靠（GetPacketIds(false) 注释：GeneralZombie 可能不是卡包 key 返回 null）——
    // 先试 GetPacketBankData("GeneralZombie")+GetZombieList()，失败再枚举 TOWERDEFENSE_PACKETBANKS
    // 的 key，找含 GetZombieList 的卡包把僵尸名追加进列表。config 查找注册先于列表追加（I-2 失败安全）。

    static bool RegisterZombieToBank(string name)
    {
        try
        {
            if (!_zombies.TryGetValue(name, out var cfg)) return false;
            // ① 先注册 config 查找（失败则不污染列表）
            if (!RegisterConfigLookup(cfg, name)) { Bootstrap.Log("自定义僵尸: config 查找注册失败，不追加列表"); return false; }
            var tdm = FindType("TowerDefenseManager");
            if (tdm == null) { Bootstrap.Log("自定义僵尸: 无 TowerDefenseManager"); return false; }
            var getBank = tdm.GetMethod("GetPacketBankData", new Type[] { typeof(string) });
            if (getBank == null) { Bootstrap.Log("自定义僵尸: 无 GetPacketBankData"); return false; }
            // ② GetPacketBankData("GeneralZombie") 通用包
            try
            {
                var bank = getBank.Invoke(null, new object[] { "GeneralZombie" });
                if (bank != null)
                {
                    var zl = bank.GetType().GetMethod("GetZombieList", Type.EmptyTypes);
                    if (zl != null)
                    {
                        var r = zl.Invoke(bank, null);
                        if (AddNameToList(r, name)) { Bootstrap.Log("自定义僵尸: 卡包注册 GeneralZombie 追加 " + name); return true; }
                    }
                }
            }
            catch { }
            // ③ 枚举 TOWERDEFENSE_PACKETBANKS 卡包 key，找含 GetZombieList 的追加
            var rm = FindResourceManagerInstance();
            if (rm == null) { Bootstrap.Log("自定义僵尸: ResourceManager 不可用"); return false; }
            var banksProp = rm.GetType().GetProperty("TOWERDEFENSE_PACKETBANKS", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var banks = banksProp != null ? banksProp.GetValue(rm) : null;
            if (banks is System.Collections.IDictionary bdict)
            {
                foreach (var bk in bdict.Keys)
                {
                    try
                    {
                        if (bk == null) continue;
                        var bank2 = getBank.Invoke(null, new object[] { bk });
                        if (bank2 == null) continue;
                        var zl2 = bank2.GetType().GetMethod("GetZombieList", Type.EmptyTypes);
                        if (zl2 == null) continue;
                        var r2 = zl2.Invoke(bank2, null);
                        if (AddNameToList(r2, name)) { Bootstrap.Log("自定义僵尸: 卡包注册 " + bk + " 追加 " + name); return true; }
                    }
                    catch { }
                }
                Bootstrap.Log("自定义僵尸: 未找到含 GetZombieList 的卡包");
                return false;
            }
            Bootstrap.Log("自定义僵尸: 无 TOWERDEFENSE_PACKETBANKS");
            return false;
        }
        catch (Exception ex) { Bootstrap.Log("自定义僵尸 卡包注册异常: " + ex.Message); return false; }
    }

    /// <summary>把 name 加进僵尸列表容器（IList.Add 优先；否则反射 Add(string)）。已存在返回 false。</summary>
    static bool AddNameToList(object container, string name)
    {
        try
        {
            if (container is System.Collections.IList il)
            {
                if (il.Contains(name)) return false;
                il.Add(name);
                return true;
            }
            if (container != null)
            {
                var t = container.GetType();
                var add = t.GetMethod("Add", new Type[] { typeof(string) });
                if (add != null)
                {
                    var contains = t.GetMethod("Contains", new Type[] { typeof(string) });
                    if (contains != null && contains.Invoke(container, new object[] { name }) is bool b && b) return false;
                    add.Invoke(container, new object[] { name });
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>探测式注册 name→cfg 到 ResourceManager 的 config 缓存字典（候选字段名列表）。命中返回 true；找不到缓存字典返回 false（I-2 失败安全）。</summary>
    static bool RegisterConfigLookup(object cfg, string name)
    {
        try
        {
            var rm = FindResourceManagerInstance();
            if (rm == null) { Bootstrap.Log("自定义僵尸: ResourceManager 不可用"); return false; }
            var t = rm.GetType();
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var fn in new[] { "_packetConfigCache", "packetConfigs", "_packetConfigs", "PacketConfigs", "_packetConfigDict" })
            {
                var f = t.GetField(fn, flags);
                if (f == null) continue;
                var v = f.GetValue(rm);
                if (v is System.Collections.IDictionary id)
                {
                    id[name] = cfg;
                    Bootstrap.Log("自定义僵尸: config缓存字典命中 " + fn);
                    return true;
                }
            }
            Bootstrap.Log("自定义僵尸: 未找到config缓存字典");
            return false;
        }
        catch { return false; }
    }

    /// <summary>取 ResourceManager.Instance（public static 字段优先，兜底属性）。</summary>
    static object FindResourceManagerInstance()
    {
        try
        {
            var rmType = FindType("ResourceManager");
            if (rmType == null) return null;
            var fld = rmType.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
            if (fld != null)
            {
                var inst = fld.GetValue(null);
                if (inst != null) return inst;
            }
            var prop = rmType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            if (prop != null)
            {
                try { return prop.GetValue(null); } catch { }
            }
        }
        catch { }
        return null;
    }

    /// <summary>按名字找游戏类型（优先主程序集）。</summary>
    static Type FindType(string name)
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

    // ==================== 手写 JSON 解析（Godot.Json） ====================

    static CustomZombieDef ParseZombieDef(string text)
    {
        try
        {
            var v = Godot.Json.ParseString(text);
            if (v.VariantType != Godot.Variant.Type.Dictionary) return null;
            var d = v.AsGodotDictionary();
            if (!TryGetStr(d, "name", out string name)) return null;
            var def = new CustomZombieDef
            {
                Name = name,
                Template = GetStr(d, "template", "ZombieNormal"),
                Hp = GetNum(d, "hp", 200),
                Speed = GetNum(d, "speed", 0.5),
                Damage = GetNum(d, "damage", 20),
                Scale = GetNum(d, "scale", 1.0),
            };
            var fd = GetDict(d, "frame");
            if (fd != null)
            {
                def.Frame.Idle = GetStr(fd, "idle", "idle.png");
                def.Frame.Attack = GetStr(fd, "attack", "attack.png");
            }
            // SP4: 僵尸行为（move/attack/rangedBullet/special/specialValue）
            def.Behavior.Move = GetStr(d, "move", "walk");
            def.Behavior.Attack = GetStr(d, "attack", "bite");
            def.Behavior.RangedBullet = GetStr(d, "rangedBullet", "Pea");
            def.Behavior.Special = GetStr(d, "special", "none");
            def.Behavior.SpecialValue = GetNum(d, "specialValue", 0);
            return def;
        }
        catch { return null; }
    }

    static Godot.Collections.Dictionary GetDict(Godot.Collections.Dictionary d, string key)
    {
        foreach (var k in d.Keys)
            if (k.AsString().Equals(key, StringComparison.OrdinalIgnoreCase))
                return d[k].VariantType == Godot.Variant.Type.Dictionary ? d[k].AsGodotDictionary() : null;
        return null;
    }

    static bool TryGetStr(Godot.Collections.Dictionary d, string key, out string val)
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

    static string GetStr(Godot.Collections.Dictionary d, string key, string def)
    {
        return TryGetStr(d, key, out string val) ? val : def;
    }

    static double GetNum(Godot.Collections.Dictionary d, string key, double def)
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

    /// <summary>取 config 文件所在目录（不含文件名）。</summary>
    static string GetDirOf(string path)
    {
        int idx = -1;
        for (int i = 0; i < path.Length; i++)
            if (path[i] == '/' || path[i] == '\\') idx = i;
        return idx < 0 ? "" : path.Substring(0, idx);
    }
}

/// <summary>自定义僵尸定义（config.json 数据）。字段名与 spec/plan 一致；Frame 复用 CustomFrameDef（idle/attack 字符串）。</summary>
public sealed class CustomZombieDef
{
    public string Name = "";
    public string Template = "ZombieNormal";   // TEMPLATES 里的模板名
    public double Hp = 200;
    public double Speed = 0.5;                 // moveSpeed/speed
    public double Damage = 20;                 // damage/attackDamage
    public CustomFrameDef Frame = new();
    public double Scale = 1.0;
    public string Dir = "";
    public ZombieBehaviorDef Behavior = new();   // SP4: 行为（移动/攻击/特殊技能）
}
