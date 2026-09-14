using System;
using System.Collections.Generic;
using Godot;

namespace PvzheMod
{
    /// <summary>
    /// 全局彩虹渐变字体：由注入点每帧调用。
    /// 给每个 Label 挂 ShaderMaterial，用顶点坐标做平滑彩虹渐变（每个文字内部都有多种颜色），并随时间流动。
    /// </summary>
    public static class GlobalColor
    {
        private static float _t;
        private static float _tq;               // 量化时间：色相每 0.2 步进（约 10 帧同色），主题色缓存命中免全量重算
        private static int _refresh;
        private static int _ensureCounter;
        private static int _btnTimer;
        private static int _diagTimer;
        private static bool _logged;
        /// <summary>TimeScale 是否正被本 MOD 占用（GameSpeed != 1.0 期间为 true）。
        /// 用于在倍速回落到 1.0 时补写一次恢复，避免联机复位 GameSpeed 后倍速残留。</summary>
        private static bool _timeScaleDirty;
        private static Node _collectedScene;
        // 无 shader：文字彩虹用标准主题色轮换（Godot CanvasItem 主题路径，渲染稳定不闪屏）
        private static readonly List<Control> _labels = new List<Control>();   // Label/RichTextLabel（主题色轮换）
        private static readonly List<Control> _buttons = new List<Control>();  // Button/ItemList/Tree（主题色轮换）

        private static bool _hooked;
        private static Node _root;
        private static ulong _lastFrame = ulong.MaxValue;
        // 加载/场景切换检测：SceneLoading / Loading 过渡节点在树中时，跳过所有遍历类工作
        // （加载最后阶段场景树巨大且未就绪，每帧/降频全树遍历会抢 CPU 导致进度条卡到最后）
        private static bool _sceneLoading;
        private static int _loadingTimer;
        private static bool _loadingDiagLogged;

        /// <summary>检测是否处于加载/场景切换：树中存在类型名含 Loading 的可见节点。
        /// 加载中每 120 帧复查一次（树在剧烈变化），正常时每 30 帧查一次；遍历带节点预算，避免大树下检测本身卡顿。</summary>
        static bool CheckSceneLoading(Node root)
        {
            int every = _sceneLoading ? 120 : 30;
            if (++_loadingTimer < every) return _sceneLoading;
            _loadingTimer = 0;
            bool prev = _sceneLoading;
            string csName = "";
            // 优先 O(1)：当前场景根类型名含 Loading（SceneLoading 过渡场景 / Loading 进度条场景）
            try
            {
                var tree = root != null ? root.GetTree() : null;
                var cs = tree != null ? tree.CurrentScene : null;
                if (cs != null && GodotObject.IsInstanceValid(cs))
                {
                    csName = cs.GetType().Name;
                    if (csName.IndexOf("Loading", StringComparison.OrdinalIgnoreCase) >= 0)
                        _sceneLoading = true;
                    else if (csName.IndexOf("Menu", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             csName.IndexOf("Game", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             csName.IndexOf("Level", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             csName.IndexOf("Scene", StringComparison.OrdinalIgnoreCase) >= 0)
                        _sceneLoading = false;
                    else
                    {
                        // 当前场景名无法判断：兜底限预算扫树找 Loading 类型节点
                        int budget = 1500;
                        _sceneLoading = FindLoadingNode(root, ref budget);
                    }
                }
                else
                {
                    int budget = 1500;
                    _sceneLoading = FindLoadingNode(root, ref budget);
                }
            }
            catch
            {
                int budget = 1500;
                _sceneLoading = FindLoadingNode(root, ref budget);
            }
            if (_sceneLoading != prev || !_loadingDiagLogged)
            {
                _loadingDiagLogged = true;
                Bootstrap.Log("加载检测: loading=" + _sceneLoading + " currentScene=" + (csName.Length > 0 ? csName : "(null)"));
            }
            return _sceneLoading;
        }

        static bool FindLoadingNode(Node node, ref int budget)
        {
            if (budget <= 0 || node == null || !GodotObject.IsInstanceValid(node)) return false;
            budget--;
            try
            {
                // 不查可见性（Node 无 IsVisibleInTree）；加载完成 CurrentScene 兜底会把 _sceneLoading 拉回 false
                if (node.GetType().Name.IndexOf("Loading", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
                foreach (var c in node.GetChildren())
                    if (FindLoadingNode(c, ref budget)) return true;
            }
            catch { }
            return false;
        }

        /// <summary>由注入点（_PhysicsProcess）每帧调用：注册 ProcessFrame 信号（暂停时也触发）并立即执行一次。</summary>
        public static void OnFrame(Node root)
        {
            try
            {
                if (root == null || !GodotObject.IsInstanceValid(root)) return;
                _root = root;
                if (!_hooked)
                {
                    _hooked = true;
                    var tree = root.GetTree();
                    if (tree != null) tree.ProcessFrame += OnProcessFrame;
                    // 引擎层：强制垂直同步 Enabled（部分 Adreno 设备 Adaptive vsync 会导致屏幕闪烁）
                    try { DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Enabled); } catch { }
                }
                OnProcessFrame();
            }
            catch (Exception ex) { Bootstrap.Log("OnFrame 异常: " + ex.GetType().Name + ": " + ex.Message); }
        }

        /// <summary>核心逻辑：ProcessFrame 信号每帧回调（暂停时也发）+ 物理帧立即执行，同帧去重。</summary>
        static void OnProcessFrame()
        {
            try
            {
                var root = _root;
                if (root == null || !GodotObject.IsInstanceValid(root)) return;
                ulong pf = Engine.GetProcessFrames();
                if (pf == _lastFrame) return;
                _lastFrame = pf;
                bool loading = CheckSceneLoading(root);
                if (loading)
                {
                    // 加载/场景切换中：跳过所有遍历类工作（场景树巨大且未就绪，遍历抢 CPU 就是进度条卡顿主因），
                    // 只保留轻量的 shader 参数推进 + 已收集控件的颜色更新
                }
                else
                {
                    if (++_ensureCounter >= 30) { _ensureCounter = 0; ModUI.Ensure(root); }
                    ModUI.TickToast(root);   // 玻璃拟态开关提示弹窗 hook（每 15 帧内部节流）
                    GameCheats.OnFrame(root);    // 游戏修改：无限阳光/金币
                    ESP.OnFrame(root);           // 透视框 + 僵尸/植物大小
                    FunMessages.OnFrame(root);   // 趣味弹幕（独立开关）
                    BackgroundFX.OnFrame(root);  // 背景彩虹（渲染友好版：VERTEX+TIME，零每帧更新）
                }
                // 游戏速度：只有用户明确调整过（≠1.0）才设置——否则完全不碰。
                // 之前每帧强制 Engine.TimeScale=1.0 会覆盖游戏自身 TimeScale（冰冻减速/暂停/慢动作特效），
                // 导致物理帧/渲染帧错位 → "僵尸在动但贴图不动/攻击没动作/植物有动作没子弹"（游戏底层贴图 bug 根源）
                //
                // ★ 联机补丁：CheatPolicy 会把 ModSettings.GameSpeed 复位为 1.0，但这不会自动把
                //   Engine.TimeScale 拨回来——原写法只在 ≠1.0 时写 TimeScale，于是
                //   「先进游戏开高倍速 → 再联机」这条路径下 GameSpeed 已回 1.0 而 TimeScale 仍停在
                //   原倍速，两端时间流速不同 → 同步失败。这里在回落到 1.0 时补写一次恢复，
                //   只写这一帧，之后立即交还控制权，不影响游戏自身的冰冻/暂停/慢动作。
                if (ModSettings.GameSpeed != 1.0f)
                {
                    _timeScaleDirty = true;
                    Engine.TimeScale = ModSettings.GameSpeed;
                }
                else if (_timeScaleDirty)
                {
                    _timeScaleDirty = false;
                    Engine.TimeScale = 1.0f;
                }
                _t += 0.02f * ModSettings.Speed;            // 统一推进律动时间（主题色用）
                _tq = (float)Math.Round(_t * 5f) / 5f;   // 量化：同色保持约 10 帧 → 缓存命中 → 少 AddThemeColorOverride
                if (!ModSettings.Enabled) return;
                if (!_logged) { _logged = true; Bootstrap.Log("OnFrame 运行中"); }

                // ===== 文字彩虹（无 shader 版）=====
                // 闪屏根源：大量 Label 挂自定义 shader 材质（即使 TIME 也每帧 GPU 重算）→ Adreno 驱动闪屏。
                // 彻底不用 shader，只用标准主题色轮换 → 文字整体变色律动，渲染走 Godot 稳定路径。
                var tree = root.GetTree();
                // 从场景树根收集，覆盖弹窗/CanvasLayer/HUD 等所有层
                Node target = tree != null && tree.Root != null ? tree.Root : root;

                // 场景变化时立即重收集；每 60 帧刷新一次（覆盖动态新增控件，避免颜色律动失效）
                // 关键：必须用 includeInternal 遍历（GetChildren(true)），战斗 UI/角色在 internal 节点下，FindChildren 扫不到！
                // 加载中不重收集（树未就绪且巨大），加载完成后 _collectedScene 变化会自动触发重收集
                if (!loading && (_collectedScene != target || ++_refresh >= 60))
                {
                    _refresh = 0;
                    _collectedScene = target;
                    _labels.Clear();
                    _buttons.Clear();
                    CollectControls(target);
                }

                // 主题色轮换律动：正常每 6 帧；禁止闪屏模式 12 帧；性能模式 30 帧（更省）
                int colorInterval = ModSettings.PerfMode ? 30 : (ModSettings.NoFlicker ? 12 : 6);
                if ((_labels.Count > 0 || _buttons.Count > 0) && ++_btnTimer >= colorInterval)
                {
                    _btnTimer = 0;
                    Color bcolor = Colors.White;
                    if (ModSettings.TextColorMode == 1) bcolor = Colors.White;
                    else if (ModSettings.TextColorMode == 2) bcolor = Colors.Black;
                    else
                    {
                        float h = (_tq % 1f + 1f) % 1f;
                        switch (ModSettings.Style)
                        {
                            case 0: break;                          // 彩虹
                            case 1: h = 0.03f + h * 0.05f; break;   // 红金
                            case 2: h = 0.58f + h * 0.10f; break;   // 蓝紫
                            case 3: h = h * 2.0f; break;            // 霓虹
                            case 4: h = 0.90f + h * 0.04f; break;   // 粉彩
                            case 5: h = 0.42f + h * 0.05f; break;   // 青绿
                            case 6: bcolor = new Color(0.92f, 0.92f, 0.92f, 1f); h = -1f; break;  // 纯白
                            case 7: h = h * 0.10f; break;           // 火焰
                            case 8: h = 0.33f + h * 0.06f; break;   // 薄荷
                            case 9: h = 0.90f + h * 0.08f; break;   // 樱花粉
                            case 10: h = 0.75f + h * 0.07f; break;  // 紫罗兰
                            case 11: h = 0.52f + h * 0.06f; break;  // 海洋
                            case 12: h = 0.05f + h * 0.12f; break;  // 日落
                            case 13: h = 0.35f + h * 0.12f; break;  // 极光
                            case 14: h = 0.20f + h * 0.05f; break;  // 森林
                            case 15: h = 0.13f + h * 0.05f; break;  // 柠檬
                        }
                        if (h >= 0f) { var bc = HsvToRgb(h, 0.6f, 1f); bcolor = new Color(bc.r, bc.g, bc.b, 1f); }
                    }
                    for (int i = 0; i < _labels.Count; i++)
                    {
                        var c = _labels[i];
                        if (!GodotObject.IsInstanceValid(c)) continue;
                        c.AddThemeColorOverride("font_color", bcolor);
                        if (!c.HasMeta("pzmInit"))
                        {
                            c.SetMeta("pzmInit", true);
                            c.AddThemeConstantOverride("outline_size", 0);
                            c.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0f));
                        }
                    }
                    for (int i = 0; i < _buttons.Count; i++)
                    {
                        var c = _buttons[i];
                        if (!GodotObject.IsInstanceValid(c)) continue;
                        c.AddThemeColorOverride("font_color", bcolor);
                        if (c is Button)
                        {
                            c.AddThemeColorOverride("font_hover_color", bcolor);
                            c.AddThemeColorOverride("font_pressed_color", bcolor);
                        }
                        if (!c.HasMeta("pzmInit"))
                        {
                            c.SetMeta("pzmInit", true);
                            c.AddThemeConstantOverride("outline_size", 0);
                            c.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0f));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Bootstrap.Log("全局彩色异常: " + ex.GetType().Name + ": " + ex.Message);
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
