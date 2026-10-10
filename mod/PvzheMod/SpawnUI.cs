using System.Collections.Generic;
using Godot;

namespace PvzheMod
{
    /// <summary>
    /// 刷怪 / 刷卡选择栏：枚举所有植物/僵尸 ID（GetPacketIds），
    /// 点击条目按当前模式操作：0=加入物品栏 1=刷出卡牌 2=刷出实物。
    /// 用 ItemList 避免信号闭包（便于合并进主程序集）。
    ///
    /// ★★ 2026-09-24 改版：
    ///   ① 格子不再写死"列 2-7 / 行 1-5"随机 —— 面板上可填**行/列**，
    ///      留空才随机；面板上实时显示本关真实行列（GetMapColCount/GetMapRowCount）。
    ///   ② 新增四个批量按钮：刷格 / 刷行 / 刷列 / 刷全屏，**都以填的行列为基准**。
    ///      原先只有"全场填充"，而且它硬编码 1-8 列（本作是 9 列，最后一列永远刷不到）。
    ///   ③ 修好反水失效：全场填充原来**没把魅惑参数传下去**，
    ///      于是"开了反水但刷出来的东西不魅惑"。现在四条路径统一传。
    ///   ④ 面板整体套 ScrollContainer：把窗口拉小/加东西之后，
    ///      底部按钮不会再被挤出可视区（原来只能手动把窗口拖大）。
    /// </summary>
    public static class SpawnUI
    {
        private static CanvasLayer _layer;
        private static PanelContainer _panel;
        private static int _mode;          // 0=加物品栏 1=刷卡牌 2=刷实物
        private static bool _showPlant = true;
        private static ItemList _list;
        private static Label _status;
        private static Label _gridInfo;
        private static SpinBox _rowBox;
        private static SpinBox _colBox;
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
                _panel.Position = new Vector2(80, 30);
                _panel.Size = new Vector2(500, 620);
                _layer.AddChild(_panel);

                // ★ 外层再套一层滚动容器：窗口矮的时候让内容滚，而不是把底部按钮裁掉。
                var outer = new ScrollContainer();
                outer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                outer.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
                _panel.AddChild(outer);

                var vb = new VBoxContainer();
                vb.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                vb.AddThemeConstantOverride("separation", 6);
                outer.AddChild(vb);

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

                // 植物/僵尸切换 + 本关行列
                var catRow = new HBoxContainer();
                catRow.AddThemeConstantOverride("separation", 4);
                vb.AddChild(catRow);
                var pBtn = new Button(); pBtn.Text = "植物"; pBtn.Pressed += OnPlant; catRow.AddChild(pBtn);
                var zBtn = new Button(); zBtn.Text = "僵尸"; zBtn.Pressed += OnZombie; catRow.AddChild(zBtn);
                _status = new Label();
                _status.AddThemeFontSizeOverride("font_size", 13);
                catRow.AddChild(_status);
                _gridInfo = new Label();
                _gridInfo.AddThemeFontSizeOverride("font_size", 13);
                catRow.AddChild(_gridInfo);

                // 魅惑阵营开关
                var charmRow = new HBoxContainer();
                charmRow.AddThemeConstantOverride("separation", 4);
                vb.AddChild(charmRow);
                var charmChk = new CheckButton();
                charmChk.Text = "刷出魅惑阵营（反水）";
                charmChk.ButtonPressed = _charmSpawn;
                charmChk.Toggled += OnCharmSpawnToggled;
                charmRow.AddChild(charmChk);

                // ★ 目标格子：留空 = 随机。用 SpinBox 是因为它自带数字约束，
                //   用户在游戏里多半是鼠标操作，拖一下箭头比敲键盘方便。
                var posRow = new HBoxContainer();
                posRow.AddThemeConstantOverride("separation", 6);
                vb.AddChild(posRow);
                var posLbl = new Label(); posLbl.Text = "目标格子（0=随机）"; posRow.AddChild(posLbl);
                var rowLbl = new Label(); rowLbl.Text = "行"; posRow.AddChild(rowLbl);
                _rowBox = new SpinBox();
                _rowBox.MinValue = 0; _rowBox.MaxValue = 60; _rowBox.Value = 0;
                _rowBox.CustomArrowStep = 1;
                posRow.AddChild(_rowBox);
                var colLbl = new Label(); colLbl.Text = "列"; posRow.AddChild(colLbl);
                _colBox = new SpinBox();
                _colBox.MinValue = 0; _colBox.MaxValue = 60; _colBox.Value = 0;
                _colBox.CustomArrowStep = 1;
                posRow.AddChild(_colBox);

                // 批量刷：全部以填的行列为基准
                var bulkRow = new HBoxContainer();
                bulkRow.AddThemeConstantOverride("separation", 4);
                vb.AddChild(bulkRow);
                var bCell = new Button(); bCell.Text = "刷格"; bCell.Pressed += OnSpawnCell; bulkRow.AddChild(bCell);
                var bRow = new Button(); bRow.Text = "刷行"; bRow.Pressed += OnSpawnRow; bulkRow.AddChild(bRow);
                var bCol = new Button(); bCol.Text = "刷列"; bCol.Pressed += OnSpawnColumn; bulkRow.AddChild(bCol);
                var bAll = new Button(); bAll.Text = "刷全屏"; bAll.Pressed += OnSpawnField; bulkRow.AddChild(bAll);

                // 列表（ItemList：ItemSelected 带索引，无需闭包）
                var scroll = new ScrollContainer();
                scroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
                scroll.CustomMinimumSize = new Vector2(0, 240);
                vb.AddChild(scroll);
                _list = new ItemList();
                _list.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                _list.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
                _list.ItemSelected += OnItemSelected;
                scroll.AddChild(_list);

                var hint = new Label();
                hint.Text = "点列表条目 = 用上方填的行列（0 则随机）刷一次；批量按钮同理，"
                          + "刷行=整行、刷列=整列、刷全屏=本关全部格子。";
                hint.AddThemeFontSizeOverride("font_size", 12);
                hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                vb.AddChild(hint);

                Refresh();
                Bootstrap.Log("SpawnUI: 选择栏已打开，本关 " + GameCheats.GetMapColCount()
                              + " 列 x " + GameCheats.GetMapRowCount() + " 行");
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
                _gridInfo = null;
                _rowBox = null;
                _colBox = null;
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
                    if (_ids.Count == 0)
                    {
                        _list.AddItem("（请进入关卡后使用：TowerDefenseManager 实例只在关卡内有）");
                    }
                    for (int i = 0; i < _ids.Count; i++)
                    {
                        string id = _ids[i];
                        _list.AddItem(GameCheats.GetPacketDisplayName(id));   // 显示图鉴中文名
                        _list.SetItemMetadata(i, id);                          // metadata 存真实 ID
                    }
                }
                if (_status != null && GodotObject.IsInstanceValid(_status))
                    _status.Text = (_showPlant ? "植物" : "僵尸") + "  " + _ids.Count + " 个";
                if (_gridInfo != null && GodotObject.IsInstanceValid(_gridInfo))
                    _gridInfo.Text = "   本关 " + GameCheats.GetMapColCount() + " 列 x "
                                   + GameCheats.GetMapRowCount() + " 行";
                Bootstrap.Log("SpawnUI: " + (_showPlant ? "植物" : "僵尸") + " 列表 " + _ids.Count + " 个");
            }
            catch (System.Exception ex) { Bootstrap.Log("SpawnUI 刷新异常: " + ex.Message); }
        }

        /// <summary>读面板上填的行列。0 表示"随机"，返回是否真的要随机。</summary>
        static void ReadTarget(out int row, out int col, out bool random)
        {
            row = _rowBox != null && GodotObject.IsInstanceValid(_rowBox) ? (int)_rowBox.Value : 0;
            col = _colBox != null && GodotObject.IsInstanceValid(_colBox) ? (int)_colBox.Value : 0;
            random = row <= 0 || col <= 0;
            if (row <= 0) row = GD.RandRange(1, System.Math.Max(1, GameCheats.GetMapRowCount()));
            if (col <= 0) col = GD.RandRange(1, System.Math.Max(1, GameCheats.GetMapColCount()));
        }

        static string SelectedId()
        {
            if (_list == null || !GodotObject.IsInstanceValid(_list)) return null;
            var sel = _list.GetSelectedItems();
            if (sel.Length == 0) return null;
            return _list.GetItemMetadata(sel[0]).AsString();
        }

        /// <summary>点条目 = 按当前模式在目标格子操作一次。</summary>
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

                ReadTarget(out int row, out int col, out bool random);
                var grid = new Vector2I(col, row);
                bool ok;
                if (_mode == 0) ok = GameCheats.AddPacketToInventory(id, _charmSpawn);
                else if (_mode == 1) ok = GameCheats.SpawnPacketToScene(id, grid, _charmSpawn);
                else ok = GameCheats.SpawnCharacter(id, grid, _charmSpawn);
                // ★ 日志把每次分支都写全：刷实物不生效时，靠"成功=False"和异常信息
                //   就能区分是参数没接上还是游戏侧拒绝，不用盲猜。
                Bootstrap.Log("SpawnUI: " + id + " 模式=" + _mode + " 行列=" + row + "," + col
                              + " 行随机=" + random + " 魅惑=" + _charmSpawn + " 成功=" + ok);
            }
            catch (System.Exception ex) { Bootstrap.Log("SpawnUI 操作异常: " + ex.Message); }
        }

        static void OnMode0() { _mode = 0; Bootstrap.Log("SpawnUI: 模式=加物品栏"); }
        static void OnMode1() { _mode = 1; Bootstrap.Log("SpawnUI: 模式=刷卡牌"); }
        static void OnMode2() { _mode = 2; Bootstrap.Log("SpawnUI: 模式=刷实物"); }
        static void OnCharmSpawnToggled(bool on) { _charmSpawn = on; Bootstrap.Log("SpawnUI: 魅惑刷出=" + on); }

        // ── 批量刷（一律走 GameCheats.SpawnRegion，它内部用本关真实行列） ──

        static void OnSpawnCell() => Bulk("单个格子", GameCheats.SpawnScope.Cell);
        static void OnSpawnRow() => Bulk("整行", GameCheats.SpawnScope.Row);
        static void OnSpawnColumn() => Bulk("整列", GameCheats.SpawnScope.Column);
        static void OnSpawnField() => Bulk("全屏", GameCheats.SpawnScope.Field);

        static void Bulk(string what, GameCheats.SpawnScope scope)
        {
            try
            {
                string id = SelectedId();
                if (string.IsNullOrEmpty(id)) { Bootstrap.Log("SpawnUI: 请先在列表选卡"); return; }
                ReadTarget(out int row, out int col, out _);
                int n = GameCheats.SpawnRegion(id, !_showPlant, _charmSpawn, row, col, scope);
                Bootstrap.Log("SpawnUI: " + what + " " + id + " x" + n
                              + " 魅力=" + _charmSpawn + "（本关 "
                              + GameCheats.GetMapColCount() + "x" + GameCheats.GetMapRowCount() + "）");
            }
            catch (System.Exception ex) { Bootstrap.Log("SpawnUI 批量异常: " + ex.Message); }
        }

        static void OnPlant() { _showPlant = true; Refresh(); }
        static void OnZombie() { _showPlant = false; Refresh(); }
        static void OnClose() { Close(); }
    }
}
