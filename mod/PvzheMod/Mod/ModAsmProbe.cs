using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;

namespace PvzheMod.Mod;

/// <summary>
/// 可行性探针（插件式 MOD 方案的生死线）：
/// AOT 裁剪的游戏运行时能否**加载一个新编译的程序集**并执行/反射调用其中的代码？
///   步骤：LoadFrom → Load(byte[]) 兜底 → 反射调静态方法 → 反射构造实例 → 触发静态构造器 → 异常跨界
/// 结论写 mod_log.txt。验证完成后本文件可整文件删除。
/// </summary>
public static class ModAsmProbe
{
    public static string Run(string dllAbsPath)
    {
        Bootstrap.Log("[MOD] ===== asm probe start =====");
        string line;
        try { line = ProbeAssembly(dllAbsPath); }
        catch (Exception ex)
        {
            // 硬引用被裁剪的方法时，异常会在进入方法时就抛，必须兜住，否则整个探针白跑
            line = "FATAL:" + ex.Message;
            Bootstrap.Log("[MOD] asm probe FATAL st: " + ex.StackTrace);
        }
        Bootstrap.Log(line);
        Bootstrap.Log("[MOD] ===== asm probe end =====");
        return line;
    }

    static string ProbeAssembly(string dllAbsPath)
    {
        var parts = new List<string>();

        if (string.IsNullOrEmpty(dllAbsPath)) return "err:no-path";

        // --- 步骤 0：只用反射"发现"API，不硬引用（被裁剪的方法硬引用会让整个方法 JIT 失败） ---
        var asmType = typeof(Assembly);
        var mLoadFrom = Find(asmType, "LoadFrom", new[] { typeof(string) });
        var mLoadFile = Find(asmType, "LoadFile", new[] { typeof(string) });
        var mLoadBytes = Find(asmType, "Load", new[] { typeof(byte[]) });
        var mReflOnly = Find(asmType, "ReflectionOnlyLoadFrom", new[] { typeof(string) });
        var dmType = Type.GetType("System.Reflection.Emit.DynamicMethod");
        parts.Add("has:LoadFrom=" + (mLoadFrom != null)
                + " LoadFile=" + (mLoadFile != null)
                + " Load(bytes)=" + (mLoadBytes != null)
                + " ReflOnly=" + (mReflOnly != null)
                + " DynamicMethod=" + (dmType != null));

        // 运行时是否允许动态代码（.NET 8 权威开关；false = 一切运行时新代码都不可能）
        try
        {
            var rf = Type.GetType("System.Runtime.CompilerServices.RuntimeFeature");
            if (rf == null) parts.Add("DynCode=<no-RuntimeFeature>");
            else
            {
                var pi = rf.GetProperty("IsDynamicCodeSupported");
                parts.Add("DynCodeSupported=" + (pi == null ? "<no-prop>" : "" + pi.GetValue(null, null)));
            }
        }
        catch (Exception ex) { parts.Add("DynCode=EX:" + ex.Message); }

        // 列出 Assembly 上还活着的 Load* 方法名（看清到底裁掉了什么）
        try
        {
            var ms0 = asmType.GetMethods(BindingFlags.Public | BindingFlags.Static);
            var names = "";
            for (var i = 0; i < ms0.Length; i++)
            {
                var n = ms0[i].Name;
                if (n.Length >= 4 && n.Substring(0, 4) == "Load") names += n + ",";
            }
            parts.Add("LoadMethods=[" + names + "]");
        }
        catch (Exception ex) { parts.Add("LoadMethods=EX:" + ex.Message); }

        // --- 步骤 1：读 MOD 程序集字节（同时验证 Godot.FileAccess 读二进制） ---
        byte[] bin = null;
        try
        {
            var fa = Godot.FileAccess.Open(dllAbsPath, Godot.FileAccess.ModeFlags.Read);
            if (fa == null) parts.Add("read=null");
            else
            {
                var len = (int)fa.GetLength();
                bin = fa.GetBuffer(len);
                fa.Close();
                parts.Add("read=ok len=" + len);
            }
        }
        catch (Exception ex) { parts.Add("read=EX:" + ex.Message); }

        // --- 步骤 2：按可用性依次尝试加载（全部经反射 Invoke，不硬引用） ---
        Assembly asm = null;
        var tried = "";
        if (mLoadFrom == null) tried += "LoadFrom=absent;";
        else
        {
            try { asm = (Assembly)mLoadFrom.Invoke(null, new object[] { dllAbsPath }); tried += "LoadFrom=ok;"; }
            catch (Exception ex) { tried += "LoadFrom=EX:" + Inner(ex) + ";"; }
        }
        if (asm == null && mLoadFile != null)
        {
            try { asm = (Assembly)mLoadFile.Invoke(null, new object[] { dllAbsPath }); tried += "LoadFile=ok;"; }
            catch (Exception ex) { tried += "LoadFile=EX:" + Inner(ex) + ";"; }
        }
        if (asm == null && mLoadBytes != null && bin != null)
        {
            try { asm = (Assembly)mLoadBytes.Invoke(null, new object[] { bin }); tried += "Load(bytes)=ok;"; }
            catch (Exception ex) { tried += "Load(bytes)=EX:" + Inner(ex) + ";"; }
        }
        parts.Add("try:" + tried);

        if (asm == null) return string.Join(" | ", parts.ToArray());

        try { parts.Add("asmName=" + asm.GetName().Name); }
        catch (Exception ex) { parts.Add("asmName=EX:" + ex.Message); }

        // --- 步骤 3：枚举类型 ---
        string typeNames = "<none>";
        Type entryType = null;
        try
        {
            var types = asm.GetTypes();
            typeNames = "";
            for (var i = 0; i < types.Length; i++)
            {
                if (i > 0) typeNames += ",";
                typeNames += types[i].FullName;
                if (types[i].FullName == "TestMod.Entry") entryType = types[i];
            }
            parts.Add("types=" + types.Length + "[" + typeNames + "]");
        }
        catch (Exception ex)
        {
            parts.Add("GetTypes=EX:" + ex.Message);
        }

        if (entryType == null) return string.Join(" | ", parts.ToArray());

        // --- 步骤 4：反射调静态方法（这一步才真正触发 JIT） ---
        parts.Add("Hello=" + Invoke(entryType, "Hello", null));
        parts.Add("Bcl=" + Invoke(entryType, "Bcl", null));
        parts.Add("GodotTouch=" + Invoke(entryType, "GodotTouch", null));
        parts.Add("Throw=" + Invoke(entryType, "ThrowInside", null));

        // --- 步骤 5：反射构造实例 + 调实例方法 ---
        try
        {
            var w = entryType.GetNestedType("Widget");
            if (w == null) parts.Add("Widget=<not-found>");
            else
            {
                var wobj = Activator.CreateInstance(w);
                var m = w.GetMethod("Describe");
                var r = m == null ? "<no-method>" : "" + m.Invoke(wobj, null);
                parts.Add("Widget=" + r);
            }
        }
        catch (Exception ex)
        {
            parts.Add("Widget=EX:" + ex.Message);
        }

        // --- 步骤 6：静态构造器是否触发 ---
        try
        {
            var c = entryType.GetNestedType("WithCctor");
            var m = c == null ? null : c.GetMethod("Get");
            parts.Add("Cctor=" + (m == null ? "<not-found>" : "" + m.Invoke(null, null)));
        }
        catch (Exception ex)
        {
            parts.Add("Cctor=EX:" + ex.Message);
        }

        return string.Join(" | ", parts.ToArray());
    }

    /// <summary>用反射找方法（不用 GetMethod(name, flags, binder, sig, modifiers)，避免重载表被裁）。</summary>
    static MethodInfo Find(Type t, string name, Type[] sig)
    {
        try
        {
            var ms = t.GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (var i = 0; i < ms.Length; i++)
            {
                if (ms[i].Name != name) continue;
                var ps = ms[i].GetParameters();
                if (ps.Length != sig.Length) continue;
                var ok = true;
                for (var j = 0; j < ps.Length; j++)
                {
                    if (ps[j].ParameterType != sig[j]) { ok = false; break; }
                }
                if (ok) return ms[i];
            }
        }
        catch { }
        return null;
    }

    /// <summary>剥掉 TargetInvocationException，取真正的原因。</summary>
    static string Inner(Exception ex)
    {
        if (ex is TargetInvocationException && ex.InnerException != null) return ex.InnerException.Message;
        return ex.Message;
    }

    /// <summary>反射调用并捕获内层异常（异常跨界是重点观测项）。</summary>
    static string Invoke(Type t, string method, object[] args)
    {
        try
        {
            var m = t.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
            if (m == null) return "<no-method>";
            var r = m.Invoke(null, args);
            return r == null ? "<null>" : "" + r;
        }
        catch (TargetInvocationException tie)
        {
            var inner = tie.InnerException;
            return "INNER-EX:" + (inner == null ? tie.Message : inner.Message);
        }
        catch (Exception ex)
        {
            Bootstrap.Log("[MOD] asm probe EX " + method + ": " + ex.Message);
            Bootstrap.Log("[MOD] asm probe EX " + method + " st: " + ex.StackTrace);
            return "EX:" + ex.Message;
        }
    }
}
