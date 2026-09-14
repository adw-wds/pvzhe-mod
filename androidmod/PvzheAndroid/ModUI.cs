using Godot;

namespace PvzheMod
{
    /// <summary>
    /// MOD 设置按钮（任意界面显示、可拖动）+ 调节面板。
    /// 动态创建，全部用静态方法处理信号（避免闭包类，便于合并进主程序集）。
    /// </summary>
    public static class ModUI
    {
        private static CanvasLayer _uiLayer;
        private static Button _btn;
        private static bool _uiHidden;      // 悬浮窗隐藏状态（音量+开关）
        private static bool _volPrev;       // 音量加键边沿检测
        private static Label _uiHint;       // 右上角快捷键提示
        private static Label _consoleHint;  // 右上角控制台快捷键提示
        private static bool _dragging;
        private static Vector2 _dragOffset;
        private static string[] _skinCards = new string[0];   // 皮肤扫描结果（分帧，防假死）
        private static PanelContainer _panel;
        private static bool _panelDragging;
        private static Vector2 _panelDragOffset;
        // 内容区任意位置触摸拖动滚动（手机版：不再只能拖右侧滑块）
        private static ScrollContainer _scrollDragTarget;   // 正在触摸滚动的容器
        private static float _scrollDragStartY;             // 触摸起点 Y（视口坐标）
        private static int _scrollDragStartVal;             // 起点滚动偏移
        private static float _scrollDragScale = 1f;         // 触摸坐标缩放（视口/窗口）
        // 左右分栏：左导航 + 右功能页（页面叠放，切换上滑动画）
        private static readonly System.Collections.Generic.List<ScrollContainer> _pages = new();
        private static readonly System.Collections.Generic.List<Button> _navButtons = new();
        private static int _pageIndex;
        private static bool _pageAnimating;              // 防动画中连点
        private static Control _pagesHost;                  // 右侧页面容器（叠放各分类页）
        private static VBoxContainer _navBox;               // 左侧导航栏
        // 全局触摸滚动（在 GUI 处理前拦截，不受按钮 grab 影响——点哪里都能划）
        private static ScrollContainer _touchScroll;
        private static ScrollContainer _navScroll;          // 左侧导航滚动容器
        private static bool _touchDown;
        private static float _touchStartY;
        private static int _touchStartVal;
        private static LineEdit _danmakuEdit;
        private static LineEdit _trickPoolEdit;
        private static Vector2 _lastSize;
        private static Vector2 _btnRel = new Vector2(0.5f, 0.15f);   // 按钮相对视口的比例位置（自适应窗口）

        private static CanvasLayer _toastLayer;                       // 功能提示弹窗层（玻璃拟态，共享）
        private static readonly System.Collections.Generic.List<PanelContainer> _toastStack = new(); // 当前显示的弹窗（从旧到新，垂直堆叠）
        private static float _toastHue;                               // 弹窗文字律动色相（0~1，彩虹）
        private static readonly System.Collections.Generic.HashSet<ulong> _toastHooked = new(); // 已订阅 Toggled 的开关（防重复）
        private static int _toastHookTimer;                           // 弹窗 hook 降频器（每 15 帧）
        private const float _toastStep = 56f;                         // 弹窗堆叠步进（高度+间距）
        // 不弹窗的辅助按钮文本（颜色样式/导航tab/-+）
        private static readonly System.Collections.Generic.HashSet<string> _toastExclude = new()
        {
            "彩虹","红金","蓝紫","霓虹","粉彩","青绿","纯白","火焰","薄荷","樱花","紫罗兰","海洋","日落","极光","森林","柠檬",
            "关于","文字","趣味","皮肤",
            "-","+"
        };

        /// <summary>开关弹窗入口：显示「功能名 已开启/已关闭」。</summary>
        static void ShowFuncToast(string name, bool on)
        {
            ShowToastText(name + (on ? " 已开启" : " 已关闭"));
        }

        /// <summary>玻璃拟态弹窗：从屏幕外滑入 → 停留 1 秒 → 滑出。多个功能往下垂直堆叠，上面的消失后下面自动上移补位；文字彩色律动（常驻不能关）。</summary>
        static void ShowToastText(string text)
        {
            try
            {
                if (_toastLayer == null || !GodotObject.IsInstanceValid(_toastLayer))
                {
                    Node parent = (_uiLayer != null && GodotObject.IsInstanceValid(_uiLayer)) ? _uiLayer : null;
                    if (parent == null)
                    {
                        var tree = Godot.Engine.GetMainLoop() as SceneTree;
                        parent = tree != null ? tree.Root : null;
                    }
                    if (parent == null) { Bootstrap.Log("弹窗: 父节点为空，无法显示"); return; }
                    _toastLayer = new CanvasLayer();
                    _toastLayer.Name = "FuncToast";
                    _toastLayer.Layer = 150;
                    _toastLayer.ProcessMode = Node.ProcessModeEnum.Always;
                    parent.AddChild(_toastLayer);
                }
                // 堆叠位置：从上往下排（16 起步，每个 +步进）
                float y = 16f + _toastStack.Count * _toastStep;
                var panel = new PanelContainer();
                var sb = new StyleBoxFlat();
                sb.BgColor = new Color(0.93f, 0.97f, 1f, 0.16f);   // 半透明白（毛玻璃质感）
                sb.SetBorderWidthAll(1);
                sb.BorderColor = new Color(1f, 1f, 1f, 0.5f);
                sb.SetCornerRadiusAll(12);
                sb.ShadowColor = new Color(0f, 0f, 0f, 0.35f);
                sb.ShadowSize = 12;
                sb.SetContentMarginAll(14);
                panel.AddThemeStyleboxOverride("panel", sb);
                panel.MouseFilter = Control.MouseFilterEnum.Ignore;   // 不拦截点击
                panel.CustomMinimumSize = new Vector2(0f, 46f);       // 统一高度，堆叠整齐
                var label = new Label();
                label.Text = text;
                label.AddThemeFontSizeOverride("font_size", 17);
                label.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.65f));
                label.AddThemeConstantOverride("shadow_offset_x", 1);
                label.AddThemeConstantOverride("shadow_offset_y", 1);
                panel.AddChild(label);
                _toastLayer.AddChild(panel);
                _toastStack.Add(panel);
                var start = new Vector2(-320f, y);
                var end = new Vector2(16f, y);
                panel.Position = start;
                var tween = panel.CreateTween();
                tween.TweenProperty(panel, "position", end, 0.35f).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
                tween.TweenInterval(1.0f);
                tween.TweenProperty(panel, "position", start, 0.35f).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
                tween.TweenCallback(Callable.From(() => RemoveToast(panel)));
                Bootstrap.Log("弹窗: " + text);
            }
            catch (System.Exception ex) { Bootstrap.Log("弹窗异常: " + ex.Message); }
        }

        /// <summary>弹窗滑出后移除，并让下方弹窗上移补位（平滑）。</summary>
        static void RemoveToast(PanelContainer panel)
        {
            try
            {
                _toastStack.Remove(panel);
                if (GodotObject.IsInstanceValid(panel)) panel.QueueFree();
                float y = 16f;
                for (int i = 0; i < _toastStack.Count; i++)
                {
                    var p = _toastStack[i];
                    if (p == null || !GodotObject.IsInstanceValid(p)) { _toastStack.RemoveAt(i); i--; continue; }
                    float ny = y;
                    y += _toastStep;
                    if (Mathf.Abs(p.Position.Y - ny) > 1f)
                    {
                        var tw = p.CreateTween();
                        tw.TweenProperty(p, "position:y", ny, 0.28f).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
                    }
                }
            }
            catch { }
        }

        /// <summary>递归遍历 MOD UI 层所有开关/点按式按钮，订阅变化→弹提示（面板重建后新控件自动被钩上）。</summary>
        static void HookToastTree(Node node)
        {
            try
            {
                int hooked = 0;
                foreach (var child in node.GetChildren())
                {
                    if (child is CheckButton cb)
                    {
                        var id = cb.GetInstanceId();
                        if (_toastHooked.Add(id)) { cb.Toggled += v => { ShowFuncToast(cb.Text, v); ModSettings.Save(); }; hooked++; }
                    }
                    else if (child is Button bt)
                    {
                        // 点按式功能按钮也弹提示（排除样式/导航/-+ 等辅助按钮）
                        if (!IsToastExcluded(bt))
                        {
                            var id = bt.GetInstanceId();
                            if (_toastHooked.Add(id)) { bt.Pressed += () => ShowToastText(bt.Text + " 已执行"); hooked++; }
                        }
                    }
                    HookToastTree(child);
                }
                if (hooked > 0) Bootstrap.Log("弹窗hook: 新订阅 " + hooked + " 个");
            }
            catch (System.Exception ex) { Bootstrap.Log("弹窗hook异常: " + ex.Message); }
        }

        /// <summary>辅助按钮（MOD主按钮/样式/导航/-+等）不弹提示。</summary>
        static bool IsToastExcluded(Button bt)
        {
            try
            {
                var t = bt.Text;
                if (string.IsNullOrEmpty(t)) return true;
                if (t.StartsWith("MOD")) return true;      // MOD 悬浮主按钮
                if (t.Contains("B站")) return true;        // B站跳转
                if (_toastExclude.Contains(t)) return true;  // 颜色样式/导航tab/-+
                return false;
            }
            catch { return true; }
        }

        /// <summary>每帧驱动：弹窗文字彩色律动 + hook（由 GlobalColor 每帧调用；Ensure 每 30 帧太慢会导致面板刚开点开关 hook 不上）。</summary>
        public static void TickToast(Node root)
        {
            try
            {
                CheckUIToggle();   // 每帧检测音量+：开关悬浮窗
                // 弹窗文字彩色律动（彩虹渐变，所有当前弹窗一起律动）
                if (_toastStack.Count > 0)
                {
                    _toastHue += 0.022f;
                    if (_toastHue > 1f) _toastHue -= 1f;
                    var c = Color.FromHsv(_toastHue, 1f, 1f);
                    for (int i = 0; i < _toastStack.Count; i++)
                    {
                        var p = _toastStack[i];
                        if (p == null || !GodotObject.IsInstanceValid(p)) continue;
                        if (p.GetChildCount() > 0 && p.GetChild(0) is Label lb)
                            lb.AddThemeColorOverride("font_color", c);
                    }
                }
                if (++_toastHookTimer % 15 != 0) return;
                if (_uiLayer == null || !GodotObject.IsInstanceValid(_uiLayer)) return;
                HookToastTree(_uiLayer);
            }
            catch { }
        }

        /// <summary>由 OnFrame 每帧调用：把按钮放到顶层 CanvasLayer，永不消失、不被游戏 UI 遮挡。</summary>
        /// <summary>每帧检测音量加键：按下切换悬浮窗显示/隐藏（边沿触发，不连发）。</summary>
        static void CheckUIToggle()
        {
            try
            {
                bool k = Input.IsPhysicalKeyPressed(Key.Volumeup) || Input.IsKeyPressed(Key.Volumeup);
                if (k && !_volPrev) ToggleUI();
                _volPrev = k;
            }
            catch { }
        }

        /// <summary>切换悬浮窗（MOD 按钮 + 面板）显示/隐藏。</summary>
        static void ToggleUI()
        {
            _uiHidden = !_uiHidden;
            if (_uiLayer != null && GodotObject.IsInstanceValid(_uiLayer))
                _uiLayer.Visible = !_uiHidden;
            Bootstrap.Log("ModUI: 悬浮窗 " + (_uiHidden ? "已隐藏" : "已显示") + "（音量+）");
        }

        public static void Ensure(Node root)
        {
            try
            {
                if (root == null || !GodotObject.IsInstanceValid(root)) return;
                var tree = root.GetTree();
                if (tree == null || tree.Root == null) return;

                // 顶层 UI 层（挂在场景树根，场景切换不销毁）
                if (_uiLayer == null || !GodotObject.IsInstanceValid(_uiLayer))
                {
                    _uiLayer = new CanvasLayer();
                    _uiLayer.Name = "PvzModUI";
                    _uiLayer.Layer = 100;
                    // 关键：图鉴/对话框会 GetTree().Paused=true 暂停游戏，
                    // 必须 Always 才能继续接收点击（否则按钮在图鉴界面点不动）
                    _uiLayer.ProcessMode = Godot.Node.ProcessModeEnum.Always;
                    tree.Root.AddChild(_uiLayer);
                }
                // 用视口尺寸（CanvasLayer 控件用视口坐标）
                var rect = tree.Root.GetViewport().GetVisibleRect();
                var vsize = rect.Size;
                // 显眼位置提示：音量+开关悬浮窗
                if (_uiHint == null || !GodotObject.IsInstanceValid(_uiHint))
                {
                    _uiHint = new Label();
                    _uiHint.Text = "按 音量+ 开关悬浮窗";
                    _uiHint.Position = new Vector2(vsize.X - 170, 6);
                    _uiHint.AddThemeFontSizeOverride("font_size", 12);
                    _uiHint.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.4f, 0.95f));
                    _uiHint.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.8f));
                    _uiHint.AddThemeConstantOverride("shadow_offset_x", 1);
                    _uiHint.AddThemeConstantOverride("shadow_offset_y", 1);
                    _uiLayer.AddChild(_uiHint);
                }

                // 控制台快捷键提示（紧挨悬浮窗提示下方）
                if (_consoleHint == null || !GodotObject.IsInstanceValid(_consoleHint))
                {
                    _consoleHint = new Label();
                    _consoleHint.Text = "控制台：音量- 开关";
                    _consoleHint.Position = new Vector2(vsize.X - 200, 26);
                    _consoleHint.AddThemeFontSizeOverride("font_size", 12);
                    _consoleHint.AddThemeColorOverride("font_color", new Color(0.7f, 1f, 0.8f, 0.95f));
                    _consoleHint.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.8f));
                    _consoleHint.AddThemeConstantOverride("shadow_offset_x", 1);
                    _consoleHint.AddThemeConstantOverride("shadow_offset_y", 1);
                    _uiLayer.AddChild(_consoleHint);
                }

                if (_btn != null && GodotObject.IsInstanceValid(_btn))
                {
                    // 只在窗口尺寸变化时按相对位置重定位（平时不动，避免跳动）
                    if (_lastSize != vsize)
                    {
                        _lastSize = vsize;
                        _btn.Position = new Vector2(vsize.X * _btnRel.X - 75, vsize.Y * _btnRel.Y);
                    }
                    return;
                }

                var btn = new Button();
                btn.Name = "ModSettingsBtn";
                btn.Text = "MOD v" + ModSettings.Version;
                // 游戏窗口内：按相对位置（默认水平居中、垂直 15%）
                btn.Position = new Vector2(vsize.X * _btnRel.X - 85, vsize.Y * _btnRel.Y);
                btn.Size = new Vector2(170, 44);
                // 悬浮按钮：液态玻璃（半透明磨砂 + 细亮边 + 圆角胶囊 + 柔光阴影）
                var bsb = new StyleBoxFlat();
                bsb.BgColor = new Color(0.05f, 0.08f, 0.055f, 0.62f);
                bsb.BorderColor = new Color(1f, 1f, 1f, 0.25f);
                bsb.SetBorderWidthAll(1);
                bsb.SetCornerRadiusAll(22);
                bsb.ShadowColor = new Color(0f, 0f, 0f, 0.4f);
                bsb.ShadowSize = 10;
                btn.AddThemeStyleboxOverride("normal", bsb);
                btn.AddThemeStyleboxOverride("hover", bsb);
                btn.AddThemeStyleboxOverride("pressed", bsb);
                btn.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
                btn.AddThemeColorOverride("font_color", new Color(0.9f, 1f, 0.8f));
                btn.GuiInput += OnBtnGuiInput;
                btn.Pressed += OnSettingsPressed;
                _uiLayer.AddChild(btn);
                _btn = btn;
                _lastSize = vsize;
                Bootstrap.Log("ModUI: 按钮已创建 视口=" + vsize.X + "x" + vsize.Y);
            }
            catch (System.Exception ex) { Bootstrap.Log("ModUI 异常: " + ex.Message); }
        }

        /// <summary>窗口尺寸变化时校正按钮/面板位置，确保始终在屏幕内（自适应窗口大小）。</summary>
        static void CheckWindowSize(Vector2 size)
        {
            try
            {
                if (_lastSize == size) return;
                _lastSize = size;
                // 按钮：默认吸附右上角；若超出屏幕则移回右上角，否则只做边界限制
                if (_btn != null && GodotObject.IsInstanceValid(_btn))
                {
                    // 只做边界限制（不拉回中间，避免四角按钮被强制移走）
                    _btn.Position = new Vector2(
                        Mathf.Max(0, Mathf.Min(_btn.Position.X, size.X - _btn.Size.X)),
                        Mathf.Max(0, Mathf.Min(_btn.Position.Y, size.Y - _btn.Size.Y)));
                }
                // 面板：只做边界限制
                if (_panel != null && GodotObject.IsInstanceValid(_panel))
                {
                    _panel.Position = new Vector2(
                        Mathf.Max(0, Mathf.Min(_panel.Position.X, size.X - _panel.Size.X)),
                        Mathf.Max(0, Mathf.Min(_panel.Position.Y, size.Y - _panel.Size.Y)));
                }
            }
            catch { }
        }

        /// <summary>启动分帧皮肤扫描（打开面板不自动扫描——同步扫描触发主线程加载所有卡角色会假死）。
        /// 结果通过 GameCheats.OnSkinScanDone 回调（主线程帧回调）更新 UI。</summary>
        static void StartSkinScan(OptionButton cardSel, OptionButton skinSel, Button scanBtn)
        {
            try
            {
                if (scanBtn != null && GodotObject.IsInstanceValid(scanBtn)) { scanBtn.Text = "扫描中..."; scanBtn.Disabled = true; }
                GameCheats.OnSkinScanDone = res =>
                {
                    try
                    {
                        _skinCards = res;
                        if (cardSel != null && GodotObject.IsInstanceValid(cardSel))
                        {
                            cardSel.Clear();
                            cardSel.AddItem("（" + res.Length + " 张卡有皮肤）", 0);
                            for (int i = 0; i < res.Length; i++)
                                cardSel.AddItem(GameCheats.GetPacketDisplayName(res[i]), i + 1);
                        }
                        if (skinSel != null && GodotObject.IsInstanceValid(skinSel)) { skinSel.Clear(); skinSel.AddItem("（先选卡牌）", 0); }
                        if (scanBtn != null && GodotObject.IsInstanceValid(scanBtn)) { scanBtn.Text = "重新扫描卡牌皮肤"; scanBtn.Disabled = false; }
                        Bootstrap.Log("ModUI: 扫描到 " + res.Length + " 张卡牌有皮肤");
                    }
                    catch { }
                };
                GameCheats.StartSkinScanFrameByFrame();
            }
            catch (System.Exception ex) { Bootstrap.Log("ModUI 启动皮肤扫描异常: " + ex.Message); }
        }

        /// <summary>拖动按钮：按住拖动（支持鼠标与手机触摸），松开停止。</summary>
        static void OnBtnGuiInput(InputEvent @event)
        {
            try
            {
                if (_btn == null || !GodotObject.IsInstanceValid(_btn)) return;
                if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
                {
                    _dragging = mb.Pressed;
                    if (_dragging)
                        _dragOffset = _btn.Position - _btn.GetGlobalMousePosition();
                }
                else if (@event is InputEventMouseMotion motion && _dragging)
                {
                    // 增量拖动：不依赖绝对坐标一致性（修复跳左上角/偏移）
                    _btn.Position += motion.Relative;
                    var tree = _btn.GetTree();
                    if (tree != null && tree.Root != null)
                    {
                        var vs = tree.Root.GetViewport().GetVisibleRect().Size;
                        if (vs.X > 1 && vs.Y > 1)
                            _btnRel = new Vector2(_btn.Position.X / vs.X, _btn.Position.Y / vs.Y);
                    }
                }
                else if (@event is InputEventScreenTouch st)
                {
                    // 手机触摸按下/抬起
                    _dragging = st.Pressed;
                    if (_dragging)
                        _dragOffset = _btn.Position - st.Position;
                }
                else if (@event is InputEventScreenDrag sd && _dragging)
                {
                    // 触摸坐标与控件坐标系可能不同（stretch），按 逻辑/物理 比例缩放增量
                    var tree = _btn.GetTree();
                    if (tree != null && tree.Root != null)
                    {
                        var vp = tree.Root.GetViewport();
                        var vs = vp.GetVisibleRect().Size;
                        var win = vp.GetWindow();
                        var ws = win != null ? win.Size : vs;
                        float sx = vs.X / Mathf.Max(1, ws.X), sy = vs.Y / Mathf.Max(1, ws.Y);
                        _btn.Position += new Vector2(sd.Relative.X * sx, sd.Relative.Y * sy);
                        GD.Print("PZM drag sd=" + sd.Position.X.ToString("0") + "," + sd.Position.Y.ToString("0")
                            + " rel=" + sd.Relative.X.ToString("0") + "," + sd.Relative.Y.ToString("0")
                            + " btn=" + _btn.Position.X.ToString("0") + "," + _btn.Position.Y.ToString("0")
                            + " vs=" + vs.X.ToString("0") + "," + vs.Y.ToString("0")
                            + " ws=" + ws.X.ToString("0") + "," + ws.Y.ToString("0"));
                        if (vs.X > 1 && vs.Y > 1)
                            _btnRel = new Vector2(_btn.Position.X / vs.X, _btn.Position.Y / vs.Y);
                    }
                }
            }
            catch (System.Exception ex) { Bootstrap.Log("ModUI 拖动异常: " + ex.Message); }
        }

        static void OnSettingsPressed()
        {
            try
            {
                Bootstrap.Log("ModUI: 面板构建开始");
                if (_uiLayer == null || !GodotObject.IsInstanceValid(_uiLayer)) return;
                if (_panel != null && GodotObject.IsInstanceValid(_panel))
                {
                    _panel.QueueFree(); _panel = null; _danmakuEdit = null;
                    ShowFloatingBtn();   // 关闭面板后恢复悬浮按钮
                    return;   // 再点一次关闭
                }
                HideFloatingBtn();   // 打开面板时隐藏悬浮按钮（避免被全屏面板遮挡）

                // ===== 手机版新 UI（重构 v2.1）：黑底大面板 + 顶栏logo/✕ + 左导航/右功能 =====
                var pc = new PanelContainer();
                pc.Name = "ModSettingsPanel";
                Vector2 vsz2 = new Vector2(1, 1);
                try { vsz2 = _uiLayer.GetTree().Root.GetViewport().GetVisibleRect().Size; } catch { }
                // 适配手机（竖屏）：上下长、两边宽 —— 宽 92%、高 90%（上下留安全边距，避开状态栏/底部手势区）
                Vector2 psize = new Vector2(vsz2.X * 0.92f, vsz2.Y * 0.90f);
                Vector2 ppos = new Vector2((vsz2.X - psize.X) / 2f, Mathf.Max(8, (vsz2.Y - psize.Y) / 2f - 10));
                pc.Position = ppos;
                pc.Size = psize;
                pc.GuiInput += OnPanelGuiInput;   // 面板可拖动（非内容区）
                _uiLayer.AddChild(pc);
                _panel = pc;

                // ---- 深色主题（黑底绿字，植物大战僵尸风）----
                try
                {
                    var theme = new Godot.Theme();
                    // 面板背景（纯黑底 + 细亮边 + 柔光阴影）
                    var psb = new StyleBoxFlat();
                    psb.BgColor = new Color(0.015f, 0.015f, 0.018f, 0.985f);
                    psb.BorderColor = new Color(1f, 1f, 1f, 0.22f);
                    psb.SetBorderWidthAll(1);
                    psb.SetCornerRadiusAll(16);
                    psb.ShadowColor = new Color(0f, 0f, 0f, 0.6f);
                    psb.ShadowSize = 16;
                    psb.ShadowOffset = new Vector2(0, 6);
                    pc.AddThemeStyleboxOverride("panel", psb);
                    // 文字颜色（绿）
                    var fg = new Color(0.8f, 0.97f, 0.65f);
                    theme.SetColor("font_color", "Label", fg);
                    theme.SetColor("font_color", "Button", new Color(0.92f, 1f, 0.85f));
                    theme.SetColor("font_color", "CheckButton", new Color(0.86f, 0.98f, 0.72f));
                    theme.SetColor("font_color", "OptionButton", new Color(0.86f, 0.98f, 0.72f));
                    theme.SetColor("font_color", "TabContainer", fg);
                    theme.SetColor("font_color", "LineEdit", new Color(0.9f, 1f, 0.8f));
                    theme.SetColor("font_hover_color", "Button", new Color(1f, 1f, 0.9f));
                    theme.SetColor("font_pressed_color", "Button", new Color(0.8f, 1f, 0.6f));
                    // 按钮深色
                    theme.SetStylebox("normal", "Button", MakeBtnBox(new Color(0.13f, 0.19f, 0.11f), new Color(0.3f, 0.52f, 0.24f)));
                    theme.SetStylebox("hover", "Button", MakeBtnBox(new Color(0.2f, 0.3f, 0.16f), new Color(0.4f, 0.65f, 0.3f)));
                    theme.SetStylebox("pressed", "Button", MakeBtnBox(new Color(0.09f, 0.13f, 0.08f), new Color(0.25f, 0.42f, 0.2f)));
                    theme.SetStylebox("focus", "Button", new StyleBoxEmpty());
                    // 开关滑钮
                    theme.SetStylebox("toggle_on", "CheckButton", MakeBtnBox(new Color(0.3f, 0.55f, 0.25f), new Color(0.5f, 0.85f, 0.4f)));
                    theme.SetStylebox("toggle_off", "CheckButton", MakeBtnBox(new Color(0.13f, 0.17f, 0.13f), new Color(0.3f, 0.35f, 0.28f)));
                    theme.SetStylebox("focus", "CheckButton", new StyleBoxEmpty());
                    // 标签页
                    theme.SetStylebox("tab_selected", "TabContainer", MakeBtnBox(new Color(0.1f, 0.15f, 0.09f), new Color(0.35f, 0.6f, 0.28f)));
                    theme.SetStylebox("tab_unselected", "TabContainer", MakeBtnBox(new Color(0.06f, 0.09f, 0.06f), new Color(0.2f, 0.3f, 0.18f)));
                    // 标签内容区：半透明磨砂内层（毛玻璃质感）
                    var tabPanel = new StyleBoxFlat();
                    tabPanel.BgColor = new Color(0.04f, 0.06f, 0.04f, 0.45f);
                    tabPanel.SetCornerRadiusAll(12);
                    theme.SetStylebox("panel", "TabContainer", tabPanel);
                    pc.Theme = theme;
                }
                catch { }

                // ---- 入场动画：从顶部滑入 + 由小到大 ----
                try
                {
                    var targetPos = pc.Position;
                    pc.Position = new Vector2(targetPos.X, targetPos.Y - 240);
                    pc.Scale = new Vector2(0.6f, 0.6f);
                    pc.PivotOffset = new Vector2(pc.Size.X / 2f, 10);
                    var tw = pc.CreateTween();
                    tw.SetParallel(true);
                    tw.TweenProperty(pc, "position", targetPos, 0.3f).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
                    tw.TweenProperty(pc, "scale", Vector2.One, 0.32f).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
                }
                catch { }

                // ===== 重构 v2.1：顶栏(logo+标题+✕) + 左右分栏(左导航+右功能页) =====
                _pages.Clear(); _navButtons.Clear(); _pageIndex = 0; _pagesHost = null; _navBox = null;
                var rootBox = new VBoxContainer();
                rootBox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                rootBox.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
                rootBox.AddThemeConstantOverride("separation", 6);
                pc.AddChild(rootBox);

                // ---- 顶栏：左上角 logo + 标题 + 右上角收起 ✕ ----
                var titleBar = new HBoxContainer();
                titleBar.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                titleBar.AddThemeConstantOverride("separation", 8);
                rootBox.AddChild(titleBar);
                // logo（左上角，只占一小块）
                var logo = new TextureRect();
                logo.CustomMinimumSize = new Vector2(52, 52);
                logo.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
                logo.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
                try
                {
                    var limg = Image.LoadFromFile("res://ModLogo.png");
                    if (limg != null)
                    {
                        var ltex = ImageTexture.CreateFromImage(limg);
                        logo.Texture = ltex;
                    }
                }
                catch { }
                titleBar.AddChild(logo);
                var titleLabel = new Label();
                titleLabel.Text = "杂交MOD 设置";
                titleLabel.VerticalAlignment = VerticalAlignment.Center;
                titleLabel.AddThemeFontSizeOverride("font_size", 16);
                titleLabel.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.4f));
                titleBar.AddChild(titleLabel);
                // 弹性占位，把 ✕ 推到右上角
                var spacer = new Control();
                spacer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                titleBar.AddChild(spacer);
                var closeBtn = new Button();
                closeBtn.Text = "✕";
                closeBtn.CustomMinimumSize = new Vector2(48, 48);
                closeBtn.Pressed += OnSettingsPressed;   // 复用开关：点 ✕ 关闭面板
                titleBar.AddChild(closeBtn);

                // ---- 主体：左导航 + 右功能页 ----
                var body = new HBoxContainer();
                body.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                body.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
                body.AddThemeConstantOverride("separation", 0);
                rootBox.AddChild(body);

                // ---- 左侧导航（分类按钮，竖排；外包滚动容器防小屏溢出）----
                var navScroll = new ScrollContainer();
                navScroll.CustomMinimumSize = new Vector2(94, 0);
                navScroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
                navScroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
                navScroll.GuiInput += ev => OnScrollGuiInput(navScroll, ev);   // 左侧也能触摸滚动
                body.AddChild(navScroll);
                _navScroll = navScroll;
                var nav = new VBoxContainer();
                nav.CustomMinimumSize = new Vector2(94, 0);
                nav.AddThemeConstantOverride("separation", 10);
                nav.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                navScroll.AddChild(nav);
                _navBox = nav;

                // ---- 右侧页面容器（叠放各分类页，切换时上滑/滑入动画）----
                var pages = new Control();
                pages.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                pages.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
                pages.ClipContents = true;   // 裁剪滑出部分
                body.AddChild(pages);
                _pagesHost = pages;

                // ===== 标签：关于（主页，快捷开关在最上面，一打开就能看到） =====
                var vbAbout = MakePage("关于");
                vbAbout.AddChild(Section("快捷开关"));
                var chkNoFlicker = new CheckButton();
                chkNoFlicker.Text = "禁止闪屏";
                chkNoFlicker.ButtonPressed = ModSettings.NoFlicker;
                chkNoFlicker.Toggled += OnNoFlickerToggled;
                vbAbout.AddChild(chkNoFlicker);
                var chkGlove = new CheckButton();
                chkGlove.Text = "手套挪动";
                chkGlove.ButtonPressed = ModSettings.GloveMode;
                chkGlove.Toggled += OnGloveToggled;
                vbAbout.AddChild(chkGlove);
                vbAbout.AddChild(new HSeparator());
                // 保存当前配置（把当前所有开关状态写入 user://modsettings.txt，重开游戏保持）
                var saveCfgBtn = new Button();
                saveCfgBtn.Text = "💾 保存当前配置";
                saveCfgBtn.Pressed += () =>
                {
                    ModSettings.Save();
                    ShowToastText("配置已保存");
                    Bootstrap.Log("ModUI: 已保存当前配置");
                };
                vbAbout.AddChild(saveCfgBtn);
                vbAbout.AddChild(Section("Mod 信息"));
                var info = new Label();
                info.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                info.Text = GetAboutText();
                vbAbout.AddChild(info);
                // 跳转按钮（点击直接打开浏览器跳转到UP主视频）
                var biliBtn = new Button();
                biliBtn.Text = "杂交MOD";
                biliBtn.Pressed += () => { try { Godot.OS.ShellOpen("https://www.bilibili.com/video/BV1Gx8g6cEyx"); } catch { } };
                vbAbout.AddChild(biliBtn);

                // ===== 标签：文字 =====
                var vb = MakePage("文字");
                vb.AddChild(Section("文字效果"));
                var chk = new CheckButton();
                chk.Text = "启用彩色";
                chk.ButtonPressed = ModSettings.Enabled;
                chk.Toggled += OnToggled;
                vb.AddChild(chk);

                var spdLabel = new Label();
                spdLabel.Text = "渐变速度";
                vb.AddChild(spdLabel);
                var speedRow = new HBoxContainer();
                speedRow.AddThemeConstantOverride("separation", 12);
                vb.AddChild(speedRow);
                var speed = MakeSlider();
                speed.MinValue = 0.1; speed.MaxValue = 4.0; speed.Step = 0.1;
                speed.Value = ModSettings.Speed;
                speed.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                speed.ValueChanged += OnSpeedChanged;
                speedRow.AddChild(speed);
                var speedVal = new Label();
                speedVal.Text = ModSettings.Speed.ToString("0.0");
                speedVal.AddThemeFontSizeOverride("font_size", 13);
                speedVal.CustomMinimumSize = new Vector2(48, 0);
                speed.ValueChanged += v => speedVal.Text = v.ToString("0.0");
                speedRow.AddChild(speedVal);

                var stLabel = new Label();
                stLabel.Text = "颜色风格";
                vb.AddChild(stLabel);
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 12);
                vb.AddChild(row);
                var b0 = new Button(); b0.Text = "彩虹"; b0.Pressed += OnStyle0; row.AddChild(b0);
                var b1 = new Button(); b1.Text = "红金"; b1.Pressed += OnStyle1; row.AddChild(b1);
                var b2 = new Button(); b2.Text = "蓝紫"; b2.Pressed += OnStyle2; row.AddChild(b2);
                var b3 = new Button(); b3.Text = "霓虹"; b3.Pressed += OnStyle3; row.AddChild(b3);
                var b4 = new Button(); b4.Text = "粉彩"; b4.Pressed += OnStyle4; row.AddChild(b4);
                var b5 = new Button(); b5.Text = "青绿"; b5.Pressed += OnStyle5; row.AddChild(b5);
                var b6 = new Button(); b6.Text = "纯白"; b6.Pressed += OnStyle6; row.AddChild(b6);
                var b7 = new Button(); b7.Text = "火焰"; b7.Pressed += OnStyle7; row.AddChild(b7);
                var b8 = new Button(); b8.Text = "薄荷"; b8.Pressed += OnStyle8; row.AddChild(b8);
                var b9 = new Button(); b9.Text = "樱花"; b9.Pressed += OnStyle9; row.AddChild(b9);
                var b10 = new Button(); b10.Text = "紫罗兰"; b10.Pressed += OnStyle10; row.AddChild(b10);
                var b11 = new Button(); b11.Text = "海洋"; b11.Pressed += OnStyle11; row.AddChild(b11);
                var b12 = new Button(); b12.Text = "日落"; b12.Pressed += OnStyle12; row.AddChild(b12);
                var b13 = new Button(); b13.Text = "极光"; b13.Pressed += OnStyle13; row.AddChild(b13);
                var b14 = new Button(); b14.Text = "森林"; b14.Pressed += OnStyle14; row.AddChild(b14);
                var b15 = new Button(); b15.Text = "柠檬"; b15.Pressed += OnStyle15; row.AddChild(b15);

                var chkBG = new CheckButton();
                chkBG.Text = "背景彩虹";
                chkBG.ButtonPressed = ModSettings.BGEnabled;
                chkBG.Toggled += OnBGToggled;
                vb.AddChild(chkBG);

                // ===== 标签：游戏 =====
                var vbGame = MakePage("游戏");
                vbGame.AddChild(Section("游戏修改"));
                var chkSun = new CheckButton();
                chkSun.Text = "无限阳光";
                chkSun.ButtonPressed = ModSettings.InfiniteSun;
                chkSun.Toggled += OnSunToggled;
                vbGame.AddChild(chkSun);
                // 自由加减阳光
                vbGame.AddChild(Section("阳光数量"));
                var sunRow = new HBoxContainer();
                sunRow.AddThemeConstantOverride("separation", 8);
                vbGame.AddChild(sunRow);
                var sunVal = new Label();
                sunVal.Text = "当前: ?";
                sunVal.CustomMinimumSize = new Vector2(90, 0);
                sunVal.AddThemeFontSizeOverride("font_size", 15);
                sunRow.AddChild(sunVal);
                var sunR200 = new Button(); sunR200.Text = "-200";
                var sunR20 = new Button(); sunR20.Text = "-20";
                var sunP20 = new Button(); sunP20.Text = "+20";
                var sunP200 = new Button(); sunP200.Text = "+200";
                sunR200.Pressed += () => { long n = GameCheats.AddSun(-200); if (n >= 0) sunVal.Text = "当前: " + n; };
                sunR20.Pressed += () => { long n = GameCheats.AddSun(-20); if (n >= 0) sunVal.Text = "当前: " + n; };
                sunP20.Pressed += () => { long n = GameCheats.AddSun(20); if (n >= 0) sunVal.Text = "当前: " + n; };
                sunP200.Pressed += () => { long n = GameCheats.AddSun(200); if (n >= 0) sunVal.Text = "当前: " + n; };
                sunRow.AddChild(sunR200); sunRow.AddChild(sunR20); sunRow.AddChild(sunP20); sunRow.AddChild(sunP200);
                // 输入框：直接输入阳光数并设为目标值
                var sunEdit = new LineEdit();
                sunEdit.Text = "100";
                sunEdit.PlaceholderText = "输入阳光数";
                sunEdit.CustomMinimumSize = new Vector2(130, 0);
                sunEdit.AddThemeFontSizeOverride("font_size", 15);
                var sunSetBtn = new Button();
                sunSetBtn.Text = "设为";
                System.Action setSun = () =>
                {
                    if (long.TryParse(sunEdit.Text.Trim(), out var target))
                    {
                        long cur = GameCheats.GetSunCount();
                        if (cur < 0) cur = 0;
                        long n = GameCheats.AddSun(target - cur);
                        if (n >= 0) sunVal.Text = "当前: " + n;
                    }
                };
                sunSetBtn.Pressed += () => setSun();
                sunEdit.TextSubmitted += _ => setSun();
                var sunEditRow = new HBoxContainer();
                sunEditRow.AddThemeConstantOverride("separation", 6);
                sunEditRow.AddChild(sunEdit);
                sunEditRow.AddChild(sunSetBtn);
                vbGame.AddChild(sunEditRow);
                var chkCoin = new CheckButton();
                chkCoin.Text = "无限金币";
                chkCoin.ButtonPressed = ModSettings.InfiniteCoin;
                chkCoin.Toggled += OnCoinToggled;
                vbGame.AddChild(chkCoin);
                var chkNc = new CheckButton();
                chkNc.Text = "卡片无冷却";
                chkNc.ButtonPressed = ModSettings.NoCooldown;
                chkNc.Toggled += OnNcToggled;
                vbGame.AddChild(chkNc);
                AddSwitch(vbGame, "炮类无冷却", ModSettings.CannonNoCooldown, OnCannonToggled, "玉米加农炮等连发");
                AddSwitch(vbGame, "无视僵尸进家", ModSettings.IgnoreHouse, OnHouseToggled, "僵尸进家不失败");
                AddSwitch(vbGame, "无视警戒线", ModSettings.IgnoreWarningLine, OnWarnToggled, "僵尸碰到警戒线不失败");
                var chkPurple = new CheckButton();
                chkPurple.Text = "无视紫卡限制";
                chkPurple.ButtonPressed = ModSettings.IgnorePurple;
                chkPurple.Toggled += OnPurpleToggled;
                vbGame.AddChild(chkPurple);
                AddSwitch(vbGame, "全植物全图射程", ModSettings.PlantFullRange, on => { ModSettings.PlantFullRange = on; Bootstrap.Log("ModUI: 全图射程=" + on); }, "任何位置都能攻击全图");
                AddSwitch(vbGame, "无限水晶", ModSettings.CrystalInfinite, on => { ModSettings.CrystalInfinite = on; Bootstrap.Log("ModUI: 无限水晶=" + on); }, "在线关卡兑换货币 CrystalNum=10亿");
                AddSwitch(vbGame, "波次暂停", ModSettings.WavePaused, on => { ModSettings.WavePaused = on; Bootstrap.Log("ModUI: 波次暂停=" + on); }, "波次时间不推进");
                var fillBtn = new Button();
                fillBtn.Text = "刷全场";
                fillBtn.Pressed += () =>
                {
                    int p = GameCheats.SpawnAllField("plant");
                    int z = GameCheats.SpawnAllField("zombie");
                    int pr = GameCheats.SpawnAllField("prop");
                    int g = GameCheats.SpawnAllField("grave");
                    Bootstrap.Log("刷全场完成: 植物" + p + " 僵尸" + z + " 道具" + pr + " 墓碑" + g);
                };
                vbGame.AddChild(fillBtn);
                var netBtn = new Button();
                netBtn.Text = "联机面板（建房 / 加入 / 房主设置）";
                netBtn.Pressed += () => NetUI.Toggle();
                vbGame.AddChild(netBtn);
                var fillSelBtn = new Button();
                fillSelBtn.Text = "刷全场自选卡";
                fillSelBtn.Pressed += OnSpawnUI;
                vbGame.AddChild(fillSelBtn);
                // ===== 电脑版补齐的功能（大开关+说明）=====
                AddSwitch(vbGame, "Boss不鞠躬", ModSettings.BossNoBow, on => { ModSettings.BossNoBow = on; Bootstrap.Log("ModUI: Boss不鞠躬=" + on); }, "Boss出场不鞠躬");
                AddSwitch(vbGame, "禁止投小鬼", ModSettings.NoThrowImp, on => { ModSettings.NoThrowImp = on; Bootstrap.Log("ModUI: 禁止投小鬼=" + on); }, "僵尸不投掷小鬼");
                AddSwitch(vbGame, "禁止出僵尸", ModSettings.NoZombieSpawn, on => { ModSettings.NoZombieSpawn = on; Bootstrap.Log("ModUI: 禁止出僵尸=" + on); }, "场上僵尸全部删除");
                AddSwitch(vbGame, "三倍种植", ModSettings.PlantTriple, on => { ModSettings.PlantTriple = on; Bootstrap.Log("ModUI: 三倍种植=" + on); }, "种一个，相邻两行也种");
                AddSwitch(vbGame, "所有关卡可选卡", ModSettings.CanChooseAll, on => { ModSettings.CanChooseAll = on; Bootstrap.Log("ModUI: 所有关卡可选卡=" + on); }, "任意关卡自由选卡");
                AddSwitch(vbGame, "强制种子雨", ModSettings.ForceRain, on => { ModSettings.ForceRain = on; Bootstrap.Log("ModUI: 强制种子雨=" + on); }, "任何关卡都掉植物卡");
                AddSwitch(vbGame, "强制迷雾", ModSettings.ForceFog, on => { ModSettings.ForceFog = on; Bootstrap.Log("ModUI: 强制迷雾=" + on); }, "任何关卡都开迷雾");
                AddSwitch(vbGame, "无视红线", ModSettings.IgnoreRedLine, on => { ModSettings.IgnoreRedLine = on; Bootstrap.Log("ModUI: 无视红线=" + on); }, "红线区也能种植物/僵尸");
                AddSwitch(vbGame, "铲子生成樱桃", ModSettings.ShovelCherry, on => { ModSettings.ShovelCherry = on; Bootstrap.Log("ModUI: 铲子生成樱桃=" + on); }, "铲植物时随机僵尸脚下生成樱桃");
                AddSwitch(vbGame, "三叶草吹飞僵尸", ModSettings.BloverClearAll, on => { ModSettings.BloverClearAll = on; Bootstrap.Log("ModUI: 三叶草吹飞=" + on); }, "种三叶草时吹飞场上所有僵尸");
                AddSwitch(vbGame, "成长植物秒熟", ModSettings.InstantGrow, on => { ModSettings.InstantGrow = on; Bootstrap.Log("ModUI: 成长植物秒熟=" + on); }, "阳光菇/小喷菇等成长计时拉满，直接到最大形态");
                // ===== 飞贼 / 小丑 组合技 =====
                AddSwitch(vbGame, "飞贼秒偷", ModSettings.BungiFastGrab, on => { ModSettings.BungiFastGrab = on; Bootstrap.Log("ModUI: 飞贼秒偷=" + on); }, "飞贼一出现立即偷走最近植物");
                AddSwitch(vbGame, "飞贼无视保护伞", ModSettings.BungiIgnoreUmbrella, on => { ModSettings.BungiIgnoreUmbrella = on; Bootstrap.Log("ModUI: 飞贼无视保护伞=" + on); }, "保护伞挡不住飞贼");
                AddSwitch(vbGame, "小丑秒炸", ModSettings.JackboxFastBomb, on => { ModSettings.JackboxFastBomb = on; Bootstrap.Log("ModUI: 小丑秒炸=" + on); }, "小丑瞬移到目标面前立即爆炸");
                AddSwitch(vbGame, "全场僵尸吸附鼠标", ModSettings.ZombiesFollowMouse, on => { ModSettings.ZombiesFollowMouse = on; Bootstrap.Log("ModUI: 僵尸吸附鼠标=" + on); }, "僵尸被拉向鼠标位置");
                AddSwitch(vbGame, "飞贼偷后生成小丑", ModSettings.BungiSpawnJackbox, on => { ModSettings.BungiSpawnJackbox = on; Bootstrap.Log("ModUI: 偷后生成小丑=" + on); }, "飞贼偷完原地生成快炸小丑");
                var btnGrabAll = new Button();
                btnGrabAll.Text = "全图生成飞贼";
                btnGrabAll.Pressed += () => { int b = GameCheats.SpawnBungiAllField(); Bootstrap.Log("ModUI: 全图生成飞贼 x" + b); };
                vbGame.AddChild(btnGrabAll);
                // 炮类一次发射多枚（数量可调 1-10）
                try
                {
                    var hbC = new HBoxContainer();
                    var l1 = new Label(); l1.Text = "炮类一次发射:"; l1.AddThemeFontSizeOverride("font_size", 14);
                    var bm = new Button(); bm.Text = "-";
                    var lv = new Label(); lv.Text = ModSettings.CannonMultiShot.ToString(); lv.AddThemeFontSizeOverride("font_size", 15); lv.CustomMinimumSize = new Vector2(30, 0); lv.HorizontalAlignment = HorizontalAlignment.Center;
                    var bp = new Button(); bp.Text = "+";
                    bm.Pressed += () => { ModSettings.CannonMultiShot = System.Math.Max(1, ModSettings.CannonMultiShot - 1); lv.Text = ModSettings.CannonMultiShot.ToString(); Bootstrap.Log("ModUI: 炮类发射数量=" + ModSettings.CannonMultiShot); };
                    bp.Pressed += () => { ModSettings.CannonMultiShot = System.Math.Min(10, ModSettings.CannonMultiShot + 1); lv.Text = ModSettings.CannonMultiShot.ToString(); Bootstrap.Log("ModUI: 炮类发射数量=" + ModSettings.CannonMultiShot); };
                    hbC.AddChild(l1); hbC.AddChild(bm); hbC.AddChild(lv); hbC.AddChild(bp);
                    vbGame.AddChild(hbC);
                }
                catch { }
                AddSwitch(vbGame, "卡价不涨价", ModSettings.NoCostRise, on => { ModSettings.NoCostRise = on; Bootstrap.Log("ModUI: 卡价不涨价=" + on); }, "金卡/重复购买不涨价");
                AddSwitch(vbGame, "零消费", ModSettings.ZeroCost, on => { ModSettings.ZeroCost = on; Bootstrap.Log("ModUI: 零消费=" + on); }, "所有卡牌免费");
                AddSwitch(vbGame, "性能模式", ModSettings.PerfMode, on => { ModSettings.PerfMode = on; Bootstrap.Log("ModUI: 性能模式=" + on); }, "降低渲染/遍历开销，卡顿时开");
                // 文字颜色模式：0=彩虹律动 1=纯白 2=纯黑（手机友好：横向按钮组代替下拉）
                vbGame.AddChild(new Label { Text = "文字颜色模式" });
                var cmRow = new HBoxContainer();
                cmRow.AddThemeConstantOverride("separation", 12);
                vbGame.AddChild(cmRow);
                string[] cmLabels = { "彩虹律动", "纯白", "纯黑" };
                int cmCur = ModSettings.TextColorMode > 2 ? 0 : ModSettings.TextColorMode;
                for (int ci = 0; ci < 3; ci++)
                {
                    int captured = ci;
                    var cb = new Button();
                    cb.Text = cmLabels[ci];
                    cb.ToggleMode = true;
                    cb.ButtonPressed = (cmCur == ci);
                    cb.Pressed += () =>
                    {
                        ModSettings.TextColorMode = captured;
                        for (int k = 0; k < cmRow.GetChildCount(); k++)
                            if (cmRow.GetChild(k) is Button kb) kb.ButtonPressed = (k == captured);
                        Bootstrap.Log("ModUI: 文字颜色模式=" + ModSettings.TextColorMode);
                    };
                    cmRow.AddChild(cb);
                }
                var killBtn = new Button();
                killBtn.Text = "杀光当前僵尸";
                killBtn.Pressed += OnKillZombies;
                vbGame.AddChild(killBtn);
                var killPlantBtn = new Button();
                killPlantBtn.Text = "杀光当前植物";
                killPlantBtn.Pressed += OnKillPlants;
                vbGame.AddChild(killPlantBtn);
                var spawnBtn = new Button();
                spawnBtn.Text = "刷怪/刷卡选择栏";
                spawnBtn.Pressed += OnSpawnUI;
                vbGame.AddChild(spawnBtn);
                var winBtn = new Button();
                winBtn.Text = "立即胜利";
                winBtn.Pressed += OnInstantWin;
                vbGame.AddChild(winBtn);
                var allBtn = new Button();
                allBtn.Text = "一键通关全部";
                allBtn.Pressed += OnCompleteAll;
                vbGame.AddChild(allBtn);
                var dailyBtn = new Button();
                dailyBtn.Text = "一键通关每日挑战";
                dailyBtn.Pressed += OnCompleteDaily;
                vbGame.AddChild(dailyBtn);
                // 按模式一键通关（复刻官方 CommandManager）
                var modeRow = new HBoxContainer();
                modeRow.AddThemeConstantOverride("separation", 12);
                vbGame.AddChild(modeRow);
                string[] modes = { "Adventure", "Challenge", "MiniGame", "PuzzleGame", "Survival", "IZM2" };
                string[] modeNames = { "冒险", "挑战", "小游戏", "拼图", "生存", "IZM2" };
                for (int mi = 0; mi < modes.Length; mi++)
                {
                    var mb = new Button();
                    mb.Text = "通关" + modeNames[mi];
                    string m = modes[mi];
                    mb.Pressed += () => GameCheats.CompleteMode(m);
                    modeRow.AddChild(mb);
                }
                var shopBtn = new Button();
                shopBtn.Text = "商店全部物品";
                shopBtn.Pressed += OnShopAll;
                vbGame.AddChild(shopBtn);
                var fxRow = new HBoxContainer();
                fxRow.AddThemeConstantOverride("separation", 12);
                vbGame.AddChild(fxRow);
                var resetBrainBtn = new Button();
                resetBrainBtn.Text = "重置脑子";
                resetBrainBtn.Pressed += OnResetBrain;
                fxRow.AddChild(resetBrainBtn);
                var rainBtn = new Button(); rainBtn.Text = "下雨"; rainBtn.Pressed += () => GameCheats.ToggleScreenEffect("Rain", true); fxRow.AddChild(rainBtn);
                var cancelRainBtn = new Button(); cancelRainBtn.Text = "停雨"; cancelRainBtn.Pressed += () => GameCheats.ToggleScreenEffect("Rain", false); fxRow.AddChild(cancelRainBtn);
                var stormBtn = new Button(); stormBtn.Text = "风暴"; stormBtn.Pressed += () => GameCheats.ToggleScreenEffect("Storm", true); fxRow.AddChild(stormBtn);
                var cancelStormBtn = new Button(); cancelStormBtn.Text = "停风暴"; cancelStormBtn.Pressed += () => GameCheats.ToggleScreenEffect("Storm", false); fxRow.AddChild(cancelStormBtn);
                var skipBtn = new Button();
                skipBtn.Text = "跳过波次等待";
                skipBtn.Pressed += OnSkipWait;
                vbGame.AddChild(skipBtn);
                var finalBtn = new Button();
                finalBtn.Text = "跳到最终波";
                finalBtn.Pressed += OnSkipFinal;
                vbGame.AddChild(finalBtn);
                var unlockBtn = new Button();
                unlockBtn.Text = "解锁全部功能";
                unlockBtn.Pressed += OnUnlockAll;
                vbGame.AddChild(unlockBtn);
                var mowerBtn = new Button();
                mowerBtn.Text = "一键启动小推车";
                mowerBtn.Pressed += OnLaunchMowers;
                vbGame.AddChild(mowerBtn);
                var restoreMowerBtn = new Button();
                restoreMowerBtn.Text = "恢复小推车";
                restoreMowerBtn.Pressed += OnRestoreMowers;
                vbGame.AddChild(restoreMowerBtn);

                vbGame.AddChild(new HSeparator());
                vbGame.AddChild(Section("生成货币"));
                vbGame.AddChild(new Label { Text = "数量" });
                var curNumRow = new HBoxContainer();
                curNumRow.AddThemeConstantOverride("separation", 12);
                vbGame.AddChild(curNumRow);
                var curNum = MakeSlider();
                curNum.MinValue = 1; curNum.MaxValue = 100; curNum.Step = 1;
                curNum.Value = _currencyNum;
                curNum.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                curNum.ValueChanged += OnCurNumChanged;
                curNumRow.AddChild(curNum);
                var curNumVal = new Label();
                curNumVal.Text = _currencyNum.ToString();
                curNumVal.AddThemeFontSizeOverride("font_size", 13);
                curNumVal.CustomMinimumSize = new Vector2(48, 0);
                curNum.ValueChanged += v => curNumVal.Text = ((int)v).ToString();
                curNumRow.AddChild(curNumVal);
                var curRow = new HBoxContainer();
                curRow.AddThemeConstantOverride("separation", 12);
                vbGame.AddChild(curRow);
                var bYB = new Button(); bYB.Text = "银币"; bYB.Pressed += OnCurYB; curRow.AddChild(bYB);
                var bCoin = new Button(); bCoin.Text = "金币"; bCoin.Pressed += OnCurCoin; curRow.AddChild(bCoin);
                var bGold = new Button(); bGold.Text = "钻石"; bGold.Pressed += OnCurGold; curRow.AddChild(bGold);
                var bBag = new Button(); bBag.Text = "钱袋"; bBag.Pressed += OnCurBag; curRow.AddChild(bBag);
                var bCup = new Button(); bCup.Text = "奖杯"; bCup.Pressed += OnCurCup; curRow.AddChild(bCup);

                var almanacBtn = new CheckButton();
                almanacBtn.Text = "图鉴全解";
                almanacBtn.ButtonPressed = ModSettings.AlmanacAll;
                almanacBtn.Toggled += OnAlmanacToggled;
                vbGame.AddChild(almanacBtn);

                var consoleBtn = new CheckButton();
                consoleBtn.Text = "游戏控制台（原生作弊）";
                consoleBtn.ButtonPressed = ModSettings.ConsoleEnabled;
                consoleBtn.Toggled += OnConsoleToggled;
                vbGame.AddChild(consoleBtn);

                var openConsoleBtn = new Button();
                openConsoleBtn.Text = "打开控制台";
                openConsoleBtn.Pressed += () => GameCheats.OpenConsolePanel();
                vbGame.AddChild(openConsoleBtn);

                var gsLabel = new Label();
                gsLabel.Text = "游戏速度";
                vbGame.AddChild(gsLabel);
                var gsRow = new HBoxContainer();
                gsRow.AddThemeConstantOverride("separation", 12);
                vbGame.AddChild(gsRow);
                // ★ 联机中锁定：TimeScale 是每端本地量，两端倍速不同会让僵尸移动/波次节奏/
                //   判定全部漂移（即同步失败）。CheatPolicy 会把 GameSpeed 复位为 1.0，
                //   这里同时把滑块锁住，免得玩家拖完又被弹回去却不知道原因。
                bool gsLocked = CheatPolicy.IsLockedForUi;
                var gs = MakeSlider();
                gs.MinValue = 0.5; gs.MaxValue = 20.0; gs.Step = 0.5;
                gs.Value = gsLocked ? 1.0 : ModSettings.GameSpeed;
                gs.Editable = !gsLocked;
                gs.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                gs.ValueChanged += OnGameSpeedChanged;
                gsRow.AddChild(gs);
                var gsVal = new Label();
                gsVal.Text = gsLocked ? "锁定" : ModSettings.GameSpeed.ToString("0.0");
                gsVal.AddThemeFontSizeOverride("font_size", 13);
                gsVal.CustomMinimumSize = new Vector2(48, 0);
                gs.ValueChanged += v => gsVal.Text = v.ToString("0.0") + "x";
                gsRow.AddChild(gsVal);
                if (gsLocked)
                {
                    var gsHint = new Label();
                    gsHint.Text = "联机中已禁用加速（避免同步失败）";
                    gsHint.AddThemeFontSizeOverride("font_size", 12);
                    gsHint.Modulate = new Color(0.75f, 0.82f, 0.88f);
                    vbGame.AddChild(gsHint);
                }

                // ===== 标签：透视 =====
                var vbEsp = MakePage("透视");
                vbEsp.AddChild(Section("透视 / 大小"));
                var chkESP = new CheckButton();
                chkESP.Text = "开启透视框";
                chkESP.ButtonPressed = ModSettings.ESPEnabled;
                chkESP.Toggled += OnESPToggled;
                vbEsp.AddChild(chkESP);
                var chkESPZ = new CheckButton();
                chkESPZ.Text = "僵尸透视";
                chkESPZ.ButtonPressed = ModSettings.ESPZombie;
                chkESPZ.Toggled += OnESPZToggled;
                vbEsp.AddChild(chkESPZ);
                var chkESPP = new CheckButton();
                chkESPP.Text = "植物透视";
                chkESPP.ButtonPressed = ModSettings.ESPPlant;
                chkESPP.Toggled += OnESPPToggled;
                vbEsp.AddChild(chkESPP);
                var chkVase = new CheckButton();
                chkVase.Text = "透视罐子";
                chkVase.ButtonPressed = ModSettings.VaseESP;
                chkVase.Toggled += OnVaseToggled;
                vbEsp.AddChild(chkVase);
                var chkFog = new CheckButton();
                chkFog.Text = "迷雾透视";
                chkFog.ButtonPressed = ModSettings.FogESP;
                chkFog.Toggled += OnFogToggled;
                vbEsp.AddChild(chkFog);

                var zsLabel = new Label();
                zsLabel.Text = "僵尸大小";
                vbEsp.AddChild(zsLabel);
                var zsRow = new HBoxContainer();
                zsRow.AddThemeConstantOverride("separation", 12);
                vbEsp.AddChild(zsRow);
                var zs = MakeSlider();
                zs.MinValue = 0.5; zs.MaxValue = 3.0; zs.Step = 0.1;
                zs.Value = ModSettings.ZombieScale;
                zs.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                zs.ValueChanged += OnZombieScaleChanged;
                zsRow.AddChild(zs);
                var zsVal = new Label();
                zsVal.Text = ModSettings.ZombieScale.ToString("0.0");
                zsVal.AddThemeFontSizeOverride("font_size", 13);
                zsVal.CustomMinimumSize = new Vector2(48, 0);
                zs.ValueChanged += v => zsVal.Text = v.ToString("0.0") + "x";
                zsRow.AddChild(zsVal);

                var psLabel = new Label();
                psLabel.Text = "植物大小";
                vbEsp.AddChild(psLabel);
                var psRow = new HBoxContainer();
                psRow.AddThemeConstantOverride("separation", 12);
                vbEsp.AddChild(psRow);
                var ps = MakeSlider();
                ps.MinValue = 0.5; ps.MaxValue = 3.0; ps.Step = 0.1;
                ps.Value = ModSettings.PlantScale;
                ps.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                ps.ValueChanged += OnPlantScaleChanged;
                psRow.AddChild(ps);
                var psVal = new Label();
                psVal.Text = ModSettings.PlantScale.ToString("0.0");
                psVal.AddThemeFontSizeOverride("font_size", 13);
                psVal.CustomMinimumSize = new Vector2(48, 0);
                ps.ValueChanged += v => psVal.Text = v.ToString("0.0") + "x";
                psRow.AddChild(psVal);

                // ===== 标签：战斗 =====
                var vbCombat = MakePage("战斗");
                // ===== 常用快捷区（最上面，大开关+说明）=====
                vbCombat.AddChild(Section("常用"));
                AddSwitch(vbCombat, "无限阳光", ModSettings.InfiniteSun, OnSunToggled, "阳光不减少，随意种");
                AddSwitch(vbCombat, "无限金币", ModSettings.InfiniteCoin, OnCoinToggled, "金币不减少，随便买");
                AddSwitch(vbCombat, "卡片无冷却", ModSettings.NoCooldown, OnNcToggled, "卡牌秒放，不用等");
                AddSwitch(vbCombat, "植物无敌", ModSettings.PlantInvincible, OnPInvToggled, "植物不受伤");
                AddSwitch(vbCombat, "僵尸无敌", ModSettings.ZombieInvincible, OnZInvToggled, "僵尸不受伤");
                AddSwitch(vbCombat, "自动开火", ModSettings.PlantAutoFire, on => { ModSettings.PlantAutoFire = on; Bootstrap.Log("ModUI: 自动开火=" + on); }, "全图植物自动打僵尸");
                AddSwitch(vbCombat, "子弹追踪", ModSettings.BulletTrack, OnTrackToggled, "子弹自动追僵尸");
                AddSwitch(vbCombat, "随机子弹", ModSettings.BulletRandom, on => { ModSettings.BulletRandom = on; if (!on) GameCheats.StopAllTracking(); Bootstrap.Log("ModUI: 随机子弹=" + on); }, "子弹随机变种类");
                vbCombat.AddChild(new HSeparator());
                vbCombat.AddChild(Section("种植"));
                var chkOv = new CheckButton();
                chkOv.Text = "植物重叠种植";
                chkOv.ButtonPressed = ModSettings.PlantOverlap;
                chkOv.Toggled += OnOverlapToggled;
                vbCombat.AddChild(chkOv);
                var chkTer = new CheckButton();
                chkTer.Text = "无视地形";
                chkTer.ButtonPressed = ModSettings.IgnoreTerrain;
                chkTer.Toggled += OnTerrainToggled;
                vbCombat.AddChild(chkTer);
                var chkChomp = new CheckButton();
                chkChomp.Text = "大嘴花秒吞咽";
                chkChomp.ButtonPressed = ModSettings.ChomperFastSwallow;
                chkChomp.Toggled += on => { ModSettings.ChomperFastSwallow = on; Bootstrap.Log("ModUI: 大嘴花秒吞咽=" + on); };
                vbCombat.AddChild(chkChomp);

                vbCombat.AddChild(new HSeparator());
                vbCombat.AddChild(Section("攻速"));
                vbCombat.AddChild(new Label { Text = "植物攻速" });
                var pasRow = new HBoxContainer();
                pasRow.AddThemeConstantOverride("separation", 12);
                vbCombat.AddChild(pasRow);
                var pas = MakeSlider();
                pas.MinValue = 1.0; pas.MaxValue = 1000.0; pas.Step = 0.5;
                pas.Value = ModSettings.PlantAttackSpeed;
                pas.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                pas.ValueChanged += OnPlantASChanged;
                pasRow.AddChild(pas);
                var pasVal = new Label();
                pasVal.Text = ModSettings.PlantAttackSpeed.ToString("0.0");
                pasVal.AddThemeFontSizeOverride("font_size", 13);
                pasVal.CustomMinimumSize = new Vector2(48, 0);
                pas.ValueChanged += v => pasVal.Text = v.ToString("0.0") + "x";
                pasRow.AddChild(pasVal);
                vbCombat.AddChild(new Label { Text = "僵尸攻速" });
                var zasRow = new HBoxContainer();
                zasRow.AddThemeConstantOverride("separation", 12);
                vbCombat.AddChild(zasRow);
                var zas = MakeSlider();
                zas.MinValue = 1.0; zas.MaxValue = 1000.0; zas.Step = 0.5;
                zas.Value = ModSettings.ZombieAttackSpeed;
                zas.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                zas.ValueChanged += OnZombieASChanged;
                zasRow.AddChild(zas);
                var zasVal = new Label();
                zasVal.Text = ModSettings.ZombieAttackSpeed.ToString("0.0");
                zasVal.AddThemeFontSizeOverride("font_size", 13);
                zasVal.CustomMinimumSize = new Vector2(48, 0);
                zas.ValueChanged += v => zasVal.Text = v.ToString("0.0") + "x";
                zasRow.AddChild(zasVal);
                vbCombat.AddChild(new Label { Text = "刷怪倍数" });
                var smRow = new HBoxContainer();
                smRow.AddThemeConstantOverride("separation", 12);
                vbCombat.AddChild(smRow);
                var sm = MakeSlider();
                sm.MinValue = 1; sm.MaxValue = 10; sm.Step = 1;
                sm.Value = ModSettings.SpawnMultiplier;
                sm.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                sm.ValueChanged += OnSpawnMultChanged;
                smRow.AddChild(sm);
                var smVal = new Label();
                smVal.Text = ModSettings.SpawnMultiplier.ToString() + "x";
                smVal.AddThemeFontSizeOverride("font_size", 13);
                smVal.CustomMinimumSize = new Vector2(48, 0);
                sm.ValueChanged += v => smVal.Text = ((int)v).ToString() + "x";
                smRow.AddChild(smVal);

                vbCombat.AddChild(new HSeparator());
                vbCombat.AddChild(Section("血量 / 无敌"));
                vbCombat.AddChild(new Label { Text = "植物血量倍率（1~100）" });
                var phpRow = new HBoxContainer();
                phpRow.AddThemeConstantOverride("separation", 12);
                vbCombat.AddChild(phpRow);
                var php = MakeSlider();
                php.MinValue = 1.0; php.MaxValue = 100.0; php.Step = 1.0;
                php.Value = ModSettings.PlantHP;
                php.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                php.ValueChanged += OnPlantHPChanged;
                phpRow.AddChild(php);
                var phpVal = new Label();
                phpVal.Text = ModSettings.PlantHP.ToString("0.0");
                phpVal.AddThemeFontSizeOverride("font_size", 13);
                phpVal.CustomMinimumSize = new Vector2(48, 0);
                php.ValueChanged += v => phpVal.Text = v.ToString("0.0") + "x";
                phpRow.AddChild(phpVal);
                vbCombat.AddChild(new Label { Text = "僵尸血量倍率（1~100）" });
                var zhpRow = new HBoxContainer();
                zhpRow.AddThemeConstantOverride("separation", 12);
                vbCombat.AddChild(zhpRow);
                var zhp = MakeSlider();
                zhp.MinValue = 1.0; zhp.MaxValue = 100.0; zhp.Step = 1.0;
                zhp.Value = ModSettings.ZombieHP;
                zhp.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                zhp.ValueChanged += OnZombieHPChanged;
                zhpRow.AddChild(zhp);
                var zhpVal = new Label();
                zhpVal.Text = ModSettings.ZombieHP.ToString("0.0");
                zhpVal.AddThemeFontSizeOverride("font_size", 13);
                zhpVal.CustomMinimumSize = new Vector2(48, 0);
                zhp.ValueChanged += v => zhpVal.Text = v.ToString("0.0") + "x";
                zhpRow.AddChild(zhpVal);
                var chkPInv = new CheckButton();
                chkPInv.Text = "植物无敌";
                chkPInv.ButtonPressed = ModSettings.PlantInvincible;
                chkPInv.Toggled += OnPInvToggled;
                vbCombat.AddChild(chkPInv);
                var chkZInv = new CheckButton();
                chkZInv.Text = "僵尸无敌";
                chkZInv.ButtonPressed = ModSettings.ZombieInvincible;
                chkZInv.Toggled += OnZInvToggled;
                vbCombat.AddChild(chkZInv);

                vbCombat.AddChild(new HSeparator());
                vbCombat.AddChild(Section("禁止行动"));
                var chkPNoAtk = new CheckButton();
                chkPNoAtk.Text = "植物无法攻击";
                chkPNoAtk.ButtonPressed = ModSettings.PlantNoAttack;
                chkPNoAtk.Toggled += OnPNoAttackToggled;
                vbCombat.AddChild(chkPNoAtk);
                var chkZNoAtk = new CheckButton();
                chkZNoAtk.Text = "僵尸无法攻击";
                chkZNoAtk.ButtonPressed = ModSettings.ZombieNoAttack;
                chkZNoAtk.Toggled += OnZNoAttackToggled;
                vbCombat.AddChild(chkZNoAtk);
                var chkZNoMove = new CheckButton();
                chkZNoMove.Text = "僵尸无法移动";
                chkZNoMove.ButtonPressed = ModSettings.ZombieNoMove;
                chkZNoMove.Toggled += OnZNoMoveToggled;
                vbCombat.AddChild(chkZNoMove);
                vbCombat.AddChild(new HSeparator());
                vbCombat.AddChild(Section("魅惑"));
                var btnCharmP = new Button();
                btnCharmP.Text = "魅惑所有植物";
                btnCharmP.Pressed += OnCharmPlants;
                vbCombat.AddChild(btnCharmP);
                var btnCharmZ = new Button();
                btnCharmZ.Text = "魅惑所有僵尸";
                btnCharmZ.Pressed += OnCharmZombies;
                vbCombat.AddChild(btnCharmZ);
                var chkCharmZ = new CheckButton();
                chkCharmZ.Text = "魅惑僵尸";
                chkCharmZ.ButtonPressed = ModSettings.CharmZombie;
                chkCharmZ.Toggled += on => { ModSettings.CharmZombie = on; Bootstrap.Log("ModUI: 魅惑僵尸(持续)=" + on); };
                vbCombat.AddChild(chkCharmZ);
                var btnUncharm = new Button();
                btnUncharm.Text = "还原全部魅惑";
                btnUncharm.Pressed += OnUncharmAll;
                vbCombat.AddChild(btnUncharm);

                vbCombat.AddChild(new HSeparator());
                vbCombat.AddChild(Section("其他"));
                var clearLockBtn = new Button();
                clearLockBtn.Text = "清除锁定卡牌";
                clearLockBtn.Pressed += () =>
                {
                    var tree = _uiLayer != null && GodotObject.IsInstanceValid(_uiLayer) ? _uiLayer.GetTree() : null;
                    var root = tree != null ? tree.Root : null;
                    if (root != null) GameCheats.ForceUnlockPackets(root);
                    ShowToastText("已清除卡牌锁定");
                };
                vbCombat.AddChild(clearLockBtn);
                var clearBankBtn = new Button();
                clearBankBtn.Text = "清空卡槽";
                clearBankBtn.Pressed += () =>
                {
                    var tree = _uiLayer != null && GodotObject.IsInstanceValid(_uiLayer) ? _uiLayer.GetTree() : null;
                    var root = tree != null ? tree.Root : null;
                    if (root != null) GameCheats.ClearSeedBankAll(root);
                };
                vbCombat.AddChild(clearBankBtn);
                var chkAutoFire = new CheckButton();
                chkAutoFire.Text = "自动开火";
                chkAutoFire.ButtonPressed = ModSettings.PlantAutoFire;
                chkAutoFire.Toggled += on => { ModSettings.PlantAutoFire = on; Bootstrap.Log("ModUI: 自动开火=" + on); };
                vbCombat.AddChild(chkAutoFire);
                // 子弹三开关相互独立（不互斥）：追踪/跟随鼠标/随机可自由组合
                var chkTrack = new CheckButton();
                chkTrack.Text = "子弹追踪";
                chkTrack.ButtonPressed = ModSettings.BulletTrack;
                chkTrack.Toggled += OnTrackToggled;
                vbCombat.AddChild(chkTrack);
                var chkMouse = new CheckButton();
                chkMouse.Text = "子弹跟随鼠标";
                chkMouse.ButtonPressed = ModSettings.BulletFollowMouse;
                chkMouse.Toggled += on => { ModSettings.BulletFollowMouse = on; if (!on) GameCheats.StopAllTracking(); Bootstrap.Log("ModUI: 子弹跟随鼠标=" + on); };
                vbCombat.AddChild(chkMouse);
                var chkBulletRnd = new CheckButton();
                chkBulletRnd.Text = "随机子弹";
                chkBulletRnd.ButtonPressed = ModSettings.BulletRandom;
                chkBulletRnd.Toggled += on => { ModSettings.BulletRandom = on; if (!on) GameCheats.StopAllTracking(); Bootstrap.Log("ModUI: 随机子弹=" + on); };
                vbCombat.AddChild(chkBulletRnd);
                // 子弹类型修改：横向按钮组选择一种子弹，所有植物子弹替换成该种类（任意模式）
                vbCombat.AddChild(new HSeparator());
                vbCombat.AddChild(Section("子弹类型修改"));
                var btLabel = new Label();
                btLabel.Text = "把植物子弹改成：";
                vbCombat.AddChild(btLabel);
                var btRow = new HFlowContainer();
                btRow.AddThemeConstantOverride("h_separation", 12);
                btRow.AddThemeConstantOverride("v_separation", 12);
                vbCombat.AddChild(btRow);
                // "关闭（不改子弹）" 按钮
                var btOff = new Button();
                btOff.Text = "关闭";
                btOff.ToggleMode = true;
                btOff.ButtonPressed = (ModSettings.BulletType.Length == 0);
                btOff.Pressed += () =>
                {
                    ModSettings.BulletType = "";
                    for (int k = 0; k < btRow.GetChildCount(); k++)
                        if (btRow.GetChild(k) is Button kb) kb.ButtonPressed = (k == 0);
                    Bootstrap.Log("ModUI: 自定义子弹类型=关闭");
                };
                btRow.AddChild(btOff);
                string[] bkeys = GameCheats.GetProjectileKeys();
                if (bkeys.Length == 0)
                    btLabel.Text = "子弹列表为空：请进入战斗场景后重开面板自动加载";
                for (int i = 0; i < bkeys.Length; i++)
                {
                    string key = bkeys[i];
                    var b = new Button();
                    b.Text = GameCheats.TranslateBulletName(key);
                    b.ToggleMode = true;
                    b.ButtonPressed = (key == ModSettings.BulletType);
                    b.Pressed += () =>
                    {
                        ModSettings.BulletType = key;
                        string curName = GameCheats.TranslateBulletName(key);
                        for (int k = 0; k < btRow.GetChildCount(); k++)
                            if (btRow.GetChild(k) is Button kb) kb.ButtonPressed = (kb.Text == "关闭" ? key.Length == 0 : kb.Text == curName);
                        Bootstrap.Log("ModUI: 自定义子弹类型=" + ModSettings.BulletType);
                    };
                    btRow.AddChild(b);
                }
                var chkCol = new CheckButton();
                chkCol.Text = "种一个出一列";
                chkCol.ButtonPressed = ModSettings.PlantColumn;
                chkCol.Toggled += OnColumnToggled;
                vbCombat.AddChild(chkCol);
                var chkNoSleep = new CheckButton();
                chkNoSleep.Text = "蘑菇白天不睡";
                chkNoSleep.ButtonPressed = ModSettings.NoSleep;
                chkNoSleep.Toggled += OnNoSleepToggled;
                vbCombat.AddChild(chkNoSleep);
                var chkConv = new CheckButton();
                chkConv.Text = "传送带加速送卡";
                chkConv.ButtonPressed = ModSettings.ConveyorFast;
                chkConv.Toggled += OnConveyorToggled;
                vbCombat.AddChild(chkConv);
                var chkRain = new CheckButton();
                chkRain.Text = "种子雨加速掉落";
                chkRain.ButtonPressed = ModSettings.RainFast;
                chkRain.Toggled += OnRainToggled;
                vbCombat.AddChild(chkRain);
                var chkCrater = new CheckButton();
                chkCrater.Text = "清除弹坑";
                chkCrater.ButtonPressed = ModSettings.ClearCrater;
                chkCrater.Toggled += OnCraterToggled;
                vbCombat.AddChild(chkCrater);
                var chkVaseRandom = new CheckButton();
                chkVaseRandom.Text = "罐子内物品随机";
                chkVaseRandom.ButtonPressed = ModSettings.VaseRandom;
                chkVaseRandom.Toggled += OnVaseRandomToggled;
                vbCombat.AddChild(chkVaseRandom);
                var chkSbRandom = new CheckButton();
                chkSbRandom.Text = "卡槽卡牌随机";
                chkSbRandom.ButtonPressed = ModSettings.SeedBankRandom;
                chkSbRandom.Toggled += OnSbRandomToggled;
                vbCombat.AddChild(chkSbRandom);
                var chkSbZombie = new CheckButton();
                chkSbZombie.Text = "卡槽随机成僵尸卡";
                chkSbZombie.ButtonPressed = ModSettings.SeedBankRandomZombie;
                chkSbZombie.Toggled += on => { ModSettings.SeedBankRandomZombie = on; Bootstrap.Log("ModUI: 卡槽随机成僵尸=" + on); };
                vbCombat.AddChild(chkSbZombie);
                var chkSbFrame = new CheckButton();
                chkSbFrame.Text = "卡槽1帧1刷";
                chkSbFrame.ButtonPressed = ModSettings.SeedBankEveryFrame;
                chkSbFrame.Toggled += on => { ModSettings.SeedBankEveryFrame = on; Bootstrap.Log("ModUI: 卡槽1帧1刷=" + on); };
                vbCombat.AddChild(chkSbFrame);

                // ===== 刷出物篡改：老虎机盲盒/种子雨/传送带 刷出内容锁定为目标池 =====
                vbCombat.AddChild(new HSeparator());
                vbCombat.AddChild(Section("刷出物篡改"));
                var trickTip = new Label();
                trickTip.Text = "把 老虎机盲盒 / 种子雨 / 传送带 自动刷出的卡，换成你指定的植物/僵尸。\n目标卡池=植物/僵尸卡 id（逗号分隔），如：PlantPeaShooterSingle,PlantSnowPea";
                trickTip.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                trickTip.AddThemeFontSizeOverride("font_size", 12);
                vbCombat.AddChild(trickTip);
                AddSwitch(vbCombat, "篡改总开关", ModSettings.TrickEnabled, on => { ModSettings.TrickEnabled = on; Bootstrap.Log("ModUI: 篡改总开关=" + on); }, "下面三类自动刷出内容改为目标池");
                AddSwitch(vbCombat, "篡改礼盒盲盒", ModSettings.TrickBox, on => { ModSettings.TrickBox = on; Bootstrap.Log("ModUI: 篡改礼盒盲盒=" + on); }, "礼盒/抽奖盒开出 = 目标池（含僵尸）");
                AddSwitch(vbCombat, "篡改种子雨", ModSettings.TrickRain, on => { ModSettings.TrickRain = on; Bootstrap.Log("ModUI: 篡改种子雨=" + on); }, "掉落卡 = 目标池（掉阳光型不适用）");
                AddSwitch(vbCombat, "篡改传送带", ModSettings.TrickConveyor, on => { ModSettings.TrickConveyor = on; Bootstrap.Log("ModUI: 篡改传送带=" + on); }, "传送带送的卡 = 目标池");
                AddSwitch(vbCombat, "固定第1张卡", ModSettings.TrickFixedOnly, on => { ModSettings.TrickFixedOnly = on; Bootstrap.Log("ModUI: 固定第1张=" + on); }, "目标池多张时只固定出第一张");
                var tpLabel = new Label();
                tpLabel.Text = "目标卡池（卡id，逗号分隔）";
                tpLabel.AddThemeFontSizeOverride("font_size", 13);
                vbCombat.AddChild(tpLabel);
                _trickPoolEdit = new LineEdit();
                _trickPoolEdit.Text = ModSettings.TrickPool;
                _trickPoolEdit.PlaceholderText = "如：PlantPeaShooterSingle,PlantWallnut";
                vbCombat.AddChild(_trickPoolEdit);
                var saveTp = new Button();
                saveTp.Text = "保存目标卡池";
                saveTp.Pressed += () => { ModSettings.TrickPool = _trickPoolEdit.Text.Trim(); ModSettings.Save(); ShowToastText("目标卡池已保存"); };
                vbCombat.AddChild(saveTp);

                // ===== 标签：趣味 =====
                var vbFun = MakePage("趣味");
                vbFun.AddChild(Section("趣味"));
                var chkFun = new CheckButton();
                chkFun.Text = "趣味弹幕";
                chkFun.ButtonPressed = ModSettings.FunEnabled;
                chkFun.Toggled += OnFunToggled;
                vbFun.AddChild(chkFun);
                var chkZC = new CheckButton();
                chkZC.Text = "僵尸变色";
                chkZC.ButtonPressed = ModSettings.ZombieColor;
                chkZC.Toggled += OnZombieColorToggled;
                vbFun.AddChild(chkZC);
                var chkZD = new CheckButton();
                chkZD.Text = "僵尸跳舞";
                chkZD.ButtonPressed = ModSettings.ZombieDance;
                chkZD.Toggled += OnZombieDanceToggled;
                vbFun.AddChild(chkZD);
                AddSwitch(vbFun, "Q弹模式", ModSettings.PlantSquash, OnSquashToggled, "植物有节奏压扁/拉扁");
                AddSwitch(vbFun, "果冻模式", ModSettings.JellyMode, on => { ModSettings.JellyMode = on; Bootstrap.Log("ModUI: 果冻模式=" + on); }, "Q弹基础上加左右摇摆抽搐");

                var danLabel = new Label();
                danLabel.Text = "自定义弹幕";
                vbFun.AddChild(danLabel);
                _danmakuEdit = new LineEdit();
                _danmakuEdit.Text = ModSettings.CustomMessages;
                _danmakuEdit.PlaceholderText = "如：僵尸来啦,加油,冲鸭";
                vbFun.AddChild(_danmakuEdit);
                var saveBtn = new Button();
                saveBtn.Text = "保存弹幕";
                saveBtn.Pressed += OnSaveDanmaku;
                vbFun.AddChild(saveBtn);

                var dsLabel = new Label();
                dsLabel.Text = "弹幕速度";
                vbFun.AddChild(dsLabel);
                var dsRow = new HBoxContainer();
                dsRow.AddThemeConstantOverride("separation", 12);
                vbFun.AddChild(dsRow);
                var ds = MakeSlider();
                ds.MinValue = 0.5; ds.MaxValue = 3.0; ds.Step = 0.1;
                ds.Value = ModSettings.DanmakuSpeed;
                ds.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                ds.ValueChanged += OnDanmakuSpeedChanged;
                dsRow.AddChild(ds);
                var dsVal = new Label();
                dsVal.Text = ModSettings.DanmakuSpeed.ToString("0.0");
                dsVal.AddThemeFontSizeOverride("font_size", 13);
                dsVal.CustomMinimumSize = new Vector2(48, 0);
                ds.ValueChanged += v => dsVal.Text = v.ToString("0.0") + "x";
                dsRow.AddChild(dsVal);

                // ===== 标签：皮肤（装扮）解锁 =====
                var vbSkin = MakePage("皮肤");
                vbSkin.AddChild(Section("皮肤解锁"));
                var skinInfo = new Label();
                skinInfo.Text = "选卡牌 + 皮肤后点『启用该皮肤』；皮肤全解 = 所有卡牌随机启用一个皮肤（写游戏存档，图鉴/选卡可见）";
                skinInfo.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                vbSkin.AddChild(skinInfo);
                var cardSel = new OptionButton();
                cardSel.AddItem("（未扫描）", 0);
                var skinSel = new OptionButton();
                skinSel.AddItem("（先选卡牌）", 0);
                skinSel.Disabled = true;
                var scanBtn = new Button();
                scanBtn.Text = "重新扫描卡牌皮肤";
                scanBtn.Pressed += () => StartSkinScan(cardSel, skinSel, scanBtn);
                vbSkin.AddChild(scanBtn);
                vbSkin.AddChild(cardSel);
                vbSkin.AddChild(skinSel);
                // 打开面板不自动扫描：同步扫描会遍历所有卡触发主线程加载角色（LoadCharacterBindingOnMainThread→Task.Wait）
                // 配合资源节流 → 主线程无限等待 → 面板打开即假死。改为点击时主线程分帧扫描（每帧几张，不阻塞）。
                cardSel.ItemSelected += idx =>
                {
                    int ci = (int)idx - 1;
                    skinSel.Clear();
                    if (ci < 0 || ci >= _skinCards.Length) { skinSel.AddItem("（先选卡牌）", 0); return; }
                    var skins = GameCheats.GetPacketSkins(_skinCards[ci]);
                    skinSel.Disabled = skins.Length == 0;
                    skinSel.AddItem("（" + skins.Length + " 个皮肤）", 0);
                    for (int i = 0; i < skins.Length; i++)
                        skinSel.AddItem(GameCheats.TranslateBulletName(skins[i]), i + 1);
                };
                var applySkinBtn = new Button();
                applySkinBtn.Text = "启用该皮肤";
                applySkinBtn.Pressed += () =>
                {
                    int ci = cardSel.Selected - 1;
                    int si = skinSel.Selected - 1;
                    if (ci < 0 || ci >= _skinCards.Length) return;
                    var skins = GameCheats.GetPacketSkins(_skinCards[ci]);
                    if (si < 0 || si >= skins.Length) return;
                    bool ok = GameCheats.UnlockSkin(_skinCards[ci], skins[si]);
                    Bootstrap.Log("ModUI: 启用皮肤 " + _skinCards[ci] + "/" + skins[si] + " → " + ok);
                };
                vbSkin.AddChild(applySkinBtn);
                var allSkinBtn = new Button();
                allSkinBtn.Text = "皮肤全解";
                allSkinBtn.Pressed += () =>
                {
                    int n = GameCheats.UnlockAllSkins();
                    Bootstrap.Log("ModUI: 皮肤全解完成 " + n + " 个");
                };
                vbSkin.AddChild(allSkinBtn);

                Bootstrap.Log("ModUI: 设置面板已打开（分类标签页）");
            }
            catch (System.Exception ex) { Bootstrap.Log("ModUI 面板异常: " + ex.Message); }
        }

        /// <summary>隐藏悬浮按钮（面板打开时调用，避免全屏面板遮挡）。</summary>
        static void HideFloatingBtn() { try { if (_btn != null && GodotObject.IsInstanceValid(_btn)) _btn.Visible = false; } catch { } }
        /// <summary>恢复悬浮按钮（面板关闭时调用）。</summary>
        static void ShowFloatingBtn() { try { if (_btn != null && GodotObject.IsInstanceValid(_btn)) _btn.Visible = true; } catch { } }

        /// <summary>手机版滑块：加高触摸区域（44px 高），适配手指拖动（OptionButton 之外第二个触摸友好化）。</summary>
        static HSlider MakeSlider()
        {
            var s = new HSlider();
            s.CustomMinimumSize = new Vector2(0, 46);
            return s;
        }

        /// <summary>拖动设置面板：按住面板空白处拖动（支持鼠标与手机触摸）。</summary>
        /// <summary>控件完全出界时拉回屏幕内；正常在屏幕内不干涉拖动（避免坐标系差异导致偏移）。</summary>
        static void ClampToScreen(Control c)
        {
            try
            {
                if (c == null || !GodotObject.IsInstanceValid(c)) return;
                var vs = c.GetViewport().GetVisibleRect().Size;
                var p = c.Position;
                if (p.X + c.Size.X < 0 || p.X > vs.X || p.Y + c.Size.Y < 0 || p.Y > vs.Y)
                {
                    c.Position = new Vector2(
                        Mathf.Clamp(p.X, 0, Mathf.Max(0, vs.X - c.Size.X)),
                        Mathf.Clamp(p.Y, 0, Mathf.Max(0, vs.Y - c.Size.Y)));
                }
            }
            catch { }
        }

        /// <summary>手机版：ScrollContainer 内容区触摸拖动即滚动（内容跟手），取代"只能拖右侧滑块"。
        /// 事件被 Accept 后不再冒泡到面板，因此内容区滑动不会拖动整个面板；非内容区（标签栏等）拖动面板照旧。</summary>
        static void OnScrollGuiInput(ScrollContainer scroll, InputEvent @event)
        {
            try
            {
                if (@event is InputEventScreenTouch st)
                {
                    if (st.Pressed)
                    {
                        _scrollDragTarget = scroll;
                        _scrollDragStartY = st.Position.Y;
                        _scrollDragStartVal = scroll.ScrollVertical;
                        var vp = scroll.GetViewport();
                        var vs = vp.GetVisibleRect().Size;
                        var win = vp.GetWindow();
                        var ws = win != null ? win.Size : vs;
                        _scrollDragScale = vs.Y / Mathf.Max(1, ws.Y);
                    }
                    else
                    {
                        _scrollDragTarget = null;
                    }
                    scroll.AcceptEvent();   // 阻止冒泡到面板（避免内容区滑动拖动面板）
                }
                else if (@event is InputEventScreenDrag sd && _scrollDragTarget == scroll)
                {
                    // 内容跟手：手指下滑（Y 增大）→ 看上方 → scroll_vertical 减小
                    float dy = (sd.Position.Y - _scrollDragStartY) * _scrollDragScale;
                    scroll.ScrollVertical = _scrollDragStartVal - (int)dy;
                    scroll.AcceptEvent();
                }
            }
            catch (System.Exception ex) { Bootstrap.Log("滚动异常: " + ex.Message); }
        }

        /// <summary>全局触摸路由（由 ModTouchRouter._Input 调用，在 GUI 处理前）：\n
        /// 按下时找触摸点所在的可滚动页，拖动时手动改 ScrollVertical——不受子控件 grab 影响，点哪里都能划。</summary>
        internal static void RouteTouch(InputEvent @event)
        {
            try
            {
                if (@event is InputEventScreenTouch st)
                {
                    if (st.Pressed)
                    {
                        _touchScroll = FindScrollAt(st.Position);
                        if (_touchScroll != null)
                        {
                            _touchDown = true;
                            _touchStartY = st.Position.Y;
                            _touchStartVal = _touchScroll.ScrollVertical;
                        }
                        else _touchDown = false;
                    }
                    else _touchDown = false;
                }
                else if (@event is InputEventScreenDrag sd && _touchDown && _touchScroll != null && GodotObject.IsInstanceValid(_touchScroll))
                {
                    // 内容跟手：手指下滑（Y 增大）→ 看上方 → scroll_vertical 减小
                    float dy = (sd.Position.Y - _touchStartY);
                    _touchScroll.ScrollVertical = _touchStartVal - (int)dy;
                }
            }
            catch { }
        }

        /// <summary>返回触摸点所在的可见可滚动页（当前页优先；含左侧导航）。</summary>
        static ScrollContainer FindScrollAt(Vector2 pos)
        {
            try
            {
                for (int i = _pages.Count - 1; i >= 0; i--)
                {
                    var s = _pages[i];
                    if (s != null && GodotObject.IsInstanceValid(s) && s.Visible && s.GetGlobalRect().HasPoint(pos)) return s;
                }
                if (_navScroll != null && GodotObject.IsInstanceValid(_navScroll) && _navScroll.GetGlobalRect().HasPoint(pos)) return _navScroll;
            }
            catch { }
            return null;
        }

        static void OnPanelGuiInput(InputEvent @event)
        {
            try
            {
                if (_panel == null || !GodotObject.IsInstanceValid(_panel)) return;
                if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
                {
                    _panelDragging = mb.Pressed;
                    if (_panelDragging)
                        _panelDragOffset = _panel.Position - _panel.GetGlobalMousePosition();
                }
                else if (@event is InputEventMouseMotion motion && _panelDragging)
                {
                    _panel.Position += motion.Relative;
                }
                else if (@event is InputEventScreenTouch st)
                {
                    _panelDragging = st.Pressed;
                    if (_panelDragging)
                        _panelDragOffset = _panel.Position - st.Position;
                }
                else if (@event is InputEventScreenDrag sd && _panelDragging)
                {
                    var vp = _panel.GetViewport();
                    var vs = vp.GetVisibleRect().Size;
                    var win = vp.GetWindow();
                    var ws = win != null ? win.Size : vs;
                    float sx = vs.X / Mathf.Max(1, ws.X), sy = vs.Y / Mathf.Max(1, ws.Y);
                    _panel.Position += new Vector2(sd.Relative.X * sx, sd.Relative.Y * sy);
                }
            }
            catch (System.Exception ex) { Bootstrap.Log("ModUI 面板拖动异常: " + ex.Message); }
        }

        /// <summary>分区标题。</summary>
        static Label Section(string text)
        {
            var l = new Label();
            l.Text = text;
            l.AddThemeFontSizeOverride("font_size", 16);
            l.AddThemeColorOverride("font_color", new Color(1f, 0.82f, 0.3f));
            return l;
        }

        /// <summary>创建右侧功能页（页面叠放铺满页面容器，含触摸滚动），并建左侧分类导航按钮；返回内容 VBox。</summary>
        static VBoxContainer MakePage(string title)
        {
            var scroll = new ScrollContainer();
            scroll.Name = title;
            scroll.SetAnchorsPreset(Control.LayoutPreset.FullRect);   // 铺满右侧页面容器
            scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
            scroll.GuiInput += ev => OnScrollGuiInput(scroll, ev);   // 内容区任意触摸拖动即滚动（兜底）
            // 右侧大滑块：加大滚动条宽度与手柄，可直接拖动滚动（最可靠）
            try
            {
                var vsb = scroll.GetVScrollBar();
                vsb.CustomMinimumSize = new Vector2(24, 0);
                vsb.AddThemeConstantOverride("grabber_width", 20);
                var grab = new StyleBoxFlat();
                grab.BgColor = new Color(0.35f, 0.62f, 0.3f, 0.95f);
                grab.SetCornerRadiusAll(7);
                var grabHi = new StyleBoxFlat();
                grabHi.BgColor = new Color(0.55f, 0.85f, 0.5f, 1f);
                grabHi.SetCornerRadiusAll(7);
                vsb.AddThemeStyleboxOverride("grabber", grab);
                vsb.AddThemeStyleboxOverride("grabber_highlight", grabHi);
                vsb.AddThemeStyleboxOverride("grabber_pressed", grabHi);
                var track = new StyleBoxFlat();
                track.BgColor = new Color(1f, 1f, 1f, 0.1f);
                track.SetCornerRadiusAll(7);
                vsb.AddThemeStyleboxOverride("track", track);
                var trackHi = new StyleBoxFlat();
                trackHi.BgColor = new Color(1f, 1f, 1f, 0.16f);
                trackHi.SetCornerRadiusAll(7);
                vsb.AddThemeStyleboxOverride("track_highlight", trackHi);
            }
            catch { }
            if (_pagesHost != null) _pagesHost.AddChild(scroll);
            int idx = _pages.Count;
            _pages.Add(scroll);
            // 左侧导航按钮（Toggle 高亮当前分类）
            var btn = new Button();
            btn.Text = title;
            btn.ToggleMode = true;
            btn.ButtonPressed = (idx == 0);
            btn.CustomMinimumSize = new Vector2(0, 42);
            if (_navBox != null) _navBox.AddChild(btn);
            int captured = idx;
            btn.Pressed += () => SwitchPage(captured);
            _navButtons.Add(btn);
            scroll.Visible = (idx == 0);   // 初始只显示第一页（其余叠在下方，切换时滑入）
            // 内容右移避开悬浮滚动条（滚动条 overlay 在内容上，留出空间开关才点得到）
            var contentMargin = new MarginContainer();
            contentMargin.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            contentMargin.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
            contentMargin.AddThemeConstantOverride("margin_right", 30);
            scroll.AddChild(contentMargin);
            var vb = new VBoxContainer();
            vb.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            vb.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
            vb.AddThemeConstantOverride("separation", 18);   // 按钮/控件之间明显隔开
            contentMargin.AddChild(vb);
            return vb;
        }

        /// <summary>切换分类页：当前页上滑消失，目标页从下方滑入（单 Tween 并行，Quad 缓动，丝滑不卡）。</summary>
        static void SwitchPage(int idx)
        {
            try
            {
                if (_pageAnimating) return;   // 动画中忽略连点
                if (_pages.Count == 0 || idx < 0 || idx >= _pages.Count || idx == _pageIndex) return;
                int from = _pageIndex;
                _pageIndex = idx;
                var oldPage = _pages[from];
                var newPage = _pages[idx];
                if (oldPage == null || newPage == null || oldPage == newPage) return;
                _pageAnimating = true;
                float h = Mathf.Max(oldPage.Size.Y, 240f);
                // 动画期间两页都忽略触摸（防滑动/点击干扰），结束后恢复
                oldPage.MouseFilter = Control.MouseFilterEnum.Ignore;
                newPage.MouseFilter = Control.MouseFilterEnum.Ignore;
                oldPage.Visible = true;
                newPage.Visible = true;
                newPage.Position = new Vector2(0, h);   // 目标页从下方待命
                // 单 Tween 并行控制两页：统一时长 + Quad Out 缓动，保证同步丝滑
                var tw = newPage.CreateTween();
                tw.SetParallel(true);
                tw.SetTrans(Tween.TransitionType.Quad);
                tw.SetEase(Tween.EaseType.Out);
                tw.TweenProperty(newPage, "position:y", 0f, 0.2f);
                tw.TweenProperty(oldPage, "position:y", -h, 0.2f);
                tw.Chain().TweenCallback(Callable.From(() =>
                {
                    try
                    {
                        if (oldPage != null && GodotObject.IsInstanceValid(oldPage))
                        {
                            oldPage.Visible = false;
                            oldPage.Position = new Vector2(0, 0);
                            oldPage.MouseFilter = Control.MouseFilterEnum.Stop;
                        }
                        if (newPage != null && GodotObject.IsInstanceValid(newPage))
                            newPage.MouseFilter = Control.MouseFilterEnum.Stop;
                    }
                    catch { }
                    _pageAnimating = false;
                }));
                // 导航高亮
                for (int i = 0; i < _navButtons.Count; i++)
                    if (_navButtons[i] != null && GodotObject.IsInstanceValid(_navButtons[i])) _navButtons[i].ButtonPressed = (i == idx);
            }
            catch { _pageAnimating = false; }
        }

        /// <summary>关于页文本：游戏名 / 进程名 / PID / 路径 / Mod 类型。
        /// 注意：合并后 .NET 9 环境 System.Diagnostics.Process.MainModule 不可用（MissingMethodException 且 try-catch 捕获不到），
        /// 必须用 Godot OS API。</summary>
        static string GetAboutText()
        {
            try
            {
                string pid = "";
                string path = "";
                string proc = "未知";
                string gameVer = "0.26.1";
                try { pid = OS.GetProcessId().ToString(); } catch { }
                try { path = OS.GetExecutablePath(); } catch { }
                try
                {
                    if (path.Length > 0)
                        proc = System.IO.Path.GetFileNameWithoutExtension(path);
                }
                catch { }
                return "类型：DLL 注入型辅助 Mod\n\n"
                    + "游戏：植物大战僵尸杂交版 " + gameVer + "\n"
                    + "进程：" + proc + "\n"
                    + "PID：" + pid + "\n"
                    + "路径：" + path + "\n\n"
                    + "Mod 版本：" + ModSettings.Version + "\n\n"
                    + "感谢使用该MOD！\n"
                    + "杂交MOD（搜索关注）";
            }
            catch { return "类型：DLL 注入型辅助 Mod\n版本：" + ModSettings.Version; }
        }

        /// <summary>大开关+说明文字（手机版新 UI）：一行开关，下面小字说明。</summary>
        static void AddSwitch(VBoxContainer vb, string name, bool val, Godot.BaseButton.ToggledEventHandler toggled, string hint)
        {
            try
            {
                var cb = new CheckButton();
                cb.Text = name;
                cb.ButtonPressed = val;
                cb.Toggled += toggled;
                vb.AddChild(cb);
                if (!string.IsNullOrEmpty(hint))
                {
                    var h = new Label();
                    h.Text = hint;
                    h.Modulate = new Color(0.62f, 0.82f, 0.52f);
                    h.AddThemeFontSizeOverride("font_size", 12);
                    vb.AddChild(h);
                }
            }
            catch { }
        }

        /// <summary>深色圆角 StyleBox（手机版新 UI）。</summary>
        static StyleBoxFlat MakeBtnBox(Color bg, Color border)
        {
            var sb = new StyleBoxFlat();
            sb.BgColor = bg;
            sb.BorderColor = border;
            sb.SetBorderWidthAll(1);
            sb.SetCornerRadiusAll(6);
            return sb;
        }

        static void OnToggled(bool on) { ModSettings.Enabled = on; Bootstrap.Log("ModUI: 启用=" + on); }
        static void OnFunToggled(bool on) { ModSettings.FunEnabled = on; Bootstrap.Log("ModUI: 弹幕=" + on); }
        static void OnBGToggled(bool on) { ModSettings.BGEnabled = on; Bootstrap.Log("ModUI: 背景=" + on); }
        static void OnSunToggled(bool on) { ModSettings.InfiniteSun = on; Bootstrap.Log("ModUI: 无限阳光=" + on); }
        static void OnCoinToggled(bool on) { ModSettings.InfiniteCoin = on; Bootstrap.Log("ModUI: 无限金币=" + on); }
        static void OnNcToggled(bool on) { ModSettings.NoCooldown = on; Bootstrap.Log("ModUI: 无冷却=" + on); }
        static void OnCannonToggled(bool on) { ModSettings.CannonNoCooldown = on; Bootstrap.Log("ModUI: 炮类无冷却=" + on); }
        static void OnHouseToggled(bool on) { ModSettings.IgnoreHouse = on; Bootstrap.Log("ModUI: 无视进家=" + on); }
        static void OnPurpleToggled(bool on) { ModSettings.IgnorePurple = on; Bootstrap.Log("ModUI: 无视紫卡=" + on); }
        static void OnInstantWin() { GameCheats.InstantWinAll(); Bootstrap.Log("ModUI: 已触发立即胜利（跳关）"); }
        static void OnCompleteAll() { GameCheats.CompleteAllLevels(); }
        static void OnCompleteDaily() { GameCheats.CompleteAllDailyLevels(); }
        static void OnShopAll() { GameCheats.GetShopAllItems(); }
        static void OnResetBrain() { GameCheats.ResetAllBrains(); }
        static void OnOverlapToggled(bool on) { ModSettings.PlantOverlap = on; Bootstrap.Log("ModUI: 植物重叠=" + on); }
        static void OnTerrainToggled(bool on) { ModSettings.IgnoreTerrain = on; Bootstrap.Log("ModUI: 无视地形=" + on); }
        static void OnGloveToggled(bool on) { ModSettings.GloveMode = on; Bootstrap.Log("ModUI: 手套挪动=" + on); }
        static void OnNoFlickerToggled(bool on) { ModSettings.NoFlicker = on; Bootstrap.Log("ModUI: 禁止闪屏=" + on); }
        static void OnPlantASChanged(double v) { ModSettings.PlantAttackSpeed = (float)v; }
        static void OnZombieASChanged(double v) { ModSettings.ZombieAttackSpeed = (float)v; }
        static void OnSpawnMultChanged(double v) { ModSettings.SpawnMultiplier = (int)v; Bootstrap.Log("ModUI: 刷怪倍数=" + (int)v + "x"); }
        static void OnPlantHPChanged(double v) { ModSettings.PlantHP = (float)v; }
        static void OnZombieHPChanged(double v) { ModSettings.ZombieHP = (float)v; }
        static void OnPInvToggled(bool on) { ModSettings.PlantInvincible = on; Bootstrap.Log("ModUI: 植物无敌=" + on); }
        static void OnZInvToggled(bool on) { ModSettings.ZombieInvincible = on; Bootstrap.Log("ModUI: 僵尸无敌=" + on); }
        static void OnPNoAttackToggled(bool on) { ModSettings.PlantNoAttack = on; Bootstrap.Log("ModUI: 植物无法攻击=" + on); }
        static void OnZNoAttackToggled(bool on) { ModSettings.ZombieNoAttack = on; Bootstrap.Log("ModUI: 僵尸无法攻击=" + on); }
        static void OnZNoMoveToggled(bool on) { ModSettings.ZombieNoMove = on; Bootstrap.Log("ModUI: 僵尸无法移动=" + on); }
        static void OnCharmPlants() { CharmClick("Plant"); }
        static void OnCharmZombies() { CharmClick("Zombie"); }
        static void CharmClick(string camp)
        {
            try
            {
                if (_uiLayer == null || !GodotObject.IsInstanceValid(_uiLayer)) return;
                var tree = _uiLayer.GetTree();
                if (tree == null || tree.Root == null) return;
                GameCheats.CharmAll(tree.Root, camp);
                Bootstrap.Log("ModUI: 魅惑" + (camp == "Plant" ? "植物" : "僵尸") + "（点按）");
            }
            catch (System.Exception ex) { Bootstrap.Log("魅惑异常: " + ex.Message); }
        }
        static void OnUncharmAll()
        {
            try
            {
                if (_uiLayer == null || !GodotObject.IsInstanceValid(_uiLayer)) return;
                var tree = _uiLayer.GetTree();
                if (tree == null || tree.Root == null) return;
                GameCheats.UncharmAll(tree.Root);
                Bootstrap.Log("ModUI: 还原全部魅惑（点按）");
            }
            catch (System.Exception ex) { Bootstrap.Log("还原魅惑异常: " + ex.Message); }
        }
        static void OnTrackToggled(bool on) { ModSettings.BulletTrack = on; if (!on) GameCheats.StopAllTracking(); Bootstrap.Log("ModUI: 子弹追踪=" + on); }
        static void OnFogToggled(bool on) { ModSettings.FogESP = on; Bootstrap.Log("ModUI: 迷雾透视=" + on); }
        static void OnColumnToggled(bool on) { ModSettings.PlantColumn = on; Bootstrap.Log("ModUI: 种一列出列=" + on); }
        static void OnNoSleepToggled(bool on) { ModSettings.NoSleep = on; Bootstrap.Log("ModUI: 蘑菇不睡=" + on); }
        static void OnConveyorToggled(bool on) { ModSettings.ConveyorFast = on; Bootstrap.Log("ModUI: 传送带加速=" + on); }
        static void OnRainToggled(bool on) { ModSettings.RainFast = on; Bootstrap.Log("ModUI: 种子雨加速=" + on); }
        static void OnVaseRandomToggled(bool on) { ModSettings.VaseRandom = on; Bootstrap.Log("ModUI: 罐子自动随机=" + on); }
        static void OnSbRandomToggled(bool on) { ModSettings.SeedBankRandom = on; Bootstrap.Log("ModUI: 卡槽卡牌随机=" + on); }
        static void OnCraterToggled(bool on) { ModSettings.ClearCrater = on; Bootstrap.Log("ModUI: 清除弹坑=" + on); }
        static void OnZombieColorToggled(bool on) { ModSettings.ZombieColor = on; Bootstrap.Log("ModUI: 僵尸变色=" + on); }
        static void OnZombieDanceToggled(bool on) { ModSettings.ZombieDance = on; Bootstrap.Log("ModUI: 僵尸跳舞=" + on); }
        static void OnWarnToggled(bool on) { ModSettings.IgnoreWarningLine = on; Bootstrap.Log("ModUI: 无视警戒线=" + on); }
        static void OnSquashToggled(bool on) { ModSettings.PlantSquash = on; Bootstrap.Log("ModUI: Q弹模式=" + on); }
        static void OnSpawnUI()
        {
            try
            {
                if (_uiLayer == null || !GodotObject.IsInstanceValid(_uiLayer)) return;
                var tree = _uiLayer.GetTree();
                if (tree == null || tree.Root == null) return;
                SpawnUI.Toggle(tree.Root);
            }
            catch (System.Exception ex) { Bootstrap.Log("SpawnUI 打开异常: " + ex.Message); }
        }
        static void OnKillZombies()
        {
            try
            {
                if (_uiLayer == null || !GodotObject.IsInstanceValid(_uiLayer)) return;
                var tree = _uiLayer.GetTree();
                if (tree == null || tree.Root == null) return;
                GameCheats.KillAllZombies(tree.Root);
                Bootstrap.Log("ModUI: 已尝试杀光僵尸");
            }
            catch (System.Exception ex) { Bootstrap.Log("杀僵尸异常: " + ex.Message); }
        }
        static void OnKillPlants() { int n = GameCheats.KillAllPlants(); Bootstrap.Log("ModUI: 已杀光植物 " + n + " 个"); }
        static void OnSkipWait() { GameCheats.SkipWaveWait(); Bootstrap.Log("ModUI: 跳过波次等待"); }
        static void OnSkipFinal() { GameCheats.SkipFinalWave(); Bootstrap.Log("ModUI: 跳到最终波"); }
        static void OnUnlockAll() { GameCheats.UnlockAllFeatures(); }
        static void OnLaunchMowers()
        {
            try
            {
                if (_uiLayer == null || !GodotObject.IsInstanceValid(_uiLayer)) return;
                var tree = _uiLayer.GetTree();
                if (tree == null || tree.Root == null) return;
                GameCheats.LaunchAllMowers();
                GameCheats.DiagnoseMowers(tree.Root);   // 启动后打印小推车/僵尸状态诊断
            }
            catch (System.Exception ex) { Bootstrap.Log("启动小推车异常: " + ex.Message); }
        }
        static void OnRestoreMowers()
        {
            try
            {
                if (_uiLayer == null || !GodotObject.IsInstanceValid(_uiLayer)) return;
                var tree = _uiLayer.GetTree();
                if (tree == null || tree.Root == null) return;
                // 彻底清空（场景树里所有小推车 + mowerLine 引用）再重建，避免小推车越积越多；自研 RestoreAllMowers 确定重建
                GameCheats.RestoreAllMowers();
                Bootstrap.Log("ModUI: 已恢复小推车");
            }
            catch (System.Exception ex) { Bootstrap.Log("恢复小推车异常: " + ex.Message); }
        }
        static int _currencyNum = 10;
        static void OnCurNumChanged(double v) { _currencyNum = (int)v; }
        static void OnCurYB() { GameCheats.CreateCurrency("Silver", _currencyNum); }
        static void OnCurCoin() { GameCheats.CreateCurrency("Gold", _currencyNum); }
        static void OnCurGold() { GameCheats.CreateCurrency("Diamond", _currencyNum); }
        static void OnCurBag() { GameCheats.SpawnAward("get_TOWER_DEFENSE_AWARD_PURSE", ""); }    // 钱袋本体（钱包）
        static void OnCurCup() { GameCheats.SpawnAward("get_TOWER_DEFENSE_AWARD_TROPHY", ""); }  // 奖杯本体
        static void OnESPToggled(bool on) { ModSettings.ESPEnabled = on; Bootstrap.Log("ModUI: 透视=" + on); }
        static void OnESPZToggled(bool on) { ModSettings.ESPZombie = on; }
        static void OnESPPToggled(bool on) { ModSettings.ESPPlant = on; }
        static void OnVaseToggled(bool on) { ModSettings.VaseESP = on; }
        static void OnAlmanacToggled(bool on)
        {
            try
            {
                ModSettings.AlmanacAll = on;
                Bootstrap.Log("ModUI: 图鉴全解=" + on);
                if (!on) return;
                if (_uiLayer == null || !GodotObject.IsInstanceValid(_uiLayer)) return;
                var tree = _uiLayer.GetTree();
                if (tree == null || tree.Root == null) return;
                // 立即对当前已打开的图鉴设置 plantShowAll（不调用 Init，避免卡顿）
                GameCheats.SetAlmanacAllNow(tree.Root);
            }
            catch (System.Exception ex) { Bootstrap.Log("图鉴全解异常: " + ex.Message); }
        }
        static void OnZombieScaleChanged(double v) { ModSettings.ZombieScale = (float)v; }
        static void OnPlantScaleChanged(double v) { ModSettings.PlantScale = (float)v; }
        static void OnDanmakuSpeedChanged(double v) { ModSettings.DanmakuSpeed = (float)v; }
        static void OnSpeedChanged(double v) { ModSettings.Speed = (float)v; }
        static void OnGameSpeedChanged(double v)
        {
            // ★ 联机中拒绝调速：CheatPolicy 每 250ms 会把 GameSpeed 复位为 1.0，
            //   直接改只会被默默改回去，玩家一头雾水。这里直接拦截并告知原因。
            if (CheatPolicy.IsLockedForUi && (float)v != 1.0f)
            {
                ShowToastText("联机中已禁用游戏加速（避免同步失败）");
                return;
            }
            ModSettings.GameSpeed = (float)v;
            Bootstrap.Log("ModUI: 游戏速度=" + v);
        }
        static void OnConsoleToggled(bool on) { ModSettings.ConsoleEnabled = on; Bootstrap.Log("ModUI: 游戏控制台=" + on); }
        static void OnSaveDanmaku()
        {
            try
            {
                if (_danmakuEdit == null || !GodotObject.IsInstanceValid(_danmakuEdit)) return;
                ModSettings.CustomMessages = _danmakuEdit.Text;
                System.IO.File.WriteAllText(@"mod\custom_danmaku.txt",
                    ModSettings.CustomMessages, System.Text.Encoding.UTF8);
                Bootstrap.Log("自定义弹幕已保存: " + ModSettings.CustomMessages);
            }
            catch (System.Exception ex) { Bootstrap.Log("保存弹幕异常: " + ex.Message); }
        }
        static void OnStyle0() { ModSettings.Style = 0; Bootstrap.Log("ModUI: 风格=彩虹"); }
        static void OnStyle1() { ModSettings.Style = 1; Bootstrap.Log("ModUI: 风格=红金"); }
        static void OnStyle2() { ModSettings.Style = 2; Bootstrap.Log("ModUI: 风格=蓝紫"); }
        static void OnStyle3() { ModSettings.Style = 3; Bootstrap.Log("ModUI: 风格=霓虹"); }
        static void OnStyle4() { ModSettings.Style = 4; Bootstrap.Log("ModUI: 风格=粉彩"); }
        static void OnStyle5() { ModSettings.Style = 5; Bootstrap.Log("ModUI: 风格=青绿"); }
        static void OnStyle6() { ModSettings.Style = 6; Bootstrap.Log("ModUI: 风格=纯白"); }
        static void OnStyle7() { ModSettings.Style = 7; Bootstrap.Log("ModUI: 风格=火焰"); }
        static void OnStyle8() { ModSettings.Style = 8; Bootstrap.Log("ModUI: 风格=薄荷"); }
        static void OnStyle9() { ModSettings.Style = 9; Bootstrap.Log("ModUI: 风格=樱花粉"); }
        static void OnStyle10() { ModSettings.Style = 10; Bootstrap.Log("ModUI: 风格=紫罗兰"); }
        static void OnStyle11() { ModSettings.Style = 11; Bootstrap.Log("ModUI: 风格=海洋"); }
        static void OnStyle12() { ModSettings.Style = 12; Bootstrap.Log("ModUI: 风格=日落"); }
        static void OnStyle13() { ModSettings.Style = 13; Bootstrap.Log("ModUI: 风格=极光"); }
        static void OnStyle14() { ModSettings.Style = 14; Bootstrap.Log("ModUI: 风格=森林"); }
        static void OnStyle15() { ModSettings.Style = 15; Bootstrap.Log("ModUI: 风格=柠檬"); }
    }

    /// <summary>全局触摸路由器：挂在 _uiLayer 下，在 GUI 处理前捕获屏幕触摸，驱动 ModUI.RouteTouch。
    /// 即使手指按在按钮/开关上也能拖动滚动（Godot 控件 grab 不影响 _Input）。</summary>
    public class ModTouchRouter : Godot.Node
    {
        public ModTouchRouter() : base() { }
        public override void _Input(Godot.InputEvent @event)
        {
            ModUI.RouteTouch(@event);
        }
    }
}
