using System;
using System.Collections.Generic;

namespace PvzheMod.Mod;

public sealed class ModPlanItem
{
    public string Id;
    public ModMeta Meta;
    /// <summary>该 MOD 声明的内容键，形如 "plant/测试花"（type/name）。</summary>
    public List<string> ContentKeys = new List<string>();
}

public enum ModPlanProblemKind { MissingDependency, VersionMismatch, Cycle, UnknownLoadAfter }

public sealed class ModPlanProblem
{
    public ModPlanProblemKind Kind;
    public string Id;
    public string Detail;
    public override string ToString() => "[" + Kind + "] " + Id + ": " + Detail;
}

public sealed class ModPlan
{
    public List<ModPlanItem> Order = new List<ModPlanItem>();
    public List<ModPlanProblem> Problems = new List<ModPlanProblem>();
    /// <summary>内容键 → 胜出的 MOD id（先加载者胜）。</summary>
    public Dictionary<string, string> Conflicts = new Dictionary<string, string>();
    /// <summary>被冲突剔除的内容键 → 被剔除的 MOD id。</summary>
    public Dictionary<string, string> Losers = new Dictionary<string, string>();
}

/// <summary>把"已启用 MOD 集合"算成"装载顺序 + 冲突 + 问题"。纯逻辑，零 Godot/IO 依赖。</summary>
public static class ModPlanner
{
    public static ModPlan Build(IEnumerable<ModPlanItem> enabled, string loaderVersion)
    {
        var plan = new ModPlan();
        var items = new List<ModPlanItem>(enabled);
        var byId = new Dictionary<string, ModPlanItem>(StringComparer.Ordinal);
        foreach (var it in items) byId[it.Id] = it;

        var loaderV = ModVersion.Parse(loaderVersion);

        // 1) 剔除自身有问题的 MOD（加载器版本 / 依赖缺失 / 依赖版本不符）
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var it in items)
        {
            if (!it.Meta.LoaderVersion.Satisfies(loaderV))
            {
                plan.Problems.Add(new ModPlanProblem
                {
                    Kind = ModPlanProblemKind.VersionMismatch,
                    Id = it.Id,
                    Detail = "modLoader 要求 " + it.Meta.LoaderVersion + "，当前 " + loaderVersion,
                });
                excluded.Add(it.Id);
                continue;
            }
            foreach (var dep in it.Meta.Dependencies)
            {
                if (!byId.TryGetValue(dep.Key, out var target))
                {
                    plan.Problems.Add(new ModPlanProblem
                    {
                        Kind = ModPlanProblemKind.MissingDependency,
                        Id = it.Id,
                        Detail = "缺少依赖 " + dep.Key + "（" + dep.Value + "）",
                    });
                    excluded.Add(it.Id);
                    continue;
                }
                var need = ModVersionConstraint.Parse(dep.Value);
                if (!need.Satisfies(target.Meta.VersionValue))
                {
                    plan.Problems.Add(new ModPlanProblem
                    {
                        Kind = ModPlanProblemKind.VersionMismatch,
                        Id = it.Id,
                        Detail = "依赖 " + dep.Key + " 需要 " + dep.Value + "，实际 " + target.Meta.Version,
                    });
                    excluded.Add(it.Id);
                }
            }
        }

        var live = new List<ModPlanItem>();
        foreach (var it in items) if (!excluded.Contains(it.Id)) live.Add(it);

        // 2) 拓扑排序（依赖边 + loadAfter 边），同层按 id 稳定排序
        var incoming = new Dictionary<string, int>(StringComparer.Ordinal);
        var outgoing = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var it in live) { incoming[it.Id] = 0; outgoing[it.Id] = new List<string>(); }

        void AddEdge(string from, string to)
        {
            if (from == to) return;
            if (!incoming.ContainsKey(from) || !incoming.ContainsKey(to)) return;
            outgoing[from].Add(to);
            incoming[to] = incoming[to] + 1;
        }

        foreach (var it in live)
        {
            foreach (var dep in it.Meta.Dependencies) AddEdge(dep.Key, it.Id);
            foreach (var after in it.Meta.LoadAfter) AddEdge(after, it.Id);
        }

        var ready = new List<string>();
        foreach (var kv in incoming) if (kv.Value == 0) ready.Add(kv.Key);
        ready.Sort(StringComparer.Ordinal);

        var ordered = new List<ModPlanItem>();
        var done = new HashSet<string>(StringComparer.Ordinal);
        while (ready.Count > 0)
        {
            var id = ready[0];
            ready.RemoveAt(0);
            ordered.Add(byId[id]);
            done.Add(id);
            foreach (var next in outgoing[id])
            {
                incoming[next] = incoming[next] - 1;
                if (incoming[next] == 0) { ready.Add(next); ready.Sort(StringComparer.Ordinal); }
            }
        }

        // 2b) 环检测：Kahn 剩余的节点 要么自己就在环里，要么被环阻塞
        if (ordered.Count != live.Count)
        {
            var leftover = new List<string>();
            foreach (var it in live) if (!done.Contains(it.Id)) leftover.Add(it.Id);
            var leftoverSet = new HashSet<string>(leftover, StringComparer.Ordinal);

            // 每个剩余节点在剩余子图里能到达哪些节点（不含自身，除非绕回自己）
            var reach = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var u in leftover)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var stack = new List<string>();
                foreach (var v in outgoing[u]) if (leftoverSet.Contains(v)) stack.Add(v);
                while (stack.Count > 0)
                {
                    var cur = stack[stack.Count - 1];
                    stack.RemoveAt(stack.Count - 1);
                    if (!seen.Add(cur)) continue;
                    foreach (var v in outgoing[cur])
                        if (leftoverSet.Contains(v) && !seen.Contains(v)) stack.Add(v);
                }
                reach[u] = seen;
            }

            // 自己可达自己 → 在环里
            var inCycle = new HashSet<string>(StringComparer.Ordinal);
            foreach (var u in leftover) if (reach[u].Contains(u)) inCycle.Add(u);

            // 互相可达的环节点归为同一个环 → 一个环只报一条问题
            var cycleNodes = new List<string>(inCycle);
            cycleNodes.Sort(StringComparer.Ordinal);
            var grouped = new HashSet<string>(StringComparer.Ordinal);
            foreach (var u in cycleNodes)
            {
                if (grouped.Contains(u)) continue;
                var members = new List<string> { u };
                grouped.Add(u);
                foreach (var v in cycleNodes)
                {
                    if (grouped.Contains(v)) continue;
                    if (reach[u].Contains(v) && reach[v].Contains(u)) { members.Add(v); grouped.Add(v); }
                }
                plan.Problems.Add(new ModPlanProblem
                {
                    Kind = ModPlanProblemKind.Cycle,
                    Id = u,
                    Detail = "依赖或 loadAfter 构成环: " + string.Join(" → ", members),
                });
            }

            // 被环阻塞（但自己不在环里）的节点单独报，避免静默消失
            foreach (var u in leftover)
            {
                if (inCycle.Contains(u)) continue;
                plan.Problems.Add(new ModPlanProblem
                {
                    Kind = ModPlanProblemKind.Cycle,
                    Id = u,
                    Detail = "被依赖环阻塞，无法确定加载顺序",
                });
            }
            return plan;   // 有环 → 不产出顺序（设计 5.5：全有或全无）
        }

        // 3) 冲突计算：先到者胜，后者剔除该内容键
        var claimed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var it in ordered)
        {
            var kept = new List<string>();
            foreach (var key in it.ContentKeys)
            {
                if (claimed.TryGetValue(key, out var winner))
                {
                    plan.Conflicts[key] = winner;
                    plan.Losers[key] = it.Id;
                    continue;   // 被先加载者占用 → 本 MOD 该项不加载
                }
                claimed[key] = it.Id;
                kept.Add(key);
            }
            it.ContentKeys = kept;
        }

        plan.Order = ordered;
        return plan;
    }
}
