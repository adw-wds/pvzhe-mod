using System;
using System.Collections.Generic;
using Godot;

namespace PvzheMod
{
    /// <summary>
    /// 文字律动组件（v1.0 架构重构）：只负责"非战斗场景"的文字彩虹 shader + 主题色。
    /// 调度由 FrameDriver 统一管理（加载检测/战斗检测/游戏速度/模块顺序），这里不包含任何调度逻辑。
    /// 性能关键：颜色 Meta 缓存（同色跳过 AddThemeColorOverride）；收集只在场景切换 + 低频刷新。
    /// </summary>
    public static class GlobalColor
    {
        private static float _t;
        private static float _tq;               // 量化时间：色相每 0.2 步进（约 10 帧同色），ApplyColor 缓存命中免全量重算
        private static int _refresh;
        private static int _btnTimer;
        private static bool _logged;
        private static Node _collectedScene;
        private static readonly List<Control> _labels = new List<Control>();   // Label/RichTextLabel（shader 渐变）
        private static readonly List<Control> _buttons = new List<Control>();  // Button（主题色渐变）
        private static Shader _shader;
        private static ShaderMaterial _sharedMat;   // 所有 Label 共享，性能优化
        private static float _lastSpeed = -1f;      // 缓存上次 speed，变化才 SetShaderParameter
        private static float _lastStyle = -1f;      // 缓存上次 style
        private static int _mountTimer;             // shader 挂载遍历降频器
        private const string ColorMetaKey = "pzmColorCache";   // 颜色缓存 meta（同色跳过 AddThemeColorOverride）

        static string ShaderCode()
        {
            return "shader_type canvas_item;\n"
                + "uniform float time = 0.0;\n"
                + "uniform float speed = 1.0;\n"
                + "uniform float sat : hint_range(0.0, 1.0) = 0.85;\n"
                + "uniform float style : hint_range(0.0, 15.0) = 0.0;\n"
                + "vec3 hsv2rgb(vec3 c){\n"
                + "  vec4 K = vec4(1.0, 2.0/3.0, 1.0/3.0, 3.0);\n"
                + "  vec3 p = abs(fract(c.xxx + K.xyz) * 6.0 - K.www);\n"
                + "  return c.z * mix(K.xxx, clamp(p - K.xxx, 0.0, 1.0), c.y);\n"
                + "}\n"
                + "void fragment(){\n"
                + "  float h = fract(VERTEX.x * 0.01 + time * speed);\n"
                + "  vec3 col;\n"
                + "  if (style < 0.5) col = hsv2rgb(vec3(h, sat, 1.0));\n"
                + "  else if (style < 1.5) col = hsv2rgb(vec3(0.03 + h * 0.05, 0.95, 1.0));\n"
                + "  else if (style < 2.5) col = hsv2rgb(vec3(0.58 + h * 0.10, 0.90, 1.0));\n"
                + "  else if (style < 3.5) col = hsv2rgb(vec3(h * 2.0, 1.0, 1.0));\n"
                + "  else if (style < 4.5) col = hsv2rgb(vec3(0.90 + h * 0.04, 0.50, 1.0));\n"
                + "  else if (style < 5.5) col = hsv2rgb(vec3(0.42 + h * 0.05, 0.55, 1.0));\n"
                + "  else if (style < 6.5) col = vec3(0.92);\n"
                + "  else if (style < 7.5) col = hsv2rgb(vec3(h * 0.10, 1.0, 1.0));\n"
                + "  else if (style < 8.5) col = hsv2rgb(vec3(0.33 + h * 0.06, 0.80, 1.0));\n"
                + "  else if (style < 9.5) col = hsv2rgb(vec3(0.90 + h * 0.08, 0.70, 1.0));\n"
                + "  else if (style < 10.5) col = hsv2rgb(vec3(0.75 + h * 0.07, 0.75, 1.0));\n"
                + "  else if (style < 11.5) col = hsv2rgb(vec3(0.52 + h * 0.06, 0.85, 1.0));\n"
                + "  else if (style < 12.5) col = hsv2rgb(vec3(0.05 + h * 0.12, 0.90, 1.0));\n"
                + "  else if (style < 13.5) col = hsv2rgb(vec3(0.35 + h * 0.12, 0.80, 1.0));\n"
                + "  else if (style < 14.5) col = hsv2rgb(vec3(0.20 + h * 0.05, 0.85, 0.95));\n"
                + "  else col = hsv2rgb(vec3(0.13 + h * 0.05, 0.95, 1.0));\n"
                + "  COLOR = vec4(col, COLOR.a);\n"
                + "}\n";
        }

        /// <summary>由 FrameDriver 每帧调用（仅非战斗场景）：文字律动主逻辑（纯组件，无调度）。</summary>
        public static void Tick(Node root)
        {
            try
            {
                if (root == null || !GodotObject.IsInstanceValid(root)) return;
                // 律动开关：Enabled 可彻底关闭律动
                if (!ModSettings.Enabled) return;
                if (!_logged) { _logged = true; Bootstrap.Log("OnFrame 运行中"); }

                _t += 0.02f * ModSettings.Speed;
                _tq = (float)Math.Round(_t * 5f) / 5f;   // 量化：同色保持约 10 帧 → 缓存命中 → 少 AddThemeColorOverride

                if (_shader == null)
                {
                    _shader = new Shader();
                    _shader.Code = ShaderCode();
                    _sharedMat = new ShaderMaterial();
                    _sharedMat.Shader = _shader;
                }

                var tree = root.GetTree();
                // 从场景树根收集，覆盖弹窗/CanvasLayer/HUD 等所有层
                Node target = tree != null && tree.Root != null ? tree.Root : root;

                // 场景变化时立即重收集；每 120 帧刷新一次（性能模式 360 帧），覆盖动态新增控件
                // 关键：必须用 includeInternal 遍历（GetChildren(true)），UI/角色在 internal 节点下
                if (_collectedScene != target || ++_refresh >= (ModSettings.PerfMode ? 360 : 120))
                {
                    _refresh = 0;
                    _collectedScene = target;
                    _labels.Clear();
                    _buttons.Clear();
                    CollectControls(target);
                    // 文字单色模式 或 大量文字（如图鉴）：不挂 shader，走纯色主题
                    if (ModSettings.TextColorMode != 0 || _labels.Count > 200)
                    {
                        foreach (var l in _labels) _buttons.Add(l);
                        _labels.Clear();
                    }
                }

                // 共享材质：time 每帧必须更新；speed/style 缓存后仅变化时设置
                _sharedMat.SetShaderParameter("time", _t);
                if (_lastSpeed != ModSettings.Speed) { _lastSpeed = ModSettings.Speed; _sharedMat.SetShaderParameter("speed", _lastSpeed); }
                if (_lastStyle != (float)ModSettings.Style) { _lastStyle = (float)ModSettings.Style; _sharedMat.SetShaderParameter("style", _lastStyle); }
                if (_sharedMat.GetShaderParameter("sat").VariantType == Variant.Type.Nil) _sharedMat.SetShaderParameter("sat", 0.6f);

                // Label/RichTextLabel：shader 渐变材质挂载（每 20 帧遍历一次；已挂的跳过）
                if (_labels.Count > 0 && ++_mountTimer >= 20)
                {
                    _mountTimer = 0;
                    for (int i = 0; i < _labels.Count; i++)
                    {
                        var c = _labels[i];
                        if (!GodotObject.IsInstanceValid(c)) continue;
                        if (c.Material != _sharedMat)
                        {
                            c.Material = _sharedMat;
                            c.AddThemeConstantOverride("outline_size", 0);
                            c.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0f));
                        }
                    }
                }

                // 主题色：带缓存（同色跳过 AddThemeColorOverride——Godot theme 重算开销大，是卡顿根因）；
                // 控件多时自动降频；性能模式 30 帧。量化色相保证同色持续约 10 帧 → 缓存大量命中。
                int btnEvery = ModSettings.PerfMode ? 30 : (_labels.Count + _buttons.Count > 100 ? 12 : 8);
                if ((_labels.Count > 0 || _buttons.Count > 0) && ++_btnTimer >= btnEvery)
                {
                    _btnTimer = 0;
                    Color bcolor;
                    if (ModSettings.TextColorMode == 1) bcolor = Colors.White;
                    else if (ModSettings.TextColorMode == 2) bcolor = Colors.Black;
                    else { var bc = HsvToRgb((_tq % 1f + 1f) % 1f, 0.45f, 1f); bcolor = new Color(bc.r, bc.g, bc.b, 1f); }
                    ApplyColor(_labels, bcolor);
                    ApplyColor(_buttons, bcolor);
                }
            }
            catch (Exception ex)
            {
                Bootstrap.Log("全局彩色异常: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>带缓存设置主题色：同色跳过 AddThemeColorOverride（Godot theme 重算开销大——卡顿根因）。</summary>
        static void ApplyColor(List<Control> list, Color c)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var ctl = list[i];
                if (!GodotObject.IsInstanceValid(ctl)) continue;
                if (ctl.HasMeta(ColorMetaKey))
                {
                    Color pc;
                    try { pc = (Color)ctl.GetMeta(ColorMetaKey); } catch { pc = new Color(-1f, -1f, -1f, -1f); }
                    if (pc == c) continue;   // 同色跳过（避免重复 AddThemeColorOverride）
                }
                ctl.SetMeta(ColorMetaKey, c);
                ctl.AddThemeColorOverride("font_color", c);
                if (ctl is Button)
                {
                    ctl.AddThemeColorOverride("font_hover_color", c);
                    ctl.AddThemeColorOverride("font_pressed_color", c);
                }
                if (!ctl.HasMeta("pzmInit"))
                {
                    ctl.SetMeta("pzmInit", true);
                    ctl.AddThemeConstantOverride("outline_size", 0);
                    ctl.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0f));
                }
            }
        }

        static (float r, float g, float b) HsvToRgb(float h, float s, float v)
        {
            int i = (int)(h * 6f);
            float f = h * 6f - i;
            float p = v * (1f - s);
            float q = v * (1f - f * s);
            float t = v * (1f - (1f - f) * s);
            switch (i % 6)
            {
                case 0: return (v, t, p);
                case 1: return (q, v, p);
                case 2: return (p, v, t);
                case 3: return (p, q, v);
                case 4: return (t, p, v);
                default: return (v, p, q);
            }
        }

        /// <summary>includeInternal 递归收集 Label/RichTextLabel（_labels）和 Button/ItemList/Tree（_buttons）。
        /// 战斗 UI/角色在 internal 节点下，必须用 GetChildren(true)。</summary>
        static void CollectControls(Node node)
        {
            foreach (var child in node.GetChildren(true))
            {
                try
                {
                    if (child is Label || child is RichTextLabel)
                        _labels.Add((Control)child);
                    else if (child is Button || child is ItemList || child is Tree)
                        _buttons.Add((Control)child);
                }
                catch { }
                CollectControls(child);
            }
        }
    }
}
