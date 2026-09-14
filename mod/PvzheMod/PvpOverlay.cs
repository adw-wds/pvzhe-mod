using System;
using System.Reflection;
using Godot;

namespace PvzheMod
{
    /// <summary>
    /// 对战模式的场地分界：把**游戏自带的那条红色警戒线**挪到两半场的分界处。
    ///
    /// ★ 为什么不再自己画线：用户明确说"不是画的红线 是游戏自带的"。
    ///   自绘的红线颜色/粗细/高度都得自己凑，跟关卡整体风格也对不上；
    ///   直接用游戏自己的 WarningLine 节点，外观天然一致、纵向范围自动就是草坪高度，
    ///   连"红线到底多高"这种问题都不存在了。
    ///
    /// ★ 位置怎么定：用游戏自己的 TowerDefenseManager.GetMapCellPlantPos(格子) 把
    ///   第 5 列与第 6 列换算成世界坐标取中点 —— 这就是分界线。
    ///   （上一版是自己扫世界坐标反推格子，绕远且脆：反推要扫一大片，谓词还不单调
    ///     —— 草坪外返回非法值，和"在右半场"同为 false，二分直接塌缩。有正向映射就不该用反推。）
    ///
    /// ★ 必须同时把 feature._triggered 置 true：
    ///   警戒线的语义是"僵尸越过它 → GameFail"。挪到中场之后僵尸**必然**要越过去，
    ///   不短路掉触发就会一越线直接判负。
    ///   植物方的失败条件不靠它 —— 那由"僵尸进家"那套单独负责，两者互不影响。
    ///
    /// ★ 不建任何自绘节点：没有 ColorRect、没有标签、没有顶部状态条。分界线就是游戏那条线本身。
    /// </summary>
    public static class PvpOverlay
    {
        static int _timer;
        static bool _calibrated;
        static bool _noLineLogged;
        static bool _tookOverLogged;
        static bool _stopLogged;
        static bool _calFailLogged;
        static int _calTries;
        /// <summary>草坪纵向范围（自绘兑底线用它定长度）。</summary>
        static float _lawnY0, _lawnY1;
        /// <summary>找不到游戏警戒线时的自绘兑底线。
        /// ★ 实测有的关卡场景里**根本没有 WarningLine 节点**（日志：“本关场景里找不到 WarningLine”）。
        ///   没有兑底就是“什么都不显示”，而用户要的是看得见的分界。</summary>
        static Node2D _fallback;
        static bool _fbLogged;
        static bool _containerLogged;
        /// <summary>分界线是否已交给游戏的 AddWarningColumn 处理。</summary>
        static bool _addedByGame;
        static int _warnTries;
        static bool _warnFailLogged;

        /// <summary>分界线的世界 X。仅当 <see cref="_calibrated"/> 为真时有意义。</summary>
        static float _lineX;

        /// <summary>已接管的那个 WarningLine 节点（场景重建后引用会失效，靠 IsInstanceValid 判）。</summary>
        static Node2D _line;
        /// <summary>它的原始世界 X，退出对战时还原。</summary>
        static float _origX;
        static bool _origXValid;

        /// <summary>每帧由 GameCheats.OnFrame 调用。</summary>
        public static void Tick(Node root)
        {
            try
            {
                if (root == null) return;
                if (!NetPvp.Active) { if (_line != null) Restore(); return; }

                // 换算世界坐标 + 全树找节点都不便宜，降到每 30 帧一次；分界线不需要每帧跟随
                if (++_timer % 30 != 0) return;

                if (!_calibrated && !Calibrate()) return;   // 还没进关 / 草坪还没就绪，下次再试
                Apply(root);
            }
            catch { }
        }

        // ================= 标定 =================

        /// <summary>算出两半场的分界线 X。返回 false = 还没就绪。</summary>
        ///
        /// ★ 用扫描法（已实测标出过 x=395，正是第 5/6 列边界），把格子→世界坐标当作**对照值**打出来。
        ///   上一版改成“只信格子→世界”，结果它拿不到值就彻底不标定了 —— 把已经好用的东西换成了没验证的。
        ///   两个都算，主用一个、另一个只做日志对照，这样哪个不可信一看日志就知道。
        static bool Calibrate()
        {
            _calTries++;

            // ① 主用：扫描法
            float bx, by0, by1;
            bool scanOk = ScanBoundary(out bx, out by0, out by1);

            // ② 对照：格子→世界的正向映射
            var a = GameCheats.NetGridToWorld(new Vector2I(NetPvp.PlantMaxColumn, 4));
            var b = GameCheats.NetGridToWorld(new Vector2I(NetPvp.PlantMaxColumn + 1, 4));
            string g2w = (a.X < -1e6f || b.X < -1e6f)
                ? "不可用"
                : ("中点=" + (int)((a.X + b.X) * 0.5f) + " 左=" + (int)a.X + " 右=" + (int)b.X);

            if (scanOk)
            {
                _lineX = bx;
                _lawnY0 = by0;
                _lawnY1 = by1;
                _calibrated = true;
                Bootstrap.Log("对战：分界线已标定 x=" + (int)bx + "（扫描法；格子→世界 " + g2w + "）");
                Bootstrap.FlushLog();
                return true;
            }

            if (!_calFailLogged && _calTries >= 40)
            {
                _calFailLogged = true;
                Bootstrap.Log("对战：分界线标定失败 —— 扫描扫不到草坪；格子→世界 " + g2w +
                              "；在战斗关=" + GameCheats.NetIsInBattleLevel() +
                              " 阵营=" + NetSession.MyFaction);
                Bootstrap.FlushLog();
            }
            return false;
        }

        /// <summary>扫描法：找一条能解析出合法草坪格子的采样行，
        /// 在上面线性扫 X，取「第 5 列最右」与「第 6 列最左」的中点。
        ///
        /// ★ 不能用二分：NetWorldToGrid 在草坪外返回 (-1,-1)，
        ///   而“非法”与“在右半场”在谓词里同为 false —— 谓词不单调（false→true→false），
        ///   二分会在 off-lawn 那段直接塌缩到左端，标出 x=-1999 这种垃圾值。
        /// ★ 采样行的 Y 不能用固定值：不同关卡草坪高度不一，先扫出能用的那一行。</summary>
        static bool ScanBoundary(out float bx, out float y0, out float y1)
        {
            bx = 0f; y0 = 0f; y1 = 0f;
            try
            {
                float probeY = float.NaN;
                for (float y = -800; y <= 2200; y += 20)
                {
                    var g = GameCheats.NetWorldToGrid(new Vector2(400, y));
                    if (g.X > 0 && g.Y >= 1 && g.Y <= 8) { probeY = y; break; }
                }
                if (float.IsNaN(probeY)) return false;

                float col5MaxX = float.MinValue, col6MinX = float.MaxValue;
                for (float x = -1200; x <= 4200; x += 10)
                {
                    var g = GameCheats.NetWorldToGrid(new Vector2(x, probeY));
                    if (g.X <= 0) continue;
                    if (g.X == NetPvp.PlantMaxColumn + 1) { if (x > col5MaxX) col5MaxX = x; }
                    else if (g.X == NetPvp.PlantMaxColumn + 2) { if (x < col6MinX) col6MinX = x; }
                }
                if (col5MaxX == float.MinValue || col6MinX == float.MaxValue) return false;

                float sampleX = col5MaxX - 20f;
                float yy0 = float.MaxValue, yy1 = float.MinValue;
                for (float y = -1000; y <= 2400; y += 10)
                {
                    var g = GameCheats.NetWorldToGrid(new Vector2(sampleX, y));
                    if (g.X > 0 && g.Y >= 1 && g.Y <= 8)
                    {
                        if (y < yy0) yy0 = y;
                        if (y > yy1) yy1 = y;
                    }
                }
                if (yy0 > yy1) return false;

                bx = (col5MaxX + col6MinX) * 0.5f;
                y0 = yy0;
                y1 = yy1;
                return true;
            }
            catch { return false; }
        }

        // ================= 接管游戏自带警戒线 =================

        static void Apply(Node root)
        {
            try
            {
                if (_addedByGame) { GameCheats.PvpKeepWarningLineInert(); return; }
                // ★ 别每帧试：上一版失败后 _addedByGame 被清回去，于是每帧重试 + 每帧打日志，
                //   日志被“AddWarningColumn 失败”刷满。改成 2 秒一次、失败只记一次。
                if (++_warnTries % 120 != 0) return;
                string e1;
                // ★ 列号要 +1：AddWarningColumn 内部画在 GetMapCellPos(column + 1)，
                //   而我们要的边界是**僵尸区左沿**（= ZombieMinColumn）。
                //   直接传 PlantMaxColumn 就落在植物区那一列，表现就是“红线比僵尸可放置范围偏左一列”。
                int warnCol = NetPvp.PlantMaxColumn + 1;
                if (GameCheats.PvpAddWarningColumn(warnCol, out e1))
                {
                    _addedByGame = true;
                    Bootstrap.Log("对战：已用游戏接口 AddWarningColumn(" + warnCol + ") 建分界线" +
                                  "（总列=" + NetPvp.MapCols() + " 植物1.." + NetPvp.PlantMaxColumn +
                                  " 僵尸" + NetPvp.ZombieMinColumn + ".." + NetPvp.MapCols() + "）");
                    Bootstrap.FlushLog();
                    return;
                }
                if (!_warnFailLogged)
                {
                    _warnFailLogged = true;
                    Bootstrap.Log("对战：AddWarningColumn 不可用（" + e1 + "）—— 改为自绘（每 2 秒重试）");
                    Bootstrap.FlushLog();
                }
                BuildFallback(root);
            }
            catch { }
        }

        static void ApplyLegacy(Node root)
        {
            try
            {
                if (_line == null || !GodotObject.IsInstanceValid(_line))
                {
                    _line = FindWarningLine(root, 0);
                    _origXValid = false;
                    if (_line == null)
                    {
                        // 本关没有游戏自带警戒线 → 兑底：自己画一条，至少看得见分界在哪
                        BuildFallback(root);
                        return;
                    }
                }

                if (!_origXValid) { _origX = _line.GlobalPosition.X; _origXValid = true; }

                var p = _line.GlobalPosition;
                if (Math.Abs(p.X - _lineX) > 0.5f)
                {
                    _line.GlobalPosition = new Vector2(_lineX, p.Y);
                    if (!_tookOverLogged)
                    {
                        _tookOverLogged = true;
                        Bootstrap.Log("对战：已把游戏自带警戒线挪到分界处（原 x=" + (int)_origX +
                                      " → " + (int)_lineX + "），分界线上方不再判负");
                        Bootstrap.FlushLog();
                    }
                }

                // ★ 每帧都短路一次：游戏自己也会重置它（Process 里检测到僵尸会把 _triggered 置回），
                //   只置一次会被覆盖，然后僵尸越线的瞬间就判负了。
                NeutralizeTrigger(_line);
            }
            catch { }
        }

        /// <summary>把警戒线的 _triggered 置 true —— OnWarningLineTriggered/Process 开头都有
        /// if(_triggered) return，置 true 后整条线变成纯展示，不再检测也不再判负。</summary>
        static void NeutralizeTrigger(Node line)
        {
            try
            {
                var ff = line.GetType().GetField("feature", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var feat = ff != null ? ff.GetValue(line) : null;
                if (feat == null) return;
                var f = feat.GetType().GetField("_triggered", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null && f.FieldType == typeof(bool) && !(bool)f.GetValue(feat)) f.SetValue(feat, true);
            }
            catch { }
        }

        /// <summary>本关没有游戏自带警戒线时，自绘一条红色分界线（至少看得见分界在哪）。</summary>
        static void BuildFallback(Node root)
        {
            try
            {
                if (_fallback != null && GodotObject.IsInstanceValid(_fallback)) return;

                // ★ 容器与位置公式**直接抄源码**（TowerDefenseBattleFeatureWarningLine.AddWarningColumn）：
                //     container = TowerDefenseManager.GetCharacterNode()
                //     pos       = GetMapCellPos(new Vector2I(column + 1, 1)) + new Vector2(-10f, 0f)
                //     长度      = GetMapGridSize().X * GetMapGridNum().X - 20f
                //   上一版自己扫世界坐标、容器又回退成 root（屏幕坐标系），所以一直看不见。
                var cn = GameCheats.NetCharacterNode();
                if (cn == null) return;                    // 还没就绪，下次再试
                var basePos = GameCheats.NetMapCellPos(new Vector2I(NetPvp.PlantMaxColumn + 1, 1));
                if (basePos.X < -1e6f) return;             // 拿不到格子坐标，下次再试
                float len = GameCheats.NetLawnLength();
                if (len <= 0f) len = 900f;

                var pos = basePos + new Vector2(-10f, 0f);
                _fallback = new Node2D { Name = "PvpDividerFallback" };
                cn.AddChild(_fallback);
                _fallback.AddChild(new ColorRect
                {
                    Color = new Color(1f, 0.25f, 0.25f, 0.65f),
                    Position = new Vector2(pos.X - 2f, pos.Y),
                    Size = new Vector2(4f, len),
                });
                Bootstrap.Log("对战：已自绘分界线 x=" + (int)pos.X + " 长=" + (int)len + "（容器=" + cn.GetType().Name + "）");
                Bootstrap.FlushLog();
            }
            catch { }
        }

        /// <summary>自绘线的挂载容器（与草坪同一坐标系）。
        ///
        /// ★ 上一次画出来看不见的原因就在这：优先找角色节点的父节点，但那时场上可能
        ///   一个角色都没有（植物方刚进关），于是回退成 root —— root 是**屏幕坐标系**，
        ///   拿草坪坐标 x=395/y=-110 去画就完全在屏幕外，自然什么都看不到。
        ///   现在改成：草坪/地图节点优先，其次角色父节点，最后才 root，并把用了哪个打出来。</summary>
        static Node FindLineContainer(Node root)
        {
            try
            {
                var lawn = FindByTypeKeyword(root, 0, "Lawn");
                if (lawn == null) lawn = FindByTypeKeyword(root, 0, "TowerDefenseMap");
                if (lawn == null) lawn = FindByTypeKeyword(root, 0, "MapPiece");
                if (lawn != null)
                {
                    if (!_containerLogged) { _containerLogged = true; Bootstrap.Log("对战分界: 容器=" + lawn.GetType().Name + "(草坪)"); }
                    return lawn;
                }
                var found = FindFirstChar(root, 0);
                if (found != null && found.GetParent() != null)
                {
                    var p = found.GetParent();
                    if (!_containerLogged) { _containerLogged = true; Bootstrap.Log("对战分界: 容器=" + p.GetType().Name + "(角色父节点)"); }
                    return p;
                }
                // ★ 场上还没角色（植物方刚进关常见）→ 用战斗 control 当容器。
                //   不能再回退成 root：root 是屏幕坐标系，草坪坐标画上去就在屏幕外。
                var ctrl = GameCheats.NetBattleControlNode();
                if (ctrl != null)
                {
                    if (!_containerLogged) { _containerLogged = true; Bootstrap.Log("对战分界: 容器=" + ctrl.GetType().Name + "(战斗control)"); }
                    return ctrl;
                }
            }
            catch { }
            if (!_containerLogged) { _containerLogged = true; Bootstrap.Log("对战分界: 容器=root（没找到草坪节点也没找到control）"); }
            return root;
        }

        static Node FindByTypeKeyword(Node n, int depth, string kw)
        {
            if (n == null || depth > 12) return null;
            try
            {
                var kids = n.GetChildren(true);
                for (int i = 0; i < kids.Count; i++)
                {
                    var c = kids[i];
                    if (c == null) continue;
                    if (c.GetType().Name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0) return c;
                    var r = FindByTypeKeyword(c, depth + 1, kw);
                    if (r != null) return r;
                }
            }
            catch { }
            return null;
        }

        /// <summary>找一个场上角色节点的父节点（保证与草坪同一坐标系）。</summary>
        static Node FindCharacterParent(Node root)
        {
            try
            {
                var found = FindFirstChar(root, 0);
                if (found != null && found.GetParent() != null) return found.GetParent();
            }
            catch { }
            return null;
        }

        static Node FindFirstChar(Node n, int depth)
        {
            if (n == null || depth > 12) return null;
            try
            {
                var kids = n.GetChildren(true);
                for (int i = 0; i < kids.Count; i++)
                {
                    var c = kids[i];
                    if (c == null) continue;
                    string tn = c.GetType().Name;
                    if (tn.IndexOf("TowerDefensePlant", StringComparison.Ordinal) >= 0 ||
                        tn.IndexOf("TowerDefenseZombie", StringComparison.Ordinal) >= 0)
                        return c;
                    var r = FindFirstChar(c, depth + 1);
                    if (r != null) return r;
                }
            }
            catch { }
            return null;
        }

        static Node2D FindWarningLine(Node n, int depth)
        {
            if (n == null || depth > 14) return null;
            try
            {
                if (n.GetType().Name == "WarningLine") return n as Node2D;
                var kids = n.GetChildren(true);
                for (int i = 0; i < kids.Count; i++)
                {
                    var r = FindWarningLine(kids[i], depth + 1);
                    if (r != null) return r;
                }
            }
            catch { }
            return null;
        }

        /// <summary>退出对战/离开关卡：把警戒线放回原位（_triggered 不用管，关卡重建时会新建）。</summary>
        static void Restore()
        {
            try
            {
                if (_line != null && GodotObject.IsInstanceValid(_line) && _origXValid)
                {
                    var p = _line.GlobalPosition;
                    _line.GlobalPosition = new Vector2(_origX, p.Y);
                    if (!_stopLogged)
                    {
                        _stopLogged = true;
                        Bootstrap.Log("对战：已把警戒线还原到 x=" + (int)_origX);
                    }
                }
            }
            catch { }
            _line = null;
            _origXValid = false;
            _calibrated = false;
            _tookOverLogged = false;
            _noLineLogged = false;
            _stopLogged = false;            _calFailLogged = false;
            _calTries = 0;
            _fbLogged = false;
            _containerLogged = false;
            _addedByGame = false;
            _warnTries = 0;
            _warnFailLogged = false;
            try { if (_fallback != null && GodotObject.IsInstanceValid(_fallback)) _fallback.QueueFree(); } catch { }
            _fallback = null;
        }
    }
}
