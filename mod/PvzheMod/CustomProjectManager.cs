using System;

namespace PvzheMod;

/// <summary>
/// 统一项目路由（子项目3）：plant/zombie/bullet/card 的 Load/List/Remove 统一分发。
/// 目录约定：custom_projects\&lt;type&gt;\&lt;name&gt;\config.json；
/// plant 兼容旧 CHANGE_ME_PROJECT_DIR\custom_plants（存在则优先）。
/// AOT 纪律：禁 System.IO/Path/System.Text.Json；目录探测用 Godot.DirAccess（绝对路径）。
/// </summary>
public static class CustomProjectManager
{
    /// <summary>
    /// 老路径（开发机约定）。**只作为最后兜底** —— 玩家机器上这个目录通常不存在。
    /// </summary>
    public const string LegacyBaseDir = "CHANGE_ME_PROJECT_DIR";

    /// <summary>内容根标记文件名：安装器发布二创内容时写在游戏主程序旁。</summary>
    public const string RootMarkerName = "pvzmod_content.txt";

    /// <summary>内容根目录名（游戏主程序旁）。</summary>
    public const string RootFolderName = "PvzModContent";

    static string _root;

    /// <summary>
    /// **内容根**（<c>custom_projects</c> 的父目录）。解析顺序（先命中先用）：
    ///   ① 游戏主程序旁的 <c>pvzmod_content.txt</c> —— 安装器发布二创内容时写的一行绝对路径
    ///   ② 游戏主程序旁的 <c>PvzModContent\</c>
    ///   ③ 老路径 <c>CHANGE_ME_PROJECT_DIR</c>（兼容开发机与老流程）
    ///
    /// 【为什么必须动态解析】原来这里是**硬编码** <c>CHANGE_ME_PROJECT_DIR</c>：
    /// 开发机上能跑，玩家机器上那目录不存在 → 二创植物永远加载不了，而且**没有任何报错**。
    /// 探测全部失败时仍返回老路径（行为可预期、日志可查），不会抛异常。
    /// </summary>
    public static string BaseDir
    {
        get
        {
            if (_root == null) _root = ResolveRoot();
            return _root;
        }
    }

    static string ResolveRoot()
    {
        try
        {
            var exeDir = GetExeDir();
            if (!string.IsNullOrEmpty(exeDir))
            {
                // ① 标记文件（安装器写的）
                var marker = exeDir + "\\" + RootMarkerName;
                if (Godot.FileAccess.FileExists(marker))
                {
                    try
                    {
                        var text = Godot.FileAccess.GetFileAsString(marker);
                        if (text != null)
                        {
                            text = text.Replace("\r", "").Replace("\n", "").Trim();
                            if (text.Length > 0 && text.IndexOf(':') == 1) return text.TrimEnd('\\', '/');
                        }
                    }
                    catch { }
                }

                // ② 同目录下的 PvzModContent\
                var beside = exeDir + "\\" + RootFolderName;
                if (Godot.DirAccess.DirExistsAbsolute(beside)) return beside;
            }
        }
        catch { }

        // ③ 老路径兜底
        return LegacyBaseDir;
    }

    /// <summary>游戏主程序所在目录（探测失败返回 null，调用方必须容错）。</summary>
    static string GetExeDir()
    {
        try
        {
            var p = Godot.OS.GetExecutablePath();
            if (string.IsNullOrEmpty(p)) return null;
            p = p.Replace('/', '\\');
            var i = p.LastIndexOf('\\');
            return i > 0 ? p.Substring(0, i) : null;
        }
        catch { return null; }
    }

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
        // 先把**解析出来的内容根**打出来：不打印的话，"0/0"既可能是"根对了但没项目"，
        // 也可能是"根根本没解析到"—— 两种情况的排查方向完全相反，而日志里看不出区别。
        Bootstrap.Log("自定义项目 内容根: " + BaseDir);

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
                if (r == "ok") ok++;
                else
                {
                    fail++;
                    // 失败必须带上**原因码**。只报"失败 1"等于没说：
                    // err:no-file / err:bad-config / err:bad-template 三种原因的排查方向完全不同。
                    Bootstrap.Log("自定义项目 加载失败: " + type + "/" + dirs[i] + " -> " + r);
                }
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
