using System.Collections.Generic;
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
        private static bool _uiHidden;      // 悬浮窗隐藏状态（Tab 开关）
        private static bool _tabPrev;       // Tab 键边沿检测
        private static Label _uiHint;       // 右上角快捷键提示
        private static Label _consoleHint;  // 右上角控制台快捷键提示
        private static bool _dragging;
        private static Vector2 _dragOffset;
        private static string[] _skinCards = new string[0];   // 皮肤扫描结果（分帧，防假死）
        private static Panel _panel;
        private static bool _panelDragging;
        private static Vector2 _panelDragOffset;
        private static VBoxContainer _navBox;                         // 左侧导航容器
        private static Control _contentArea;                          // 右侧内容区
        private static readonly Dictionary<string, VBoxContainer> _panelTabs = new();
        private static readonly Dictionary<string, Button> _navBtns = new();
        private static float _goldT;                       // 金色流光时间
        private static int _fxTimer;                       // 流光降频器（每 3 帧）
        private static string _curNav = "关于";            // 当前选中导航
        private static CpuParticles2D[] _edgeParticles;    // 面板四周金色粒子
        private static Panel _borderDot;                    // 边框流光光点
        private static LineEdit _danmakuEdit;
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

        /// <summary>每帧驱动：弹窗文字彩色律动 + hook（由 FrameDriver/GlobalColor 每帧调用；Ensure 每 30 帧太慢会导致面板刚开点开关 hook 不上）。</summary>
        public static void TickToast(Node root)
        {
            try
            {
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
                // 显眼位置提示：按 Tab 开关悬浮窗（跟随 UI 层显示/隐藏）
                if (_uiHint == null || !GodotObject.IsInstanceValid(_uiHint))
                {
                    _uiHint = new Label();
                    _uiHint.Text = "按 Tab 开关悬浮窗";
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
                    _consoleHint.Text = "控制台：Caps Lock 开关";
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
                // 金色奢华悬浮按钮：深金黑底 + 金色描边 + 金色投影（流光由 UpdateFx 每帧驱动）
                btn.AddThemeStyleboxOverride("normal", MakeGoldStyleBox(14));
                btn.AddThemeStyleboxOverride("hover", MakeGoldStyleBox(14, true));
                btn.AddThemeStyleboxOverride("pressed", MakeGoldStyleBox(14, true, true));
                btn.AddThemeStyleboxOverride("focus", MakeGoldStyleBox(14));
                // 游戏窗口内：按相对位置（默认水平居中、垂直 15%）
                btn.Position = new Vector2(vsize.X * _btnRel.X - 85, vsize.Y * _btnRel.Y);
                btn.Size = new Vector2(170, 44);
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
                    // 超出右边界则回到顶部中央
                    if (_btn.Position.X > size.X - 20)
                        _btn.Position = new Vector2(size.X / 2 - 75, 16);
                    else
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

        /// <summary>拖动按钮：按住左键拖动，松开停止。</summary>
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
                else if (@event is InputEventMouseMotion && _dragging)
                {
                    _btn.Position = _btn.GetGlobalMousePosition() + _dragOffset;
                    // 更新相对位置，保证窗口变化时按钮仍在新位置的比例处
                    var tree = _btn.GetTree();
                    if (tree != null && tree.Root != null)
                    {
                        var vs = tree.Root.GetViewport().GetVisibleRect().Size;
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
                    _panel.QueueFree(); _panel = null; _danmakuEdit = null; return;   // 再点一次关闭
                }

                // Panel（非 Container）：布局由内部 HBox 锚点控制，尺寸固定不随内容乱变
                var pc = new Panel();
                pc.Name = "ModSettingsPanel";
                pc.Position = new Vector2(176, 16);
                pc.Size = new Vector2(560, 600);   // 扩大宽度 + 适中高度（不顶屏幕）
                pc.GuiInput += OnPanelGuiInput;   // 面板可拖动
                // 金色奢华：深金黑底 + 金色描边 + 金色投影（流光由 UpdateFx 驱动）；裁剪圆角外子节点（防溢出）
                pc.AddThemeStyleboxOverride("panel", MakeGoldStyleBox(18));
                pc.ClipContents = true;
                _uiLayer.AddChild(pc);
                _panel = pc;

                // 炫酷动画：打开金辉淡入
                try
                {
                    pc.Modulate = new Color(1f, 1f, 1f, 0f);
                    var tw = pc.CreateTween();
                    tw.TweenProperty(pc, "modulate:a", 1f, 0.28f);
                }
                catch { }

                // 炫酷动画：金色粒子从面板四周向外飘散（跟 UI 同色）
                try
                {
                    _edgeParticles = new CpuParticles2D[4];
                    var sz = pc.Size;
                    AddEdgeParticles(pc, 0, new Vector2(sz.X * 0.5f, 0), new Vector2(0, -1));
                    AddEdgeParticles(pc, 1, new Vector2(sz.X * 0.5f, sz.Y), new Vector2(0, 1));
                    AddEdgeParticles(pc, 2, new Vector2(0, sz.Y * 0.5f), new Vector2(-1, 0));
                    AddEdgeParticles(pc, 3, new Vector2(sz.X, sz.Y * 0.5f), new Vector2(1, 0));
                }
                catch { }

                // 炫酷动画：边框流光光点（沿面板边框顺时针环绕）
                try
                {
                    var dot = new Panel();
                    dot.Size = new Vector2(4, 4);
                    var dotSb = new StyleBoxFlat();
                    dotSb.BgColor = new Color(1f, 0.9f, 0.55f, 1f);
                    dotSb.SetCornerRadiusAll(2);
                    dotSb.ShadowColor = new Color(1f, 0.85f, 0.4f, 0.9f);
                    dotSb.ShadowSize = 14;
                    dot.AddThemeStyleboxOverride("panel", dotSb);
                    dot.MouseFilter = Control.MouseFilterEnum.Ignore;
                    pc.AddChild(dot);
                    _borderDot = dot;
                }
                catch { }

                // 炫酷动画：背景金色光斑（缓慢浮动）
                try
                {
                    for (int i = 0; i < 3; i++)
                    {
                        var orb = new Panel();
                        var ob = new StyleBoxFlat();
                        ob.BgColor = new Color(0.95f, 0.75f, 0.3f, 0.05f);
                        ob.SetCornerRadiusAll(45);
                        ob.ShadowColor = new Color(0.95f, 0.75f, 0.3f, 0.18f);
                        ob.ShadowSize = 30;
                        orb.AddThemeStyleboxOverride("panel", ob);
                        var op = new Vector2(40 + i * 130, 60 + (i % 2) * 200);
                        orb.Position = op;
                        orb.Size = new Vector2(90, 90);
                        orb.MouseFilter = Control.MouseFilterEnum.Ignore;
                        pc.AddChild(orb);   // 在 root 之前加入 → 作为背景层
                        var tw = orb.CreateTween();
                        tw.SetLoops(0);
                        tw.TweenProperty(orb, "position", op + new Vector2(34, 22), 6.0f).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
                        tw.TweenProperty(orb, "position", op, 6.0f).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
                    }
                }
                catch { }

                // 布局：左侧导航栏 + 右侧内容区（类似桌面软件设置页）
                var root = new HBoxContainer();
                root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                root.AddThemeConstantOverride("separation", 0);
                pc.AddChild(root);
                var nav = new VBoxContainer();
                nav.CustomMinimumSize = new Vector2(104, 0);
                nav.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
                nav.AddThemeConstantOverride("separation", 2);
                root.AddChild(nav);
                _navBox = nav;
                var contentArea = new Control();
                contentArea.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                contentArea.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
                contentArea.ClipContents = true;
                root.AddChild(contentArea);
                _contentArea = contentArea;
                _panelTabs.Clear();
                _navBtns.Clear();

                // 功能分类由 MakeTab 创建：左侧导航按钮 + 右侧内容（见 MakeTab/SwitchPanel）
                // ===== 标签：关于（主页，显示游戏/进程信息） =====
                var vbAbout = MakeTab(_contentArea, "关于");
                // 一键开启全部功能（避免逐个找开关漏开）
                var allBtn = new Button();
                allBtn.Text = "一键开启全部功能";
                allBtn.Pressed += () =>
                {
                    ModSettings.GloveMode = true; ModSettings.PlantTriple = true;
                    ModSettings.NoCostRise = true; ModSettings.ZeroCost = true;
                    ModSettings.CanChooseAll = true; ModSettings.NoZombieSpawn = true;
                    ModSettings.BossNoBow = true; ModSettings.NoThrowImp = true;
                    ModSettings.ZombieNoMove = true; ModSettings.BulletTrack = true;
                    ModSettings.BulletRandom = true; ModSettings.PlantAutoFire = true;
                    ModSettings.NoCooldown = true; ModSettings.InfiniteSun = true;
                    ModSettings.InfiniteCoin = true; ModSettings.AlmanacAll = true;
                    ModSettings.IgnorePurple = true;
                    ModSettings.Save();
                    Bootstrap.Log("ModUI: 一键开启全部功能");
                };
                vbAbout.AddChild(allBtn);
                // 一键全部关闭（测试原始行为/排查 bug 用）
                var offBtn = new Button();
                offBtn.Text = "一键全部关闭（测原始行为）";
                offBtn.Pressed += () =>
                {
                    ModSettings.ResetAll();
                    // "测原始行为"：把默认就开的美化项也强制关掉（弹幕/背景/透视/重叠/无视地形等）
                    ModSettings.FunEnabled = false; ModSettings.BGEnabled = false;
                    ModSettings.ESPEnabled = false; ModSettings.VaseESP = false;
                    ModSettings.PlantOverlap = false; ModSettings.IgnoreTerrain = false;
                    ModSettings.Save();
                    Bootstrap.Log("ModUI: 已一键全部关闭");
                };
                vbAbout.AddChild(offBtn);
                // 保存当前配置（把当前所有开关状态写入 modsettings.txt，重开游戏保持）
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
                biliBtn.Text = "杂交MOD（最新V0.9.3修复版）";
                biliBtn.Pressed += () => { try { Godot.OS.ShellOpen("https://www.bilibili.com/video/BV1Gx8g6cEyx"); } catch { } };
                vbAbout.AddChild(biliBtn);

                // ===== 标签：文字 =====
                var vb = MakeTab(_contentArea, "文字");
                vb.AddChild(Section("文字效果"));
                var chk = new CheckButton();
                chk.Text = "启用彩色";
                chk.ButtonPressed = ModSettings.Enabled;
                chk.Toggled += OnToggled;
                vb.AddChild(chk);

                var cmLabel = new Label();
                cmLabel.Text = "文字颜色模式";
                vb.AddChild(cmLabel);
                var cmSel = new OptionButton();
                cmSel.AddItem("彩虹律动", 0);
                cmSel.AddItem("纯白（不律动）", 1);
                cmSel.AddItem("纯黑（不律动）", 2);
                cmSel.Select(ModSettings.TextColorMode > 2 ? 0 : ModSettings.TextColorMode);
                cmSel.ItemSelected += idx =>
                {
                    ModSettings.TextColorMode = (int)idx;
                    Bootstrap.Log("ModUI: 文字颜色模式=" + ModSettings.TextColorMode);
                };
                vb.AddChild(cmSel);

                var spdLabel = new Label();
                spdLabel.Text = "渐变速度";
                vb.AddChild(spdLabel);
                var speedRow = new HBoxContainer();
                speedRow.AddThemeConstantOverride("separation", 8);
                vb.AddChild(speedRow);
                var speed = new HSlider();
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
                row.AddThemeConstantOverride("separation", 6);
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
                var vbGame = MakeTab(_contentArea, "游戏");
                vbGame.AddChild(Section("游戏修改"));
                var chkPerf = new CheckButton();
                chkPerf.Text = "性能优化模式";
                chkPerf.ButtonPressed = ModSettings.PerfMode;
                chkPerf.Toggled += on => { ModSettings.PerfMode = on; Bootstrap.Log("ModUI: 性能优化模式=" + on); };
                vbGame.AddChild(chkPerf);
                var chkSun = new CheckButton();
                chkSun.Text = "无限阳光";
                chkSun.ButtonPressed = ModSettings.InfiniteSun;
                chkSun.Toggled += OnSunToggled;
                vbGame.AddChild(chkSun);
                // 自由加减阳光
                vbGame.AddChild(Section("阳光数量"));
                var sunRow = new HBoxContainer();
                sunRow.AddThemeConstantOverride("separation", 6);
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
                var chkCannon = new CheckButton();
                chkCannon.Text = "炮类无冷却";
                chkCannon.ButtonPressed = ModSettings.CannonNoCooldown;
                chkCannon.Toggled += OnCannonToggled;
                vbGame.AddChild(chkCannon);
                var chkHouse = new CheckButton();
                chkHouse.Text = "无视僵尸进家";
                chkHouse.ButtonPressed = ModSettings.IgnoreHouse;
                chkHouse.Toggled += OnHouseToggled;
                vbGame.AddChild(chkHouse);
                var chkWarn = new CheckButton();
                chkWarn.Text = "无视警戒线";
                chkWarn.ButtonPressed = ModSettings.IgnoreWarningLine;
                chkWarn.Toggled += OnWarnToggled;
                vbGame.AddChild(chkWarn);
                var chkPurple = new CheckButton();
                chkPurple.Text = "无视紫卡限制";
                chkPurple.ButtonPressed = ModSettings.IgnorePurple;
                chkPurple.Toggled += OnPurpleToggled;
                vbGame.AddChild(chkPurple);
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
                var winAllBtn = new Button();
                winAllBtn.Text = "一键通关全部";
                winAllBtn.Pressed += OnCompleteAll;
                vbGame.AddChild(winAllBtn);
                var dailyBtn = new Button();
                dailyBtn.Text = "一键通关每日挑战";
                dailyBtn.Pressed += OnCompleteDaily;
                vbGame.AddChild(dailyBtn);
                // 按模式一键通关（复刻官方 CommandManager）
                var modeRow = new HBoxContainer();
                modeRow.AddThemeConstantOverride("separation", 4);
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
                fxRow.AddThemeConstantOverride("separation", 4);
                vbGame.AddChild(fxRow);
                var resetBrainBtn = new Button();
                resetBrainBtn.Text = "重置脑子";
                resetBrainBtn.Pressed += OnResetBrain;
                fxRow.AddChild(resetBrainBtn);
                var rainBtn = new Button(); rainBtn.Text = "下雨"; rainBtn.Pressed += () => GameCheats.ToggleScreenEffect("Rain", true); fxRow.AddChild(rainBtn);
                var cancelRainBtn = new Button(); cancelRainBtn.Text = "停雨"; cancelRainBtn.Pressed += () => GameCheats.ToggleScreenEffect("Rain", false); fxRow.AddChild(cancelRainBtn);
                var stormBtn = new Button(); stormBtn.Text = "风暴"; stormBtn.Pressed += () => GameCheats.ToggleScreenEffect("Storm", true); fxRow.AddChild(stormBtn);
                var cancelStormBtn = new Button(); cancelStormBtn.Text = "停风暴"; cancelStormBtn.Pressed += () => GameCheats.ToggleScreenEffect("Storm", false); fxRow.AddChild(cancelStormBtn);
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
                var fillSelBtn = new Button();
                fillSelBtn.Text = "刷全场自选卡";
                fillSelBtn.Pressed += OnSpawnUI;
                vbGame.AddChild(fillSelBtn);
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
                curNumRow.AddThemeConstantOverride("separation", 8);
                vbGame.AddChild(curNumRow);
                var curNum = new HSlider();
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
                curRow.AddThemeConstantOverride("separation", 4);
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
                gsRow.AddThemeConstantOverride("separation", 8);
                vbGame.AddChild(gsRow);
                // ★ 联机中锁定：TimeScale 是每端本地量，两端倍速不同会让僵尸移动/波次节奏/
                //   判定全部漂移（即同步失败）。CheatPolicy 会把 GameSpeed 复位为 1.0，
                //   这里同时把滑块锁住，免得玩家拖完又被弹回去却不知道原因。
                bool gsLocked = CheatPolicy.IsLockedForUi;
                var gs = new HSlider();
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
                var vbEsp = MakeTab(_contentArea, "透视");
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
                zsRow.AddThemeConstantOverride("separation", 8);
                vbEsp.AddChild(zsRow);
                var zs = new HSlider();
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
                psRow.AddThemeConstantOverride("separation", 8);
                vbEsp.AddChild(psRow);
                var ps = new HSlider();
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
                var vbCombat = MakeTab(_contentArea, "战斗");
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
                pasRow.AddThemeConstantOverride("separation", 8);
                vbCombat.AddChild(pasRow);
                var pas = new HSlider();
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
                zasRow.AddThemeConstantOverride("separation", 8);
                vbCombat.AddChild(zasRow);
                var zas = new HSlider();
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
                smRow.AddThemeConstantOverride("separation", 8);
                vbCombat.AddChild(smRow);
                var sm = new HSlider();
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
                phpRow.AddThemeConstantOverride("separation", 8);
                vbCombat.AddChild(phpRow);
                var php = new HSlider();
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
                zhpRow.AddThemeConstantOverride("separation", 8);
                vbCombat.AddChild(zhpRow);
                var zhp = new HSlider();
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
                var chkGlove = new CheckButton();
                chkGlove.Text = "手套挪动";
                chkGlove.ButtonPressed = ModSettings.GloveMode;
                chkGlove.Toggled += OnGloveToggled;
                vbCombat.AddChild(chkGlove);
                var chkTriple = new CheckButton();
                chkTriple.Text = "三倍种植";
                chkTriple.ButtonPressed = ModSettings.PlantTriple;
                chkTriple.Toggled += on => { ModSettings.PlantTriple = on; Bootstrap.Log("ModUI: 三倍种植=" + on); };
                vbCombat.AddChild(chkTriple);
                var chkNoRise = new CheckButton();
                chkNoRise.Text = "卡价不涨价";
                chkNoRise.ButtonPressed = ModSettings.NoCostRise;
                chkNoRise.Toggled += on => { ModSettings.NoCostRise = on; Bootstrap.Log("ModUI: 卡价不涨价=" + on); };
                vbCombat.AddChild(chkNoRise);
                var chkZero = new CheckButton();
                chkZero.Text = "零消费";
                chkZero.ButtonPressed = ModSettings.ZeroCost;
                chkZero.Toggled += on => { ModSettings.ZeroCost = on; Bootstrap.Log("ModUI: 零消费=" + on); };
                vbCombat.AddChild(chkZero);
                var chkChoose = new CheckButton();
                chkChoose.Text = "所有关卡可选卡";
                chkChoose.ButtonPressed = ModSettings.CanChooseAll;
                chkChoose.Toggled += on => { ModSettings.CanChooseAll = on; Bootstrap.Log("ModUI: 所有关卡可选卡=" + on); };
                vbCombat.AddChild(chkChoose);
                var chkForceRain = new CheckButton();
                chkForceRain.Text = "强制种子雨";
                chkForceRain.ButtonPressed = ModSettings.ForceRain;
                chkForceRain.Toggled += on => { ModSettings.ForceRain = on; Bootstrap.Log("ModUI: 强制种子雨=" + on); };
                vbCombat.AddChild(chkForceRain);
                var chkForceFog = new CheckButton();
                chkForceFog.Text = "强制迷雾";
                chkForceFog.ButtonPressed = ModSettings.ForceFog;
                chkForceFog.Toggled += on => { ModSettings.ForceFog = on; Bootstrap.Log("ModUI: 强制迷雾=" + on); };
                vbCombat.AddChild(chkForceFog);
                var chkRedLine = new CheckButton();
                chkRedLine.Text = "无视红线";
                chkRedLine.ButtonPressed = ModSettings.IgnoreRedLine;
                chkRedLine.Toggled += on => { ModSettings.IgnoreRedLine = on; Bootstrap.Log("ModUI: 无视红线=" + on); };
                vbCombat.AddChild(chkRedLine);
                var chkShovel = new CheckButton();
                chkShovel.Text = "铲子铲植物生成樱桃";
                chkShovel.ButtonPressed = ModSettings.ShovelCherry;
                chkShovel.Toggled += on => { ModSettings.ShovelCherry = on; Bootstrap.Log("ModUI: 铲子生成樱桃=" + on); };
                vbCombat.AddChild(chkShovel);
                var chkBlover = new CheckButton();
                chkBlover.Text = "三叶草吹飞所有僵尸";
                chkBlover.ButtonPressed = ModSettings.BloverClearAll;
                chkBlover.Toggled += on => { ModSettings.BloverClearAll = on; Bootstrap.Log("ModUI: 三叶草吹飞=" + on); };
                vbCombat.AddChild(chkBlover);
                // 炮类一次发射多枚（数量可调 1-10）：玉米投手/卷心菜/西瓜/豌豆等原生 fireNum 生效
                var hbCannon = new HBoxContainer();
                var lblCannonT = new Label(); lblCannonT.Text = "炮类一次发射:"; lblCannonT.AddThemeFontSizeOverride("font_size", 14);
                var btnCMinus = new Button(); btnCMinus.Text = "-";
                var lblCannonV = new Label(); lblCannonV.Text = ModSettings.CannonMultiShot.ToString(); lblCannonV.AddThemeFontSizeOverride("font_size", 15); lblCannonV.CustomMinimumSize = new Vector2(30, 0); lblCannonV.HorizontalAlignment = HorizontalAlignment.Center;
                var btnCPlus = new Button(); btnCPlus.Text = "+";
                btnCMinus.Pressed += () => { ModSettings.CannonMultiShot = System.Math.Max(1, ModSettings.CannonMultiShot - 1); lblCannonV.Text = ModSettings.CannonMultiShot.ToString(); Bootstrap.Log("ModUI: 炮类发射数量=" + ModSettings.CannonMultiShot); };
                btnCPlus.Pressed += () => { ModSettings.CannonMultiShot = System.Math.Min(10, ModSettings.CannonMultiShot + 1); lblCannonV.Text = ModSettings.CannonMultiShot.ToString(); Bootstrap.Log("ModUI: 炮类发射数量=" + ModSettings.CannonMultiShot); };
                hbCannon.AddChild(lblCannonT); hbCannon.AddChild(btnCMinus); hbCannon.AddChild(lblCannonV); hbCannon.AddChild(btnCPlus);
                vbCombat.AddChild(hbCannon);
                var chkRange = new CheckButton();
                chkRange.Text = "全植物全图射程";
                chkRange.ButtonPressed = ModSettings.PlantFullRange;
                chkRange.Toggled += on => { ModSettings.PlantFullRange = on; Bootstrap.Log("ModUI: 全图射程=" + on); };
                vbCombat.AddChild(chkRange);
                var chkCrystal = new CheckButton();
                chkCrystal.Text = "无限水晶(10亿)";
                chkCrystal.ButtonPressed = ModSettings.CrystalInfinite;
                chkCrystal.Toggled += on => { ModSettings.CrystalInfinite = on; Bootstrap.Log("ModUI: 无限水晶=" + on); };
                vbCombat.AddChild(chkCrystal);
                var chkWave = new CheckButton();
                chkWave.Text = "波次暂停";
                chkWave.ButtonPressed = ModSettings.WavePaused;
                chkWave.Toggled += on => { ModSettings.WavePaused = on; Bootstrap.Log("ModUI: 波次暂停=" + on); };
                vbCombat.AddChild(chkWave);
                var chkNoZ = new CheckButton();
                chkNoZ.Text = "禁止出僵尸";
                chkNoZ.ButtonPressed = ModSettings.NoZombieSpawn;
                chkNoZ.Toggled += on => { ModSettings.NoZombieSpawn = on; Bootstrap.Log("ModUI: 禁止出僵尸=" + on); };
                vbCombat.AddChild(chkNoZ);
                var chkBow = new CheckButton();
                chkBow.Text = "僵王不低头";
                chkBow.ButtonPressed = ModSettings.BossNoBow;
                chkBow.Toggled += on => { ModSettings.BossNoBow = on; Bootstrap.Log("ModUI: 僵王不低头=" + on); };
                vbCombat.AddChild(chkBow);
                var chkImp = new CheckButton();
                chkImp.Text = "巨人禁止投小鬼";
                chkImp.ButtonPressed = ModSettings.NoThrowImp;
                chkImp.Toggled += on => { ModSettings.NoThrowImp = on; Bootstrap.Log("ModUI: 巨人禁投小鬼=" + on); };
                vbCombat.AddChild(chkImp);
                // ===== 飞贼 / 小丑 组合技 =====
                vbCombat.AddChild(new HSeparator());
                vbCombat.AddChild(Section("飞贼/小丑组合技"));
                var chkBungi = new CheckButton();
                chkBungi.Text = "飞贼秒偷";
                chkBungi.ButtonPressed = ModSettings.BungiFastGrab;
                chkBungi.Toggled += on => { ModSettings.BungiFastGrab = on; Bootstrap.Log("ModUI: 飞贼秒偷=" + on); };
                vbCombat.AddChild(chkBungi);
                var chkUmbrella = new CheckButton();
                chkUmbrella.Text = "飞贼无视保护伞";
                chkUmbrella.ButtonPressed = ModSettings.BungiIgnoreUmbrella;
                chkUmbrella.Toggled += on => { ModSettings.BungiIgnoreUmbrella = on; Bootstrap.Log("ModUI: 飞贼无视保护伞=" + on); };
                vbCombat.AddChild(chkUmbrella);
                var chkJack = new CheckButton();
                chkJack.Text = "小丑秒炸";
                chkJack.ButtonPressed = ModSettings.JackboxFastBomb;
                chkJack.Toggled += on => { ModSettings.JackboxFastBomb = on; Bootstrap.Log("ModUI: 小丑秒炸=" + on); };
                vbCombat.AddChild(chkJack);
                var chkFollow = new CheckButton();
                chkFollow.Text = "全场僵尸吸附鼠标";
                chkFollow.ButtonPressed = ModSettings.ZombiesFollowMouse;
                chkFollow.Toggled += on => { ModSettings.ZombiesFollowMouse = on; Bootstrap.Log("ModUI: 僵尸吸附鼠标=" + on); };
                vbCombat.AddChild(chkFollow);
                var chkSpawnJack = new CheckButton();
                chkSpawnJack.Text = "飞贼偷后生成小丑";
                chkSpawnJack.ButtonPressed = ModSettings.BungiSpawnJackbox;
                chkSpawnJack.Toggled += on => { ModSettings.BungiSpawnJackbox = on; Bootstrap.Log("ModUI: 偷后生成小丑=" + on); };
                vbCombat.AddChild(chkSpawnJack);
                var btnGrabAll = new Button();
                btnGrabAll.Text = "全图生成飞贼";
                btnGrabAll.Pressed += () => { int b = GameCheats.SpawnBungiAllField(); Bootstrap.Log("ModUI: 全图生成飞贼 x" + b); };
                vbCombat.AddChild(btnGrabAll);

                // 子弹类型修改：下拉选择一种子弹，所有植物子弹替换成该种类（任意模式）
                vbCombat.AddChild(new HSeparator());
                vbCombat.AddChild(Section("子弹类型修改"));
                var btLabel = new Label();
                btLabel.Text = "把植物子弹改成（需战斗中）：";
                vbCombat.AddChild(btLabel);
                var btSel = new OptionButton();
                btSel.AddItem("关闭（不改子弹）", 0);
                string[] bkeys = GameCheats.GetProjectileKeys();
                if (bkeys.Length == 0)
                    btLabel.Text = "子弹列表为空：请进入战斗场景后重开面板自动加载";
                int curIdx = 0;
                for (int i = 0; i < bkeys.Length; i++)
                {
                    btSel.AddItem(GameCheats.TranslateBulletName(bkeys[i]), i + 1);
                    if (bkeys[i] == ModSettings.BulletType) curIdx = i + 1;
                }
                btSel.Select(curIdx);
                int sel = curIdx;
                btSel.ItemSelected += idx =>
                {
                    sel = (int)idx;
                    ModSettings.BulletType = (sel > 0 && sel <= bkeys.Length) ? bkeys[sel - 1] : "";
                    Bootstrap.Log("ModUI: 自定义子弹类型=" + ModSettings.BulletType);
                };
                vbCombat.AddChild(btSel);

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

                // ===== 标签：趣味 =====
                var vbFun = MakeTab(_contentArea, "趣味");
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
                var chkSquash = new CheckButton();
                chkSquash.Text = "Q弹模式";
                chkSquash.ButtonPressed = ModSettings.PlantSquash;
                chkSquash.Toggled += OnSquashToggled;
                vbFun.AddChild(chkSquash);
                var chkJelly = new CheckButton();
                chkJelly.Text = "果冻模式";
                chkJelly.ButtonPressed = ModSettings.JellyMode;
                chkJelly.Toggled += on => { ModSettings.JellyMode = on; Bootstrap.Log("ModUI: 果冻模式=" + on); };
                vbFun.AddChild(chkJelly);

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
                dsRow.AddThemeConstantOverride("separation", 8);
                vbFun.AddChild(dsRow);
                var ds = new HSlider();
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

                // ===== 标签：自动（自动打对局） =====
                // ===== 标签：皮肤（装扮）解锁 =====
                var vbSkin = MakeTab(_contentArea, "皮肤");
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
                // 炫酷动画：给面板内所有按钮挂悬浮光晕 + 点击金色波纹
                try { ApplyGoldFx(pc); } catch { }
                SwitchPanel("关于");   // 默认选中"关于"
            }
            catch (System.Exception ex) { Bootstrap.Log("ModUI 面板异常: " + ex.Message); }
        }

        /// <summary>拖动设置面板：按住面板空白处拖动。</summary>
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
                else if (@event is InputEventMouseMotion && _panelDragging)
                {
                    _panel.Position = _panel.GetGlobalMousePosition() + _panelDragOffset;
                }
            }
            catch (System.Exception ex) { Bootstrap.Log("ModUI 面板拖动异常: " + ex.Message); }
        }

        /// <summary>分区标题（带金色流光扫过）。</summary>
        static Label Section(string text)
        {
            var l = new Label();
            l.Text = text;
            l.AddThemeFontSizeOverride("font_size", 16);
            l.AddThemeColorOverride("font_color", new Color(1f, 0.82f, 0.3f));
            // 炫酷动画：金色流光周期性扫过标题
            try
            {
                var shine = new ColorRect();
                shine.Color = new Color(1f, 0.85f, 0.5f, 0f);
                shine.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                shine.MouseFilter = Control.MouseFilterEnum.Ignore;
                l.AddChild(shine);
                var tw = shine.CreateTween();
                tw.SetLoops(0);
                tw.TweenProperty(shine, "color:a", 0.20f, 0.6f).SetDelay(1.5f);
                tw.TweenProperty(shine, "color:a", 0f, 0.6f);
            }
            catch { }
            return l;
        }

        /// <summary>创建分类内容（右侧）+ 左侧导航按钮，返回内容 VBox；title 即导航名。</summary>
        static VBoxContainer MakeTab(Control area, string title)
        {
            var scroll = new ScrollContainer();
            scroll.Name = title;
            scroll.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
            area.AddChild(scroll);
            var vb = new VBoxContainer();
            vb.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            vb.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
            vb.AddThemeConstantOverride("separation", 4);
            scroll.AddChild(vb);
            // 金色滚动条（深色背景下可见）
            try
            {
                var vbar = scroll.GetVScrollBar();
                if (vbar != null)
                {
                    var grab = new StyleBoxFlat();
                    grab.BgColor = new Color(0.85f, 0.68f, 0.3f, 0.9f);
                    grab.SetCornerRadiusAll(3);
                    vbar.AddThemeStyleboxOverride("grabber", grab);
                    vbar.AddThemeStyleboxOverride("grabber_highlight", grab);
                    var bg = new StyleBoxFlat();
                    bg.BgColor = new Color(0.3f, 0.24f, 0.14f, 0.5f);
                    bg.SetCornerRadiusAll(3);
                    vbar.AddThemeStyleboxOverride("scroll", bg);
                }
            }
            catch { }
            _panelTabs[title] = vb;
            // 左侧导航按钮
            if (_navBox != null)
            {
                var nb = new Button();
                nb.Text = title;
                nb.Alignment = HorizontalAlignment.Left;
                var t = title;
                nb.Pressed += () => SwitchPanel(t);
                _navBox.AddChild(nb);
                _navBtns[title] = nb;
                StyleNav(nb, false);
            }
            return vb;
        }

        /// <summary>左侧导航按钮样式（选中金色高亮 + 金色左边条）。</summary>
        static void StyleNav(Button nb, bool selected)
        {
            nb.AddThemeStyleboxOverride("normal", MakeNavStyle(selected));
            nb.AddThemeStyleboxOverride("hover", MakeNavStyle(selected));
            nb.AddThemeStyleboxOverride("pressed", MakeNavStyle(selected));
            nb.AddThemeStyleboxOverride("focus", MakeNavStyle(selected));
        }

        static StyleBoxFlat MakeNavStyle(bool selected)
        {
            var sb = new StyleBoxFlat();
            sb.BgColor = selected ? new Color(0.55f, 0.45f, 0.20f, 0.42f) : new Color(0f, 0f, 0f, 0f);
            sb.SetCornerRadiusAll(8);
            sb.ContentMarginLeft = 10;
            sb.ContentMarginRight = 6;
            sb.ContentMarginTop = 8;
            sb.ContentMarginBottom = 8;
            if (selected)
            {
                sb.BorderColor = GoldColor();
                sb.SetBorderWidthAll(1);
                sb.BorderWidthLeft = 4;   // 金色左边条
            }
            return sb;
        }

        /// <summary>切换左侧导航：显示对应分类 + 金色闪光扫过。</summary>
        static void SwitchPanel(string name)
        {
            _curNav = name;
            foreach (var kv in _panelTabs)
            {
                bool sel = kv.Key == name;
                kv.Value.Visible = sel;
                if (kv.Value.GetParent() is Control p) p.Visible = sel;
                if (_navBtns.TryGetValue(kv.Key, out var nb)) StyleNav(nb, sel);
            }
            // 炫酷动画：内容区金色闪光
            try
            {
                if (_contentArea != null && GodotObject.IsInstanceValid(_contentArea))
                {
                    var flash = new ColorRect();
                    flash.Color = new Color(0.95f, 0.75f, 0.3f, 0f);
                    flash.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                    flash.MouseFilter = Control.MouseFilterEnum.Ignore;
                    _contentArea.AddChild(flash);
                    var fw = flash.CreateTween();
                    fw.TweenProperty(flash, "color:a", 0.22f, 0.10f);
                    fw.TweenProperty(flash, "color:a", 0f, 0.28f);
                    fw.TweenCallback(Callable.From(flash.QueueFree));
                }
            }
            catch { }
            // 炫酷动画：新内容滑动进入（只缩放滑入，不做透明度——避免内容半透明看不见开关）
            try
            {
                if (_panelTabs.TryGetValue(name, out var cur) && cur != null && GodotObject.IsInstanceValid(cur))
                {
                    cur.Modulate = new Color(1f, 1f, 1f, 1f);   // 强制不透明
                    cur.PivotOffset = new Vector2(0, cur.Size.Y * 0.5f);
                    cur.Scale = new Vector2(0.97f, 1f);
                    var tw2 = cur.CreateTween();
                    tw2.TweenProperty(cur, "scale", Vector2.One, 0.22f).SetTrans(Tween.TransitionType.Quint).SetEase(Tween.EaseType.Out);
                }
            }
            catch { }
        }

        /// <summary>金色流光：每 20 帧更新面板/悬浮按钮/选中导航的金色描边（由 FrameDriver 每帧驱动）。
        /// 降频至 20 帧：AddThemeStyleboxOverride 会触发 Godot theme 重算，拖动面板时每 3 帧全量重算是卡顿根因。</summary>
        public static void UpdateFx(Node root)
        {
            try
            {
                CheckUIToggle();   // 每帧检测 Tab：开关悬浮窗
                if (++_fxTimer % 20 != 0) return;
                _goldT += 0.05f;
                if (_panel != null && GodotObject.IsInstanceValid(_panel))
                    _panel.AddThemeStyleboxOverride("panel", MakeGoldStyleBox(18));
                if (_btn != null && GodotObject.IsInstanceValid(_btn))
                {
                    _btn.AddThemeStyleboxOverride("normal", MakeGoldStyleBox(14));
                    _btn.AddThemeStyleboxOverride("hover", MakeGoldStyleBox(14, true));
                }
                if (_navBtns.TryGetValue(_curNav, out var nb) && nb != null && GodotObject.IsInstanceValid(nb))
                    StyleNav(nb, true);
                // 边框流光光点：沿面板边框顺时针环绕
                if (_borderDot != null && GodotObject.IsInstanceValid(_borderDot) && _panel != null && GodotObject.IsInstanceValid(_panel))
                {
                    float t = (_goldT * 0.5f) % 1f;
                    var sz = _panel.Size;
                    Vector2 pos;
                    if (t < 0.25f) pos = new Vector2(sz.X * (t / 0.25f), 0);
                    else if (t < 0.5f) pos = new Vector2(sz.X, sz.Y * ((t - 0.25f) / 0.25f));
                    else if (t < 0.75f) pos = new Vector2(sz.X * (1 - (t - 0.5f) / 0.25f), sz.Y);
                    else pos = new Vector2(0, sz.Y * (1 - (t - 0.75f) / 0.25f));
                    _borderDot.Position = pos - new Vector2(2, 2);
                }
                // 性能模式：停粒子（零 CPU 粒子模拟）
                if (_edgeParticles != null)
                {
                    bool emit = !ModSettings.PerfMode;
                    foreach (var p in _edgeParticles)
                        if (p != null && GodotObject.IsInstanceValid(p) && p.Emitting != emit) p.Emitting = emit;
                }
            }
            catch { }
        }

        /// <summary>每帧检测 Tab 键：按下切换悬浮窗显示/隐藏（边沿触发，不连发）。</summary>
        static void CheckUIToggle()
        {
            try
            {
                bool k = Input.IsKeyPressed(Key.Tab);
                if (k && !_tabPrev) ToggleUI();
                _tabPrev = k;
            }
            catch { }
        }

        /// <summary>切换悬浮窗（MOD 按钮 + 面板）显示/隐藏。</summary>
        static void ToggleUI()
        {
            _uiHidden = !_uiHidden;
            if (_uiLayer != null && GodotObject.IsInstanceValid(_uiLayer))
                _uiLayer.Visible = !_uiHidden;
            Bootstrap.Log("ModUI: 悬浮窗 " + (_uiHidden ? "已隐藏" : "已显示") + "（Tab）");
        }

        static readonly Dictionary<(int, bool, bool), StyleBoxFlat> _sbCache = new();

        /// <summary>金色奢华样式：深金黑底 + 金色描边 + 金色投影（流光色由 GoldColor 驱动）。
        /// 复用缓存 StyleBoxFlat（仅刷新 BorderColor），避免拖动/流光时每帧 new + theme 重算。</summary>
        static StyleBoxFlat MakeGoldStyleBox(int radius, bool bright = false, bool pressed = false)
        {
            var key = (radius, bright, pressed);
            if (_sbCache.TryGetValue(key, out var cached)) { cached.BorderColor = GoldColor(); return cached; }
            var sb = new StyleBoxFlat();
            sb.BgColor = new Color(0.16f, 0.13f, 0.09f, 0.97f);
            sb.SetCornerRadiusAll(radius);
            sb.BorderColor = GoldColor();
            sb.SetBorderWidthAll(pressed ? 2 : 1);
            sb.ShadowColor = new Color(0.55f, 0.4f, 0.1f, bright ? 0.40f : 0.22f);
            sb.ShadowSize = bright ? 14 : 10;
            sb.ShadowOffset = new Vector2(0, 3);
            _sbCache[key] = sb;
            return sb;
        }

        /// <summary>金色流光颜色（亮度/色相轻微脉动）。</summary>
        static Color GoldColor()
        {
            return new Color(0.92f, 0.72f + 0.12f * Mathf.Sin(_goldT * 0.9f), 0.30f, 1f);
        }

        /// <summary>面板边缘金色粒子发射器（跟 UI 同色的金色粒子向外飘散）。</summary>
        static void AddEdgeParticles(Control parent, int idx, Vector2 pos, Vector2 dir)
        {
            var p = new CpuParticles2D();
            p.Name = "GoldFx" + idx;
            p.Position = pos;
            p.Amount = 12;
            p.Lifetime = 1.4f;
            p.OneShot = false;
            p.Emitting = true;
            p.Explosiveness = 0.6f;
            p.Direction = dir;
            p.Spread = 28f;
            p.InitialVelocityMin = 18f;
            p.InitialVelocityMax = 42f;
            p.Gravity = new Vector2(0, 14f);   // 轻微下沉，粒子有重量感
            p.ScaleAmountMin = 0.6f;
            p.ScaleAmountMax = 1.3f;
            p.Texture = MakeDotTexture(16);   // 圆点纹理（无纹理时粒子不可见）
            p.ColorRamp = MakeGoldRamp();
            parent.AddChild(p);
            if (_edgeParticles != null && idx >= 0 && idx < _edgeParticles.Length)
                _edgeParticles[idx] = p;
        }

        /// <summary>金色粒子渐变（亮→暗→透明）。</summary>
        static Gradient MakeGoldRamp()
        {
            var g = new Gradient();
            g.Colors = new Color[] {
                new Color(0.95f, 0.75f, 0.30f, 0f),
                new Color(0.95f, 0.75f, 0.30f, 0.9f),
                new Color(1f, 0.92f, 0.55f, 0.9f),
                new Color(0.95f, 0.75f, 0.30f, 0f),
            };
            g.Offsets = new float[] { 0f, 0.3f, 0.6f, 1f };
            return g;
        }

        /// <summary>生成白色圆点纹理（粒子贴图，无纹理时粒子不可见）。</summary>
        static Texture2D MakeDotTexture(int size)
        {
            try
            {
                var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
                float c = size / 2f;
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        float dx = x + 0.5f - c, dy = y + 0.5f - c;
                        float d = Mathf.Sqrt(dx * dx + dy * dy);
                        float a = Mathf.Clamp(c - d, 0f, 1f);
                        img.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                    }
                return ImageTexture.CreateFromImage(img);
            }
            catch { return null; }
        }

        /// <summary>给面板内所有按钮挂：深金色适配样式 + 悬浮放大 + 点击金色波纹。</summary>
        static void ApplyGoldFx(Node node)
        {
            foreach (var child in node.GetChildren(true))
            {
                try
                {
                    if (child is Button b && !b.HasMeta("pzmGoldFx"))
                    {
                        b.SetMeta("pzmGoldFx", true);
                        // 填满容器宽度（长文字 CheckButton 不再超宽被右侧裁剪按不到），文字超宽截断
                        b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                        b.ClipText = true;
                        // 适配深色金色 UI：默认主题按钮是浅色块，在深色面板上很突兀
                        b.AddThemeStyleboxOverride("normal", MakeDarkBtn(false));
                        b.AddThemeStyleboxOverride("hover", MakeDarkBtn(true));
                        b.AddThemeStyleboxOverride("pressed", MakeDarkBtn(true, true));
                        b.AddThemeStyleboxOverride("focus", MakeDarkBtn(false));
                        var orig = b.Scale;
                        b.MouseEntered += () => { if (GodotObject.IsInstanceValid(b)) { b.PivotOffset = b.Size * 0.5f; b.Scale = orig * 1.04f; } };
                        b.MouseExited += () => { if (GodotObject.IsInstanceValid(b)) b.Scale = orig; };
                        b.Pressed += () => SpawnRipple(b);
                    }
                }
                catch { }
                ApplyGoldFx(child);
            }
        }

        /// <summary>深金色按钮样式（适配金色 UI 的深色背景）。</summary>
        static StyleBoxFlat MakeDarkBtn(bool bright, bool pressed = false)
        {
            var sb = new StyleBoxFlat();
            sb.BgColor = bright ? new Color(0.30f, 0.24f, 0.16f, 0.92f) : new Color(0.20f, 0.16f, 0.11f, 0.88f);
            sb.SetCornerRadiusAll(8);
            sb.BorderColor = GoldColor();
            sb.SetBorderWidthAll(pressed ? 2 : 1);
            sb.ShadowColor = new Color(0.55f, 0.4f, 0.1f, bright ? 0.30f : 0.12f);
            sb.ShadowSize = bright ? 10 : 6;
            return sb;
        }

        /// <summary>点击金色波纹：从按钮中心扩散一个金色圆环。</summary>
        static void SpawnRipple(Button b)
        {
            try
            {
                if (_panel == null || !GodotObject.IsInstanceValid(_panel) || b == null || !GodotObject.IsInstanceValid(b)) return;
                var local = _panel.GetLocalMousePosition();
                var ring = new Panel();
                ring.Size = new Vector2(18, 18);
                ring.Position = local - new Vector2(9, 9);
                var sb = new StyleBoxFlat();
                sb.BgColor = new Color(0f, 0f, 0f, 0f);
                sb.BorderColor = new Color(1f, 0.85f, 0.4f, 0.8f);
                sb.SetBorderWidthAll(2);
                sb.SetCornerRadiusAll(9);
                ring.AddThemeStyleboxOverride("panel", sb);
                ring.MouseFilter = Control.MouseFilterEnum.Ignore;
                ring.PivotOffset = new Vector2(9, 9);
                _panel.AddChild(ring);
                var tw = ring.CreateTween();
                tw.SetParallel(true);
                tw.TweenProperty(ring, "scale", new Vector2(7, 7), 0.5f).SetTrans(Tween.TransitionType.Quart).SetEase(Tween.EaseType.Out);
                tw.TweenProperty(ring, "modulate:a", 0f, 0.5f);
                tw.TweenCallback(Callable.From(ring.QueueFree));
            }
            catch { }
        }

        /// <summary>清新卡片样式：浅色 + 圆角 + 细边 + 柔和阴影。</summary>
        private static StyleBoxFlat MakeCardStyleBox(Color bg, int radius, Color? border = null)
        {
            var sb = new StyleBoxFlat();
            sb.BgColor = bg;
            sb.SetCornerRadiusAll(radius);
            sb.BorderColor = border ?? new Color(0f, 0f, 0f, 0.08f);
            sb.SetBorderWidthAll(1);
            sb.ShadowColor = new Color(0f, 0f, 0f, 0.12f);
            sb.ShadowSize = 12;
            sb.ShadowOffset = new Vector2(0, 3);
            return sb;
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

        /// <summary>手写版本号提取（Godot 导出裁剪了 Regex.Match 重载，用字符串扫描避免 Method not found）。
        /// 从 "数字.数字(.数字)" 片段提取版本，未找到返回 "?"。</summary>
        static string ExtractVersion(string s)
        {
            if (string.IsNullOrEmpty(s)) return "?";
            for (int i = 0; i < s.Length; i++)
            {
                if (!char.IsDigit(s[i])) continue;
                int j = i;
                while (j < s.Length && char.IsDigit(s[j])) j++;
                if (j < s.Length && s[j] == '.')
                {
                    int k = j + 1;
                    int dots = 0;
                    while (k < s.Length && (char.IsDigit(s[k]) || s[k] == '.'))
                    {
                        if (s[k] == '.') { dots++; if (dots > 2) break; }
                        k++;
                    }
                    string cand = s.Substring(i, k - i);
                    var parts = cand.Split('.');
                    if (parts.Length >= 2 && parts.Length <= 3 && parts[0].Length > 0 && parts[1].Length > 0)
                        return cand;
                }
            }
            return "?";
        }

        static void OnToggled(bool on) { ModSettings.Enabled = on; Bootstrap.Log("ModUI: 启用=" + on); }
        static void OnFunToggled(bool on) { ModSettings.FunEnabled = on; Bootstrap.Log("ModUI: 弹幕=" + on); }
        static void OnBGToggled(bool on) { ModSettings.BGEnabled = on; Bootstrap.Log("ModUI: 背景=" + on); }
        static void OnSunToggled(bool on) { ModSettings.InfiniteSun = on; Bootstrap.Log("ModUI: 无限阳光=" + on); }
        static void OnCoinToggled(bool on) { ModSettings.InfiniteCoin = on; Bootstrap.Log("ModUI: 无限金币=" + on); }
        static void OnNcToggled(bool on) { ModSettings.NoCooldown = on; Bootstrap.Log("ModUI: 无冷却=" + on); }
        static void OnCannonToggled(bool on) { ModSettings.CannonNoCooldown = on; Bootstrap.Log("ModUI: 炮类无冷却=" + on); }
        static void OnHouseToggled(bool on) { ModSettings.IgnoreHouse = on; Bootstrap.Log("ModUI: 无视进家=" + on); }
        static void OnWarnToggled(bool on) { ModSettings.IgnoreWarningLine = on; Bootstrap.Log("ModUI: 无视警戒线=" + on); }
        static void OnPurpleToggled(bool on) { ModSettings.IgnorePurple = on; Bootstrap.Log("ModUI: 无视紫卡=" + on); }
        static void OnInstantWin() { GameCheats.InstantWinAll(); Bootstrap.Log("ModUI: 已触发立即胜利（跳关）"); }
        static void OnCompleteAll() { GameCheats.CompleteAllLevels(); }
        static void OnCompleteDaily() { GameCheats.CompleteAllDailyLevels(); }
        static void OnShopAll() { GameCheats.GetShopAllItems(); }
        static void OnResetBrain() { GameCheats.ResetAllBrains(); }
        static void OnOverlapToggled(bool on) { ModSettings.PlantOverlap = on; Bootstrap.Log("ModUI: 植物重叠=" + on); }
        static void OnTerrainToggled(bool on) { ModSettings.IgnoreTerrain = on; Bootstrap.Log("ModUI: 无视地形=" + on); }
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
        static void OnGloveToggled(bool on) { ModSettings.GloveMode = on; Bootstrap.Log("ModUI: 手套挪动=" + on); }
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
}
