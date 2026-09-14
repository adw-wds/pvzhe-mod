using Godot;

namespace PvzheMod
{
    /// <summary>
    /// 背景彩虹氛围：全屏半透明彩虹渐变滤镜（背景层，不挡操作）。
    /// 无 shader 实现：GradientTexture2D 渐变纹理 + 每 1 秒低频流动。
    /// 之前闪屏根源：全屏自定义 shader（SCREEN_UV/TIME）在 Adreno 驱动上每帧 GPU 重算 → 闪屏。
    /// 现在走 Godot 标准纹理渲染路径，动画每 1 秒才重绘一次 → 渲染稳定不闪屏。
    /// </summary>
    public static class BackgroundFX
    {
        private static CanvasLayer _layer;
        private static TextureRect _rect;
        private static GradientTexture2D _tex;
        private static Gradient _grad;
        private static int _flowTimer;

        public static void OnFrame(Node root)
        {
            try
            {
                if (root == null || !GodotObject.IsInstanceValid(root)) return;
                var tree = root.GetTree();
                if (tree == null || tree.Root == null) return;

                if (ModSettings.BGEnabled)
                {
                    if (_layer == null || !GodotObject.IsInstanceValid(_layer))
                        Ensure(tree.Root);
                    if (_layer != null && GodotObject.IsInstanceValid(_layer) && _rect != null)
                    {
                        var size = tree.Root.Size;
                        if (_rect.Size != size) _rect.Size = size;
                        // 每 1 秒轮换一次色相 → 彩虹缓慢流动；禁止闪屏模式完全静态（零重绘）
                        if (!ModSettings.NoFlicker && ++_flowTimer >= 60)
                        {
                            _flowTimer = 0;
                            FlowGradient();
                        }
                    }
                }
                else
                {
                    Destroy();
                }
            }
            catch { }
        }

        static void Ensure(Node root)
        {
            try
            {
                if (_layer != null && GodotObject.IsInstanceValid(_layer)) return;
                _layer = new CanvasLayer();
                _layer.Name = "PvzBGFX";
                _layer.Layer = -10;   // 背景层
                root.AddChild(_layer);

                // 彩虹渐变（红橙黄绿蓝紫红，首尾同色保证循环流动无缝）
                _grad = new Gradient();
                _grad.Colors = new Color[]
                {
                    new Color(1f, 0.2f, 0.2f),
                    new Color(1f, 0.7f, 0.2f),
                    new Color(0.9f, 1f, 0.2f),
                    new Color(0.2f, 1f, 0.4f),
                    new Color(0.2f, 0.7f, 1f),
                    new Color(0.5f, 0.3f, 1f),
                    new Color(1f, 0.2f, 0.2f),
                };
                _tex = new GradientTexture2D();
                _tex.Gradient = _grad;
                _tex.Fill = GradientTexture2D.FillEnum.Linear;
                _tex.FillFrom = new Vector2(0, 0);
                _tex.FillTo = new Vector2(1, 0);
                _tex.Width = 256;
                _tex.Height = 8;

                _rect = new TextureRect();
                _rect.Texture = _tex;
                _rect.StretchMode = TextureRect.StretchModeEnum.Scale;
                // 半透明氛围：alpha 0.07 很淡，不挡游戏画面
                _rect.Modulate = new Color(1f, 1f, 1f, 0.07f);
                // 锚点铺满视口：修复手机横屏/视口拉伸下只覆盖部分屏幕（右半屏闪色块）的问题
                _rect.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                var vp = root.GetViewport();
                _rect.Size = vp != null ? vp.GetVisibleRect().Size : new Vector2(1280, 720);
                _rect.MouseFilter = Control.MouseFilterEnum.Ignore;
                _layer.AddChild(_rect);
                Bootstrap.Log("背景彩虹已启用（无 shader）");
            }
            catch (System.Exception ex) { Bootstrap.Log("背景彩虹异常: " + ex.Message); }
        }

        /// <summary>每 1 秒把渐变颜色整体轮换一个位置 → 彩虹缓慢流动（低频重绘，无 shader）。</summary>
        static void FlowGradient()
        {
            try
            {
                var cols = _grad.Colors;
                if (cols == null || cols.Length < 2) return;
                var first = cols[0];
                for (int i = 0; i < cols.Length - 1; i++) cols[i] = cols[i + 1];
                cols[cols.Length - 1] = first;
                _grad.Colors = cols;   // 触发纹理低频重生成
            }
            catch { }
        }

        static void Destroy()
        {
            if (_layer != null && GodotObject.IsInstanceValid(_layer)) _layer.QueueFree();
            _layer = null;
            _rect = null;
            _tex = null;
            _grad = null;
        }
    }
}
