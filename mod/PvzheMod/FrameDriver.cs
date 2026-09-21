using System;
using Godot;

namespace PvzheMod
{
    /// <summary>
    /// 统一每帧调度器（v1.0 架构重构核心）：mod 所有运行时工作由这里统一调度。
    /// 由 patcher 注入到 SceneManager/MainMenu._PhysicsProcess 开头调用（替换旧的 GlobalColor.OnFrame 注入）。
    /// 设计原则（v0.9.3 修复版确立）：
    ///   1. 全关零开销 —— GameCheats.OnFrame 内部总开关快速路径，全关时第一行返回
    ///   2. 战斗零律动 —— 战斗场景（TowerDefense*）不挂 shader、不改任何控件颜色
    ///   3. 模块按需 —— ESP/面板/弹幕/背景各有独立开关+降频，全关零开销
    ///   4. 调度单一职责 —— 加载检测/战斗检测/游戏速度/模块顺序全部在这里，GlobalColor 只做律动
    /// </summary>
    public static class FrameDriver
    {
        private static bool _hooked;
        private static Node _root;
        private static int _procHeartbeat;
        private static bool _sceneLoading;
        private static int _loadingTimer;
        private static bool _loadingDiagLogged;
        private static bool _battleScene;
        private static int _battleTimer;
        private static int _ensureCounter;
        private static bool _frameErrorLogged;
        /// <summary>TimeScale 是否正被本 MOD 占用（GameSpeed != 1.0 期间为 true）。
        /// 用于在倍速回落到 1.0 时补写一次恢复，避免联机复位 GameSpeed 后倍速残留。</summary>
        private static bool _timeScaleDirty;

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
                    if (tree != null) tree.ProcessFrame += Tick;
                }
                Tick();
            }
            catch (Exception ex)
            {
                // 只记录首次（含堆栈），避免每帧重复抛异常写盘
                if (!_frameErrorLogged)
                {
                    _frameErrorLogged = true;
                    Bootstrap.Log("FrameDriver.OnFrame 异常(首次): " + ex.GetType().Name + ": " + ex.Message);
                    Bootstrap.Log("  " + (ex.StackTrace != null ? ex.StackTrace.Replace("\n", "\n  ") : ""));
                    if (ex.InnerException != null)
                        Bootstrap.Log("  Inner: " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message);
                }
            }
        }

        /// <summary>每帧调度：加载检测 → 游戏功能 → 战斗检测 → 通用模块 → 战斗零律动 → 非战斗律动。</summary>
        static void Tick()
        {
            try
            {
                var root = _root;
                if (root == null || !GodotObject.IsInstanceValid(root)) return;
                // 心跳：每 1800 次调用（约 30 秒）确认主循环在跑（诊断用，低频避免写盘）+ 缓冲日志落盘
                if (++_procHeartbeat >= 1800) { _procHeartbeat = 0; Bootstrap.Log("FrameDriver 心跳: 主循环运行中"); Bootstrap.FlushLog(); }

                // 功能开关变化采集（内部降频）：diff ModSettings 全部静态字段，供外置修改器弹提示。
                // 放在 loading 判断之前 —— 加载中/主菜单切换开关也要能弹。
                ChangeLog.Tick();

                bool loading = CheckSceneLoading(root);
                // 游戏功能：内部已有"全关总开关快速路径"——全关时第一行返回（零遍历零反射）
                GameCheats.OnFrame(root);
                // 功能开启提示已移到游戏窗口外（由外置修改器在窗口上方弹出）
                if (loading)
                {
                    // 加载/场景切换中：完全休眠（不碰 shader/遍历/按钮）——加载卡顿根因之一
                    return;
                }

                // 战斗场景检测（每 30 帧缓存一次，避免每帧 GetTree().CurrentScene 开销）
                if (++_battleTimer >= 30) { _battleTimer = 0; _battleScene = IsBattleScene(root); }

                // 通用模块（战斗/非战斗都跑；均有独立开关+降频，全关零开销）：
                // ESP 透视/缩放 —— 战斗功能，战斗场景也必须跑
                ESP.OnFrame(root);
                // 游戏内 MOD 悬浮界面已移除（改由外置修改器控制）
                // if (++_ensureCounter >= (ModSettings.PerfMode ? 120 : 30)) { _ensureCounter = 0; ModUI.Ensure(root); }
                // ModUI.UpdateFx(root);        // 金色 UI 流光（每 3 帧内部降频）
                // ModUI.TickToast(root);       // 玻璃拟态开关提示弹窗 hook（每 15 帧内部节流）
                // 性能模式：跳过弹幕/背景动效（真实减负）
                if (!ModSettings.PerfMode)
                {
                    FunMessages.OnFrame(root);   // 趣味弹幕（独立开关）
                    BackgroundFX.OnFrame(root);  // 背景彩虹氛围（独立开关）
                }

                // 游戏速度：始终同步 TimeScale（含改回 1.0，0/负值兜底 1.0）
                ApplyGameSpeed();

                // 战斗场景：到此为止——零律动（不收集、不挂 shader、不改任何控件颜色）。
                // （旧版每 1~6 帧对全场数百~上千控件 AddThemeColorOverride，触发 Godot theme
                //   重算 + 文本重布局，CPU 满载 → "所有动画卡住 / 贴图跟不上 / 子弹打不出"的根因）
                if (_battleScene) return;

                // 非战斗场景：文字律动（Enabled 开关；纯组件，无调度）
                GlobalColor.Tick(root);
            }
            catch (Exception ex)
            {
                Bootstrap.Log("FrameDriver 异常: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>检测是否处于加载/场景切换：树中存在类型名含 Loading 的节点。
        /// 加载中每 120 帧复查一次（树在剧烈变化），正常时每 30 帧查一次；遍历带节点预算，避免大树下检测本身卡顿。</summary>
        static bool CheckSceneLoading(Node root)
        {
            int every = _sceneLoading ? 120 : 30;
            if (++_loadingTimer < every) return _sceneLoading;
            _loadingTimer = 0;
            bool prev = _sceneLoading;
            string csName = "";
            // 优先 O(1)：当前场景根类型名判断（Loading 过渡 / 已知非加载场景）
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
                             csName.IndexOf("Scene", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             csName.IndexOf("TowerDefense", StringComparison.OrdinalIgnoreCase) >= 0)
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
                if (node.GetType().Name.IndexOf("Loading", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
                foreach (var c in node.GetChildren())
                    if (FindLoadingNode(c, ref budget)) return true;
            }
            catch { }
            return false;
        }

        /// <summary>判断是否战斗/游戏场景：战斗及战斗相关界面都是 TowerDefense* 前缀
        /// （TowerDefenseControlNew 战斗、选卡、结算等）——战斗时不挂律动 shader。
        /// 注意：不要用 Game/Level 等宽泛关键词——LevelEditorStage 等非战斗场景会被误判为战斗，导致弹幕/律动被跳过。</summary>
        static bool IsBattleScene(Node root)
        {
            try
            {
                var tree = root.GetTree();
                var cs = tree != null ? tree.CurrentScene : null;
                if (cs != null)
                {
                    var n = cs.GetType().Name;
                    return n.StartsWith("TowerDefense", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { }
            return false;
        }

        /// <summary>游戏速度：只在 GameSpeed 非 1.0 时设置 TimeScale（用户开加速才碰）。
        /// 关键：GameSpeed==1.0 时绝不强制 TimeScale=1.0——游戏自身会控制 TimeScale 实现
        /// 冰冻减速/暂停/慢动作特效，每帧强制 1.0 会覆盖它们，导致游戏逻辑卡住（僵尸/植物动不了）。
        /// 此修复来自手机版 androidmod/PvzheAndroid/GlobalColor.cs：
        /// "之前每帧强制 Engine.TimeScale=1.0 会覆盖游戏自身 TimeScale（冰冻减速/暂停/慢动作特效）"。
        ///
        /// ★ 联机补丁：CheatPolicy 会把 ModSettings.GameSpeed 复位为 1.0，但这**不会**自动
        ///   把 Engine.TimeScale 拨回来——原实现只在 GameSpeed != 1.0 时写 TimeScale，
        ///   于是「先进游戏开高倍速 → 再联机」这条路径下，GameSpeed 已回到 1.0 而 TimeScale
        ///   仍停在原倍速。两端时间流速不同 → 僵尸移动/波次节奏/判定全部漂移 = 同步失败。
        ///   这里在"从非 1.0 回落到 1.0"时补写一次 1.0；只写这一帧，之后立即交还控制权，
        ///   因此不会覆盖游戏自身的冰冻/暂停/慢动作。</summary>
        static void ApplyGameSpeed()
        {
            if (ModSettings.GameSpeed != 1.0f)
            {
                _timeScaleDirty = true;
                Engine.TimeScale = ModSettings.GameSpeed;
                return;
            }
            if (_timeScaleDirty)
            {
                _timeScaleDirty = false;
                Engine.TimeScale = 1.0f;   // 补写一次，把上一条路径遗留的倍速归位
            }
        }
    }
}
