using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PvzheMod.Mod;

public enum ModJsonKind { Null, Bool, Number, String, Array, Object }

/// <summary>极简 JSON：只支持 JSON 标准子集，够解析 mod.json / config.json。零 Godot / BCL-JSON 依赖。</summary>
public sealed class ModJsonValue
{
    public ModJsonKind Kind { get; }
    private readonly bool _bool;
    private readonly double _num;
    private readonly string _str;
    private readonly List<ModJsonValue> _arr;
    private readonly List<KeyValuePair<string, ModJsonValue>> _obj;

    private ModJsonValue(ModJsonKind kind, bool b, double n, string s,
        List<ModJsonValue> arr, List<KeyValuePair<string, ModJsonValue>> obj)
    {
        Kind = kind; _bool = b; _num = n; _str = s; _arr = arr; _obj = obj;
    }

    public static ModJsonValue Null() => new ModJsonValue(ModJsonKind.Null, false, 0, null, null, null);
    public static ModJsonValue Bool(bool v) => new ModJsonValue(ModJsonKind.Bool, v, 0, null, null, null);
    public static ModJsonValue Number(double v) => new ModJsonValue(ModJsonKind.Number, false, v, null, null, null);
    public static ModJsonValue Str(string v) => new ModJsonValue(ModJsonKind.String, false, 0, v, null, null);
    public static ModJsonValue Arr(List<ModJsonValue> items) => new ModJsonValue(ModJsonKind.Array, false, 0, null, items, null);
    public static ModJsonValue Obj(List<KeyValuePair<string, ModJsonValue>> members) => new ModJsonValue(ModJsonKind.Object, false, 0, null, null, members);

    public int Count => Kind == ModJsonKind.Array ? _arr.Count : Kind == ModJsonKind.Object ? _obj.Count : 0;

    public ModJsonValue Item(int index)
        => Kind == ModJsonKind.Array && index >= 0 && index < _arr.Count ? _arr[index] : null;

    public string KeyAt(int index)
        => Kind == ModJsonKind.Object && index >= 0 && index < _obj.Count ? _obj[index].Key : null;

    public ModJsonValue Get(string key)
    {
        if (Kind != ModJsonKind.Object || key == null) return null;
        for (var i = 0; i < _obj.Count; i++) if (_obj[i].Key == key) return _obj[i].Value;
        return null;
    }

    public string AsString() => Kind == ModJsonKind.String ? _str : null;
    public bool AsBool() => Kind == ModJsonKind.Bool && _bool;
    public double AsDouble() => Kind == ModJsonKind.Number ? _num : 0;

    public int AsInt(int fallback)
    {
        if (Kind != ModJsonKind.Number) return fallback;
        return (int)Math.Round(_num);
    }

    public static ModJsonValue Parse(string text)
    {
        if (text == null) throw new FormatException("输入为 null");
        var i = 0;
        SkipWs(text, ref i);
        var v = ParseValue(text, ref i);
        SkipWs(text, ref i);
        if (i != text.Length) throw new FormatException("JSON 尾部有多余内容，位置 " + i);
        return v;
    }

    private static void SkipWs(string s, ref int i)
    {
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
    }

    private static ModJsonValue ParseValue(string s, ref int i)
    {
        SkipWs(s, ref i);
        if (i >= s.Length) throw new FormatException("JSON 意外结束");
        var c = s[i];
        switch (c)
        {
            case '{': return ParseObject(s, ref i);
            case '[': return ParseArray(s, ref i);
            case '"': return Str(ParseString(s, ref i));
            case 't': Expect(s, ref i, "true"); return Bool(true);
            case 'f': Expect(s, ref i, "false"); return Bool(false);
            case 'n': Expect(s, ref i, "null"); return Null();
            default:
                if (c == '-' || (c >= '0' && c <= '9')) return Number(ParseNumber(s, ref i));
                throw new FormatException("JSON 非法字符 '" + c + "'，位置 " + i);
        }
    }

    private static void Expect(string s, ref int i, string word)
    {
        if (i + word.Length > s.Length || string.CompareOrdinal(s, i, word, 0, word.Length) != 0)
            throw new FormatException("期望 " + word + "，位置 " + i);
        i += word.Length;
    }

    private static ModJsonValue ParseObject(string s, ref int i)
    {
        i++; // {
        var members = new List<KeyValuePair<string, ModJsonValue>>();
        SkipWs(s, ref i);
        if (i < s.Length && s[i] == '}') { i++; return Obj(members); }
        while (true)
        {
            SkipWs(s, ref i);
            if (i >= s.Length || s[i] != '"') throw new FormatException("对象的键必须是字符串，位置 " + i);
            var key = ParseString(s, ref i);
            SkipWs(s, ref i);
            if (i >= s.Length || s[i] != ':') throw new FormatException("键后缺少 ':'，位置 " + i);
            i++;
            var val = ParseValue(s, ref i);
            members.Add(new KeyValuePair<string, ModJsonValue>(key, val));
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("对象未闭合");
            if (s[i] == ',') { i++; continue; }
            if (s[i] == '}') { i++; return Obj(members); }
            throw new FormatException("对象内期望 ',' 或 '}'，位置 " + i);
        }
    }

    private static ModJsonValue ParseArray(string s, ref int i)
    {
        i++; // [
        var items = new List<ModJsonValue>();
        SkipWs(s, ref i);
        if (i < s.Length && s[i] == ']') { i++; return Arr(items); }
        while (true)
        {
            items.Add(ParseValue(s, ref i));
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("数组未闭合");
            if (s[i] == ',') { i++; continue; }
            if (s[i] == ']') { i++; return Arr(items); }
            throw new FormatException("数组内期望 ',' 或 ']'，位置 " + i);
        }
    }

    private static string ParseString(string s, ref int i)
    {
        if (s[i] != '"') throw new FormatException("期望字符串，位置 " + i);
        i++;
        var sb = new StringBuilder();
        while (true)
        {
            if (i >= s.Length) throw new FormatException("字符串未闭合");
            var c = s[i++];
            if (c == '"') return sb.ToString();
            if (c != '\\') { sb.Append(c); continue; }
            if (i >= s.Length) throw new FormatException("转义未完成");
            var e = s[i++];
            switch (e)
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    if (i + 4 > s.Length) throw new FormatException("\\u 转义不完整");
                    var hex = s.Substring(i, 4);
                    i += 4;
                    sb.Append((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    break;
                default: throw new FormatException("未知转义 \\" + e);
            }
        }
    }

    private static double ParseNumber(string s, ref int i)
    {
        var start = i;
        if (i < s.Length && s[i] == '-') i++;
        while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
        if (i < s.Length && s[i] == '.')
        {
            i++;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
        }
        if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
        {
            i++;
            if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
        }
        var slice = s.Substring(start, i - start);
        if (!double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            throw new FormatException("非法数字: " + slice);
        return d;
    }
}

public static class ModJson
{
    public static ModJsonValue Parse(string text) => ModJsonValue.Parse(text);
}
