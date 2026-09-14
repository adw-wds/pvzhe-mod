using System;
using System.Collections.Generic;
using System.Text;
using System.Reflection;

namespace PvzheMod;

/// <summary>
/// 自制子弹（子项目2）：克隆模板子弹 config（TowerDefenseProjectileData）
/// → 覆盖数值/命中音效/贴图/效果 → 注册进 GameCheats._projKeys 供植物/随机子弹引用。
///
/// AOT 纪律：禁 System.IO/Path/System.Text.Json/PropertyInfo.SetValue；
/// 文件用 Godot.FileAccess，JSON 用 Godot.Json.ParseString → AsGodotDictionary 手写映射。
/// 字段全部"探测式"：命中即生效，未命中仅日志不抛——字段名以运行时 ProbeBulletFieldsOnce 的
/// ProbeLog 为准更新（speed/penetrateNum/damageFlags/rangeSize/hitPesontage/buffList 等
/// 在 0.27 反编译 config 上未直接确认，可能位于 scene 或 spawn 数据层，属已知探测项）。
/// </summary>
public sealed class CustomBulletManager
{
    /// <summary>子弹名 → 克隆后的 config（注册成功后缓存，供 List/Remove/按名解析）。</summary>
    static readonly Dictionary<string, object> _bullets = new();

    /// <summary>子弹名 → 原始 def（List 元数据用，Load 时缓存）。</summary>
    static readonly Dictionary<string, CustomBulletDef> _bulletDefs = new();

    /// <summary>探测日志去重（每模板只打一次，避免刷屏）。</summary>
    static readonly HashSet<string> _probeLogged = new();

    // ==================== 命令入口 ====================

    /// <summary>CustomBulletLoad：读 config.json → 解析 → 注册 → 缓存 def。返回 ok/err。</summary>
    public static string CustomBulletLoad(string file)
    {
        try
        {
            if (!Godot.FileAccess.FileExists(file)) return "err:no-file";
            var fa = Godot.FileAccess.Open(file, Godot.FileAccess.ModeFlags.Read);
            if (fa == null) { Bootstrap.Log("自制子弹 Load: 无法打开 " + file); return "err:bad-config"; }
            string text = fa.GetAsText();
            fa.Close();
            var def = ParseBulletDef(text);
            if (def == null || string.IsNullOrWhiteSpace(def.Name)) return "err:bad-config";
            def.Dir = GetDirOf(file);
            if (!RegisterBullet(def)) return "err:register-fail";
            _bulletDefs[def.Name] = def;
            return "ok";
        }
        catch (Exception ex) { Bootstrap.Log("自制子弹 Load 异常: " + ex.Message); return "err:bad-config"; }
    }

    /// <summary>CustomBulletList：JSON [{name,template,damage}]。</summary>
    public static string CustomBulletList()
    {
        var sb = new StringBuilder("[");
        bool first = true;
        foreach (var kv in _bulletDefs)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"name\":\"").Append(kv.Key)
              .Append("\",\"template\":\"").Append(kv.Value.Template)
              .Append("\",\"damage\":").Append(kv.Value.Damage).Append('}');
        }
        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>CustomBulletRemove：清 _bullets + _bulletDefs + 从 _projKeys 注册集合移除。返回 ok/err:not-found。</summary>
    public static string CustomBulletRemove(string name)
    {
        bool removed = _bullets.Remove(name) | _bulletDefs.Remove(name);   // |：两处都执行
        GameCheats.UnregisterProjectileKey(name);
        return removed ? "ok" : "err:not-found";
    }

    /// <summary>已加载子弹项目名（游戏内面板列出用，_bulletDefs 与 _bullets 同键）。</summary>
    public static System.Collections.Generic.List<string> LoadedNames()
    {
        return new System.Collections.Generic.List<string>(_bulletDefs.Keys);
    }

    // ==================== 注册核心 ====================

    /// <summary>克隆模板子弹 config → 覆盖字段/音效/贴图/效果 → 追加进 _projKeys + 登记 _bullets。</summary>
    public static bool RegisterBullet(CustomBulletDef def)
    {
        try
        {
            if (def == null || string.IsNullOrWhiteSpace(def.Name)) return false;
            // 1) 加载模板子弹 config：优先 GetProjectileConfig（战斗内字典），兜底 PROJECTILE_RESOURCE 路径 GD.Load
            var src = LoadTemplateProjectile(def.Template);
            if (src == null) { Bootstrap.Log("自制子弹: 模板为空 " + def.Template); return false; }
            var cfg = DuplicateProjectile(src);
            if (cfg == null) { Bootstrap.Log("自制子弹: 克隆失败"); return false; }
            // 2) 覆盖字段（探测式）：baseDamage/vel 或 speed/penetrateNum/scale + splatAudio
            bool bd = SetNum(cfg, "baseDamage", def.Damage);
            bool sp = SetNum(cfg, "speed", def.Speed);
            bool pn = SetNum(cfg, "penetrateNum", def.Penetrate);
            bool sa = SetStr(cfg, "splatAudio", def.Audio);
            ApplyBulletScale(cfg, def);
            ApplyBulletSplat(cfg, def);   // SP8: splatSceneType 字符串（MVP）+ splatScene PackedScene 替换（探测式）
            // 3) 换图：替换 projectileScene 的 Sprite 贴图（def.Dir/bullet.png），复用反射
            ApplyBulletTexture(cfg, def);
            // 4) 效果：burn/freeze/stun/explode（探测 Buff 字段；失败仅日志）
            ApplyBulletEffect(cfg, def);
            // 5) 注册进 _projKeys（追加）+ 登记字典 _bullets[name]=cfg
            GameCheats.RegisterProjectileKey(def.Name);
            _bullets[def.Name] = cfg;
            // SP8: 卡牌图标：项目目录有 icon.png 则替换卡牌图标（探测式，失败仅日志）
            CustomProjectManager.ApplyCardIcon(def.Dir, cfg);
            Bootstrap.Log("自制子弹已注册: " + def.Name + " 模板=" + def.Template + " 伤害=" + def.Damage
                + " [baseDamage=" + (bd ? "命中" : "无") + " speed=" + (sp ? "命中" : "无")
                + " penetrateNum=" + (pn ? "命中" : "无") + " splatAudio=" + (sa ? "命中" : "无") + "]");
            return true;
        }
        catch (Exception ex) { Bootstrap.Log("自制子弹注册异常: " + ex.Message); return false; }
    }

    // ==================== 模板加载 / 克隆 ====================

    /// <summary>按名加载模板子弹 config。优先 TowerDefenseManager.GetProjectileConfig（与 GameCheats 同路径），
    /// 兜底反射 ResourceManager.PROJECTILE_RESOURCE static 字段（名字→res 路径）→ ResourceLoader.Load。
    /// 探测式，失败返回 null 不抛。</summary>
    static object LoadTemplateProjectile(string name)
    {
        try
        {
            if (string.IsNullOrEmpty(name)) return null;
            // ① GetProjectileConfig（战斗内字典解析，GameCheats.RandomProjectileConfig 主路径）
            var tdm = FindType("TowerDefenseManager");
            if (tdm != null)
            {
                var m = tdm.GetMethod("GetProjectileConfig", new Type[] { typeof(string) });
                if (m != null)
                {
                    try { var r = m.Invoke(null, new object[] { name }); if (r != null) return r; } catch { }
                }
            }
            // ② PROJECTILE_RESOURCE.Data 路径兜底
            var rmType = FindType("ResourceManager");
            if (rmType == null) return null;
            var pr = rmType.GetField("PROJECTILE_RESOURCE", BindingFlags.NonPublic | BindingFlags.Static);
            var json = pr != null ? pr.GetValue(null) : null;
            if (json == null) return null;
            var dataP = json.GetType().GetProperty("Data", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var data = dataP != null ? dataP.GetValue(json) : null;
            if (data is System.Collections.IDictionary d && d.Contains(name))
            {
                object v = null;
                try { v = d[name]; } catch { }
                string path = v as string;
                if (string.IsNullOrEmpty(path) && v != null) { try { path = v.ToString(); } catch { } }
                if (!string.IsNullOrEmpty(path))
                    return Godot.ResourceLoader.Load(path);
            }
        }
        catch (Exception ex) { Bootstrap.Log("自制子弹 模板加载异常: " + ex.Message); }
        return null;
    }

    /// <summary>克隆 config（反射 Duplicate(bool)）。失败返回 null。</summary>
    static object DuplicateProjectile(object cfg)
    {
        try
        {
            var dm = cfg.GetType().GetMethod("Duplicate", new Type[] { typeof(bool) });
            if (dm != null) return dm.Invoke(cfg, new object[] { true });
        }
        catch { }
        return null;
    }

    // ==================== 字段反射（探测式） ====================

    /// <summary>设置数值字段（double/float/int 自动转类型）。命中返回 true。</summary>
    static bool SetNum(object obj, string field, double val)
    {
        try
        {
            var f = FindField(obj, field);
            if (f == null) return false;
            if (f.FieldType == typeof(double)) { f.SetValue(obj, val); return true; }
            if (f.FieldType == typeof(float))  { f.SetValue(obj, (float)val); return true; }
            if (f.FieldType == typeof(int))    { f.SetValue(obj, (int)val); return true; }
            return false;
        }
        catch { return false; }
    }

    /// <summary>设置字符串字段。命中返回 true。</summary>
    static bool SetStr(object obj, string field, string val)
    {
        try
        {
            var f = FindField(obj, field);
            if (f == null) return false;
            if (f.FieldType == typeof(string)) { f.SetValue(obj, val); return true; }
            return false;
        }
        catch { return false; }
    }

    /// <summary>查找字段（含 "_" 前缀）。未命中返回 null。</summary>
    static FieldInfo FindField(object obj, string field)
    {
        var t = obj.GetType();
        foreach (var name in new[] { field, "_" + field })
        {
            var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) return f;
        }
        return null;
    }

    /// <summary>字段优先、属性兜底取值（场景/资源引用字段可能是导出属性）。失败返回 null。</summary>
    static object GetMember(object obj, string name)
    {
        try
        {
            var f = FindField(obj, name);
            if (f != null) return f.GetValue(obj);
            var p = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (p != null) { try { return p.GetValue(obj); } catch { } }
        }
        catch { }
        return null;
    }

    /// <summary>scale 是 Vector2 字段（SnowBulletDefault.tres 有 scale=Vector2），单独按比例设置。</summary>
    static void ApplyBulletScale(object cfg, CustomBulletDef def)
    {
        try
        {
            var f = FindField(cfg, "scale");
            if (f == null) return;
            if (f.FieldType == typeof(Godot.Vector2)) f.SetValue(cfg, new Godot.Vector2((float)def.Scale, (float)def.Scale));
            else if (f.FieldType == typeof(Godot.Vector2?)) f.SetValue(cfg, new Godot.Vector2((float)def.Scale, (float)def.Scale));
        }
        catch { }
    }

    // ==================== 换图（实例级探测） ====================

    /// <summary>加载 PNG 为 ImageTexture（dir 绝对目录 + file 文件名）。与 CustomPlantManager.LoadTex 同实现（该方法 private，此处自实现）。</summary>
    static Godot.Texture2D LoadTex(string dir, string file)
    {
        try
        {
            var p = dir.Length == 0 ? file : dir + "/" + file;
            if (!Godot.FileAccess.FileExists(p)) return null;
            var img = new Godot.Image();
            var err = img.Load(p);   // Godot 4 Image.Load 支持 OS 绝对路径
            if (err != Godot.Error.Ok) { Bootstrap.Log("自制子弹: 图片加载失败 " + p + " " + err); return null; }
            return Godot.ImageTexture.CreateFromImage(img);
        }
        catch (Exception ex) { Bootstrap.Log("自制子弹: 贴图异常 " + ex.Message); return null; }
    }

    /// <summary>替换 config 的 projectileScene（PackedScene）实例内 Sprite 贴图（bullet.png）→ QueueFree（实例级）。</summary>
    static void ApplyBulletTexture(object cfg, CustomBulletDef def)
    {
        try
        {
            if (string.IsNullOrEmpty(def.Dir)) { Bootstrap.Log("自制子弹: 无 Dir（换图跳过）"); return; }
            var tex = LoadTex(def.Dir, "bullet.png");
            if (tex == null) { Bootstrap.Log("自制子弹: 子弹图缺失 " + def.Dir + "/bullet.png"); return; }
            if (!(GetMember(cfg, "projectileScene") is Godot.PackedScene packed))
            {
                Bootstrap.Log("自制子弹: 无 projectileScene（换图跳过）");
                return;
            }
            var root = packed.Instantiate();
            ReplaceSpriteTextures(root, tex, (float)def.Scale);
            root.QueueFree();
        }
        catch (Exception ex) { Bootstrap.Log("自制子弹 换图异常: " + ex.Message); }
    }

    /// <summary>实例级递归替换：Sprite2D 直设纹理，非 Sprite2D 反射兜底（类型名含 Sprite 且带 Texture）。</summary>
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
                TrySetTextureByReflection(child, tex, new Godot.Vector2(scale, scale));
            }
            ReplaceSpriteTextures(child, tex, scale);
        }
    }

    /// <summary>反射兜底：类型名含 "Sprite" 且带 Texture 的节点，字段优先 SetValue，否则属性经 Setter 方法（避免 PropertyInfo.SetValue 在 AOT 抛 MissingMethod）。</summary>
    static void TrySetTextureByReflection(object node, Godot.Texture2D tex, Godot.Vector2 scale)
    {
        try
        {
            var t = node.GetType();
            if (!t.Name.Contains("Sprite")) return;
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            // Texture：字段优先，否则属性经 Setter 方法
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

    // ==================== 效果（探测式，字段名以运行时 ProbeLog 更新） ====================

    /// <summary>
    /// 效果挂接：burn/freeze/stun 探测 cfg 的 buffList/damageFlags 字段设置；explode 探测 hitPesontage/rangeSize/damageFlags。
    /// 全部探测式：找不到仅日志不抛。split 不做（已知限制）。
    /// 注：burn/freeze/stun 在 0.27 的 config 上是经 hitTargetEventList/hitCharacterEventList 里的
    /// TowerDefenseCharacterEventAddBuff（带 buffList）挂接的，直接字段探测大概率未命中——运行时 ProbeLog 会
    /// 打印 config 顶层字段名，据此更新候选字段即可。
    /// </summary>
    static void ApplyBulletEffect(object cfg, CustomBulletDef def)
    {
        try
        {
            string fx = string.IsNullOrEmpty(def.Effect) ? "none" : def.Effect.ToLowerInvariant();
            switch (fx)
            {
                case "burn":
                case "freeze":
                case "stun":
                {
                    bool hasBuffList = FindField(cfg, "buffList") != null;
                    bool setFlags = SetNum(cfg, "damageFlags", def.EffectValue);
                    ProbeBulletFieldsOnce(cfg, def.Template);
                    Bootstrap.Log("自制子弹: 效果 " + fx + " 挂接尝试 buffList=" + (hasBuffList ? "命中" : "无")
                        + " damageFlags=" + (setFlags ? "命中" : "无/失败") + "（buffList 为资源数组时需运行时探测更新）");
                    break;
                }
                case "explode":
                {
                    bool p = SetNum(cfg, "hitPesontage", def.EffectValue);
                    bool r = SetNum(cfg, "rangeSize", def.EffectValue);   // rangeSize 为 Vector2，SetNum 会失败→仅日志
                    bool f = SetNum(cfg, "damageFlags", def.EffectValue);
                    ProbeBulletFieldsOnce(cfg, def.Template);
                    Bootstrap.Log("自制子弹: explode 范围 hitPesontage=" + (p ? "命中" : "无/失败")
                        + " rangeSize=" + (r ? "命中" : "无/失败") + " damageFlags=" + (f ? "命中" : "无/失败"));
                    break;
                }
                case "split":
                    Bootstrap.Log("自制子弹: split 分裂效果=已知限制(未实现)");
                    break;
                default:
                    break;   // none
            }
        }
        catch (Exception ex) { Bootstrap.Log("自制子弹 效果异常: " + ex.Message); }
    }

    /// <summary>探测：打印 config 顶层字段名（每模板一次），供运行期核对候选字段名。</summary>
    static void ProbeBulletFieldsOnce(object cfg, string template)
    {
        try
        {
            var key = template + "|" + cfg.GetType().Name;
            if (!_probeLogged.Add(key)) return;
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var names = new List<string>();
            foreach (var f in cfg.GetType().GetFields(flags))
            {
                if (names.Count >= 40) break;
                names.Add(f.Name);
            }
            Bootstrap.Log("自制子弹探测: [" + template + ":" + cfg.GetType().Name + "] " + string.Join(",", names));
        }
        catch { }
    }
    // ==================== splat 特效（SP8，探测式） ====================

    /// <summary>
    /// 命中特效挂接（探测式）：MVP 设置 splatSceneType 字符串（若 Splat 非 none）；
    /// 若 Splat 映射到游戏特效场景则替换 cfg.splatScene（PackedScene）。失败仅日志不抛。
    /// 注：lightning 无对应 Splats 场景，仅设类型字符串；splatSceneType 值语义以运行期 ProbeLog 为准。
    /// </summary>
    static void ApplyBulletSplat(object cfg, CustomBulletDef def)
    {
        try
        {
            string sp = string.IsNullOrEmpty(def.Splat) ? "none" : def.Splat.ToLowerInvariant();
            bool st = false;
            if (sp != "none")
                st = SetStr(cfg, "splatSceneType", def.Splat);   // MVP：splatSceneType 字符串探测
            bool sc = false;
            string path = SplatScenePath(sp);
            if (!string.IsNullOrEmpty(path))
            {
                var packed = Godot.ResourceLoader.Load(path) as Godot.PackedScene;
                if (packed != null) sc = SetScene(cfg, "splatScene", packed);
            }
            if (sp != "none" || !string.IsNullOrEmpty(path))
                Bootstrap.Log("自制子弹: splat=" + sp + " splatSceneType=" + (st ? "命中" : "无") + " splatScene=" + (sc ? "命中" : "无/失败"));
        }
        catch (Exception ex) { Bootstrap.Log("自制子弹 splat 异常: " + ex.Message); }
    }

    /// <summary>splat 特效 → 游戏特效场景路径映射（Prefab/Particles/Splats/ 探测）。未知返回 null（仅设类型不换场景）。</summary>
    static string SplatScenePath(string sp)
    {
        switch (sp)
        {
            case "fire":    return "res://Prefab/Particles/Splats/FireSplats/FireSplats.tscn";
            case "freeze":  return "res://Prefab/Particles/Splats/SnowPeaSplats/SnowPeaSplats.tscn";
            case "explode": return "res://Prefab/Particles/Splats/PeaBombSplats/PeaBombSplats.tscn";
            default:        return null;   // none/lightning/未知：仅设 splatSceneType，不换场景
        }
    }

    /// <summary>设置 PackedScene 字段。命中返回 true。</summary>
    static bool SetScene(object obj, string field, Godot.PackedScene val)
    {
        try
        {
            var f = FindField(obj, field);
            if (f == null) return false;
            if (typeof(Godot.PackedScene).IsAssignableFrom(f.FieldType)) { f.SetValue(obj, val); return true; }
        }
        catch { }
        return false;
    }
    // ==================== 手写 JSON 解析（Godot.Json） ====================

    static CustomBulletDef ParseBulletDef(string text)
    {
        try
        {
            var v = Godot.Json.ParseString(text);
            if (v.VariantType != Godot.Variant.Type.Dictionary) return null;
            var d = v.AsGodotDictionary();
            if (!TryGetStr(d, "name", out string name)) return null;
            var def = new CustomBulletDef
            {
                Name = name,
                Template = GetStr(d, "template", "Pea"),
                Damage = GetNum(d, "damage", 40),
                Speed = GetNum(d, "speed", 600),
                Range = GetNum(d, "range", 2000),
                Penetrate = (int)GetNum(d, "penetrate", 0),
                Scale = GetNum(d, "scale", 1.0),
                Effect = GetStr(d, "effect", "none"),
                EffectValue = GetNum(d, "effectValue", 2.0),
                Audio = GetStr(d, "audio", "Ignite"),
                Splat = GetStr(d, "splat", "none"),
            };
            return def;
        }
        catch { return null; }
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
}

/// <summary>自制子弹定义（config.json 数据）。字段名与 spec/plan 一致。</summary>
public sealed class CustomBulletDef
{
    public string Name = "";        // 子弹名（注册 key）
    public string Template = "Pea"; // 克隆自哪个现有子弹
    public double Damage = 40;      // baseDamage
    public double Speed = 600;      // 子弹速度
    public double Range = 2000;     // 射程
    public int Penetrate = 0;       // 穿透数
    public double Scale = 1.0;      // 大小
    public string Effect = "none";  // none/burn/freeze/stun/explode
    public double EffectValue = 2.0;
    public string Audio = "Ignite"; // 命中音效
    public string Splat = "none";  // 命中特效 none/explode/freeze/lightning/fire
    public string Dir = "";
}
