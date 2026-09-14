using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace PvzheRemote
{
    /// <summary>
    /// 联机独立窗口（仅电脑版）。
    /// 三个分区：联机大厅 / 创建房间 / 加入房间；一旦进入房间，窗口直接切到「房间界面」。
    /// 服务器只显示区域名（香港1区…），不出现域名/IP；玩家名默认用游戏存档名。
    /// </summary>
    public partial class NetWindow : Window
    {
        /// <summary>玩家颜色（与游戏内光标、中继名单同一套 8 色板）。</summary>
        static readonly string[] Palette = { "FF6B6B", "4DACFF", "06D6A0", "FFD166", "C77DFF", "FF9F1C", "2EC4B6", "F72585" };

        readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2000) };
        readonly Dictionary<string, string> _st = new Dictionary<string, string>();
        readonly List<string[]> _rooms = new List<string[]>();
        readonly List<string[]> _peers = new List<string[]>();
        readonly List<string[]> _levels = new List<string[]>();
        string _levelCat = "adv";
        bool _levelsLoaded;
        readonly List<string> _chat = new List<string>();

        bool _busy;
        bool _regionBuilt;
        bool _settingsLoaded;
        bool _nameLoaded;
        string _page = "lobby";
        string _chatDump = "";
        string _chatLast = "";
        string _battle = "";
        string _status = "";

        public NetWindow()
        {
            InitializeComponent();

            foreach (var s in new[] { "2", "4", "6", "8" })
            {
                CreateMaxBox.Items.Add(s);
                SetMaxBox.Items.Add(s);
            }
            CreateMaxBox.SelectedIndex = 1;
            SetMaxBox.SelectedIndex = 1;
            // ★ 联机时强制关闭全部修改器：界面不再提供开关（控件已 IsEnabled=False）
            CreateCheatsCheck.IsChecked = false;
            CreateLateCheck.IsChecked = true;
            SetCheatsCheck.IsChecked = false;
            SetLateCheck.IsChecked = true;

            Loaded += async (_, __) => await BootAsync();
            Closed += (_, __) => _timer.Stop();
            _timer.Tick += async (_, __) => await RefreshAsync();
            _timer.Start();
        }

        // ================= 与 MOD 通信 =================

        static async System.Threading.Tasks.Task<string> Cmd(string path)
        {
            string r = "";
            try { r = await MainWindow.GetAsync(path); } catch { }
            if (string.IsNullOrEmpty(r)) return "err:MOD 无响应（游戏没运行？）";
            return r.Trim();
        }

        async System.Threading.Tasks.Task BootAsync()
        {
            await RefreshAsync();
            if (Get("state") == "Offline") await Cmd("/cmd?name=NetLobby");
            await RefreshAsync();
        }

        async System.Threading.Tasks.Task RefreshAsync()
        {
            if (_busy) return;
            _busy = true;
            try
            {
                ParseStatus(await Cmd("/cmd?name=NetStatus"));

                string state = Get("state");
                if (Get("inRoom") != "1" && (state == "Lobby" || state == "Signaling"))
                {
                    ParseRooms(await Cmd("/cmd?name=NetRooms"));
                }
                Apply();
            }
            catch { }
            finally { _busy = false; }
        }

        string Get(string k)
        {
            string v;
            return _st.TryGetValue(k, out v) ? v : "";
        }

        static int ParseI(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int v = 0, i = 0, sign = 1;
            if (s[0] == '-') { sign = -1; i = 1; }
            for (; i < s.Length; i++)
            {
                if (s[i] < '0' || s[i] > '9') break;
                v = v * 10 + (s[i] - '0');
            }
            return v * sign;
        }

        static Brush BrushOf(string hex)
        {
            try { return (Brush)new BrushConverter().ConvertFromString("#" + hex); }
            catch { return Brushes.Gray; }
        }

        // ================= 解析 =================

        void ParseStatus(string raw)
        {
            _st.Clear();
            _peers.Clear();
            _chat.Clear();
            if (string.IsNullOrEmpty(raw)) return;

            var lines = raw.Replace("\r", "").Split('\n');
            foreach (var kv in lines[0].Split('|'))
            {
                int i = kv.IndexOf('=');
                if (i > 0) _st[kv.Substring(0, i)] = kv.Substring(i + 1);
            }
            for (int i = 1; i < lines.Length; i++)
            {
                string L = lines[i];
                if (L.StartsWith("peers=")) ParsePeers(L.Substring(6));
                else if (L.StartsWith("battle=")) _battle = L.Substring(7);
                else if (L.StartsWith("chat=")) _chatDump = L.Substring(5);
            }
        }

        void ParsePeers(string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            foreach (var one in s.Split(';'))
            {
                if (one.Length == 0) continue;
                var f = one.Split(',');
                if (f.Length >= 4) _peers.Add(f);
            }
        }

        void ParseRooms(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return;
            var lines = raw.Replace("\r", "").Split('\n');
            string state = "";
            string auth = "";
            foreach (var kv in lines[0].Split('|'))
            {
                int i = kv.IndexOf('=');
                if (i <= 0) continue;
                string k = kv.Substring(0, i), v = kv.Substring(i + 1);
                if (k == "state") state = v;
                else if (k == "auth") auth = v;
            }
            if (state != "Lobby" && state != "Signaling") return;   // 不在大厅时保留上次列表
            // ★ 未鉴权（刚上线 / 刚离开房间重连）时 MOD 拿不到列表，返回的必然是空表。
            //   这里直接保留上一次结果，否则大厅会先闪一下「暂时没有房间」再冒出房间。
            if (auth == "0") return;

            _rooms.Clear();
            for (int i = 1; i < lines.Length; i++)
            {
                string L = lines[i];
                if (!L.StartsWith("room=")) continue;
                _rooms.Add(L.Substring(5).Split(','));
            }
        }

        // ================= 界面刷新 =================

        string MyName()
        {
            string typed = PlayerNameBox != null ? PlayerNameBox.Text.Trim() : "";
            if (typed.Length > 0) return typed.Length > 12 ? typed.Substring(0, 12) : typed;
            string s = Get("savename");
            if (string.IsNullOrEmpty(s)) s = Get("myname");
            if (string.IsNullOrEmpty(s)) s = "玩家";
            return s.Length > 12 ? s.Substring(0, 12) : s;
        }

        void Apply()
        {
            string state = Get("state");
            bool inRoom = Get("inRoom") == "1";
            _status = Get("error").Length > 0 ? Get("error") : Get("notice");

            StatePill.Text = state == "Relay" || state == "Direct" || state == "Playing" ? "房间中"
                           : state == "Lobby" || state == "Signaling" ? "联机大厅"
                           : state == "Reconnecting" ? "重连中"
                           : state == "Ended" ? "已结束" : "未连接";
            PlayerNameText_Update();

            BuildRegions();
            SelectRegion(Get("region"));

            if (inRoom)
            {
                if (_page != "room") ShowPage("room");
                ApplyRoom();
                ApplyFaction();     // 对战阵营卡片（非对战模式下自行隐藏）
            }
            else
            {
                if (_page == "room") ShowPage("lobby");
                if (_page == "lobby") ApplyLobby();
            }

            // 连接状态提示（让用户分清"没连上服务器"和"大厅里没房间"）
            // M5：顺带显示当前在线（中继连接数）。联机界面**只显示当前在线**，不显示当日在线。
            bool connected = state == "Lobby" || state == "Signaling" || state == "Relay" || state == "Playing";
            string live = Get("live");
            LobbyConnText.Text = connected
                ? "已连接：" + Get("region") + "（" + StatePill.Text + "）" + (live.Length > 0 ? "· 在线 " + live : "")
                : (Get("error").Length > 0 ? "未连接：" + Get("error") : "未连接服务器（点右侧按钮重连）");

            // 延迟显示（自己到服务器的往返）
            int pingMs = ParseI(Get("ping"));
            if (connected && pingMs > 0)
            {
                PingText.Text = "延迟 " + pingMs + " ms";
                PingText.Foreground = pingMs < 100 ? BrushOf("06A06A") : (pingMs < 250 ? BrushOf("D79A00") : BrushOf("C2403A"));
            }
            else
            {
                PingText.Text = "延迟 --";
                PingText.Foreground = (Brush)FindResource("SubBrush");
            }

            StatusText.Text = _status.Length > 0 ? _status : "就绪";
        }

        void BuildRegions()
        {
            if (_regionBuilt) return;
            string regions = Get("regions");
            if (string.IsNullOrEmpty(regions)) return;

            foreach (var one in regions.Split(';'))
            {
                if (one.Length == 0) continue;
                int c = one.LastIndexOf(':');
                string name = c > 0 ? one.Substring(0, c) : one;
                bool ready = c > 0 && one.Substring(c + 1) == "1";
                var it = new ComboBoxItem
                {
                    Content = ready ? name : name + "（未开放）",
                    IsEnabled = ready,
                    Tag = name
                };
                RegionBox.Items.Add(it);
            }
            if (RegionBox.Items.Count > 0 && RegionBox.SelectedIndex < 0) RegionBox.SelectedIndex = 0;
            _regionBuilt = true;
        }

        void PlayerNameText_Update()
        {
            // 自动填一次存档名（之后不再覆盖用户手输的值）
            if (_nameLoaded) return;
            string s = Get("savename");
            if (string.IsNullOrEmpty(s)) s = Get("myname");
            if (string.IsNullOrEmpty(s)) return;
            if (PlayerNameBox != null) PlayerNameBox.Text = s.Length > 12 ? s.Substring(0, 12) : s;
            _nameLoaded = true;
        }

        void UseSaveNameBtn_Click(object s, RoutedEventArgs e)
        {
            string name = Get("savename");
            if (string.IsNullOrEmpty(name)) name = Get("myname");
            if (string.IsNullOrEmpty(name)) { StatusText.Text = "没读到存档名，请手动填写玩家名"; return; }
            PlayerNameBox.Text = name.Length > 12 ? name.Substring(0, 12) : name;
            _nameLoaded = true;
            StatusText.Text = "已改用存档名：" + PlayerNameBox.Text;
        }

        void SelectRegion(string name)
        {
            for (int i = 0; i < RegionBox.Items.Count; i++)
            {
                var it = RegionBox.Items[i] as ComboBoxItem;
                if (it != null && (it.Tag as string) == name)
                {
                    if (RegionBox.SelectedIndex != i) RegionBox.SelectedIndex = i;
                    return;
                }
            }
        }

        string SelectedRegion()
        {
            var it = RegionBox.SelectedItem as ComboBoxItem;
            return it != null && it.Tag != null ? (string)it.Tag : "香港1区";
        }

        void ShowPage(string page)
        {
            _page = page;
            PageLobby.Visibility = page == "lobby" ? Visibility.Visible : Visibility.Collapsed;
            PageCreate.Visibility = page == "create" ? Visibility.Visible : Visibility.Collapsed;
            PageJoin.Visibility = page == "join" ? Visibility.Visible : Visibility.Collapsed;
            PageRoom.Visibility = page == "room" ? Visibility.Visible : Visibility.Collapsed;

            bool room = page == "room";
            TabLobby.Visibility = room ? Visibility.Collapsed : Visibility.Visible;
            TabCreate.Visibility = room ? Visibility.Collapsed : Visibility.Visible;
            TabJoin.Visibility = room ? Visibility.Collapsed : Visibility.Visible;
            TabBack.Visibility = room ? Visibility.Visible : Visibility.Collapsed;

            Highlight(TabLobby, page == "lobby");
            Highlight(TabCreate, page == "create");
            Highlight(TabJoin, page == "join");
        }

        void Highlight(Button b, bool on)
        {
            b.Background = on ? (Brush)FindResource("AccentBrush") : Brushes.Transparent;
            b.Foreground = on ? Brushes.White : (Brush)FindResource("SubBrush");
        }

        void ApplyLobby()
        {
            RoomListPanel.Children.Clear();
            string region = Get("region");
            if (_rooms.Count == 0)
            {
                LobbyHint.Text = "大厅暂时没有房间。可以点「创建房间」自己开一个，房间号（邀请码）发给朋友即可。";
                return;
            }
            LobbyHint.Text = "共 " + _rooms.Count + " 个房间 · 当前服务器：" + region;
            foreach (var r in _rooms) RoomListPanel.Children.Add(BuildRoomCard(r));
        }

        FrameworkElement BuildRoomCard(string[] r)
        {
            string code = r.Length > 0 ? r[0] : "";
            int players = r.Length > 1 ? ParseI(r[1]) : 0;
            int max = r.Length > 2 ? ParseI(r[2]) : 4;
            bool started = r.Length > 3 && r[3] == "1";
            bool needPass = r.Length > 4 && r[4] == "1";
            int ttl = r.Length > 5 ? ParseI(r[5]) : -1;
            bool cheats = r.Length > 6 && r[6] == "1";
            string host = r.Length > 7 && r[7].Length > 0 ? r[7] : "玩家";

            var bd = new Border { Style = (Style)FindResource("RoomItem") };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel();
            var top = new StackPanel { Orientation = Orientation.Horizontal };
            top.Children.Add(new TextBlock
            {
                Text = host,
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("TextBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });
            top.Children.Add(Badge("房间号 " + code));
            top.Children.Add(Badge(players + "/" + max + " 人"));
            if (needPass) top.Children.Add(Badge("需口令"));
            left.Children.Add(top);

            var sub = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
            sub.Children.Add(SubText(Get("region")));
            sub.Children.Add(SubText("·"));
            sub.Children.Add(SubText(cheats ? "作弊开" : "作弊关"));
            sub.Children.Add(SubText("·"));
            sub.Children.Add(SubText(ttl < 0 ? "不限时" : "剩 " + ttl + " 分"));
            sub.Children.Add(SubText("·"));
            sub.Children.Add(SubText(started ? "对局进行中" : "等待开始"));
            left.Children.Add(sub);
            Grid.SetColumn(left, 0);
            g.Children.Add(left);

            var btn = new Button
            {
                Content = "加入房间",
                Style = (Style)FindResource("Primary"),
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 104
            };
            string cap = code;
            btn.Click += async (s, e) =>
            {
                if (needPass && JoinPassBox.Text.Trim().Length == 0)
                {
                    ShowPage("join");
                    JoinCodeBox.Text = cap;
                    JoinHint.Text = "该房间需要口令，请输入口令后加入";
                    JoinPassBox.Focus();
                    return;
                }
                await JoinAsync(cap, JoinPassBox.Text.Trim());
            };
            Grid.SetColumn(btn, 1);
            g.Children.Add(btn);

            bd.Child = g;
            return bd;
        }

        Border Badge(string text) => new Border
        {
            Style = (Style)FindResource("Badge"),
            Margin = new Thickness(8, 0, 0, 0),
            Child = new TextBlock { Text = text, FontSize = 11, Foreground = (Brush)FindResource("SubBrush") }
        };

        TextBlock SubText(string t) => new TextBlock
        {
            Text = t,
            FontSize = 12,
            Foreground = (Brush)FindResource("SubBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0)
        };

        void ApplyRoom()
        {
            bool isHost = Get("role") == "房主";
            int ttl = ParseI(Get("ttl"));
            bool cheats = Get("allowCheats") == "1";

            RoomCodeText.Text = Get("room");
            RoomRoleText.Text = isHost ? "你是房主" : "你是客机";
            RoomCountText.Text = Get("count") + "/" + Get("max") + " 人";
            int pingMs = ParseI(Get("ping"));
            RoomMetaText.Text = "服务器：" + Get("region")
                + "    延迟：" + (pingMs > 0 ? pingMs + " ms" : "--")
                + "    作弊：" + (cheats ? "允许" : "禁止")
                + "    剩余时间：" + (ttl > 0 ? ttl + " 分钟" : "不限")
                + "    中途加入：" + (Get("late") == "1" ? "允许" : "禁止")
                + "    加密：" + (Get("e2e") == "1" ? "已加密" : "未启用")
                + "    对局：" + (Get("started") == "1" ? "进行中（battle " + Get("battleId") + "）" : "未开始");

            // 玩家列表
            PeerPanel.Children.Clear();
            foreach (var p in _peers)
            {
                int id = ParseI(p[0]);
                int slot = ParseI(p[1]);
                bool host = p[2] == "1";
                string nick = p.Length > 3 && p[3].Length > 0 ? p[3] : "玩家";

                var row = new Border
                {
                    Background = (Brush)FindResource("CardHiBrush"),
                    BorderBrush = (Brush)FindResource("BorderBrush"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(12, 8, 12, 8),
                    Margin = new Thickness(0, 0, 0, 7)
                };
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = 10,
                    Height = 10,
                    Fill = BrushOf(Palette[((slot % Palette.Length) + Palette.Length) % Palette.Length]),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0)
                };
                Grid.SetColumn(dot, 0);
                g.Children.Add(dot);

                var name = new TextBlock
                {
                    Text = nick + (host ? "   ★ 房主" : ""),
                    FontSize = 13,
                    FontWeight = host ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = (Brush)FindResource("TextBrush"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(name, 1);
                g.Children.Add(name);

                if (isHost && !host)
                {
                    var kick = new Button
                    {
                        Content = "移出房间",
                        Style = (Style)FindResource("Danger"),
                        Margin = new Thickness(8, 0, 0, 0)
                    };
                    int kid = id;
                    kick.Click += async (s, e) =>
                    {
                        await Cmd("/cmd?name=NetKick&id=" + kid);
                        await RefreshAsync();
                    };
                    Grid.SetColumn(kick, 2);
                    g.Children.Add(kick);
                }

                row.Child = g;
                PeerPanel.Children.Add(row);
            }

            // 房间设置（房主可改；进入房间时载入一次，避免覆盖正在输入的内容）
            SettingsCard.IsEnabled = isHost;
            ApplySetBtn.IsEnabled = isHost;
            if (!_settingsLoaded)
            {
                _settingsLoaded = true;
                SelectCombo(SetMaxBox, Get("max"));
                SetTtlBox.Text = ttl > 0 ? ttl + "" : "120";
                SetCheatsCheck.IsChecked = false;   // 恒为关（联机强制关闭修改器）
                SetLateCheck.IsChecked = Get("late") == "1";
                SetBattleCheck.IsChecked = Get("battleMode") == "1";
                SetBalanceCheck.IsChecked = Get("balance") == "1";
                _pendingMask = ParseMask(Get("mask"));
                foreach (var kv in _cheatChecks) kv.Value.IsChecked = (_pendingMask & kv.Key) != 0;
            }
            _ = BuildCheatGridAsync();   // 名称表首次拿到后建表（已建则立即返回）

            // 选关卡片
            string lk = Get("levelkey"), ln = Get("levelname");
            LevelCurText.Text = lk.Length > 0
                ? "已选关卡：" + (ln.Length > 0 ? ln : lk) + "（开始对局时带所有人进这个关卡）"
                : "未选关：开始对局时跟随房主当前所在的关卡";
            LevelCard.IsEnabled = isHost;
            if (!_levelsLoaded) { HighlightLevelCats(); _ = LoadLevelsAsync(); }

            // 操作按钮
            StartBtn.IsEnabled = isHost && Get("started") != "1";
            EndBtn.IsEnabled = isHost && Get("started") == "1";
            CloseBtn.IsEnabled = isHost;
            BattleHint.Text = isHost
                ? "作为房主：进入你想玩的关卡后点「开始对局」，其他玩家会自动进入同一关卡。"
                : "等待房主开始对局；房主开始后你会自动进入他所在的关卡。";

            // 聊天
            var dump = _chatDump;
            if (dump != _chatLast)
            {
                _chatLast = dump;
                _chat.Clear();
                foreach (var m in dump.Split('\u001f'))
                    if (m.Length > 0) _chat.Add(m);
                ChatList.Items.Clear();
                foreach (var m in _chat)
                    ChatList.Items.Add(new TextBlock
                    {
                        Text = m,
                        FontSize = 12,
                        Foreground = (Brush)FindResource("TextBrush"),
                        Margin = new Thickness(0, 0, 0, 4),
                        TextWrapping = TextWrapping.Wrap
                    });
            }
        }

        // ================= 选关 =================

        static string CatName(string cat)
        {
            if (cat == "adv") return "冒险";
            if (cat == "fun") return "娱乐";
            if (cat == "challenge") return "挑战";
            if (cat == "puzzle") return "拼图";
            if (cat == "survival") return "生存";
            if (cat == "daily") return "每日关卡";
            if (cat == "online") return "在线关卡";
            return cat;
        }

        void HighlightLevelCats()
        {
            if (LevelCatPanel == null) return;
            foreach (var c in LevelCatPanel.Children)
            {
                var b = c as Button;
                if (b == null) continue;
                string cat = b.Tag as string ?? "";
                bool on = cat == _levelCat;
                b.Background = on ? (Brush)FindResource("AccentBrush") : Brushes.Transparent;
                b.Foreground = on ? Brushes.White : (Brush)FindResource("SubBrush");
            }
        }

        async void LevelCat_Click(object s, RoutedEventArgs e)
        {
            var b = s as Button;
            string cat = b != null && b.Tag != null ? (string)b.Tag : "adv";
            _levelCat = cat;
            HighlightLevelCats();
            await LoadLevelsAsync();
        }

        void LevelSearchBox_TextChanged(object s, TextChangedEventArgs e) { RenderLevels(); }

        async System.Threading.Tasks.Task LoadLevelsAsync()
        {
            _levelsLoaded = true;
            ShowBusy("正在读取关卡列表…", "分类：" + CatName(_levelCat));
            try
            {
                string raw = await Cmd("/cmd?name=NetLevels&type=" + _levelCat);
                _levels.Clear();
                var lines = raw.Replace("\r", "").Split('\n');
                for (int i = 1; i < lines.Length; i++)
                {
                    string L = lines[i];
                    if (!L.StartsWith("lv=")) continue;
                    var f = L.Substring(3).Split('|');
                    if (f.Length < 2) continue;
                    _levels.Add(new string[] { f[0], f[1], f.Length > 2 ? f[2] : "" });
                }
                RenderLevels();
                if (_levels.Count == 0)
                    LevelCountText.Text = "这一类没找到关卡" + (raw.StartsWith("err") ? "：" + raw : "（每日/在线关卡需要先在游戏内下载过）");
            }
            catch (Exception ex) { LevelCountText.Text = "读取失败：" + ex.Message; }
            finally { HideBusy(); }
        }

        void RenderLevels()
        {
            if (LevelListPanel == null) return;
            LevelListPanel.Children.Clear();
            string filter = LevelSearchBox != null ? LevelSearchBox.Text.Trim() : "";
            int total = 0, shown = 0;
            for (int i = 0; i < _levels.Count; i++)
            {
                var it = _levels[i];
                if (filter.Length > 0)
                {
                    bool hit = it[1].IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                            || it[0].IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!hit) continue;
                }
                total++;
                if (shown >= 120) continue;
                shown++;
                LevelListPanel.Children.Add(BuildLevelRow(it));
            }
            LevelCountText.Text = "共 " + _levels.Count + " 关"
                + (filter.Length > 0 ? "，匹配 " + total + " 关" : "")
                + (total > shown ? "（仅显示前 " + shown + " 关，可用搜索缩小）" : "");
        }

        FrameworkElement BuildLevelRow(string[] it)
        {
            var bd = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(0, 0, 0, 2)
            };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            bool picked = Get("levelkey") == it[0];
            var name = new TextBlock
            {
                Text = it[1] + (it[2].Length > 0 ? "　·　" + it[2] : ""),
                FontSize = 12,
                Foreground = picked ? (Brush)FindResource("AccentDimBrush") : (Brush)FindResource("TextBrush"),
                FontWeight = picked ? FontWeights.Bold : FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(name, 0);
            g.Children.Add(name);

            var btn = new Button
            {
                Content = picked ? "已选" : "选它",
                Style = (Style)FindResource(picked ? "Primary" : "Ghost"),
                Margin = new Thickness(8, 0, 0, 0)
            };
            string k = it[0], n = it[1];
            btn.Click += async (s, e) =>
            {
                btn.IsEnabled = false;
                string ret = await Cmd("/cmd?name=NetSetLevel&key=" + Uri.EscapeDataString(k) + "&lvname=" + Uri.EscapeDataString(n));
                await RefreshAsync();
                RenderLevels();
                StatusText.Text = ret.StartsWith("ok") ? "已选关卡：" + n : ret;
            };
            Grid.SetColumn(btn, 1);
            g.Children.Add(btn);

            bd.Child = g;
            return bd;
        }

        async void LevelClearBtn_Click(object s, RoutedEventArgs e)
        {
            await Cmd("/cmd?name=NetSetLevel");
            await RefreshAsync();
            RenderLevels();
            StatusText.Text = "已清除选关：开始对局时将跟随你当前所在的关卡";
        }

        static void SelectCombo(ComboBox box, string value)
        {
            for (int i = 0; i < box.Items.Count; i++)
                if (box.Items[i] as string == value) { box.SelectedIndex = i; return; }
        }

        // ================= 进度遮罩 =================

        /// <summary>显示进度圈 + “当前在干什么”。</summary>
        void ShowBusy(string what, string sub = "")
        {
            try
            {
                BusyText.Text = what;
                BusySubText.Text = sub;
                BusyOverlay.Visibility = Visibility.Visible;
                var anim = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromMilliseconds(900)))
                {
                    RepeatBehavior = RepeatBehavior.Forever
                };
                SpinRotate.BeginAnimation(RotateTransform.AngleProperty, anim);
            }
            catch { }
        }

        void HideBusy()
        {
            try
            {
                SpinRotate.BeginAnimation(RotateTransform.AngleProperty, null);
                BusyOverlay.Visibility = Visibility.Collapsed;
            }
            catch { }
        }

        /// <summary>轮询等待进入房间（创建/加入后使用，不走 RefreshAsync 的重入锁）。</summary>
        async System.Threading.Tasks.Task<bool> WaitInRoomAsync(int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                ParseStatus(await Cmd("/cmd?name=NetStatus"));
                if (Get("inRoom") == "1")
                {
                    Apply();
                    return true;
                }
                await System.Threading.Tasks.Task.Delay(500);
                waited += 500;
            }
            await RefreshAsync();
            return Get("inRoom") == "1";
        }

        /// <summary>取一条可读的失败原因（error 优先，其次 notice）。</summary>
        string WhyFail()
        {
            string why = Get("error");
            if (why.Length == 0) why = Get("notice");
            return why.Length > 0 ? why : "服务器没有回应，可先点「重新连接服务器」再试一次";
        }

        // ================= 事件 =================

        void TabLobby_Click(object s, RoutedEventArgs e) { ShowPage("lobby"); _ = RefreshAsync(); }

        async void LobbyRefreshBtn_Click(object s, RoutedEventArgs e)
        {
            string ret = await Cmd("/cmd?name=NetRooms");
            await RefreshAsync();
            StatusText.Text = ret.StartsWith("err") ? ret : "房间列表已刷新（当前 " + _rooms.Count + " 个房间）";
        }

        async void LobbyReconnectBtn_Click(object s, RoutedEventArgs e)
        {
            StatusText.Text = "正在重新连接 " + SelectedRegion() + " …";
            await Cmd("/cmd?name=NetLobbyClose");
            await Cmd("/cmd?name=NetServer&addr=" + Uri.EscapeDataString(SelectedRegion()));
            string ret = await Cmd("/cmd?name=NetLobby");
            await RefreshAsync();
            StatusText.Text = ret.StartsWith("ok") ? "已连接服务器：" + SelectedRegion() : ret;
        }
        void TabCreate_Click(object s, RoutedEventArgs e) { ShowPage("create"); }
        void TabJoin_Click(object s, RoutedEventArgs e) { ShowPage("join"); }
        void TabBack_Click(object s, RoutedEventArgs e) { ShowPage("lobby"); }

        async void RegionBox_SelectionChanged(object s, SelectionChangedEventArgs e)
        {
            if (!_regionBuilt) return;
            string name = SelectedRegion();
            if (string.IsNullOrEmpty(name)) return;
            await Cmd("/cmd?name=NetServer&addr=" + Uri.EscapeDataString(name));
            await RefreshAsync();
        }

        async void CreateBtn_Click(object s, RoutedEventArgs e)
        {
            CreateBtn.IsEnabled = false;
            CreateHint.Text = "";
            ShowBusy("正在创建房间…", "连接服务器 → 分配房间号 → 进入房间");
            try
            {
                string nick = MyName();
                int max = ParseI(CreateMaxBox.SelectedItem as string);
                int ttl = ParseI(CreateTtlBox.Text.Trim());
                if (max < 2 || max > 8) max = 4;
                if (ttl < 5 || ttl > 1440) ttl = 120;
                string pass = CreatePassBox.Text.Trim();

                string ret = await Cmd("/cmd?name=NetCreate&nick=" + Uri.EscapeDataString(nick)
                    + "&max=" + max + "&ttl=" + ttl
                    + "&allowCheats=0"
                    + "&late=" + (CreateLateCheck.IsChecked == true ? "1" : "0")
                    + "&battle=" + (CreateBattleCheck.IsChecked == true ? "1" : "0")
                    + "&balance=" + (CreateBalanceCheck.IsChecked == true ? "1" : "0")
                    + "&password=" + Uri.EscapeDataString(pass));   // ★ 必须叫 password：MOD 的 BuildNetSettings 读这个名

                _settingsLoaded = false;

                if (!ret.StartsWith("ok"))
                {
                    CreateHint.Text = "创建失败：" + ret;
                    StatusText.Text = CreateHint.Text;
                    return;
                }

                ShowBusy("正在进入房间…", "等待服务器下发房间号");
                if (await WaitInRoomAsync(8000))
                {
                    CreateHint.Text = "";
                    StatusText.Text = "房间已创建，邀请码 " + Get("room") + "（可点「复制邀请码」发给朋友）";
                }
                else
                {
                    CreateHint.Text = "房间没建起来：" + WhyFail();
                    StatusText.Text = CreateHint.Text;
                }
            }
            catch (Exception ex) { CreateHint.Text = "创建异常：" + ex.Message; }
            finally { HideBusy(); CreateBtn.IsEnabled = true; }
        }

        async void JoinBtn_Click(object s, RoutedEventArgs e) => await JoinAsync(JoinCodeBox.Text.Trim(), JoinPassBox.Text.Trim());

        async System.Threading.Tasks.Task JoinAsync(string code, string pass)
        {
            if (string.IsNullOrEmpty(code)) { ShowPage("join"); JoinHint.Text = "请先填写房间号"; return; }
            JoinBtn.IsEnabled = false;
            JoinHint.Text = "";
            ShowBusy("正在加入房间…", "房间号 " + code);
            try
            {
                string ret = await Cmd("/cmd?name=NetJoin&code=" + Uri.EscapeDataString(code)
                    + "&nick=" + Uri.EscapeDataString(MyName())
                    + "&password=" + Uri.EscapeDataString(pass ?? ""));   // 与建房统一用 password（MOD 两个名字都收）
                _settingsLoaded = false;

                if (!ret.StartsWith("ok"))
                {
                    JoinHint.Text = "加入失败：" + ret;
                    StatusText.Text = JoinHint.Text;
                    return;
                }

                if (await WaitInRoomAsync(8000))
                {
                    JoinHint.Text = "";
                    StatusText.Text = "已进入房间 " + Get("room");
                }
                else
                {
                    JoinHint.Text = "没能进入房间：" + WhyFail();
                    StatusText.Text = JoinHint.Text;
                }
            }
            catch (Exception ex) { JoinHint.Text = "加入异常：" + ex.Message; }
            finally { HideBusy(); JoinBtn.IsEnabled = true; }
        }

        async void StartBtn_Click(object s, RoutedEventArgs e)
        {
            StartBtn.IsEnabled = false;
            ShowBusy("正在开始对局…", "把你当前所在的关卡通知给房间里其他玩家");
            try
            {
                string ret = await Cmd("/cmd?name=NetStart");
                await RefreshAsync();
                if (ret.StartsWith("ok"))
                {
                    string lv = "";
                    int i = ret.IndexOf("level=");
                    if (i >= 0) lv = ret.Substring(i + 6);
                    if (lv.Length > 0)
                    {
                        int sl = lv.LastIndexOf('/');
                        string shortName = sl >= 0 ? lv.Substring(sl + 1) : lv;
                        BattleHint.Text = "已开始对局：其他玩家会自动进入你当前关卡（" + shortName + "）。";
                    }
                    else BattleHint.Text = "已开始对局：其他玩家会自动进入你当前所在的关卡。";
                }
                else BattleHint.Text = ret;
            }
            finally { HideBusy(); StartBtn.IsEnabled = true; }
        }

        async void EndBtn_Click(object s, RoutedEventArgs e) { await Cmd("/cmd?name=NetEnd"); await RefreshAsync(); }

        async void CloseBtn_Click(object s, RoutedEventArgs e)
        {
            if (MessageBox.Show(this, "确定解散房间？房间内其他玩家会被移出。", "解散房间",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            await Cmd("/cmd?name=NetClose");
            _settingsLoaded = false;
            await RefreshAsync();
        }

        async void LeaveBtn_Click(object s, RoutedEventArgs e)
        {
            await Cmd("/cmd?name=NetLeave");
            // ★ 离开房间会断开中继连接，房间里的人也都不在了 —— 立刻丢掉旧列表，
            //   否则界面上会残留一个已经不存在的房间（等人重新鉴权后才被刷掉）。
            _rooms.Clear();
            _settingsLoaded = false;
            await RefreshAsync();
        }

        async void ApplySetBtn_Click(object s, RoutedEventArgs e)
        {
            int max = ParseI(SetMaxBox.SelectedItem as string);
            int ttl = ParseI(SetTtlBox.Text.Trim());
            if (max < 2 || max > 8) max = 4;
            if (ttl < 5 || ttl > 1440) ttl = 120;
            // ★ 参数名必须和 MOD 的 BuildNetSettings 对齐：【口令是 password 不是 pass】。
            //   旧代码发 &pass=，而 MOD 只读 qs["password"] → 电脑版设的房间口令从来没生效过。
            //   同时补上 mask（逐项允许作弊白名单），手机版一直有、电脑版从来没发。
            await Cmd("/cmd?name=NetSet&max=" + max + "&ttl=" + ttl
                + "&allowCheats=0"
                + "&late=" + (SetLateCheck.IsChecked == true ? "1" : "0")
                + "&battle=" + (SetBattleCheck.IsChecked == true ? "1" : "0")
                + "&balance=" + (SetBalanceCheck.IsChecked == true ? "1" : "0")
                + "&password=" + Uri.EscapeDataString(SetPassBox.Text.Trim())
                + "&mask=0x" + CheatMaskFromUi().ToString("X8"));
            _settingsLoaded = false;
            await RefreshAsync();
        }

        // ================= 对战分边 =================

        async void JoinPlantBtn_Click(object s, RoutedEventArgs e)
        {
            await Cmd("/cmd?name=NetFaction&f=1");
            await RefreshAsync();
        }

        async void JoinZombieBtn_Click(object s, RoutedEventArgs e)
        {
            await Cmd("/cmd?name=NetFaction&f=2");
            await RefreshAsync();
        }

        /// <summary>刷新对战阵营卡片（非对战模式下整张卡片隐藏）。
        /// 人数平衡的置灰规则与手机版 NetUI、以及中继的仲裁用同一套算法：
        /// 先把“自己”从原阵营摘掉再算，否则切边时自己会被重复计入。</summary>
        void ApplyFaction()
        {
            if (FactionCard == null) return;
            bool battle = Get("battleMode") == "1";
            bool inRoom = Get("inRoom") == "1";
            // ★ 不在房间时才隐藏。原来非对战模式直接 Collapsed —— 玩家勾选框没生效时
            //   整块卡片凭空消失，既看不到阵营也看不到原因，只会觉得"没法选阵营"。
            //   现在只要在房间里就显示，并用文字说明当前处于哪种模式、怎么切过去。
            if (!inRoom)
            {
                FactionCard.Visibility = Visibility.Collapsed;
                return;
            }
            FactionCard.Visibility = Visibility.Visible;

            if (!battle)
            {
                FactionHint.Text = "本房间是【合作模式】—— 双方一起打僵尸，不分阵营。"
                    + (Get("role") == "房主" ? "想改成对战：勾选下面的「对战模式」即可。" : "想改成对战：请让房主勾选「对战模式」。");
                JoinPlantBtn.IsEnabled = false;
                JoinZombieBtn.IsEnabled = false;
                JoinPlantBtn.Content = "加入植物方";
                JoinZombieBtn.Content = "加入僵尸方";
                return;
            }

            int pc = ParseI(Get("plantCount"));
            int zc = ParseI(Get("zombieCount"));
            int mine = ParseI(Get("myFaction"));
            bool started = Get("started") == "1";
            bool balance = Get("balance") == "1";

            string mineName = mine == 1 ? "植物方" : mine == 2 ? "僵尸方" : "未选";
            FactionHint.Text = "植物方 " + pc + " 人 · 僵尸方 " + zc + " 人　你的阵营：" + mineName
                + (started ? "（已开战，阵营锁定）" : "")
                + (balance ? "　人数平衡：开" : "　人数平衡：关");

            bool canPick = !started;
            JoinPlantBtn.IsEnabled = canPick && mine != 1 && CanJoinFaction(1, mine, pc, zc, balance);
            JoinZombieBtn.IsEnabled = canPick && mine != 2 && CanJoinFaction(2, mine, pc, zc, balance);
            JoinPlantBtn.Content = mine == 1 ? "植物方（你）" : "加入植物方";
            JoinZombieBtn.Content = mine == 2 ? "僵尸方（你）" : "加入僵尸方";
        }

        static bool CanJoinFaction(int want, int mine, int pc, int zc, bool balance)
        {
            if (!balance) return true;
            if (mine == 1) pc--;
            else if (mine == 2) zc--;
            int targetAfter = (want == 1 ? pc : zc) + 1;
            int otherAfter = (want == 1 ? zc : pc);
            return targetAfter <= otherAfter + 1;
        }

        // ---- 逐项允许作弊（对齐手机版：手机能设 15 项白名单，电脑版以前只能设 4 项）----
        readonly System.Collections.Generic.Dictionary<uint, CheckBox> _cheatChecks = new System.Collections.Generic.Dictionary<uint, CheckBox>();
        bool _cheatGridBuilt;
        uint _pendingMask = CheatBitsAll;
        const uint CheatBitsAll = 0x7FFFFFFF;   // 全部允许（与 MOD 的 CheatBits.All 一致，够用即可）

        uint CheatMaskFromUi()
        {
            if (_cheatChecks.Count == 0) return CheatBitsAll;
            uint m = 0;
            foreach (var kv in _cheatChecks) if (kv.Value.IsChecked == true) m |= kv.Key;
            return m;
        }

        static uint ParseMask(string s)
        {
            if (string.IsNullOrEmpty(s)) return CheatBitsAll;
            string hv = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s.Substring(2) : s;
            uint m = 0;
            foreach (var ch in hv)
            {
                int d;
                if (ch >= '0' && ch <= '9') d = ch - '0';
                else if (ch >= 'a' && ch <= 'f') d = ch - 'a' + 10;
                else if (ch >= 'A' && ch <= 'F') d = ch - 'A' + 10;
                else return CheatBitsAll;
                m = (m << 4) | (uint)d;
            }
            return m;
        }

        /// <summary>构建逐项允许作弊勾选表。名称由 MOD 的 NetCheatNames 提供，避免两头各维护一份。</summary>
        async Task BuildCheatGridAsync()
        {
            if (_cheatGridBuilt) return;
            string ret = await Cmd("/cmd?name=NetCheatNames");
            int p = ret.IndexOf("names=", StringComparison.Ordinal);
            if (p < 0) { _cheatGridBuilt = true; return; }   // 拿不到就不建（比如老版 DLL）
            foreach (var one in ret.Substring(p + 6).Trim().Split(';'))
            {
                if (one.Length == 0) continue;
                int c = one.IndexOf(':');
                if (c <= 0) continue;
                uint bit;
                if (!uint.TryParse(one.Substring(0, c), out bit)) continue;
                var cb = new CheckBox
                {
                    Content = one.Substring(c + 1),
                    FontSize = 12,
                    Foreground = (Brush)FindResource("TextBrush"),
                    Style = (Style)FindResource("NeoCheck"),
                    Margin = new Thickness(0, 0, 14, 8),
                    IsChecked = (_pendingMask & bit) != 0
                };
                _cheatChecks[bit] = cb;
                SetCheatGrid.Children.Add(cb);
            }
            _cheatGridBuilt = true;
        }

        async void SendChatBtn_Click(object s, RoutedEventArgs e) => await SendChat();

        async void ChatBox_KeyDown(object s, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { e.Handled = true; await SendChat(); }
        }

        async System.Threading.Tasks.Task SendChat()
        {
            string t = ChatBox.Text.Trim();
            if (t.Length == 0) return;
            ChatBox.Text = "";
            await Cmd("/cmd?name=NetChat&text=" + Uri.EscapeDataString(t));
            await RefreshAsync();
        }

        void CopyCodeBtn_Click(object s, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(RoomCodeText.Text);
                StatusText.Text = "邀请码已复制：" + RoomCodeText.Text + "（发给朋友即可加入）";
            }
            catch { StatusText.Text = "复制失败，请手动记录房间号：" + RoomCodeText.Text; }
        }
    }
}
