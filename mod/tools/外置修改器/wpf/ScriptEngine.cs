using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace PvzheRemote;

/// <summary>脚本编译缓存与执行（后台线程）。</summary>
public static class ScriptEngine
{
    static readonly ConcurrentDictionary<string, Script<object>> _cache = new();
    static readonly ScriptApi _api = new();

    /// <summary>编译脚本（按 file+code 缓存）；返回 null 表示成功，否则错误信息。</summary>
    public static string? Compile(ScriptDef d, string code)
    {
        try
        {
            var key = d.File + "|" + code.GetHashCode();
            if (_cache.ContainsKey(key)) return null;
            var opts = ScriptOptions.Default
                .AddReferences(typeof(ScriptApi).Assembly)
                .AddImports("System", "System.Threading.Tasks", "System.Linq");
            var script = CSharpScript.Create<object>(code, opts, typeof(ScriptApi));
            script.Compile();
            _cache[key] = script;
            // 清理同一文件旧版本缓存
            foreach (var k in _cache.Keys)
                if (k.StartsWith(d.File + "|", StringComparison.Ordinal) && k != key)
                    _cache.TryRemove(k, out _);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>执行脚本；返回结果字符串（异常/编译错误返回 err 信息）。</summary>
    public static async Task<string> RunAsync(ScriptDef d, string code)
    {
        try
        {
            var err = Compile(d, code);
            if (err != null) return "编译失败: " + err;
            var key = d.File + "|" + code.GetHashCode();
            if (!_cache.TryGetValue(key, out var script)) return "脚本未编译";
            var state = await script.RunAsync(_api);
            return state.ReturnValue?.ToString() ?? "ok";
        }
        catch (Exception ex) { return "运行失败: " + ex.Message; }
    }
}
