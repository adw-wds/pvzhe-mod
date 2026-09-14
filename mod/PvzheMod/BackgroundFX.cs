using Godot;

namespace PvzheMod
{
    /// <summary>
    /// 背景彩虹氛围：全屏半透明彩虹滤镜（背景层，不挡操作）。
    /// ColorRect 实现（最稳）：每 0.5 秒轮换一次色相，零 shader、零 GradientTexture2D——
    /// 旧 GradientTexture2D 版在合并程序集下反复抛 "Could not load assembly PvzheMod"，已弃用。
    /// 失败一次自动熔断（_broken），不再每帧重试/刷日志。
    /// </summary>
    public static class BackgroundFX
    {
        private static CanvasLayer _layer;
        private static ColorRect _rect;
        private static double _lastSwitch;
        private static float _hue;
        private static bool _broken;

        public static void OnFrame(Node root)
        {
            try
            {
                if (_broken) return;
                if (root == null || !GodotObject.IsInstanceValid(root)) return;
                var tree = root.GetTree();
                if (tree == null || tree.Root == null) return;

                if (ModSettings.BGEnabled)
                {
                    if (_layer == null || !GodotObject.IsInstanceValid(_layer))
                        Ensure(tree.Root);
                    if (_layer != null && GodotObject.IsInstanceValid(_layer))
                    {
                        var size = tree.Root.Size;
                        if (_rect.Size != size) _rect.Size = size;
                        // 每 0.5 秒轮换一次色相（纯色半透明，零渲染开销）
                        var now = Time.GetTicksMsec() / 1000.0;
                        if (now - _lastSwitch >= 0.5)
                        {
                            _lastSwitch = now;
                            _hue += 0.03f;
                            if (_hue >= 1f) _hue -= 1f;
                            var c = Color.FromHsv(_hue, 0.6f, 1f);
                            _rect.Color = new Color(c.R, c.G, c.B, 0.06f);   // 低 alpha 不遮挡游戏画面
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

                _rect = new ColorRect();
                _rect.MouseFilter = Control.MouseFilterEnum.Ignore;
                var vp = root.GetViewport();
                _rect.Size = vp != null ? vp.GetVisibleRect().Size : new Vector2(1280, 720);
                _rect.Color = new Color(1f, 0.5f, 0.2f, 0.06f);
                _layer.AddChild(_rect);
                _lastSwitch = Time.GetTicksMsec() / 1000.0;
                Bootstrap.Log("背景彩虹已启用(ColorRect)");
            }
            catch (System.Exception ex)
            {
                _broken = true;   // 熔断：失败一次后不再重试，避免每帧异常+刷日志
                Bootstrap.Log("背景彩虹异常: " + ex.Message);
            }
        }

        static void Destroy()
        {
            if (_layer != null && GodotObject.IsInstanceValid(_layer)) _layer.QueueFree();
            _layer = null;
            _rect = null;
        }
    }
}
