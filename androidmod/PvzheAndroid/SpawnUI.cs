using System.Collections.Generic;
using Godot;

namespace PvzheMod
{
    /// <summary>
    /// 刷怪 / 刷卡选择栏：枚举所有植物/僵尸 ID（GetPlantList/GetZombieList），
    /// 点击条目按当前模式操作：0=加入物品栏 1=刷出卡牌 2=刷出实物。
    /// 用 ItemList 避免信号闭包（便于合并进主程序集）。
    /// </summary>
    public static class SpawnUI
    {
        private static CanvasLayer _layer;
        private static PanelContainer _panel;
        private static int _mode;          // 0=加物品栏 1=刷卡牌 2=刷实物
        private static bool _showPlant = true;
        private static ItemList _list;
        private static Label _status;
        private static List<string> _ids = new List<string>();
        private static bool _charmSpawn;   // 刷出魅惑阵营（反转 camp）

        public static void Toggle(Node root)
        {
            if (_panel != null && GodotObject.IsInstanceValid(_panel)) { Close(); return; }
            Open(root);
        }

        static void Open(Node root)
        {
            try
            {
                var tree = root.GetTree();
                if (tree == null || tree.Root == null) return;
                _layer = new CanvasLayer();
                _layer.Name = "PvzSpawnUI";
                _layer.Layer = 110;
                _layer.ProcessMode = Godot.Node.ProcessModeEnum.Always;
                tree.Root.AddChild(_layer);

                _panel = new PanelContainer();
                _panel.Position = new Vector2(80, 40);
                _panel.Size = new Vector2(480, 580);
                _layer.AddChild(_panel);

                var vb = new VBoxContainer();
                vb.AddThemeConstantOverride("separation", 6);
                _panel.AddChild(vb);

                var title = new Label();
                title.Text = "刷怪 / 刷卡选择栏";
                title.AddThemeFontSizeOverride("font_size", 20);
                vb.AddChild(title);

                // 模式行
                var modeRow = new HBoxContainer();
                modeRow.AddThemeConstantOverride("separation", 4);
                vb.AddChild(modeRow);
                var m0 = new Button(); m0.Text = "加物品栏"; m0.Pressed += OnMode0; modeRow.AddChild(m0);
                var m1 = new Button(); m1.Text = "刷卡牌"; m1.Pressed += OnMode1; modeRow.AddChild(m1);
                var m2 = new Button(); m2.Text = "刷实物"; m2.Pressed += OnMode2; modeRow.AddChild(m2);
                var close = new Button(); close.Text = "关闭"; close.Pressed += OnClose; modeRow.AddChild(close);

                // 植物/僵尸切换
                var catRow = new HBoxContainer();
                catRow.AddThemeConstantOverride("separation", 4);
                vb.AddChild(catRow);
                var pBtn = new Button(); pBtn.Text = "植物"; pBtn.Pressed += OnPlant; catRow.AddChild(pBtn);
                var zBtn = new Button(); zBtn.Text = "僵尸"; zBtn.Pressed += OnZombie; catRow.AddChild(zBtn);
                _status = new Label();
                _status.AddThemeFontSizeOverride("font_size", 13);
                catRow.AddChild(_status);

                // 魅惑阵营开关
                var charmRow = new HBoxContainer();
                charmRow.AddThemeConstantOverride("separation", 4);
                vb.AddChild(charmRow);
                var charmChk = new CheckButton();
                charmChk.Text = "刷出魅惑阵营（反水）";
                charmChk.ButtonPressed = _charmSpawn;
                charmChk.Toggled += OnCharmSpawnToggled;
                charmRow.AddChild(charmChk);

                // 全场填充（自选卡刷满全场）
                var fillRow = new HBoxContainer();
                fillRow.AddThemeConstantOverride("separation", 4);
                vb.AddChild(fillRow);
                var fillBtn = new Button();
                fillBtn.Text = "全场填充当前选中卡";
                fillBtn.Pressed += OnFillField;
                fillRow.AddChild(fillBtn);

                // 列表（ItemList：ItemSelected 带索引，无需闭包）
                var scroll = new ScrollContainer();
                scroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
                vb.AddChild(scroll);
                _list = new ItemList();
                _list.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                _list.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
                _list.ItemSelected += OnItemSelected;
                scroll.AddChild(_list);

                var hint = new Label();
                hint.Text = "模式：加物品栏=直接进背包；刷卡牌/刷实物=刷到第2行随机列格子";
                hint.AddThemeFontSizeOverride("font_size", 12);
                hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                vb.AddChild(hint);

                Refresh();
                Bootstrap.Log("SpawnUI: 选择栏已打开");
            }
            catch (System.Exception ex) { Bootstrap.Log("SpawnUI 异常: " + ex.Message); }
        }

        static void Close()
        {
            try
            {
                if (_panel != null && GodotObject.IsInstanceValid(_panel)) _panel.QueueFree();
                _panel = null;
                _list = null;
                _status = null;
                if (_layer != null && GodotObject.IsInstanceValid(_layer)) _layer.QueueFree();
                _layer = null;
            }
            catch { }
        }

        static void Refresh()
        {
            try
            {
                _ids = GameCheats.GetPacketIds(_showPlant);
                if (_list != null && GodotObject.IsInstanceValid(_list))
                {
                    _list.Clear();
                    for (int i = 0; i < _ids.Count; i++)
                    {
                        string id = _ids[i];
                        _list.AddItem(GameCheats.GetPacketDisplayName(id));   // 显示图鉴中文名
                        _list.SetItemMetadata(i, id);                          // metadata 存真实 ID
                    }
                }
                if (_status != null && GodotObject.IsInstanceValid(_status))
                    _status.Text = (_showPlant ? "植物" : "僵尸") + "  " + _ids.Count + " 个";
                Bootstrap.Log("SpawnUI: " + (_showPlant ? "植物" : "僵尸") + " 列表 " + _ids.Count + " 个");
            }
            catch (System.Exception ex) { Bootstrap.Log("SpawnUI 刷新异常: " + ex.Message); }
        }

        static void OnItemSelected(long index)
        {
            try
            {
                if (index < 0 || index >= _ids.Count) return;
                string id;
                if (_list != null && GodotObject.IsInstanceValid(_list))
                    id = _list.GetItemMetadata((int)index).AsString();
                else
                    id = _ids[(int)index];
                if (string.IsNullOrEmpty(id)) return;
                // 行索引从 1 开始（GetMapGridPos 返回 +1），有效行 1..5——之前用 0..4 会往上错一格
                var grid = new Vector2I(GD.RandRange(2, 7), GD.RandRange(1, 5));
                bool ok;
                if (_mode == 0) ok = GameCheats.AddPacketToInventory(id, _charmSpawn);
                else if (_mode == 1) ok = GameCheats.SpawnPacketToScene(id, grid, _charmSpawn);
                else ok = GameCheats.SpawnCharacter(id, grid, _charmSpawn);
                Bootstrap.Log("SpawnUI: " + id + " 模式=" + _mode + " 魅惑=" + _charmSpawn + " 成功=" + ok);
            }
            catch (System.Exception ex) { Bootstrap.Log("SpawnUI 操作异常: " + ex.Message); }
        }

        static void OnMode0() { _mode = 0; Bootstrap.Log("SpawnUI: 模式=加物品栏"); }
        static void OnMode1() { _mode = 1; Bootstrap.Log("SpawnUI: 模式=刷卡牌"); }
        static void OnMode2() { _mode = 2; Bootstrap.Log("SpawnUI: 模式=刷实物"); }
        static void OnCharmSpawnToggled(bool on) { _charmSpawn = on; Bootstrap.Log("SpawnUI: 魅惑刷出=" + on); }
        static void OnFillField()
        {
            try
            {
                if (_list == null || !GodotObject.IsInstanceValid(_list)) return;
                var sel = _list.GetSelectedItems();
                if (sel.Length == 0) { Bootstrap.Log("SpawnUI: 请先在列表选卡"); return; }
                string id = _list.GetItemMetadata(sel[0]).AsString();
                if (string.IsNullOrEmpty(id)) return;
                int n = GameCheats.SpawnAllFieldWith(id, !_showPlant);
                Bootstrap.Log("SpawnUI: 全场填充 " + id + " x" + n);
            }
            catch (System.Exception ex) { Bootstrap.Log("SpawnUI 全场填充异常: " + ex.Message); }
        }
        static void OnPlant() { _showPlant = true; Refresh(); }
        static void OnZombie() { _showPlant = false; Refresh(); }
        static void OnClose() { Close(); }
    }
}
