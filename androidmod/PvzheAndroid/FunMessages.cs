using System;
using System.Collections.Generic;
using Godot;

namespace PvzheMod
{
    /// <summary>
    /// 趣味弹幕：游戏时随机飘过彩色搞笑短句（支持自定义）。
    /// </summary>
    public static class FunMessages
    {
        private static float _timer;
        private static int _seed;
        private static bool _loaded;
        private static bool _logged;
        private static int _colorTimer;   // 改色降频：手机 GPU 下每帧改主题色重绘会导致部分机型全屏/半屏闪烁
        private static readonly List<Label> _active = new List<Label>();
        private static readonly string CustomPath = @"mod\custom_danmaku.txt";

        private static readonly string[] Messages = new string[]
        {
            "感谢使用该MOD！",
            "感谢使用该MOD！",
            "感谢使用该MOD！",
            "感谢使用该MOD！",
            "感谢使用该MOD！",
            "感谢使用该MOD！",
            "感谢使用该MOD！",
            "感谢使用该MOD！",
        };

        /// <summary>由 OnFrame 每帧调用。</summary>
        public static void OnFrame(Node root)
        {
            try
            {
                if (!_logged)
                {
                    _logged = true;
                    Bootstrap.Log("FunMessages 运行中 FunEnabled=" + ModSettings.FunEnabled);
                }
                if (!_loaded)
                {
                    _loaded = true;
                    LoadCustom();
                }
                if (!ModSettings.FunEnabled || ModSettings.NoFlicker)   // 禁止闪屏模式：动态飘字弹幕也禁用
                {
                    if (_active.Count > 0) { foreach (var l in _active) { if (GodotObject.IsInstanceValid(l)) l.QueueFree(); } _active.Clear(); }
                    return;
                }

                // 简易计时（按 60fps 估算）
                _timer -= 0.016f;
                if (_timer <= 0 && _active.Count < 3)
                {
                    _timer = 10f + (_seed % 15) * 1.2f;
                    Spawn(root);
                }

                // 移动 + 变色（改色降频到每 8 帧：每帧改主题色会让部分手机重绘闪烁）
                var c = HsvToRgb((_seed * 0.13f + _timer * 0.2f) % 1f, 0.8f, 1f);
                var color = new Color(c.r, c.g, c.b, 1f);
                bool recolor = (++_colorTimer % 8) == 0;
                for (int i = _active.Count - 1; i >= 0; i--)
                {
                    var l = _active[i];
                    if (!GodotObject.IsInstanceValid(l)) { _active.RemoveAt(i); continue; }
                    l.Position += new Vector2(-4f * ModSettings.DanmakuSpeed, 0);
                    if (recolor) l.AddThemeColorOverride("font_color", color);
                    if (l.Position.X < -360) { l.QueueFree(); _active.RemoveAt(i); }
                }
            }
            catch (Exception ex) { Bootstrap.Log("FunMessages 异常: " + ex.GetType().Name + ": " + ex.Message); }
        }

        static void Spawn(Node root)
        {
            var tree = root.GetTree();
            if (tree == null || tree.Root == null) return;
            _seed++;
            var label = new Label();
            label.Text = PickMessage();
            label.AddThemeFontSizeOverride("font_size", 26);
            label.ZIndex = 300;
            // 用视口尺寸（Label 在 Window 下用视口坐标）
            var vp = tree.Root.GetViewport();
            var vsize = vp != null ? vp.GetVisibleRect().Size : new Vector2(1280, 720);
            float y = 80f + (_seed * 97 % (int)Math.Max(1, vsize.Y - 180));
            label.Position = new Vector2(vsize.X, y);
            label.AddThemeConstantOverride("outline_size", 4);
            label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.85f));
            tree.Root.AddChild(label);
            _active.Add(label);
            Bootstrap.Log("趣味弹幕: " + label.Text);
        }

        /// <summary>选弹幕：有自定义则优先用自定义，否则用内置。
        /// 注意：不能用 new char[]{...} 拆分（会生成 <PrivateImplementationDetails> 静态数组，合并后无法解析）。</summary>
        static string PickMessage()
        {
            if (!string.IsNullOrEmpty(ModSettings.CustomMessages))
            {
                var msg = ModSettings.CustomMessages
                    .Replace('，', ',').Replace('、', ',').Replace(';', ',').Replace('；', ',')
                    .Replace('\n', ',').Replace('\r', ',');
                var custom = msg.Split(',');
                if (custom.Length > 0)
                {
                    var pick = custom[_seed % custom.Length].Trim();
                    if (pick.Length > 0) return pick;
                    for (int i = 0; i < custom.Length; i++)
                        if (custom[i].Trim().Length > 0) return custom[i].Trim();
                }
            }
            return Messages[_seed % Messages.Length];
        }

        /// <summary>从文件加载自定义弹幕（启动时一次）。</summary>
        static void LoadCustom()
        {
            try
            {
                if (Godot.FileAccess.FileExists(CustomPath))
                {
                    var f = Godot.FileAccess.Open(CustomPath, Godot.FileAccess.ModeFlags.Read);
                    if (f != null) { ModSettings.CustomMessages = f.GetAsText(); f.Close(); }
                }
            }
            catch { }
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
    }
}
