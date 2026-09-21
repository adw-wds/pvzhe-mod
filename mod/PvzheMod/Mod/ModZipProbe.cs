using System;
using System.Collections.Generic;
using Godot;

namespace PvzheMod.Mod;

/// <summary>
/// Task 1 临时探针。验证 AOT 裁剪 + 注入环境下的两类可用性：
///   1) Godot.ZipReader 能否读取 .pvzmod（决定 Task 7 走"游戏内解包"还是"仅目录包"）
///   2) Task 2~5 纯逻辑层能否真实运行 —— 重点验 HashSet / Dictionary(StringComparer)
///      / List.Sort / string.Join / Encoding.UTF8.GetString（仓库记忆里这些有运行时炸的历史）
/// 结论写 mod_log.txt。验证完成后本文件可整文件删除。
/// </summary>
public static class ModZipProbe
{
    public static string Run(string zipAbsPath)
    {
        Bootstrap.Log("[MOD] ===== probe start =====");
        string zip = ProbeZip(zipAbsPath);
        string logic = ProbeLogic();
        string line = "[MOD] probe zip=" + zip + " || logic=" + logic;
        Bootstrap.Log(line);
        Bootstrap.Log("[MOD] ===== probe end =====");
        return line;
    }

    // 不用委托/lambda：仓库记忆记录过 MOD 内委托派发会抛 EntryPointNotFoundException
    static string ProbeZip(string zipAbsPath)
    {
        try
        {
            if (string.IsNullOrEmpty(zipAbsPath)) return "err:no-path";
            if (!Godot.FileAccess.FileExists(zipAbsPath)) return "err:no-file";

            var zr = new Godot.ZipReader();
            var err = zr.Open(zipAbsPath);
            if (err != Error.Ok) return "err:open-" + err;

            var files = zr.GetFiles();
            var list = "";
            for (var i = 0; i < files.Length; i++)
            {
                if (i > 0) list += ";";
                list += files[i];
            }

            var bytes = zr.ReadFile("payload/hello.txt");
            var len = bytes == null ? -1 : bytes.Length;

            string text;
            try
            {
                text = bytes == null ? "<null>" : System.Text.Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex)
            {
                text = "ENC-EX:" + ex.Message;
            }

            // 顺带验一下缺失条目是否安全返回 null（Task 7 会依赖这个行为）
            var missing = zr.ReadFile("payload/not_exist.txt");

            zr.Close();
            return "ok files=" + files.Length + "[" + list + "] len=" + len
                 + " text=" + text + " missing=" + (missing == null ? "null" : "NOT-null");
        }
        catch (Exception ex)
        {
            Bootstrap.Log("[MOD] probe EX zip: " + ex.Message);
            Bootstrap.Log("[MOD] probe EX zip st: " + ex.StackTrace);
            return "EX:" + ex.Message;
        }
    }

    static string ProbeLogic()
    {
        try
        {
            // --- 1) 清单解析（ModJson 手写解析器 + 校验 + 依赖约束） ---
            string cfgA = "{\"schema\":1,\"id\":\"probe.a\",\"name\":\"探针A\",\"version\":\"1.0.0\","
                        + "\"author\":\"probe\",\"description\":\"d\","
                        + "\"dependencies\":{\"probe.b\":\">=1.0\"},\"loadAfter\":[\"probe.b\"],"
                        + "\"contentTypes\":[\"plant\"]}";
            string cfgB = "{\"schema\":1,\"id\":\"probe.b\",\"name\":\"探针B\",\"version\":\"1.2\",\"author\":\"probe\"}";

            var ra = ModMetaParser.Parse(cfgA);
            if (!ra.Ok) return "metaA-err:" + ra.Error;
            var rb = ModMetaParser.Parse(cfgB);
            if (!rb.Ok) return "metaB-err:" + rb.Error;

            // 顺带验非法清单是否被正确拒绝（错误串必须带 [MOD] 前缀）
            var bad = ModMetaParser.Parse("{\"schema\":1,\"id\":\"BAD ID\",\"name\":\"x\",\"version\":\"1\",\"author\":\"y\"}");
            var badInfo = bad.Ok ? "BAD-ACCEPTED" : (bad.Error != null && bad.Error.StartsWith("[MOD]") ? "rejected" : "wrong-prefix");

            // --- 2) 排序 + 冲突（HashSet / Dictionary / List.Sort / StringComparer.Ordinal） ---
            var items = new List<ModPlanItem>();
            var ia = new ModPlanItem { Id = "probe.a", Meta = ra.Meta };
            ia.ContentKeys.Add("plant/探针花");
            ia.ContentKeys.Add("plant/共享花");
            var ib = new ModPlanItem { Id = "probe.b", Meta = rb.Meta };
            ib.ContentKeys.Add("plant/共享花");
            items.Add(ia);      // 故意乱序：a 依赖 b，但 a 先入列表
            items.Add(ib);

            var plan = ModPlanner.Build(items, "1.0");
            var order = "";
            for (var i = 0; i < plan.Order.Count; i++)
            {
                if (i > 0) order += ",";
                order += plan.Order[i].Id;
            }
            var conf = plan.Conflicts.ContainsKey("plant/共享花") ? plan.Conflicts["plant/共享花"] : "<none>";
            var lose = plan.Losers.ContainsKey("plant/共享花") ? plan.Losers["plant/共享花"] : "<none>";
            var keptA = plan.Order.Count > 0 ? plan.Order[0].ContentKeys.Count : -1;

            // --- 3) 环检测（string.Join + 分组用的 HashSet/Dictionary） ---
            var c1 = new ModPlanProblemKind[0];   // 仅确认枚举可用
            var cyc = new List<ModPlanItem>();
            var m1 = new ModMeta { Id = "cyc.1", Name = "c1", Version = "1.0", Author = "p", VersionValue = ModVersion.Parse("1.0") };
            m1.Dependencies.Add(new KeyValuePair<string, string>("cyc.2", ""));
            var m2 = new ModMeta { Id = "cyc.2", Name = "c2", Version = "1.0", Author = "p", VersionValue = ModVersion.Parse("1.0") };
            m2.Dependencies.Add(new KeyValuePair<string, string>("cyc.1", ""));
            cyc.Add(new ModPlanItem { Id = "cyc.1", Meta = m1 });
            cyc.Add(new ModPlanItem { Id = "cyc.2", Meta = m2 });
            var cplan = ModPlanner.Build(cyc, "1.0");
            var cycleMsg = cplan.Problems.Count > 0 ? cplan.Problems[0].Detail : "<none>";

            // --- 4) 版本比较与约束 ---
            var cmp = ModVersion.Parse("1.2.3").CompareTo(ModVersion.Parse("1.2"));

            return "ok order=[" + order + "] problems=" + plan.Problems.Count
                 + " keptA=" + keptA + " conflict=" + conf + "/" + lose
                 + " deps=" + ra.Meta.Dependencies.Count
                 + " disp=" + ra.Meta.Display
                 + " bad=" + badInfo
                 + " cycOrder=" + cplan.Order.Count + " cycProbs=" + cplan.Problems.Count
                 + " cycMsg=" + cycleMsg
                 + " cmp=" + cmp
                 + " sat=" + ModVersionConstraint.Parse(">=1.0").Satisfies(ModVersion.Parse("1.2"))
                 + " enumCount=" + c1.Length;
        }
        catch (Exception ex)
        {
            Bootstrap.Log("[MOD] probe EX logic: " + ex.Message);
            Bootstrap.Log("[MOD] probe EX logic st: " + ex.StackTrace);
            return "EX:" + ex.Message;
        }
    }
}
