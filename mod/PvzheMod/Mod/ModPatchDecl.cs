using System;
using System.Text.RegularExpressions;

namespace PvzheMod.Mod;

/// <summary>
/// <c>mod.json</c> 里一条「声明补丁」的纯逻辑模型。
///
/// 只做**解析与字段校验**；转成 installer 的 <c>PatchEntry</c> 与保留区编号在 `ModPatchSource` 侧做
/// —— 这样本文件零 Mono.Cecil 依赖，可纯桌面单测。
///
/// 【权限约定】MOD 只能声明 <c>hook</c> 键，**不能指定 hostMethod** ——
/// 注入调用哪条宿主方法由平台按 arity/kind 决定，防止 MOD 注入调用任意宿主方法。
/// </summary>
public sealed class ModPatchDecl
{
    public string Hook = "";
    public string Kind = "";
    public string TargetType = "";
    public string TargetMethod = "";
    public bool PassArgs;
    public int ArgCount;
    public int Seq = ModSeqFloor;
    public bool Required;
    public int ConstI;
    public string Note = "";

    /// <summary>保留区起点：0–99 是平台内置补丁（现有 10/20），MOD 声明从 100 起。</summary>
    public const int ModSeqFloor = 100;

    /// <summary>宿主只有 Fire0..Fire8。</summary>
    public const int MaxArgs = 8;

    static readonly Regex HookRx = new Regex("^[a-z0-9._-]{1,64}$", RegexOptions.Compiled);

    static readonly string[] KnownKinds =
    {
        "BeforeCall", "AfterCall",
        "GateReturnTrue", "GateReturnFalse", "GateReturnZero", "GateReturnConst",
    };

    /// <summary>校验本声明。返回 null = 通过，否则是中文原因。</summary>
    public string Validate()
    {
        if (string.IsNullOrEmpty(Hook)) return "[PvzMod] 声明补丁缺少 hook";
        if (!HookRx.IsMatch(Hook)) return "[PvzMod] hook 名非法（只允许 a-z0-9._- ，≤64）: " + Hook;

        var kind = NormalizeKind(Kind);
        if (kind == null) return "[PvzMod] 未知 kind: " + Kind;

        if (string.IsNullOrEmpty(TargetType)) return "[PvzMod] 声明补丁缺少 targetType";
        if (string.IsNullOrEmpty(TargetMethod)) return "[PvzMod] 声明补丁缺少 targetMethod";

        if (Seq < ModSeqFloor)
            return "[PvzMod] seq 必须 ≥" + ModSeqFloor + "（0-" + (ModSeqFloor - 1) + " 是内置保留区）: " + Seq;

        if (PassArgs)
        {
            if (kind != "BeforeCall" && kind != "AfterCall")
                return "[PvzMod] " + kind + " 不支持 passArgs（只有 BeforeCall/AfterCall 支持）";
            if (ArgCount < 0 || ArgCount > MaxArgs)
                return "[PvzMod] argCount 必须在 0.." + MaxArgs + ": " + ArgCount;
        }
        else if (ArgCount != 0)
        {
            return "[PvzMod] passArgs=false 时不应指定 argCount";
        }

        return null;
    }

    /// <summary>把 kind 归一化到已知值；未知返回 null。大小写不敏感。</summary>
    public static string NormalizeKind(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        for (var i = 0; i < KnownKinds.Length; i++)
            if (string.Equals(KnownKinds[i], raw, StringComparison.OrdinalIgnoreCase))
                return KnownKinds[i];
        return null;
    }
}
