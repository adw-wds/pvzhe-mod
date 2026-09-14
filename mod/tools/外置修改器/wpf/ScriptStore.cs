using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PvzheRemote;

/// <summary>脚本目录与元数据读写（代码存 scripts/*.csx，元数据存 scripts/scripts.json）。</summary>
public static class ScriptStore
{
    public static string Dir { get; private set; } = "";
    static string MetaPath => Path.Combine(Dir, "scripts.json");
    public static List<ScriptDef> List { get; private set; } = new();

    public static void Init()
    {
        Dir = Path.Combine(AppContext.BaseDirectory, "scripts");
        try { Directory.CreateDirectory(Dir); } catch { }
        List = Load();
    }

    static List<ScriptDef> Load()
    {
        try
        {
            if (!File.Exists(MetaPath)) return new List<ScriptDef>();
            var arr = JsonSerializer.Deserialize<List<ScriptDef>>(File.ReadAllText(MetaPath));
            return arr ?? new List<ScriptDef>();
        }
        catch { return new List<ScriptDef>(); }
    }

    public static void Save()
    {
        try { File.WriteAllText(MetaPath, JsonSerializer.Serialize(List, new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }

    public static string FullPath(ScriptDef d) =>
        string.IsNullOrWhiteSpace(d.File) ? Path.Combine(Dir, d.Name + ".csx") : Path.Combine(Dir, d.File);

    public static string ReadCode(ScriptDef d) => File.Exists(FullPath(d)) ? File.ReadAllText(FullPath(d)) : Template(d.Name);

    public static void WriteCode(ScriptDef d, string code) { try { File.WriteAllText(FullPath(d), code); } catch { } }

    /// <summary>新建脚本：生成唯一文件名与模板，返回 ScriptDef（已加入 List，未保存）。</summary>
    public static ScriptDef Create(string name, string cat)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? "新脚本" : name.Trim();
        var file = baseName + ".csx";
        int i = 2;
        while (List.Any(x => string.Equals(x.File, file, StringComparison.OrdinalIgnoreCase)))
            file = baseName + "_" + (i++) + ".csx";
        var def = new ScriptDef { Name = baseName, Cat = cat, File = file };
        WriteCode(def, Template(def.Name));
        List.Add(def);
        return def;
    }

    public static void Remove(ScriptDef d)
    {
        List.Remove(d);
        try { if (File.Exists(FullPath(d))) File.Delete(FullPath(d)); } catch { }
    }

    public static string Template(string name) =>
        "// " + name + " —— 外置修改器 C# 脚本\n" +
        "// 可用 Api（全部 async）：\n" +
        "//   await Api.CallAsync(\"/sun?add=100\")       加 100 阳光\n" +
        "//   await Api.AddPacketAsync(\"PlantCherryBomb\") 发一张樱桃卡\n" +
        "//   await Api.SpawnAsync(\"PlantPeaShooter\",2,3) 指定格种植\n" +
        "//   await Api.CmdAsync(\"KillAllZombies\")      执行命令\n" +
        "//   await Api.AddSunAsync(100)                快捷加阳光\n" +
        "// 在下面写你的代码：\n" +
        "await Api.AddSunAsync(100);\n";
}
