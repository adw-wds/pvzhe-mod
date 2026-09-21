using System;
using System.Collections.Generic;

namespace PvzheMod.Mod;

/// <summary>MOD 清单（mod.json 解析结果）。纯数据，零 Godot 依赖。</summary>
public sealed class ModMeta
{
    public string Id;
    public string Name;
    public string Version;
    public string Author;
    public string Description;
    /// <summary>MOD 程序集文件名（包内相对路径）。</summary>
    public string AssemblyName;
    /// <summary>MOD 入口类型全名，必须实现 PvzMod.IMod。</summary>
    public string EntryType;
    /// <summary>该 MOD 所有类型的命名空间前缀（MOD 间类型不冲突的硬保证）。</summary>
    public string NamespacePrefix;
    public string GameVersionRaw;
    public string LoaderVersionRaw;
    public List<KeyValuePair<string, string>> Dependencies = new List<KeyValuePair<string, string>>();
    public List<string> LoadAfter = new List<string>();
    public List<string> ContentTypes = new List<string>();
    public ModVersion VersionValue;
    /// <summary>默认"任意"：非解析器构造的 ModMeta 也不会因 null 崩溃。</summary>
    public ModVersionConstraint GameVersion = ModVersionConstraint.Any();
    public ModVersionConstraint LoaderVersion = ModVersionConstraint.Any();

    /// <summary>mod.json 里声明的 schema（1 = 旧格式，2 = 支持声明补丁）。</summary>
    public int Schema = 1;

    /// <summary>
    /// schema≥2 的声明补丁。**由安装器的 ModPatchSource.Attach 填充**，
    /// 不是 ModMetaParser 填的（原因见 PatchNodes）。
    /// </summary>
    public List<ModPatchDecl> Patches = new List<ModPatchDecl>();

    /// <summary>
    /// patches 数组的**原始 JSON 节点**，只做到"取出来 + 确认是对象"。
    /// 具体字段解析交给安装器的 <c>ModPatchSource</c>：
    /// ModMeta 会被编译进**游戏内 MOD 程序集**，那里根本没有 ModPatchSource，
    /// 把解析放这里会逼出一个错误方向的依赖（游戏内代码依赖安装器）。
    /// </summary>
    public List<ModJsonValue> PatchNodes = new List<ModJsonValue>();

    public string Display => Name + " " + Version + "  (" + Author + ")";
}

public sealed class ModMetaParseResult
{
    public bool Ok;
    public ModMeta Meta;
    public string Error;
}

public static class ModMetaParser
{
    /// <summary>
    /// 本加载器支持的最高 schema。
    /// **schema=1 的老 MOD 必须继续能装**（向后兼容），所以判定是 "MinSchema..SupportedSchema"，
    /// 而不是"必须等于 SupportedSchema"。
    /// </summary>
    public const int SupportedSchema = 2;

    /// <summary>支持的最低 schema（老包）。</summary>
    public const int MinSchema = 1;
    private const int MaxIdLen = 64;
    private const int MaxNameLen = 40;
    private const int MaxAuthorLen = 60;
    private const int MaxDescLen = 200;

    public static ModMetaParseResult Fail(string message)
        => new ModMetaParseResult { Ok = false, Error = "[MOD] " + message };

    public static ModMetaParseResult Parse(string jsonText)
    {
        ModJsonValue root;
        try
        {
            root = ModJson.Parse(jsonText);
        }
        catch (FormatException ex)
        {
            return Fail("mod.json 解析失败: " + ex.Message);
        }

        if (root.Kind != ModJsonKind.Object) return Fail("mod.json 根节点必须是对象");

        var schema = root.Get("schema");
        if (schema == null || schema.Kind != ModJsonKind.Number) return Fail("缺少必需字段: schema");
        var schemaVersion = schema.AsInt(0);
        if (schemaVersion < MinSchema || schemaVersion > SupportedSchema)
            return Fail("不支持的 schema=" + schemaVersion + "（本加载器支持 " + MinSchema + ".." + SupportedSchema + "）");

        var id = Str(root, "id");
        if (string.IsNullOrEmpty(id)) return Fail("缺少必需字段: id");
        if (id.Length > MaxIdLen) return Fail("字段 id 过长（≤" + MaxIdLen + "）");
        for (var i = 0; i < id.Length; i++)
        {
            var c = id[i];
            var ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-';
            if (!ok) return Fail("字段 id 含非法字符 '" + c + "'（仅允许 a-z 0-9 . _ -）");
        }

        var name = Str(root, "name");
        if (string.IsNullOrEmpty(name)) return Fail("缺少必需字段: name");
        if (name.Length > MaxNameLen) return Fail("字段 name 过长（≤" + MaxNameLen + "）");

        var version = Str(root, "version");
        if (string.IsNullOrEmpty(version)) return Fail("缺少必需字段: version");

        ModVersion versionValue;
        try { versionValue = ModVersion.Parse(version); }
        catch (FormatException) { return Fail("字段 version 不是合法版本号: " + version); }

        var author = Str(root, "author");
        if (string.IsNullOrEmpty(author)) return Fail("缺少必需字段: author");
        if (author.Length > MaxAuthorLen) return Fail("字段 author 过长（≤" + MaxAuthorLen + "）");

        var asmName = Str(root, "assembly");
        if (string.IsNullOrEmpty(asmName)) return Fail("缺少必需字段: assembly");
        if (asmName.IndexOf('/') >= 0 || asmName.IndexOf('\\') >= 0)
            return Fail("字段 assembly 只能是文件名，不能带路径: " + asmName);

        // entryType 是**可选**的：零依赖 MOD 只需写约定的 OnModLoad/OnModFrame 静态方法，
        // 安装器会按 namespacePrefix 自动扫描入口；填了则走旧的 IMod 契约路径。
        var entryType = Str(root, "entryType");

        var nsPrefix = Str(root, "namespacePrefix");
        if (string.IsNullOrEmpty(nsPrefix)) return Fail("缺少必需字段: namespacePrefix");
        for (var i = 0; i < nsPrefix.Length; i++)
        {
            var c = nsPrefix[i];
            var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                     || c == '.' || c == '_';
            if (!ok) return Fail("字段 namespacePrefix 含非法字符 '" + c + "'（仅允许字母数字 . _）");
        }

        var desc = Str(root, "description");
        if (desc != null && desc.Length > MaxDescLen) desc = desc.Substring(0, MaxDescLen);

        var gameRaw = Str(root, "gameVersion");
        // 新平台清单用 hostVersion；兼容旧字段 modLoader
        var loaderRaw = Str(root, "hostVersion");
        if (string.IsNullOrEmpty(loaderRaw)) loaderRaw = Str(root, "modLoader");
        ModVersionConstraint gameC, loaderC;
        try { gameC = ModVersionConstraint.Parse(gameRaw); }
        catch (FormatException ex) { return Fail("字段 gameVersion 非法: " + ex.Message); }
        try { loaderC = ModVersionConstraint.Parse(loaderRaw); }
        catch (FormatException ex) { return Fail("字段 modLoader 非法: " + ex.Message); }

        var meta = new ModMeta
        {
            Id = id,
            Name = name,
            Version = version,
            Author = author,
            Description = desc,
            AssemblyName = asmName,
            EntryType = entryType,
            NamespacePrefix = nsPrefix,
            GameVersionRaw = gameRaw,
            LoaderVersionRaw = loaderRaw,
            VersionValue = versionValue,
            GameVersion = gameC,
            LoaderVersion = loaderC,
        };

        var deps = root.Get("dependencies");
        if (deps != null)
        {
            if (deps.Kind != ModJsonKind.Object) return Fail("字段 dependencies 必须是对象");
            for (var i = 0; i < deps.Count; i++)
            {
                var key = deps.KeyAt(i);
                var val = deps.Item(i);
                var constraint = val != null && val.Kind == ModJsonKind.String ? val.AsString() : "";
                try { ModVersionConstraint.Parse(constraint); }
                catch (FormatException ex) { return Fail("依赖 " + key + " 的版本约束非法: " + ex.Message); }
                meta.Dependencies.Add(new KeyValuePair<string, string>(key, constraint ?? ""));
            }
        }

        var after = root.Get("loadAfter");
        if (after != null)
        {
            if (after.Kind != ModJsonKind.Array) return Fail("字段 loadAfter 必须是数组");
            for (var i = 0; i < after.Count; i++)
            {
                var s = after.Item(i).Kind == ModJsonKind.String ? after.Item(i).AsString() : null;
                if (!string.IsNullOrEmpty(s)) meta.LoadAfter.Add(s);
            }
        }

        var types = root.Get("contentTypes");
        if (types != null)
        {
            if (types.Kind != ModJsonKind.Array) return Fail("字段 contentTypes 必须是数组");
            for (var i = 0; i < types.Count; i++)
            {
                var s = types.Item(i).Kind == ModJsonKind.String ? types.Item(i).AsString() : null;
                if (!string.IsNullOrEmpty(s)) meta.ContentTypes.Add(s);
            }
        }

        meta.Schema = schemaVersion;

        var patches = root.Get("patches");
        if (patches != null)
        {
            // 声明了 patches 但没升 schema → **必须报错，不能默默忽略**。
            // 静默失效是最难查的一类 bug：MOD 作者以为补丁生效了，实际什么都没发生。
            if (schemaVersion < 2)
                return Fail("声明了 patches 就必须把 schema 升到 2（当前 schema=" + schemaVersion
                          + "），否则补丁会被静默忽略");

            if (patches.Kind != ModJsonKind.Array) return Fail("字段 patches 必须是数组");
            for (var i = 0; i < patches.Count; i++)
            {
                var item = patches.Item(i);
                if (item == null || item.Kind != ModJsonKind.Object)
                    return Fail("patches[" + i + "] 必须是对象");
                meta.PatchNodes.Add(item);
            }
        }

        return new ModMetaParseResult { Ok = true, Meta = meta };
    }

    private static string Str(ModJsonValue obj, string key)
    {
        var v = obj.Get(key);
        return v != null && v.Kind == ModJsonKind.String ? v.AsString() : null;
    }
}
