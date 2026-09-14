using System;
using Godot;

/// <summary>
/// 联机提示气泡（屏幕中下方，几秒后自动消失）。
///
/// 为什么必须有：
///   <c>NetSession.Notice</c> 只写 <c>LastNotice</c> + 日志，**只有打开联机面板才看得到**。
///   于是"客机没能跟随进关"这种失败，在玩家眼里就是「点了开始对局，客机完全没反应」——
///   明明有原因，界面上却一片安静，只能靠猜。
///
/// 实现：订阅 <c>NetSession.OnNotice</c>，把每条提示显示成一个居中靠下的气泡。
/// 纯展示，不接输入（MouseFilter=Ignore），任何异常都不冒泡。
/// </summary>
public static class NetToast
{
    /// <summary>单条停留时长（毫秒）。</summary>
    const int LifeMs = 4500;
    /// <summary>同一条文本的最小重复间隔（毫秒）——防止刷新类的提示刷屏。</summary>
    const int DupMs = 2000;

    static CanvasLayer _layer;
    static PanelContainer _box;
    static Label _label;
    static bool _hooked;

    static string _text = "";
    static string _lastText = "";
    static int _shownMs;
    static int _lastSameMs;

    /// <summary>手动弹一条（模块内部用；也会被 OnNotice 自动调用）。</summary>
    public static void Show(string s)
    {
        try
        {
            if (string.IsNullOrEmpty(s)) return;
            if (s == _lastText && _shownMs != 0 && NowMs() - _lastSameMs < DupMs) return;
            _lastText = s;
            _lastSameMs = NowMs();
            _text = s;
            _shownMs = NowMs();
        }
        catch { }
    }

    static int NowMs() { return (int)(Time.GetTicksMsec() & 0x7FFFFFFF); }

    public static void Tick(Node root)
    {
        try
        {
            TickCore(root);
        }
        catch { }
    }

    static void TickCore(Node root)
    {
        if (root == null || !GodotObject.IsInstanceValid(root)) return;

        if (!_hooked)
        {
            _hooked = true;
            try { NetSession.OnNotice += Show; } catch { }
        }

        Ensure(root);
        if (_layer == null || !GodotObject.IsInstanceValid(_layer)) return;

        bool show = _shownMs != 0 && NowMs() - _shownMs <= LifeMs;
        _layer.Visible = show;
        if (!show) return;

        if (_label != null && GodotObject.IsInstanceValid(_label) && _label.Text != _text)
            _label.Text = _text;
        Layout(root);
    }

    static void Ensure(Node root)
    {
        if (_layer != null && GodotObject.IsInstanceValid(_layer)) return;

        _layer = new CanvasLayer { Name = "NetToastLayer", Layer = 155, Visible = false };
        root.AddChild(_layer);

        var sb = new StyleBoxFlat();
        sb.BgColor = new Color(0.09f, 0.11f, 0.13f, 0.94f);
        sb.SetBorderWidthAll(2);
        sb.BorderColor = new Color(0.42f, 0.72f, 0.62f, 0.95f);
        sb.SetCornerRadiusAll(10);
        sb.SetContentMarginAll(10);

        _box = new PanelContainer { Name = "NetToastBox", MouseFilter = Control.MouseFilterEnum.Ignore };
        _box.AddThemeStyleboxOverride("panel", sb);
        _layer.AddChild(_box);

        _label = new Label
        {
            Text = "",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Modulate = new Color(0.94f, 1f, 0.97f)
        };
        _box.AddChild(_label);
    }

    /// <summary>居中靠下摆放。用固定锚点（0.5 / 1.0）+ 按内容最小尺寸算偏移，
    /// 不依赖 LayoutPreset（那个会重置 offset，在 CanvasLayer 下量不准）。</summary>
    static void Layout(Node root)
    {
        try
        {
            if (_box == null || !GodotObject.IsInstanceValid(_box)) return;
            var vp = root.GetViewport();
            Vector2 vs = vp != null ? vp.GetVisibleRect().Size : new Vector2(1280, 720);
            if (vs.X < 10 || vs.Y < 10) vs = new Vector2(1280, 720);

            float maxW = vs.X - 60f;
            if (maxW < 200f) maxW = 200f;
            if (maxW > 620f) maxW = 620f;
            if (_label != null && GodotObject.IsInstanceValid(_label))
                _label.CustomMinimumSize = new Vector2(maxW - 24f, 0);

            Vector2 ms = _box.GetCombinedMinimumSize();
            float w = ms.X, h = ms.Y;
            if (w < 120f) w = 120f;

            _box.AnchorLeft = 0.5f;
            _box.AnchorRight = 0.5f;
            _box.AnchorTop = 1f;
            _box.AnchorBottom = 1f;
            _box.OffsetLeft = -w * 0.5f;
            _box.OffsetRight = w * 0.5f;
            _box.OffsetTop = -(h + 84f);
            _box.OffsetBottom = -84f;
        }
        catch { }
    }
}
