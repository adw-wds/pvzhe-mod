using System;
using System.Collections.Generic;
using Godot;

namespace PvzheMod
{
    /// <summary>
    /// 游戏内自定义项目面板（子项目8 Task 1）。
    /// CanvasLayer Layer=150 ProcessMode=Always 挂 SceneTree.Root：列出全部已加载自定义项目
    /// （植物/僵尸/子弹/卡牌），一键种植/刷出，不依赖外置修改器。
    /// 打开：F9（GameCheats.OnFrame 边沿轮询）或外置 /cmd OpenProjectPanel。
    /// AOT 纪律：禁 System.IO/Path/System.Text.Json/PropertyInfo.SetValue；纯 Godot Control 构建，
    /// 数据直接读各 Manager 公开注册表（植物 CustomPlantManager.Dict / 僵尸/子弹/卡牌 LoadedNames 访问器）。
    /// </summary>
    public sealed class CustomProjectPanel : CanvasLayer
    {
        static CustomProjectPanel _inst;   // 唯一实例（懒加载，首次打开创建）
        static readonly Dictionary<string, string> _typeNames = new()
        {
            ["plant"] = "植物",
            ["zombie"] = "僵尸",
            ["bullet"] = "子弹",
            ["card"] = "卡牌",
        };

        PanelContainer _panel;
        VBoxContainer _rootBox;
        HBoxContainer _tabs;
        ScrollContainer _scroll;
        VBoxContainer _list;
        Label _status;
        string _curType = "plant";
        readonly Dictionary<string, Button> _tabBtns = new();

        /// <summary>显示/隐藏切换（懒加载：首次构建并挂 root）。外部入口（RemoteServer/GameCheats）。</summary>
        public static void Toggle()
        {
            try
            {
                if (_inst == null || !GodotObject.IsInstanceValid(_inst))
                {
                    _inst = Create();
                    if (_inst == null) return;
                }
                _inst.ToggleVisible();
            }
            catch (Exception ex) { Bootstrap.Log("自定义项目面板 Toggle 异常: " + ex.Message); }
        }

        /// <summary>创建实例：挂 SceneTree.Root + 构建 UI。失败返回 null（已记录日志）。</summary>
        static CustomProjectPanel Create()
        {
            try
            {
                var tree = Engine.GetMainLoop() as SceneTree;
                var root = tree != null ? tree.Root : null;
                if (root == null) { Bootstrap.Log("自定义项目面板: 无根节点，无法挂载"); return null; }
                var p = new CustomProjectPanel();
                p.Name = "CustomProjectPanel";
                p.Layer = 150;
                p.ProcessMode = Node.ProcessModeEnum.Always;
                root.AddChild(p);
                try { p._Build(); }
                catch (Exception ex) { Bootstrap.Log("自定义项目面板 构建异常: " + ex.Message); p.QueueFree(); return null; }
                Bootstrap.Log("自定义项目面板: 已挂载 root layer=150");
                return p;
            }
            catch (Exception ex) { Bootstrap.Log("自定义项目面板 创建异常: " + ex.Message); return null; }
        }

        void ToggleVisible()
        {
            if (_panel == null || !GodotObject.IsInstanceValid(_panel)) return;
            _panel.Visible = !_panel.Visible;
            if (_panel.Visible)
            {
                _status.Text = "";
                RefreshList();
            }
            Bootstrap.Log("自定义项目面板: " + (_panel.Visible ? "打开" : "关闭"));
        }

        // ==================== UI 构建 ====================

        void _Build()
        {
            _panel = new PanelContainer();
            _panel.Name = "Panel";
            var sb = new StyleBoxFlat();
            sb.BgColor = new Color(0.07f, 0.09f, 0.12f, 0.94f);          // 深色半透明底
            sb.SetBorderWidthAll(1);
            sb.BorderColor = new Color(0.35f, 0.55f, 0.85f, 0.9f);
            sb.SetCornerRadiusAll(8);
            sb.ShadowColor = new Color(0f, 0f, 0f, 0.5f);
            sb.ShadowSize = 12;
            sb.SetContentMarginAll(10);
            _panel.AddThemeStyleboxOverride("panel", sb);
            // 居中固定尺寸（CanvasLayer 无父布局，用锚点+偏移）
            _panel.AnchorLeft = _panel.AnchorRight = 0.5f;
            _panel.AnchorTop = _panel.AnchorBottom = 0.5f;
            _panel.OffsetLeft = -200f; _panel.OffsetTop = -250f;
            _panel.OffsetRight = 200f; _panel.OffsetBottom = 250f;
            _panel.Visible = false;
            AddChild(_panel);

            _rootBox = new VBoxContainer();
            _rootBox.AddThemeConstantOverride("separation", 8);
            _panel.AddChild(_rootBox);

            // 标题行：标题 + 关闭
            var header = new HBoxContainer();
            header.AddThemeConstantOverride("separation", 8);
            _rootBox.AddChild(header);
            var title = new Label();
            title.Text = "自定义项目";
            title.AddThemeFontSizeOverride("font_size", 20);
            title.AddThemeColorOverride("font_color", new Color(1f, 0.95f, 0.8f));
            title.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.6f));
            title.AddThemeConstantOverride("shadow_offset_x", 1);
            title.AddThemeConstantOverride("shadow_offset_y", 1);
            title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            header.AddChild(title);
            var close = new Button();
            close.Text = "关闭";
            close.AddThemeFontSizeOverride("font_size", 13);
            close.CustomMinimumSize = new Vector2(56, 30);
            close.Pressed += () => ToggleVisible();
            header.AddChild(close);

            // tab 行：植物/僵尸/子弹/卡牌
            _tabs = new HBoxContainer();
            _tabs.AddThemeConstantOverride("separation", 6);
            _rootBox.AddChild(_tabs);
            foreach (var kv in _typeNames)
            {
                string type = kv.Key;
                var tb = new Button();
                tb.Text = kv.Value;
                tb.AddThemeFontSizeOverride("font_size", 13);
                tb.CustomMinimumSize = new Vector2(70, 30);
                tb.Pressed += () => SwitchTab(type);
                _tabs.AddChild(tb);
                _tabBtns[type] = tb;
            }

            // 列表滚动区
            _scroll = new ScrollContainer();
            _scroll.CustomMinimumSize = new Vector2(360, 360);
            _scroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
            _rootBox.AddChild(_scroll);
            _list = new VBoxContainer();
            _list.AddThemeConstantOverride("separation", 4);
            _list.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            _scroll.AddChild(_list);

            // 状态文字
            _status = new Label();
            _status.Text = "";
            _status.AddThemeFontSizeOverride("font_size", 13);
            _status.AddThemeColorOverride("font_color", new Color(0.8f, 0.95f, 0.8f));
            _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _rootBox.AddChild(_status);

            RefreshTabHighlight();
        }

        // ==================== 数据 / 列表 ====================

        void SwitchTab(string type)
        {
            _curType = type;
            RefreshTabHighlight();
            RefreshList();
        }

        void RefreshTabHighlight()
        {
            foreach (var kv in _tabBtns)
            {
                bool active = kv.Key == _curType;
                kv.Value.AddThemeColorOverride("font_color", active ? new Color(1f, 0.85f, 0.3f) : new Color(1f, 1f, 1f));
            }
        }

        /// <summary>按当前 tab 从对应 Manager 注册表读取项目名列表并重建列表 UI。</summary>
        void RefreshList()
        {
            try
            {
                foreach (Node c in _list.GetChildren()) c.QueueFree();
                var names = GetLoadedNames(_curType);
                if (names.Count == 0)
                {
                    _status.Text = "暂无已加载" + TypeName(_curType) + "项目（用外置修改器加载后自动刷新）";
                    return;
                }
                foreach (var name in names) AddRow(name);
                _status.Text = "共 " + names.Count + " 个" + TypeName(_curType) + "项目";
            }
            catch (Exception ex) { Bootstrap.Log("自定义项目面板 刷新列表异常: " + ex.Message); }
        }

        static string TypeName(string type) => _typeNames.TryGetValue(type, out var n) ? n : type;

        /// <summary>从各 Manager 注册表取已加载项目名（植物走公开 Dict；僵尸/子弹/卡牌走 LoadedNames 访问器）。</summary>
        static List<string> GetLoadedNames(string type)
        {
            switch (type)
            {
                case "plant":
                    var plants = new List<string>();
                    foreach (var k in CustomPlantManager.Dict.Keys) plants.Add(k);
                    return plants;
                case "zombie":
                    return CustomZombieManager.LoadedNames();
                case "bullet":
                    return CustomBulletManager.LoadedNames();
                case "card":
                    return CustomCardManager.LoadedNames();
                default:
                    return new List<string>();
            }
        }

        void AddRow(string name)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);
            row.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            var lb = new Label();
            lb.Text = name;
            lb.AddThemeFontSizeOverride("font_size", 14);
            lb.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(lb);
            var btn = new Button();
            btn.CustomMinimumSize = new Vector2(72, 30);
            btn.AddThemeFontSizeOverride("font_size", 13);
            string type = _curType;
            if (type == "plant") { btn.Text = "种植"; btn.Pressed += () => SpawnPlant(name); }
            else if (type == "zombie") { btn.Text = "刷出"; btn.Pressed += () => SpawnZombie(name); }
            else { btn.Text = "信息"; btn.Pressed += () => ShowInfo(type, name); }
            row.AddChild(btn);
            _list.AddChild(row);
        }

        // ==================== 动作 ====================

        void SpawnPlant(string name)
        {
            var gp = RandomGridPos();
            bool ok = CustomPlantManager.Spawn(name, gp);
            _status.Text = (ok ? "已种植 " : "种植失败 ") + name + "  → 格(" + gp.X + "," + gp.Y + ")";
        }

        void SpawnZombie(string name)
        {
            var gp = RandomGridPos();
            bool ok = CustomZombieManager.Spawn(name, gp);
            _status.Text = (ok ? "已刷出 " : "刷出失败 ") + name + "  → 格(" + gp.X + "," + gp.Y + ")";
        }

        void ShowInfo(string type, string name)
        {
            switch (type)
            {
                case "bullet":
                    _status.Text = "子弹 " + name + "：已注册（伤害/速度由 config 决定），命中即生效，不可直接放置";
                    break;
                case "card":
                    _status.Text = "卡牌 " + name + "：卡面定义（显示名/费用），种植走其引用植物/僵尸";
                    break;
                default:
                    _status.Text = name;
                    break;
            }
        }

        /// <summary>随机可种植格：行 = 地图实际行数内随机（GameCheats.GetMapRowCount 探测，兜底 5），列 = 0..8 随机。</summary>
        static Vector2I RandomGridPos()
        {
            int rows = GameCheats.GetMapRowCount();
            int row = GD.RandRange(0, Math.Max(0, rows - 1));
            int col = GD.RandRange(0, 8);
            return new Vector2I(col, row);
        }
    }
}
