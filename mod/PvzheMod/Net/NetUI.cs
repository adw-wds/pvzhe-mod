using System;
using System.Collections.Generic;
using Godot;
using PvzheMod;

/// <summary>
/// 联机面板（游戏内）。
/// 手机：由 ModUI 的「联机面板」按钮调用 Toggle()；电脑：游戏内面板已下线，改用外置修改器的「联机」窗口。
///
/// ★ 2026-09-12 重做：旧版是「固定位置 (12,40) 的 384×460 滚动条」，没有标题栏/关闭按钮，
///   状态、服务器、建房、玩家、15 项作弊、踢人、聊天全挤在一条竖列里 —— 手机上既拖不动也关不掉。
///   新版对齐电脑版结构：**可拖动标题栏 + ✕ 关闭 + 四个页签（大厅/创建/加入/房间）+ 触摸滚动内容区**。
///   服务器只显示区域名（香港1区…），不再暴露裸域名。
/// </summary>
public static class NetUI
{
    // ---- 面板尺寸/定位 ----
    const float PanelW = 560f;
    const float PanelH = 620f;
    const float MinPanelW = 360f;
    const float MinPanelH = 360f;

    static CanvasLayer _layer;
    static PanelContainer _panel;
    static bool _visible;
    static bool _built;
    static ulong _lastRefreshMs;

    // ---- 拖动 ----
    static bool _dragging;

    // ---- 页签 ----
    static string _page = "lobby";
    static readonly Dictionary<string, Button> _tabs = new Dictionary<string, Button>();
    static readonly Dictionary<string, ScrollContainer> _pages = new Dictionary<string, ScrollContainer>();
    static bool _autoSwitchedToRoom;

    // ---- 各页控件 ----
    static Label _statusBar, _noticeBar;
    static Label _lobbyHint;
    static VBoxContainer _roomListBox;
    static OptionButton _regionBox;
    static LineEdit _nickEdit;

    static LineEdit _cNameEdit, _cTtlEdit, _cPassEdit;
    static CheckButton _cLate;
    // ---- 对战模式 ----
    static CheckButton _cBattle;      // 建房页：对战模式
    static CheckButton _cBalance;     // 建房页：人数平衡
    static CheckButton _sBattle;      // 房间页房主设置：对战模式
    static CheckButton _sBalance;     // 房间页房主设置：人数平衡
    static Label _factionHint;        // 房间页：分边提示
    static Button _factionPlant;      // 房间页：加入植物方
    static Button _factionZombie;     // 房间页：加入僵尸方
    static OptionButton _cMaxBox;

    static LineEdit _jCodeEdit, _jPassEdit;

    static Label _roomTitle, _roomMeta;
    static VBoxContainer _peerList;
    static LineEdit _sTtlEdit, _sPassEdit, _sKickEdit;
    static CheckButton _sLate;
    static OptionButton _sMaxBox;
    static Button _startBtn, _endBtn, _closeRoomBtn, _leaveBtn;

    static VBoxContainer _chatList;
    static LineEdit _chatInput;
    static readonly List<string> _lines = new List<string>();
    static readonly List<string> _chatSeen = new List<string>();

    // ================= 构建 =================
    public static void Ensure(Node root)
    {
        if (_layer != null && GodotObject.IsInstanceValid(_layer)) return;
        if (root == null) return;

        _layer = new CanvasLayer { Name = "NetUILayer", Layer = 151 };
        root.AddChild(_layer);

        _panel = new PanelContainer { Name = "NetPanel", Visible = false };
        var sb = new StyleBoxFlat();
        sb.BgColor = new Color(0.10f, 0.12f, 0.14f, 0.97f);
        sb.SetBorderWidthAll(2);
        sb.BorderColor = new Color(0.35f, 0.62f, 0.55f, 0.9f);
        sb.SetCornerRadiusAll(12);
        sb.SetContentMarginAll(10);
        _panel.AddThemeStyleboxOverride("panel", sb);
        _layer.AddChild(_panel);
        FitPanel();

        var root3 = new VBoxContainer();
        root3.AddThemeConstantOverride("separation", 8);
        _panel.AddChild(root3);

        // ---- 标题栏（拖动把手 + 关闭）----
        var title = new PanelContainer();
        var tsb = new StyleBoxFlat();
        tsb.BgColor = new Color(0.16f, 0.30f, 0.26f, 0.95f);
        tsb.SetCornerRadiusAll(8);
        tsb.SetContentMarginAll(6);
        title.AddThemeStyleboxOverride("panel", tsb);
        var titleRow = new HBoxContainer();
        title.AddChild(titleRow);
        titleRow.AddChild(new Label
        {
            Text = "联机",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Modulate = new Color(0.75f, 1f, 0.86f)
        });
        titleRow.AddChild(new Label { Text = "（按住标题栏可拖动）", Modulate = new Color(0.65f, 0.72f, 0.78f), });
        var closeBtn = new Button { Text = "✕", CustomMinimumSize = new Vector2(40, 32) };
        closeBtn.Pressed += () => SetVisible(false);
        titleRow.AddChild(closeBtn);
        title.GuiInput += OnTitleGuiInput;
        root3.AddChild(title);

        // ---- 页签 ----
        var tabRow = new HBoxContainer();
        root3.AddChild(tabRow);
        AddTab(tabRow, "lobby", "联机大厅");
        AddTab(tabRow, "create", "创建房间");
        AddTab(tabRow, "join", "加入房间");
        AddTab(tabRow, "room", "我的房间");

        // ---- 内容宿主：每页一个 ScrollContainer，叠放铺满 ----
        var host = new Control
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            ClipContents = true,
            // ★ 这个最小值不能大：手机上视口矮的时候，300 会把底部的状态栏/提示栏挤到面板外面去
            CustomMinimumSize = new Vector2(0, 80)
        };
        root3.AddChild(host);
        BuildLobbyPage(host);
        BuildCreatePage(host);
        BuildJoinPage(host);
        BuildRoomPage(host);

        // ---- 底部状态条 ----
        root3.AddChild(new HSeparator());
        _statusBar = new Label { Text = "状态：Offline", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        root3.AddChild(_statusBar);
        _noticeBar = new Label { Text = "", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = new Color(1f, 0.86f, 0.5f) };
        root3.AddChild(_noticeBar);

        _built = true;
        ShowPage("lobby");
        Refresh();

        NetSession.OnChat += s =>
        {
            _lines.Add(s);
            if (_lines.Count > 60) _lines.RemoveAt(0);
            Refresh();
        };
        NetSession.OnStateChanged += _ => Refresh();
        NetSession.OnSettingsChanged += () => Refresh();
        NetSession.OnNotice += _ => Refresh();
        NetSession.OnRoomList += Refresh;
        NetLog.Info("联机面板已就绪（新版：可拖动/可关闭/四页签）");
    }

    static void AddTab(HBoxContainer row, string key, string text)
    {
        var b = new Button { Text = text, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        b.Pressed += () => ShowPage(key);
        _tabs[key] = b;
        row.AddChild(b);
    }

    static ScrollContainer NewPage(Node host, string key)
    {
        var sc = new ScrollContainer { Name = "page_" + key, Visible = false };
        sc.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        sc.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        sc.GuiInput += ev => OnScrollGuiInput(sc, ev);
        host.AddChild(sc);
        try
        {
            var vsb = sc.GetVScrollBar();
            vsb.CustomMinimumSize = new Vector2(22, 0);
            vsb.AddThemeConstantOverride("grabber_width", 18);
        }
        catch { }
        _pages[key] = sc;
        return sc;
    }

    static VBoxContainer PageBox(ScrollContainer sc)
    {
        var vb = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        vb.AddThemeConstantOverride("separation", 8);
        sc.AddChild(vb);
        return vb;
    }

    static Label Section(string t)
    {
        var l = new Label { Text = t, Modulate = new Color(1f, 0.82f, 0.35f) };
        l.AddThemeFontSizeOverride("font_size", 15);
        return l;
    }

    // ================= 页：联机大厅 =================
    static void BuildLobbyPage(Node host)
    {
        var vb = PageBox(NewPage(host, "lobby"));

        vb.AddChild(Section("服务器"));
        var rowSrv = new HBoxContainer();
        rowSrv.AddChild(new Label { Text = "区域" });
        _regionBox = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        RefreshRegionBox();
        _regionBox.ItemSelected += idx =>
        {
            if (NetSession.SetRegionByIndex((int)idx))
            {
                NetSession.PostNotice("已切换到 " + NetSession.CurrentRegionName());
            }
            Refresh();
        };
        rowSrv.AddChild(_regionBox);
        vb.AddChild(rowSrv);

        var rowNick = new HBoxContainer();
        rowNick.AddChild(new Label { Text = "昵称" });
        _nickEdit = new LineEdit { Text = "玩家", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        rowNick.AddChild(_nickEdit);
        vb.AddChild(rowNick);

        var rowBtn = new HBoxContainer();
        var refreshBtn = new Button { Text = "刷新房间列表", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        refreshBtn.Pressed += () =>
        {
            if (NetSession.EnsureLobby()) NetSession.RequestRoomList();
            else NetSession.PostNotice("连接服务器中，稍后自动刷新…");
            Refresh();
        };
        rowBtn.AddChild(refreshBtn);
        var leaveLobbyBtn = new Button { Text = "断开大厅" };
        leaveLobbyBtn.Pressed += () => { NetSession.CloseLobby(); Refresh(); };
        rowBtn.AddChild(leaveLobbyBtn);
        vb.AddChild(rowBtn);

        _lobbyHint = new Label { Text = "", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = new Color(0.75f, 0.82f, 0.88f) };
        vb.AddChild(_lobbyHint);

        vb.AddChild(Section("房间列表"));
        _roomListBox = new VBoxContainer();
        vb.AddChild(_roomListBox);
    }

    // ================= 页：创建房间 =================
    static void BuildCreatePage(Node host)
    {
        var vb = PageBox(NewPage(host, "create"));

        vb.AddChild(Section("创建房间"));
        var rowName = new HBoxContainer();
        rowName.AddChild(new Label { Text = "房主名" });
        _cNameEdit = new LineEdit { Text = "玩家", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        rowName.AddChild(_cNameEdit);
        vb.AddChild(rowName);

        var rowMax = new HBoxContainer();
        rowMax.AddChild(new Label { Text = "人数上限" });
        _cMaxBox = new OptionButton();
        for (int i = 2; i <= NetConstants.MaxPlayers; i++) _cMaxBox.AddItem(i + " 人");
        _cMaxBox.Selected = 2;   // 默认 4 人
        rowMax.AddChild(_cMaxBox);
        rowMax.AddChild(new Label { Text = "存活(分)" });
        _cTtlEdit = new LineEdit { Text = "120", CustomMinimumSize = new Vector2(70, 0) };
        rowMax.AddChild(_cTtlEdit);
        vb.AddChild(rowMax);

        var rowPass = new HBoxContainer();
        rowPass.AddChild(new Label { Text = "口令" });
        _cPassEdit = new LineEdit { PlaceholderText = "留空=无口令", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        rowPass.AddChild(_cPassEdit);
        vb.AddChild(rowPass);

        // 联机时修改器一律强制关闭（两端必须一致，否则表现不同步），所以建房页不再提供作弊开关；
        // 同理也没有“逐项允许”——那些勾选框和“强制关作弊”相互矛盾，已移除。
        _cLate = new CheckButton { Text = "允许中途加入", ButtonPressed = true };
        vb.AddChild(_cLate);

        // 对战模式：开了之后房间页会出现分边入口，双方各占草坪一半互相对打
        _cBattle = new CheckButton { Text = "对战模式（植物 vs 僵尸）" };
        vb.AddChild(_cBattle);
        _cBalance = new CheckButton { Text = "人数平衡（两边人数差不超过 1）", ButtonPressed = true };
        vb.AddChild(_cBalance);

        var createBtn = new Button { Text = "创建房间", CustomMinimumSize = new Vector2(0, 44) };
        createBtn.Pressed += () =>
        {
            var s = new RoomSettings
            {
                MaxPlayers = (byte)(_cMaxBox.Selected + 2),
                TtlMinutes = ParseByte(_cTtlEdit.Text, RoomSettings.MaxTtlMinutes, 0, RoomSettings.MaxTtlMinutes),
                Password = _cPassEdit.Text ?? "",
                AllowCheats = false,          // 联机强制关作弊（UI 已不提供开关）
                AllowLateJoin = _cLate.ButtonPressed,
                BattleMode = _cBattle.ButtonPressed,
                BalanceTeams = _cBalance.ButtonPressed,
                CheatMask = CheatBits.All
            };
            if (NetSession.CreateRoom(string.IsNullOrEmpty(_cNameEdit.Text) ? "玩家" : _cNameEdit.Text, s))
            {
                NetSession.PostNotice("正在创建房间…");
                ShowPage("room");
            }
            Refresh();
        };
        vb.AddChild(createBtn);
        vb.AddChild(new Label
        {
            Text = "提示：创建后进入「我的房间」，可在那里选关、开始对局、踢人、解散。",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Modulate = new Color(0.7f, 0.76f, 0.82f)
        });
    }

    // ================= 页：加入房间 =================
    static void BuildJoinPage(Node host)
    {
        var vb = PageBox(NewPage(host, "join"));

        vb.AddChild(Section("加入房间"));
        var rowCode = new HBoxContainer();
        rowCode.AddChild(new Label { Text = "邀请码" });
        _jCodeEdit = new LineEdit { PlaceholderText = "4 位数字", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        rowCode.AddChild(_jCodeEdit);
        vb.AddChild(rowCode);

        var rowPass = new HBoxContainer();
        rowPass.AddChild(new Label { Text = "口令" });
        _jPassEdit = new LineEdit { PlaceholderText = "无口令可留空", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        rowPass.AddChild(_jPassEdit);
        vb.AddChild(rowPass);

        rowPass.AddChild(new Label { Text = "昵称" });
        var jNick = new LineEdit { Text = "玩家", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        rowPass.AddChild(jNick);

        var joinBtn = new Button { Text = "加入房间", CustomMinimumSize = new Vector2(0, 44) };
        joinBtn.Pressed += () =>
        {
            string code = (_jCodeEdit.Text ?? "").Trim();
            if (code.Length == 0) { NetSession.PostNotice("请先填邀请码"); Refresh(); return; }
            if (NetSession.JoinRoom(code, string.IsNullOrEmpty(jNick.Text) ? "玩家" : jNick.Text, _jPassEdit.Text ?? ""))
            {
                NetSession.PostNotice("正在加入房间 " + code + " …");
                ShowPage("room");
            }
            Refresh();
        };
        vb.AddChild(joinBtn);
    }

    // ================= 房间内：选关（房主） =================
    // ★ 与电脑版外置修改器一致：选关卡片就放在「我的房间」页里（不单开页签），只有房主能点。
    static string _levelCat = "adv";
    static VBoxContainer _levelListBox;
    static Label _levelCurText;
    static Label _levelCountText;
    static readonly List<Button> _levelBtns = new List<Button>();
    static bool _levelsLoaded;

    static void BuildLevelSection(VBoxContainer vb)
    {
        vb.AddChild(Section("选择关卡（房主）"));
        vb.AddChild(new Label
        {
            Text = "选好后点「开始对局」，房间里其他人会自动进入这个关卡。不选则跟随房主当前所在的关卡。",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Modulate = new Color(0.74f, 0.8f, 0.86f)
        });

        _levelCurText = new Label { Text = "未选关", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = new Color(0.75f, 1f, 0.86f) };
        vb.AddChild(_levelCurText);
        _levelCountText = new Label { Text = "", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = new Color(0.7f, 0.76f, 0.82f) };
        vb.AddChild(_levelCountText);

        // 分类行（两行铺开，手机上不挤）
        var cats = new string[] { "adv", "fun", "challenge", "puzzle", "survival", "daily", "online" };
        var catNames = new string[] { "冒险", "娱乐", "挑战", "拼图", "生存", "每日", "在线" };
        var catRow = new HBoxContainer();
        var catRow2 = new HBoxContainer();
        for (int i = 0; i < cats.Length; i++)
        {
            string key = cats[i];
            var b = new Button { Text = catNames[i], SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 38) };
            b.Pressed += () => { _levelCat = key; RefreshLevelList(); };
            _levelBtns.Add(b);
            (i < 4 ? catRow : catRow2).AddChild(b);
        }
        vb.AddChild(catRow);
        vb.AddChild(catRow2);

        var row = new HBoxContainer();
        var clearBtn = new Button { Text = "清除选择（跟随房主关卡）", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        clearBtn.Pressed += () =>
        {
            NetSession.LevelOverrideKey = "";
            NetSession.LevelOverrideName = "";
            Refresh();
        };
        _levelBtns.Add(clearBtn);
        row.AddChild(clearBtn);
        var reloadBtn = new Button { Text = "刷新列表" };
        reloadBtn.Pressed += () => { _levelsLoaded = true; RefreshLevelList(); };
        _levelBtns.Add(reloadBtn);
        row.AddChild(reloadBtn);
        vb.AddChild(row);

        vb.AddChild(Section("关卡列表"));
        _levelListBox = new VBoxContainer();
        vb.AddChild(_levelListBox);
        vb.AddChild(new Label
        {
            Text = "「每日 / 在线」列的是本机下载过的关卡。房主选了这类关卡时，会自动把关卡文件推给客机（客机不用自己下载）。",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Modulate = new Color(0.68f, 0.74f, 0.8f)
        });
    }

    /// <summary>重建关卡列表（分类切换/刷新时调用）。列表最多渲染 200 条，防手机卡顿。</summary>
    static void RefreshLevelList()
    {
        if (_levelListBox == null || !GodotObject.IsInstanceValid(_levelListBox)) return;
        foreach (var c in _levelListBox.GetChildren()) c.QueueFree();

        System.Collections.Generic.List<string[]> list = null;
        string err = "";
        try { list = GameCheats.ListLevels(_levelCat); } catch (System.Exception ex) { err = ex.Message; }
        if (list == null || list.Count == 0)
        {
            if (_levelCountText != null && GodotObject.IsInstanceValid(_levelCountText))
            {
                bool userCat = _levelCat == "daily" || _levelCat == "online";
                _levelCountText.Text = "这一类没找到关卡" + (err.Length > 0 ? "：" + err : "") +
                    (userCat ? "（每日/在线关卡需要先在游戏内下载过，或由房主选一次自动同步过来）" : "");
            }
            _levelListBox.AddChild(new Label { Text = "（空）", Modulate = new Color(0.7f, 0.76f, 0.82f) });
            return;
        }

        bool editable = NetSession.IsHost;
        int n = list.Count > 200 ? 200 : list.Count;
        for (int i = 0; i < n; i++)
        {
            var it = list[i];
            if (it == null || it.Length < 2) continue;
            string key = it[0];
            string name = it[1];
            bool picked = NetSession.LevelOverrideKey == key;
            var b = new Button
            {
                Text = (i + 1) + ". " + name + (picked ? "   ✔ 已选" : ""),
                Alignment = HorizontalAlignment.Left,
                CustomMinimumSize = new Vector2(0, 38),
                Disabled = !editable
            };
            b.Pressed += () =>
            {
                NetSession.LevelOverrideKey = key;
                NetSession.LevelOverrideName = name;
                Refresh();
                NetSession.PostNotice("已选关卡：" + name);
            };
            _levelListBox.AddChild(b);
        }
        if (list.Count > n)
            _levelListBox.AddChild(new Label { Text = "（还有 " + (list.Count - n) + " 个未显示）", Modulate = new Color(0.7f, 0.76f, 0.82f) });

        if (_levelCountText != null && GodotObject.IsInstanceValid(_levelCountText))
            _levelCountText.Text = "共 " + list.Count + " 关" + (editable ? "" : "（只有房主可以选择）");
    }

    // ================= 页：我的房间 =================
    static void BuildRoomPage(Node host)
    {
        var vb = PageBox(NewPage(host, "room"));

        _roomTitle = new Label { Text = "未在房间", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _roomTitle.AddThemeFontSizeOverride("font_size", 16);
        vb.AddChild(_roomTitle);
        _roomMeta = new Label { Text = "", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = new Color(0.8f, 0.88f, 0.95f) };
        vb.AddChild(_roomMeta);

        var rowBattle = new HBoxContainer();
        _startBtn = new Button { Text = "开始对局", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 42) };
        _startBtn.Pressed += () =>
        {
            string key = NetSession.LevelOverrideKey;
            if (string.IsNullOrEmpty(key)) key = GameCheats.NetGetLevelRef();
            if (NetSession.StartBattle(key))
                NetSession.PostNotice("已开始对局：关卡 " + (string.IsNullOrEmpty(key) ? "?" : key));
            Refresh();
        };
        rowBattle.AddChild(_startBtn);
        _endBtn = new Button { Text = "结束对局", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 42) };
        _endBtn.Pressed += () => { NetSession.EndBattle(); Refresh(); };
        rowBattle.AddChild(_endBtn);
        vb.AddChild(rowBattle);

        // ★ 选关卡片就在房间页里（和电脑版一致）
        BuildLevelSection(vb);

        // ★ 对战模式：分边入口（仅对战模式显示）。
        //   两边人数与按钮可用性在 Refresh() 里按中继下发的名单实时刷新：
        //   人数多的一方按钮置灰，真正仲裁仍在中继（客户端只做提示）。
        vb.AddChild(Section("对战阵营"));
        _factionHint = new Label { Text = "", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = new Color(0.8f, 0.88f, 0.95f) };
        vb.AddChild(_factionHint);
        var rowFac = new HBoxContainer();
        _factionPlant = new Button { Text = "加入植物方", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 40) };
        _factionPlant.Pressed += () => { NetSession.SetFaction(NetFaction.Plant); Refresh(); };
        rowFac.AddChild(_factionPlant);
        _factionZombie = new Button { Text = "加入僵尸方", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 40) };
        _factionZombie.Pressed += () => { NetSession.SetFaction(NetFaction.Zombie); Refresh(); };
        rowFac.AddChild(_factionZombie);
        vb.AddChild(rowFac);

        vb.AddChild(Section("玩家"));
        _peerList = new VBoxContainer();
        vb.AddChild(_peerList);

        vb.AddChild(Section("房主设置"));
        var rowMax = new HBoxContainer();
        rowMax.AddChild(new Label { Text = "人数上限" });
        _sMaxBox = new OptionButton();
        for (int i = 2; i <= NetConstants.MaxPlayers; i++) _sMaxBox.AddItem(i + " 人");
        _sMaxBox.ItemSelected += _ => ApplySettingsFromUI();
        rowMax.AddChild(_sMaxBox);
        rowMax.AddChild(new Label { Text = "存活(分)" });
        _sTtlEdit = new LineEdit { Text = "120", CustomMinimumSize = new Vector2(70, 0) };
        _sTtlEdit.TextSubmitted += _ => ApplySettingsFromUI();
        rowMax.AddChild(_sTtlEdit);
        vb.AddChild(rowMax);

        var rowPass2 = new HBoxContainer();
        rowPass2.AddChild(new Label { Text = "口令" });
        _sPassEdit = new LineEdit { PlaceholderText = "留空=取消口令", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _sPassEdit.TextSubmitted += _ => ApplySettingsFromUI();
        rowPass2.AddChild(_sPassEdit);
        vb.AddChild(rowPass2);

        // ★ 联机一律强制关闭修改器，这里不再提供任何作弊相关选项。
        //   原来有一个禁用的「强制关闭」展示项 + 15 项「逐项允许」勾选框，
        //   后者明明写着强制关却还能勾选，自相矛盾（用户实测后要求删掉）。
        _sLate = new CheckButton { Text = "允许中途加入", ButtonPressed = true };
        _sLate.Toggled += _ => ApplySettingsFromUI();
        vb.AddChild(_sLate);

        // 对战模式：开战后房间页的「对战阵营」分区才可用；开战中途改会与已锁定的阵营冲突，
        // 所以这里不做本地禁用（中继对已开战的房间会拒绝分边），只在提示里说明。
        _sBattle = new CheckButton { Text = "对战模式（植物 vs 僵尸）" };
        _sBattle.Toggled += _ => ApplySettingsFromUI();
        vb.AddChild(_sBattle);
        _sBalance = new CheckButton { Text = "人数平衡（两边人数差不超过 1）", ButtonPressed = true };
        _sBalance.Toggled += _ => ApplySettingsFromUI();
        vb.AddChild(_sBalance);

        var rowKick = new HBoxContainer();
        _sKickEdit = new LineEdit { PlaceholderText = "玩家 id（见上方列表 #编号）", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        rowKick.AddChild(_sKickEdit);
        var kickBtn = new Button { Text = "踢出" };
        kickBtn.Pressed += () =>
        {
            int kid;
            if (int.TryParse((_sKickEdit.Text ?? "").Trim(), out kid) && kid > 0 && kid <= 65535)
                NetSession.KickPlayer((ushort)kid);
            Refresh();
        };
        rowKick.AddChild(kickBtn);
        vb.AddChild(rowKick);

        var rowLeave = new HBoxContainer();
        _leaveBtn = new Button { Text = "离开房间", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _leaveBtn.Pressed += () => { NetSession.Leave(); _autoSwitchedToRoom = false; ShowPage("lobby"); Refresh(); };
        rowLeave.AddChild(_leaveBtn);
        _closeRoomBtn = new Button { Text = "解散房间（房主）", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _closeRoomBtn.Pressed += () => { NetSession.RequestCloseRoom(); Refresh(); };
        rowLeave.AddChild(_closeRoomBtn);
        vb.AddChild(rowLeave);

        vb.AddChild(Section("聊天"));
        _chatList = new VBoxContainer();
        vb.AddChild(_chatList);
        _chatInput = new LineEdit { PlaceholderText = "输入后回车发送", CustomMinimumSize = new Vector2(0, 40) };
        _chatInput.TextSubmitted += t =>
        {
            if (!string.IsNullOrEmpty(t)) NetSession.SendChat(t);
            _chatInput.Text = "";
        };
        vb.AddChild(_chatInput);
    }

    static void BuildCheatChecks()
    {
        // 已废弃：联机强制关作弊，不再提供逐项允许（保留空方法避免旧引用报错）
    }

    // ================= 页签切换 =================
    static void ShowPage(string key)
    {
        _page = key;
        foreach (var kv in _pages) kv.Value.Visible = (kv.Key == key);
        foreach (var kv in _tabs)
        {
            bool on = (kv.Key == key);
            kv.Value.Modulate = on ? new Color(1f, 0.92f, 0.6f) : new Color(0.75f, 0.8f, 0.86f);
        }
        // 选关列表懒加载：第一次进房间才去扫关卡（扫一遍不便宜）
        if (key == "room" && !_levelsLoaded && _levelListBox != null && GodotObject.IsInstanceValid(_levelListBox))
        {
            _levelsLoaded = true;
            RefreshLevelList();
        }
        Refresh();
    }

    public static void Toggle() { SetVisible(!_visible); }

    static void SetVisible(bool v)
    {
        _visible = v;
        if (_panel != null && GodotObject.IsInstanceValid(_panel)) _panel.Visible = v;
        if (v)
        {
            FitPanel();
            // 下一帧再修正一次尺寸（首次打开时视口还没量准 → 底栏被挤出面板）
            try
            {
                var tree = _panel != null && GodotObject.IsInstanceValid(_panel) ? _panel.GetTree() : null;
                if (tree != null) tree.ProcessFrame += RefitNextFrame;
            }
            catch { }
        }
        Refresh();
    }

    /// <summary>按视口自适应尺寸并居中（手机上竖屏/横屏比例差别大，不能写死）。</summary>
    static void FitPanel()
    {
        try
        {
            if (_panel == null || !GodotObject.IsInstanceValid(_panel)) return;
            var vp = _panel.GetViewport();
            Vector2 vs = vp != null ? vp.GetVisibleRect().Size : new Vector2(1280, 720);
            if (vs.X < 10 || vs.Y < 10) vs = new Vector2(1280, 720);
            float w = Mathf.Clamp(vs.X * 0.74f, MinPanelW, PanelW);
            float h = Mathf.Clamp(vs.Y * 0.88f, MinPanelH, PanelH);
            // ★ 底栏会“消失”就是因为高度算得比内容的固定部分还小：
            //   标题栏 + 页签行 + 状态栏 + 提示栏 是固定高度，先把它们兜住再夹到视口内。
            float need = 0f;
            try { need = _panel.GetCombinedMinimumSize().Y; } catch { }
            if (h < need) h = need;
            if (w > vs.X - 16) w = vs.X - 16;
            if (h > vs.Y - 16) h = vs.Y - 16;
            if (w < 200) w = 200;
            if (h < 200) h = 200;
            _panel.CustomMinimumSize = new Vector2(w, h);
            _panel.Size = new Vector2(w, h);
            _panel.Position = new Vector2((vs.X - w) * 0.5f, (vs.Y - h) * 0.5f);
        }
        catch { }
    }

    /// <summary>下一帧再量一次尺寸。
    /// ★ 首次显示时视口尺寸 / 内容最小尺寸还没算好，`FitPanel` 量出来偏小 → 底栏被挤出面板。
    ///   （用户报过：手机端第一次点开联机界面底栏就没了）</summary>
    static void RefitNextFrame()
    {
        try
        {
            if (_panel != null && GodotObject.IsInstanceValid(_panel))
            {
                var tree = _panel.GetTree();
                if (tree != null) tree.ProcessFrame -= RefitNextFrame;
            }
        }
        catch { }
        FitPanel();
    }

    // ================= 拖动 / 滚动 =================
    static void OnTitleGuiInput(InputEvent @event)
    {
        try
        {
            if (_panel == null || !GodotObject.IsInstanceValid(_panel)) return;
            if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
            {
                _dragging = mb.Pressed;
            }
            else if (@event is InputEventMouseMotion mm && _dragging)
            {
                _panel.Position += mm.Relative;
            }
            else if (@event is InputEventScreenTouch st)
            {
                _dragging = st.Pressed;
            }
            else if (@event is InputEventScreenDrag sd && _dragging)
            {
                // 高 DPI 手机上触摸坐标与画布坐标有缩放，必须换算，否则拖动会飘
                Vector2 scale = TouchScale(_panel);
                _panel.Position += new Vector2(sd.Relative.X * scale.X, sd.Relative.Y * scale.Y);
            }
        }
        catch { }
    }

    static Vector2 TouchScale(Control c)
    {
        try
        {
            var vp = c.GetViewport();
            if (vp == null) return Vector2.One;
            Vector2 vs = vp.GetVisibleRect().Size;
            var win = vp.GetWindow();
            Vector2 ws = win != null ? win.Size : vs;
            return new Vector2(vs.X / Mathf.Max(1, ws.X), vs.Y / Mathf.Max(1, ws.Y));
        }
        catch { return Vector2.One; }
    }

    static ScrollContainer _scrollDragTarget;
    static float _scrollDragStartY, _scrollDragScale = 1f;
    static int _scrollDragStartVal;

    /// <summary>手机上内容区触摸拖动即滚动（跟手），取代“只能拖右侧滑块”。</summary>
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
                    _scrollDragScale = TouchScale(scroll).Y;
                }
                else _scrollDragTarget = null;
                scroll.AcceptEvent();   // 别冒泡到面板，否则滑内容会拖走整个面板
            }
            else if (@event is InputEventScreenDrag sd && _scrollDragTarget == scroll)
            {
                float dy = (sd.Position.Y - _scrollDragStartY) * _scrollDragScale;
                scroll.ScrollVertical = _scrollDragStartVal - (int)dy;
                scroll.AcceptEvent();
            }
        }
        catch { }
    }

    // ================= 设置下发 =================
    static void ApplySettingsFromUI()
    {
        if (!_built || !NetSession.IsHost) return;
        var s = new RoomSettings
        {
            MaxPlayers = (byte)(_sMaxBox.Selected + 2),
            // ★ 强制关闭：不再从 UI 取值（就算有人改了也无效，CheatPolicy 会强制全锁）
            AllowCheats = false,
            AllowLateJoin = _sLate.ButtonPressed,
            TtlMinutes = ParseByte(_sTtlEdit.Text, RoomSettings.MaxTtlMinutes, 0, RoomSettings.MaxTtlMinutes),
            Password = _sPassEdit.Text ?? "",
            BattleMode = _sBattle != null && _sBattle.ButtonPressed,
            BalanceTeams = _sBalance != null && _sBalance.ButtonPressed,
            IpWhitelist = NetSession.Settings.IpWhitelist,
            // 掩码不再由 UI 控制（逐项勾选已移除）；CheatPolicy 会强制全锁
            CheatMask = CheatBits.All
        };
        NetSession.SendRoomSettings(s);
    }

    /// <summary>人数平衡下能否加入某一边。与中继的仲裁用同一套算法，客户端只用它来置灰按钮
    /// （真正的裁定在中继，这里算错也不会导致不公平）。
    /// ★ 关键：先把自己从原阵营摘掉再算 —— 否则切边时自己会被重复计入。</summary>
    static bool CanJoinFaction(byte want, byte mine, int pc, int zc)
    {
        if (!NetSession.IsBalanceTeams) return true;
        if (mine == NetFaction.Plant) pc--;
        else if (mine == NetFaction.Zombie) zc--;
        int targetAfter = (want == NetFaction.Plant ? pc : zc) + 1;
        int otherAfter = (want == NetFaction.Plant ? zc : pc);
        return targetAfter <= otherAfter + 1;
    }

    static byte ParseByte(string text, byte def, int min, int max)
    {
        int v;
        if (!int.TryParse((text ?? "").Trim(), out v)) return def;
        if (v < min) v = min;
        if (v > max) v = max;
        return (byte)v;
    }

    static void RefreshRegionBox()
    {
        if (_regionBox == null || !GodotObject.IsInstanceValid(_regionBox)) return;
        _regionBox.Clear();
        for (int i = 0; i < NetSession.RegionNames.Length; i++)
        {
            string label = NetSession.RegionReady[i] ? NetSession.RegionNames[i] : NetSession.RegionNames[i] + "（未开放）";
            _regionBox.AddItem(label, i);
            _regionBox.SetItemDisabled(i, !NetSession.RegionReady[i]);
        }
        int cur = NetSession.RegionIndex();
        if (cur >= 0) _regionBox.Selected = cur;
    }

    // ================= 刷新 =================
    static void Refresh()
    {
        if (!_built || _statusBar == null) return;

        var s = NetSession.Settings ?? new RoomSettings();
        string lockTag = "  【联机中：修改器已强制关闭】";
        _statusBar.Text = Cut("状态：" + NetSession.State +
                          "  服务器：" + NetSession.CurrentRegionName() +
                          "  通道：" + NetSession.ChannelName() +
                          "  人数：" + NetSession.PeerCount + "/" + NetConstants.MaxPlayers +
                          (NetSession.PingMs >= 0 ? ("  延迟：" + NetSession.PingMs + "ms") : "") +
                          (NetSession.Started ? "  对局已开始" : "") +
                          (NetSession.TtlMinutesLeft >= 0 ? ("  剩余：" + NetSession.TtlMinutesLeft + "分") : "") +
                          (NetSession.Authenticated ? "  已鉴权" : "") + lockTag, 130);

        string notice = string.IsNullOrEmpty(NetSession.LastError)
            ? NetSession.LastNotice
            : ("错误：" + NetSession.LastError);
        _noticeBar.Text = string.IsNullOrEmpty(notice) ? "" : Cut("提示：" + notice, 110);

        // ---- 大厅 ----
        if (_lobbyHint != null)
        {
            int n = NetSession.RoomList != null ? NetSession.RoomList.Count : 0;
            // ★ 在线统计：与电脑版外置修改器对齐（那里显示「· 在线 N」）。
            //   手机端原本只在房间页显示「人数：己方/N」，大厅里看不到服务器上有多少人。
            string stats = NetSession.Authenticated
                ? ("　在线 " + NetSession.LiveCount + " 人 · 今日 " + NetSession.TodayCount + " 人")
                : "";
            _lobbyHint.Text = "共 " + n + " 个房间" + stats +
                              (NetSession.InRoom ? "（你已在房间内，先离开才能再加入）" : "");
        }
        TextEntered(_nickEdit, string.IsNullOrEmpty(NetSession.MyNick) ? "玩家" : NetSession.MyNick);
        if (_roomListBox != null) RebuildRoomList();

        // ---- 房间页 ----
        bool inRoom = NetSession.InRoom;
        bool editable = NetSession.IsHost;
        if (_roomTitle != null)
        {
            _roomTitle.Text = inRoom
                ? ("邀请码：" + NetSession.RoomCode + "   id：" + NetSession.MyPlayerId +
                   "   角色：" + (NetSession.IsHost ? "房主" : "客机"))
                : "未在房间";
            _roomMeta.Text = inRoom ? (NetBattle.Describe() + (NetLevelShare.IsWaiting || NetSession.IsHost ? ("\n" + NetLevelShare.Describe()) : "")) : "先到「联机大厅」创建或加入房间。";
        }
        if (_peerList != null) RebuildPeers();

        // ---- 对战阵营（仅对战模式可用）----
        if (_factionHint != null)
        {
            bool battle = inRoom && NetSession.IsBattleMode;
            int pc = NetSession.CountFaction(NetFaction.Plant);
            int zc = NetSession.CountFaction(NetFaction.Zombie);
            byte mine = NetSession.MyFaction;

            if (!inRoom) _factionHint.Text = "先创建或加入房间。";
            else if (!battle) _factionHint.Text = "本房间是合作模式（房主可改成对战模式）。";
            else
            {
                _factionHint.Text = "植物方 " + pc + " 人 · 僵尸方 " + zc + " 人　你的阵营：" + NetFaction.Name(mine)
                    + (NetSession.Started ? "（已开战，阵营锁定）" : "")
                    + (NetSession.IsBalanceTeams ? "　人数平衡：开" : "　人数平衡：关");
            }

            bool canPick = battle && !NetSession.Started;
            if (_factionPlant != null)
                _factionPlant.Disabled = !canPick || mine == NetFaction.Plant || !CanJoinFaction(NetFaction.Plant, mine, pc, zc);
            if (_factionZombie != null)
                _factionZombie.Disabled = !canPick || mine == NetFaction.Zombie || !CanJoinFaction(NetFaction.Zombie, mine, pc, zc);
            if (_factionPlant != null) _factionPlant.Text = mine == NetFaction.Plant ? "植物方（你）" : "加入植物方";
            if (_factionZombie != null) _factionZombie.Text = mine == NetFaction.Zombie ? "僵尸方（你）" : "加入僵尸方";
        }
        if (_startBtn != null)
        {
            _startBtn.Disabled = !editable || !inRoom || NetSession.Started;
            _startBtn.Text = NetSession.Started ? "对局已开始" : "开始对局";
        }
        if (_endBtn != null) _endBtn.Disabled = !editable || !NetSession.Started;
        if (_closeRoomBtn != null) _closeRoomBtn.Disabled = !editable || !inRoom;
        if (_leaveBtn != null) _leaveBtn.Disabled = !inRoom;

        if (_sLate != null)
        {
            _sLate.Disabled = !editable;
            _sMaxBox.Disabled = !editable;
            _sTtlEdit.Editable = editable;
            _sPassEdit.Editable = editable;
            // 只有房主才把服务端设置回填到输入框；客机回填会覆盖用户正在输入的内容
            if (editable)
            {
                _sLate.ButtonPressed = s.AllowLateJoin;
                // 注：赋同值不会触发 Toggled（Godot 只在真正变化时发），所以这里不会造成回写循环
                if (_sBattle != null) _sBattle.ButtonPressed = s.BattleMode;
                if (_sBalance != null) _sBalance.ButtonPressed = s.BalanceTeams;
                int mi = s.MaxPlayers - 2;
                if (mi >= 0 && mi < _sMaxBox.ItemCount) _sMaxBox.Selected = mi;
                if (!_sTtlEdit.HasFocus()) _sTtlEdit.Text = s.TtlMinutes.ToString();
            }
        }

        if (_chatList != null) RebuildChat();

        // 选关（房间页内）：显示当前已选 + 只有房主能操作（与电脑版 LevelCard.IsEnabled = isHost 对齐）
        if (_levelCurText != null && GodotObject.IsInstanceValid(_levelCurText))
        {
            bool has = !string.IsNullOrEmpty(NetSession.LevelOverrideKey);
            _levelCurText.Text = has
                ? ("已选关卡：" + (string.IsNullOrEmpty(NetSession.LevelOverrideName) ? NetSession.LevelOverrideKey : NetSession.LevelOverrideName))
                : "未选关：开始对局时跟随房主当前所在的关卡";
            for (int i = 0; i < _levelBtns.Count; i++)
            {
                var b = _levelBtns[i];
                if (b != null && GodotObject.IsInstanceValid(b)) b.Disabled = !editable;
            }
            if (editable && !_levelsLoaded)
            {
                _levelsLoaded = true;
                RefreshLevelList();
            }
        }

        // 进房后自动跳到「我的房间」（只自动跳一次，之后用户可以自由切页）
        if (inRoom && !_autoSwitchedToRoom) { _autoSwitchedToRoom = true; ShowPage("room"); }
        if (!inRoom) _autoSwitchedToRoom = false;
    }

    /// <summary>截断长文本（单行化）。底栏会换行 → 行数多了会把面板内容顶出去。</summary>
    static string Cut(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace("\n", " ").Replace("\r", " ");
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        return s.Length <= max ? s : (s.Substring(0, max) + "…");
    }

    static void TextEntered(LineEdit e, string fallback)
    {
        if (e == null || !GodotObject.IsInstanceValid(e)) return;
        if (e.HasFocus()) return;                       // 别打断用户输入
        if (string.IsNullOrEmpty(e.Text)) e.Text = fallback;
    }

    static void RebuildRoomList()
    {
        foreach (var ch in _roomListBox.GetChildren()) ch.QueueFree();
        var rooms = NetSession.RoomList;
        if (rooms == null || rooms.Count == 0)
        {
            _roomListBox.AddChild(new Label { Text = "（暂无房间，点「刷新房间列表」再试）", Modulate = new Color(0.7f, 0.76f, 0.82f) });
            return;
        }
        for (int i = 0; i < rooms.Count; i++)
        {
            var r = rooms[i];
            var row = new PanelContainer();
            var rsb = new StyleBoxFlat();
            rsb.BgColor = new Color(0.15f, 0.19f, 0.23f, 0.95f);
            rsb.SetCornerRadiusAll(8);
            rsb.SetContentMarginAll(8);
            row.AddThemeStyleboxOverride("panel", rsb);

            var hb = new HBoxContainer();
            row.AddChild(hb);

            string desc = r.Code + "  " + r.HostNick + "  " + r.Players + "/" + r.Max +
                          "  " + (r.Started ? "进行中" : "未开始") +
                          (r.NeedPass ? "  需口令" : "") +
                          (r.AllowCheats ? "" : "  禁作弊") +
                          (r.TtlMinutesLeft == RoomListCodec.TtlUnlimited ? "  不限时" : ("  剩" + r.TtlMinutesLeft + "分"));
            var lb = new Label
            {
                Text = desc,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            hb.AddChild(lb);

            var jb = new Button { Text = "加入", CustomMinimumSize = new Vector2(72, 36) };
            string code = r.Code;
            jb.Disabled = !r.Joinable || NetSession.InRoom;
            jb.Pressed += () =>
            {
                if (r.NeedPass)
                {
                    // 需要口令：把人送到「加入房间」页填口令（那里有口令框）
                    _jCodeEdit.Text = code;
                    ShowPage("join");
                    NetSession.PostNotice("该房间需要口令，请填口令后点加入");
                }
                else if (NetSession.JoinRoom(code, string.IsNullOrEmpty(NetSession.MyNick) ? "玩家" : NetSession.MyNick, ""))
                {
                    NetSession.PostNotice("正在加入房间 " + code + " …");
                }
                Refresh();
            };
            hb.AddChild(jb);
            _roomListBox.AddChild(row);
        }
    }

    static void RebuildPeers()
    {
        foreach (var ch in _peerList.GetChildren()) ch.QueueFree();
        if (NetSession.Players.Count == 0)
        {
            _peerList.AddChild(new Label { Text = "（未加入房间）", Modulate = new Color(0.7f, 0.76f, 0.82f) });
            return;
        }
        for (int i = 0; i < NetSession.Players.Count; i++)
        {
            var p = NetSession.Players[i];
            if (p == null) continue;
            float r0, g0, b0;
            PeerColors.GetRgb(p.Color, out r0, out g0, out b0);
            _peerList.AddChild(new Label
            {
                Text = (p.IsHost ? "★ " : "• ") + p.Nick + "  #" + p.Id,
                Modulate = new Color(r0, g0, b0)
            });
        }
    }

    static void RebuildChat()
    {
        // 只在内容变化时重建，避免每秒把历史消息的滚动位置重置
        bool same = _chatSeen.Count == _lines.Count;
        if (same)
        {
            for (int i = 0; i < _lines.Count; i++)
                if (_chatSeen[i] != _lines[i]) { same = false; break; }
        }
        if (same) return;

        _chatSeen.Clear();
        _chatSeen.AddRange(_lines);
        foreach (var c in _chatList.GetChildren()) c.QueueFree();
        foreach (var line in _lines)
            _chatList.AddChild(new Label { Text = line, AutowrapMode = TextServer.AutowrapMode.WordSmart });
    }

    // ================= 每帧 =================
    public static void Tick(Node root)
    {
        Ensure(root);
        if (_panel != null && GodotObject.IsInstanceValid(_panel) && !_visible && _panel.Visible) _panel.Visible = false;

        ulong now = Time.GetTicksMsec();
        if (now - _lastRefreshMs >= 1000)
        {
            _lastRefreshMs = now;
            if (_visible) Refresh();
        }
    }

    /// <summary>面板是否已创建（供外部判断是否需要 Init）。</summary>
    public static bool Built { get { return _built; } }
}
