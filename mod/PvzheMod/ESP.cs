using System.Collections.Generic;
using System.Reflection;
using Godot;

namespace PvzheMod
{
    /// <summary>
    /// 透视框（ESP）+ 僵尸/植物大小修改。
    /// 遍历场景树找 TowerDefenseCharacter 节点（反射读 _camp 区分阵营）。
    /// </summary>
    public static class ESP
    {
        private static CanvasLayer _layer;
        private static int _frame;
        private static int _logTimer;
        private static int _zombieCount;
        private static int _plantCount;
        private static int _node2dCount;
        private static int _charCount;
        private static readonly List<string> _typeNames = new List<string>();
        private static readonly List<Node> _drawn = new List<Node>();
        private static Label _hud;

        public static void OnFrame(Node root)
        {
            try
            {
                if (root == null || !GodotObject.IsInstanceValid(root)) return;
                var tree = root.GetTree();
                if (tree == null || tree.Root == null) return;

                bool needESP = ModSettings.ESPEnabled;
                bool needScale = ModSettings.ZombieScale != 1.0f || ModSettings.PlantScale != 1.0f;
                // 透视总开关关闭时移除统计 HUD（否则"僵尸: X  植物: Y"数量还挂在屏幕上）
                if (!needESP && _hud != null && GodotObject.IsInstanceValid(_hud))
                {
                    _hud.QueueFree();
                    _hud = null;
                }
                if (!needESP && !needScale)
                {
                    // 关闭透视/缩放时：先清除已画的框（否则取消勾选后框还在），随后彻底休眠（零遍历、零扫描）
                    if (_drawn.Count > 0)
                    {
                        foreach (var d in _drawn) if (GodotObject.IsInstanceValid(d)) d.QueueFree();
                        _drawn.Clear();
                    }
                    return;
                }

                if (needESP && (_layer == null || !GodotObject.IsInstanceValid(_layer)))
                {
                    _layer = new CanvasLayer();
                    _layer.Name = "PvzESP";
                    _layer.Layer = 100;
                    _layer.ProcessMode = Godot.Node.ProcessModeEnum.Always;
                    tree.Root.AddChild(_layer);
                    // 统计 HUD：显示僵尸/植物数量
                    _hud = new Label();
                    _hud.Name = "PvzESPHud";
                    _hud.Position = new Vector2(20, 16);
                    _hud.AddThemeFontSizeOverride("font_size", 20);
                    _hud.ZIndex = 300;
                    _layer.AddChild(_hud);
                }

                // 罐子透视：物理帧末强制"路灯照亮"（覆盖罐子自身 BatchUpdate）
                if (needESP && ModSettings.VaseESP) HookVaseFrame(root);

                if (++_frame % 3 != 0)
                {
                    // 缩放与透视分离：缩放必须每帧设置（僵尸 Scale 被游戏每帧重置，3 帧一次会被打回 → "僵尸本体建模不变大小"）
                    if (needScale) { _zombieCount = 0; _plantCount = 0; WalkScale(tree.Root); }
                    return;
                }

                if (needESP)
                {
                    foreach (var d in _drawn) if (GodotObject.IsInstanceValid(d)) d.QueueFree();
                    _drawn.Clear();
                }
                _zombieCount = 0;
                _plantCount = 0;
                _node2dCount = 0;
                _charCount = 0;
                if (needScale)
                    WalkScale(tree.Root);   // 每帧缩放（3 帧档位也走，保证帧末是缩放后的值）
                if (needESP)
                    Walk(tree.Root, true, false);   // 透视 + 统计（画框）
                if (_hud != null && GodotObject.IsInstanceValid(_hud))
                    _hud.Text = "僵尸: " + _zombieCount + "  植物: " + _plantCount;
                if (++_logTimer >= 100)
                {
                    _logTimer = 0;
                    Bootstrap.Log("ESP 扫描: Node2D=" + _node2dCount + " 角色=" + _charCount + " 僵尸=" + _zombieCount + " 植物=" + _plantCount + " 框=" + _drawn.Count);
                    if (_typeNames.Count > 0)
                    {
                        var sb = new System.Text.StringBuilder();
                        for (int i = 0; i < _typeNames.Count; i++) { if (i > 0) sb.Append(','); sb.Append(_typeNames[i]); }
                        Bootstrap.Log("节点类型: " + sb.ToString());
                        _typeNames.Clear();
                    }
                }
            }
            catch { }
        }

        /// <summary>每帧缩放（不画框）：僵尸/植物 Scale 每帧设置——僵尸的 Scale 被游戏每帧重置，3 帧一次会被打回。</summary>
        static void WalkScale(Node node)
        {
            foreach (var child in node.GetChildren(true))
            {
                if (child is Node2D n2)
                {
                    string camp = GetCamp(n2);
                    if (camp != null)
                    {
                        bool zombie = camp == "Zombie";
                        if (zombie) _zombieCount++; else _plantCount++;
                        float s = zombie ? ModSettings.ZombieScale : ModSettings.PlantScale;
                        n2.Scale = new Vector2(s, s);
                    }
                }
                WalkScale(child);
            }
        }

        static void Walk(Node node, bool needESP, bool needScale)
        {
            // 用 includeInternal=true：角色挂在 internal 节点（characterRegistry）下，默认 GetChildren 扫不到！
            foreach (var child in node.GetChildren(true))
            {
                if (child is Node2D n2)
                {
                    _node2dCount++;
                    var tn = n2.GetType().Name;
                    if (!tn.StartsWith("Godot") && !tn.StartsWith("TowerDefenseGroundItem") && _typeNames.Count < 10 && !_typeNames.Contains(tn))
                        _typeNames.Add(tn);
                    // 透视罐子：类型名含 Vase 的节点（在物理帧末由 OnVaseFrame 统一强制照亮，这里只做统计）
                    if (needESP && ModSettings.VaseESP && tn.Contains("Vase"))
                        _vaseCount++;
                    string camp = GetCamp(n2);
                    if (camp != null)
                    {
                        _charCount++;
                        bool zombie = camp == "Zombie";
                        if (zombie) _zombieCount++; else _plantCount++;
                        if (needScale)
                        {
                            float s = zombie ? ModSettings.ZombieScale : ModSettings.PlantScale;
                            n2.Scale = new Vector2(s, s);
                        }
                        if (needESP && ((zombie && ModSettings.ESPZombie) || (!zombie && ModSettings.ESPPlant)))
                            DrawBox(n2, zombie);
                    }
                }
                Walk(child, needESP, needScale);
            }
        }

        /// <summary>反射判断节点是否为角色（继承链有 _camp 字段），返回阵营名（Zombie/Plant/…），非角色返回 null。
        /// 性能优化：类型级缓存 _camp 字段位置（同一类型所有实例字段位置相同），避免每节点基类链 GetField 反射。</summary>
        static readonly System.Collections.Concurrent.ConcurrentDictionary<System.Type, System.Reflection.FieldInfo> _campFieldCache = new();
        public static string GetCamp(Node2D n)
        {
            try
            {
                var t = n.GetType();
                if (!_campFieldCache.TryGetValue(t, out var f))
                {
                    // 遍历基类链找 _camp 字段（TowerDefenseCharacter 定义），不管类型名
                    var bt = t;
                    while (bt != null)
                    {
                        f = bt.GetField("_camp", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (f != null) break;
                        bt = bt.BaseType;
                    }
                    _campFieldCache[t] = f;
                }
                if (f == null) return null;
                var v = f.GetValue(n);
                if (v == null) return "";
                var s = v.ToString().ToLowerInvariant();
                if (s.Contains("zombie")) return "Zombie";
                if (s.Contains("plant")) return "Plant";
                return s;
            }
            catch { return null; }
        }

        /// <summary>透视罐子：像被路灯照亮——罐子正面壳变透明，直接露出里面的内容物卡牌。
        /// 由 PhysicsFrame 信号（物理帧末，晚于罐子自身 BatchUpdate）每帧强制执行，防止被 BatchUpdate 改回。</summary>
        static void RevealVase(Node2D n)
        {
            try
            {
                var t = n.GetType();
                // 1) 正面壳变透明：调用私有 SetFrontShellRevealed(true)（沿基类链找）
                var m = FindMethod(t, "SetFrontShellRevealed");
                if (m != null) { try { m.Invoke(n, new object[] { true }); } catch { } }
                // 2) 内容物卡牌显示：_packetShow 可见 + 不透明
                var pf = FindField(t, "_packetShow");
                if (pf != null)
                {
                    var ps = pf.GetValue(n) as CanvasItem;
                    if (ps != null && GodotObject.IsInstanceValid(ps))
                    {
                        ps.Visible = true;
                        ps.Modulate = new Color(ps.Modulate.R, ps.Modulate.G, ps.Modulate.B, 1f);
                    }
                }
            }
            catch { }
        }

        static bool _vaseHooked;
        private static Node _hookRoot;
        private static int _vaseCount;

        /// <summary>连接一次 PhysicsFrame 信号：此后每个物理帧末尾强制所有罐子"路灯照亮"。
        /// 必须在罐子自身 BatchUpdate 之后执行，否则会被覆盖。</summary>
        static void HookVaseFrame(Node root)
        {
            try
            {
                if (_vaseHooked) return;
                _vaseHooked = true;
                _hookRoot = root;
                var tree = root.GetTree();
                if (tree != null) tree.PhysicsFrame += OnVaseFrame;
            }
            catch { }
        }

        static void OnVaseFrame()
        {
            try
            {
                var root = _hookRoot;
                if (root == null || !GodotObject.IsInstanceValid(root)) return;
                var tree = root.GetTree();
                if (tree == null || tree.Root == null) return;
                WalkReveal(tree.Root);
            }
            catch { }
        }

        static void WalkReveal(Node node)
        {
            foreach (var child in node.GetChildren(true))
            {
                if (child is Node2D n2 && GodotObject.IsInstanceValid(n2))
                {
                    var tn = n2.GetType().Name;
                    if (tn.Contains("Vase")) RevealVase(n2);
                }
                WalkReveal(child);
            }
        }

        static System.Reflection.FieldInfo FindField(System.Type t, string name)
        {
            var bt = t;
            while (bt != null)
            {
                var f = bt.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null) return f;
                bt = bt.BaseType;
            }
            return null;
        }

        static System.Reflection.MethodInfo FindMethod(System.Type t, string name)
        {
            var bt = t;
            while (bt != null)
            {
                var m = bt.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (m != null) return m;
                bt = bt.BaseType;
            }
            return null;
        }

        /// <summary>反射读罐子内容名（保留备用）：找 PacketConfig 字段的 saveKey。</summary>
        static string ReadVaseContent(Node2D n)
        {
            try
            {
                var t = n.GetType();
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (f.FieldType.Name.Contains("PacketConfig"))
                    {
                        var cfg = f.GetValue(n);
                        if (cfg != null)
                        {
                            var sk = cfg.GetType().GetField("saveKey", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            if (sk != null)
                            {
                                var v = sk.GetValue(cfg);
                                if (v != null) return v.ToString();
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        static void DrawBox(Node2D n, bool zombie)
        {
            try
            {
                var pos = n.GlobalPosition;
                float w = zombie ? 46 : 42;
                float h = zombie ? 72 : 58;
                var color = zombie ? new Color(1f, 0.2f, 0.2f) : new Color(0.2f, 1f, 0.3f);

                // 线框（不填充）
                var line = new Line2D();
                line.Points = new Vector2[]
                {
                    new Vector2(-w / 2, -h / 2), new Vector2(w / 2, -h / 2),
                    new Vector2(w / 2, h / 2), new Vector2(-w / 2, h / 2), new Vector2(-w / 2, -h / 2)
                };
                line.Width = 2f;
                line.DefaultColor = color;
                line.Position = pos;
                _layer.AddChild(line);
                _drawn.Add(line);

                // 从 HUD 连一根线到角色
                if (_hud != null && GodotObject.IsInstanceValid(_hud))
                {
                    var link = new Line2D();
                    var hudPos = new Vector2(_hud.Position.X + 40, _hud.Position.Y + 14);
                    link.Points = new Vector2[] { hudPos, pos };
                    link.Width = 1f;
                    link.DefaultColor = new Color(color.R, color.G, color.B, 0.5f);
                    _layer.AddChild(link);
                    _drawn.Add(link);
                }
            }
            catch { }
        }

        static void Destroy()
        {
            foreach (var d in _drawn) if (GodotObject.IsInstanceValid(d)) d.QueueFree();
            _drawn.Clear();
            if (_hud != null && GodotObject.IsInstanceValid(_hud)) _hud.QueueFree();
            _hud = null;
            if (_layer != null && GodotObject.IsInstanceValid(_layer)) _layer.QueueFree();
            _layer = null;
        }
    }
}
