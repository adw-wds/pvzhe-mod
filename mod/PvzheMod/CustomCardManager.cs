using System;
using System.Collections.Generic;
using System.Text;

namespace PvzheMod;

/// <summary>
/// 自定义卡牌（子项目3，MVP 占位）：仅解析 config.json + 缓存定义字典，不注册进游戏。
/// MVP 语义：卡牌=植物/僵尸的"卡面"绑定（displayName + 图 + 阳光），实际种植走其引用对象，
/// 完整卡牌展示/种植逻辑留 SP4 或后续扩展。
/// AOT 纪律：禁 System.IO/Path/System.Text.Json；文件 Godot.FileAccess，JSON Godot.Json 手写。
/// </summary>
public static class CustomCardManager
{
    /// <summary>卡牌名 → 解析后的 def（MVP 仅缓存定义，List/Remove 用）。</summary>
    static readonly Dictionary<string, CustomCardDef> _cards = new();

    // ==================== 命令入口 ====================

    /// <summary>CustomCardLoad：读 config.json → 解析 → 缓存 def → 返回 ok。MVP 仅存定义。</summary>
    public static string Load(string file)
    {
        try
        {
            if (!Godot.FileAccess.FileExists(file)) return "err:no-file";
            var fa = Godot.FileAccess.Open(file, Godot.FileAccess.ModeFlags.Read);
            if (fa == null) { Bootstrap.Log("自定义卡牌 Load: 无法打开 " + file); return "err:bad-config"; }
            string text = fa.GetAsText();
            fa.Close();
            var def = ParseCardDef(text);
            if (def == null || string.IsNullOrWhiteSpace(def.Name)) return "err:bad-config";
            def.Dir = GetDirOf(file);
            _cards[def.Name] = def;
            // SP8: 卡牌图标：项目目录有 icon.png 则替换卡牌图标（探测式，失败仅日志）
            CustomProjectManager.ApplyCardIcon(def.Dir, def);
            Bootstrap.Log("自定义卡牌已加载: " + def.Name + " 费用=" + def.Cost + " 冷却=" + def.Cooldown
                + " 图鉴名=" + def.DisplayName);
            return "ok";
        }
        catch (Exception ex) { Bootstrap.Log("自定义卡牌 Load 异常: " + ex.Message); return "err:bad-config"; }
    }

    /// <summary>CustomCardList：JSON [{name,cost,cooldown,displayName}]。</summary>
    public static string List()
    {
        var sb = new StringBuilder("[");
        bool first = true;
        foreach (var kv in _cards)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"name\":\"").Append(kv.Key)
              .Append("\",\"cost\":").Append(kv.Value.Cost)
              .Append(",\"cooldown\":").Append(kv.Value.Cooldown)
              .Append(",\"displayName\":\"").Append(Escape(kv.Value.DisplayName)).Append('"').Append('}');
        }
        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>CustomCardRemove：清 _cards。返回 ok/err:not-found。</summary>
    public static string Remove(string name)
    {
        return _cards.Remove(name) ? "ok" : "err:not-found";
    }

    /// <summary>已加载卡牌项目名（游戏内面板列出用）。</summary>
    public static System.Collections.Generic.List<string> LoadedNames()
    {
        return new System.Collections.Generic.List<string>(_cards.Keys);
    }

    static string Escape(string s)
    {
        return string.IsNullOrEmpty(s) ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    // ==================== 手写 JSON 解析（Godot.Json） ====================
    // 兼容统一模型（spec）：卡牌属性可放顶层（cost/cooldown/displayName/desc），也可放 "card" 子对象。

    static CustomCardDef ParseCardDef(string text)
    {
        try
        {
            var v = Godot.Json.ParseString(text);
            if (v.VariantType != Godot.Variant.Type.Dictionary) return null;
            var d = v.AsGodotDictionary();
            if (!TryGetStr(d, "name", out string name)) return null;
            var def = new CustomCardDef
            {
                Name = name,
                Cost = (int)GetNum(d, "cost", 125),
                Cooldown = GetNum(d, "cooldown", 7.5),
                DisplayName = GetStr(d, "displayName", name),
                Desc = GetStr(d, "desc", ""),
            };
            // spec 统一模型：卡牌属性在 "card" 子对象（覆盖顶层默认）
            var cd = GetDict(d, "card");
            if (cd != null)
            {
                def.Cost = (int)GetNum(cd, "cost", def.Cost);
                def.Cooldown = GetNum(cd, "cooldown", def.Cooldown);
                def.DisplayName = GetStr(cd, "displayName", def.DisplayName);
            }
            var fd = GetDict(d, "frame");
            if (fd != null)
            {
                def.Frame.Idle = GetStr(fd, "idle", "idle.png");
                def.Frame.Attack = GetStr(fd, "attack", "attack.png");
            }
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

/// <summary>自定义卡牌定义（config.json 数据）。字段名与 spec/plan 一致；Frame 复用 CustomFrameDef（卡面图标用 idle）。</summary>
public sealed class CustomCardDef
{
    public string Name = "";
    public int Cost = 125;
    public double Cooldown = 7.5;
    public string DisplayName = "";   // 图鉴名（默认=项目名）
    public string Desc = "";          // 图鉴描述
    public string Dir = "";
    public Godot.Texture2D cardIcon = null;   // SP8: 应用 icon.png 后存入（卡牌卡面图标，游戏内面板可显示）
    public CustomFrameDef Frame = new();
}
