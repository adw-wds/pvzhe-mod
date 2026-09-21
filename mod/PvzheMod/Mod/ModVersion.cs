using System;

namespace PvzheMod.Mod;

/// <summary>版本号：点分数字，比较时右侧补 0（1.0 == 1.0.0）。零 Godot 依赖。</summary>
public sealed class ModVersion : IComparable<ModVersion>
{
    public int[] Parts { get; }

    private ModVersion(int[] parts) { Parts = parts; }

    public static ModVersion Parse(string s)
    {
        if (string.IsNullOrEmpty(s)) throw new FormatException("版本号为空");
        var raw = s.Trim();
        var segs = raw.Split('.');
        var parts = new int[segs.Length];
        for (var i = 0; i < segs.Length; i++)
        {
            if (segs[i].Length == 0) throw new FormatException("版本号有空段: " + s);
            for (var j = 0; j < segs[i].Length; j++)
                if (segs[i][j] < '0' || segs[i][j] > '9') throw new FormatException("版本号含非数字: " + s);
            parts[i] = int.Parse(segs[i]);
        }
        return new ModVersion(parts);
    }

    public int CompareTo(ModVersion other)
    {
        var n = Math.Max(Parts.Length, other.Parts.Length);
        for (var i = 0; i < n; i++)
        {
            var a = i < Parts.Length ? Parts[i] : 0;
            var b = i < other.Parts.Length ? other.Parts[i] : 0;
            if (a != b) return a < b ? -1 : 1;
        }
        return 0;
    }

    public override string ToString() => string.Join(".", Parts);
}

/// <summary>版本约束：支持 "&gt;=x.y[.z]"、"&lt;=x.y[.z]"、"=x.y.z"、空或 "*"（任意）。</summary>
public sealed class ModVersionConstraint
{
    public bool IsAny { get; }
    private readonly int _op;          // 0=any 1=&gt;= 2=&lt;= 3==
    private readonly ModVersion _target;

    private ModVersionConstraint(bool any, int op, ModVersion target)
    {
        IsAny = any; _op = op; _target = target;
    }

    public static ModVersionConstraint Any() => new ModVersionConstraint(true, 0, null);

    public static ModVersionConstraint Parse(string s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Trim() == "*") return Any();
        var raw = s.Trim();
        int op;
        string body;
        if (raw.StartsWith(">=")) { op = 1; body = raw.Substring(2); }
        else if (raw.StartsWith("<=")) { op = 2; body = raw.Substring(2); }
        else if (raw.StartsWith("=")) { op = 3; body = raw.Substring(1); }
        else throw new FormatException("不支持的约束运算符: " + s + "（仅支持 >=、<=、=）");

        var v = ModVersion.Parse(body);
        return new ModVersionConstraint(false, op, v);
    }

    public bool Satisfies(ModVersion v)
    {
        if (IsAny) return true;
        if (v == null) return false;      // 未知版本无法满足具体约束
        var c = v.CompareTo(_target);
        switch (_op)
        {
            case 1: return c >= 0;
            case 2: return c <= 0;
            case 3: return c == 0;
            default: return true;
        }
    }

    public override string ToString()
    {
        if (IsAny) return "*";
        var op = _op == 1 ? ">=" : _op == 2 ? "<=" : "=";
        return op + _target;
    }
}
