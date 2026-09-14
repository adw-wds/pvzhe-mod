using System;

namespace PvzheMod;

/// <summary>
/// 统一项目路由（子项目3）：plant/zombie/bullet/card 的 Load/List/Remove 统一分发。
/// 目录约定：custom_projects\&lt;type&gt;\&lt;name&gt;\config.json；
/// plant 兼容旧 custom_plants（存在则优先）。
/// AOT 纪律：禁 System.IO/Path/System.Text.Json；目录探测用 Godot.DirAccess（绝对路径）。
/// </summary>
public static class CustomProjectManager
{
    /// <summary>统一项目根目录（与 WPF 设计器约定一致）。</summary>
    public const string BaseDir = "自定义项目";   // ★ 开源版占位：改成你自己的自定义项目目录（可用绝对路径）

    /// <summary>type → 项目根目录。plant 兼容旧 custom_plants（若存在）。</summary>
    public static string GetTypeDir(string type)
    {
        switch (type)
        {
            case "plant":
                // 旧目录兼容：custom_plants 存在则用旧的，否则统一目录
                if (Godot.DirAccess.DirExistsAbsolute(BaseDir + "\\custom_plants"))
                    return BaseDir + "\\custom_plants";
                return BaseDir + "\\custom_projects\\plant";
            case "zombie":
                return BaseDir + "\\custom_projects\\zombie";
            case "bullet":
                return BaseDir + "\\custom_projects\\bullet";
            case "card":
                return BaseDir + "\\custom_projects\\card";
            default:
                return BaseDir + "\\custom_projects\\" + type;
        }
    }

    /// <summary>项目 config.json 完整路径：&lt;typeDir&gt;\&lt;name&gt;\config.json。</summary>
    public static string ResolveProjectPath(string type, string name)
    {
        return GetTypeDir(type) + "\\" + name + "\\config.json";
    }

    // ==================== 统一命令路由（RemoteServer 用） ====================

    /// <summary>CustomProjectLoad：按 type 分发到对应 Manager.Load(file)。type 未知返回 err:bad-type。</summary>
    public static string Load(string type, string file)
    {
        switch (type)
        {
            case "plant": return CustomPlantManager.Load(file);
            case "bullet": return CustomBulletManager.CustomBulletLoad(file);
            case "zombie": return CustomZombieManager.Load(file);
            case "card": return CustomCardManager.Load(file);
            default: return "err:bad-type";
        }
    }

    /// <summary>CustomProjectList：按 type 分发到对应 Manager 的列表。type 未知返回 "[]"。</summary>
    public static string List(string type)
    {
        switch (type)
        {
            case "plant": return CustomPlantManager.ListJson();
            case "bullet": return CustomBulletManager.CustomBulletList();
            case "zombie": return CustomZombieManager.List();
            case "card": return CustomCardManager.List();
            default: return "[]";
        }
    }

    /// <summary>CustomProjectRemove：按 type 分发到对应 Manager.Remove(name)。type 未知返回 err:bad-type。</summary>
    public static string Remove(string type, string name)
    {
        switch (type)
        {
            case "plant": return CustomPlantManager.Remove(name);
            case "bullet": return CustomBulletManager.CustomBulletRemove(name);
            case "zombie": return CustomZombieManager.Remove(name);
            case "card": return CustomCardManager.Remove(name);
            default: return "err:bad-type";
        }
    }

    /// <summary>扫描 custom_projects\&lt;type&gt;（plant 兼容旧 custom_plants）自动加载全部项目。幂等（Load 覆盖语义）。</summary>
    public static string AutoLoadAll()
    {
        int ok = 0, fail = 0;
        var types = new[] { "plant", "zombie", "bullet", "card" };
        foreach (var type in types)
        {
            var dir = GetTypeDir(type);
            if (!Godot.DirAccess.DirExistsAbsolute(dir)) continue;
            var d = Godot.DirAccess.Open(dir);
            if (d == null) continue;
            var dirs = d.GetDirectories();
            for (int i = 0; i < dirs.Length; i++)
            {
                var cfg = dir + "/" + dirs[i] + "/config.json";
                if (!Godot.FileAccess.FileExists(cfg)) continue;
                var r = Load(type, cfg);
                if (r == "ok") ok++; else fail++;
            }
        }
        Bootstrap.Log("自定义项目 自动加载: 成功 " + ok + " 失败 " + fail);
        return ok + "/" + fail;
    }

    // ==================== 卡牌图标（SP8，icon.png） ====================

    /// <summary>若项目目录有 icon.png 则替换卡牌图标（探测 GetPacketSprite 机制；失败仅日志）。</summary>
    public static void ApplyCardIcon(string dir, object cfg)
    {
        try
        {
            var iconPath = dir + "/icon.png";
            if (!Godot.FileAccess.FileExists(iconPath)) return;
            var img = new Godot.Image();
            if (img.Load(iconPath) != Godot.Error.Ok) return;
            var tex = Godot.ImageTexture.CreateFromImage(img);
            // 探测卡牌图标字段：packetIcon/texture/icon 等；或走 GameCheats 图标导出机制替换
            SetTextureField(cfg, tex);
            Bootstrap.Log("自定义项目 卡牌图标应用: " + dir);
        }
        catch (Exception ex) { Bootstrap.Log("自定义项目 卡牌图标异常: " + ex.Message); }
    }

    /// <summary>探测并设置卡牌图标 Texture2D 字段（icon/packetIcon/texture/_icon/cardIcon）。</summary>
    static void SetTextureField(object obj, Godot.Texture2D tex)
    {
        foreach (var n in new[] { "icon", "packetIcon", "texture", "_icon", "cardIcon" })
        {
            var f = obj.GetType().GetField(n, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (f != null && (f.FieldType == typeof(Godot.Texture2D) || f.FieldType.IsAssignableFrom(typeof(Godot.Texture2D))))
            { f.SetValue(obj, tex); return; }
        }
    }
}
