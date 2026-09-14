using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WF = System.Windows.Forms;

namespace PvzheRemote
{
    public partial class MainWindow : Window
    {
        const string BASE = "http://127.0.0.1:28999";
        static readonly HttpClient _http = CreateHttp();
        static HttpClient CreateHttp()
        {
            var h = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromMilliseconds(2000) };
            h.DefaultRequestHeaders.ConnectionClose = true;   // 每请求独立连接，配合 MOD 端 Connection: close
            return h;
        }

        sealed record Cat(string Key, string Zh, string Icon, string Parent);
        sealed record Feat(string Name, string Zh, string Icon, string Kind,
                           double Min = 0, double Max = 1, double Tick = 0.1, string Suffix = "",
                           string[]? Options = null);

        // ★ Win10 设置式两层导航：6 个父类 + 18 个子页（Key 全部保持原值，
        //   所以 SelectCat 的分派表与 Feats 查表完全不用改）
        static readonly (string Key, string Zh, string Icon)[] Parents =
        {
            ("sys",  "系统",            "\uE770"),
            ("game", "游戏",            "\uE7FC"),
            ("net",  "网络和 Internet", "\uE774"),
            ("app",  "应用",            "\uE71D"),
            ("acc",  "账户",            "\uE77B"),
            ("stor", "存储",            "\uE7B8"),
        };

        static readonly Cat[] Cats =
        {
            // 系统
            new("home",   "首页",     "\uE80F", "sys"),
            new("system", "系统",     "\uE713", "sys"),
            new("log",    "日志",     "\uE9D9", "sys"),
            // 游戏
            new("spawn",  "刷物",     "\uE710", "game"),
            new("money",  "经济",     "\uE8C7", "game"),
            new("battle", "战斗",     "\uE7FC", "game"),
            new("plant",  "种植",     "\uE8D4", "game"),
            new("zombie", "僵尸",     "\uE7EE", "game"),
            new("esp",    "透视",     "\uE7B3", "game"),
            new("fx",     "特效",     "\uE7F1", "game"),
            new("level",  "关卡",     "\uE81E", "game"),
            new("entity", "实体属性", "\uE7C3", "game"),
            new("trick",  "篡改",     "\uE70F", "game"),
            new("custom", "自定义",   "\uE790", "game"),
            // 网络和 Internet
            new("net",    "联机",     "\uE774", "net"),
            // 应用
            new("scripts","脚本",     "\uE943", "app"),
            // 账户
            new("user",   "用户中心", "\uE77B", "acc"),
            // 存储
            new("save",   "存档",     "\uE7B8", "stor"),
        };

        // MOD 全部功能（对照 ModSettings.cs 84 字段）
        static readonly Dictionary<string, Feat[]> Feats = new()
        {
            ["money"] = new Feat[] {
                new("InfiniteSun", "无限阳光", "☀️", "switch"),
                new("InfiniteCoin", "无限金币", "🪙", "switch"),
                new("ZeroCost", "零消费", "🆓", "switch"),
                new("NoCostRise", "卡价不涨价", "💹", "switch"),
                new("CrystalInfinite", "无限水晶", "💎", "switch"),
            },
            ["battle"] = new Feat[] {
                new("PlantInvincible", "植物无敌", "🛡️", "switch"),
                new("ZombieInvincible", "僵尸无敌", "🧟", "switch"),
                new("PlantNoAttack", "植物无法攻击", "⛔", "switch"),
                new("ZombieNoAttack", "僵尸无法攻击", "🚫", "switch"),
                new("ZombieNoMove", "僵尸无法移动", "🐌", "switch"),
                new("CharmPlant", "魅惑植物", "💚", "switch"),
                new("CharmZombie", "魅惑僵尸", "💜", "switch"),
                new("PlantAutoFire", "自动开火", "🔥", "switch"),
                new("PlantFullRange", "全图射程", "🎯", "switch"),
                new("BulletTrack", "子弹追踪", "🎯", "switch"),
                new("BulletFollowMouse", "子弹跟随鼠标", "🖱️", "switch"),
                new("BulletRandom", "随机子弹", "🎲", "switch"),
                new("PlantAttackSpeed", "植物攻速", "⚡", "slider", 1, 1000, 0.5, "×"),
                new("ZombieAttackSpeed", "僵尸攻速", "⚡", "slider", 1, 1000, 0.5, "×"),
                new("PlantHP", "植物血量", "❤️", "slider", 1, 100, 1, "×"),
                new("ZombieHP", "僵尸血量", "❤️", "slider", 1, 100, 1, "×"),
                new("CannonNoCooldown", "炮类无冷却", "🚀", "switch"),
                new("CannonMultiShot", "炮类多弹", "💥", "slider", 1, 100, 1, "×"),
                new("ShovelCherry", "铲子铲出樱桃", "🍒", "switch"),
                new("BloverClearAll", "三叶草吹飞", "☘️", "switch"),
                new("KillAllZombies", "秒杀全部僵尸", "💀", "action"),
                new("KillAllPlants", "秒杀全部植物", "🥀", "action"),
            },
            ["plant"] = new Feat[] {
                new("NoCooldown", "卡片无冷却", "⏱️", "switch"),
                new("PlantOverlap", "植物重叠", "🔀", "switch"),
                new("IgnoreTerrain", "无视地形", "⛰️", "switch"),
                new("IgnorePurple", "无视紫卡", "🟣", "switch"),
                new("PlantColumn", "一列种植", "📐", "switch"),
                new("PlantTriple", "三倍种植", "3️⃣", "switch"),
                new("NoSleep", "蘑菇不睡觉", "😴", "switch"),
                new("CanChooseAll", "全关可选卡", "🃏", "switch"),
                new("UnlockPackets", "清除锁定卡", "🔓", "switch"),
                new("IgnoreRedLine", "无视红线", "🚧", "switch"),
                new("GloveMode", "手套挪动", "🧤", "switch"),
            },
            ["zombie"] = new Feat[] {
                new("NoZombieSpawn", "禁止出僵尸", "🚫", "switch"),
                new("BossNoBow", "僵王不低头", "👑", "switch"),
                new("NoThrowImp", "巨人不投小鬼", "🎈", "switch"),
                new("IgnoreHouse", "无视进家", "🏠", "switch"),
                new("IgnoreWarningLine", "无视警戒线", "⚠️", "switch"),
                new("WavePaused", "波次暂停", "⏸️", "switch"),
                new("SpawnMultiplier", "刷怪倍数", "🐺", "slider", 1, 10, 1, "×"),
                new("BungiFastGrab", "飞贼秒偷", "🪂", "switch"),
                new("BungiIgnoreUmbrella", "无视保护伞", "☂️", "switch"),
                new("JackboxFastBomb", "小丑秒炸", "🤡", "switch"),
                new("BungiSpawnJackbox", "飞贼生小丑", "🃏", "switch"),
                new("ZombiesFollowMouse", "僵尸吸附鼠标", "🧲", "switch"),
            },
            ["esp"] = new Feat[] {
                new("ESPEnabled", "透视总开关", "👁️", "switch"),
                new("ESPZombie", "僵尸透视", "🧟", "switch"),
                new("ESPPlant", "植物透视", "🌿", "switch"),
                new("VaseESP", "罐子透视", "🏺", "switch"),
                new("FogESP", "迷雾透视", "🌫️", "switch"),
                new("AlmanacAll", "图鉴全解", "📖", "switch"),
                new("UnlockAllSkins", "皮肤全解", "🎨", "action"),
                new("ZombieScale", "僵尸大小", "📏", "slider", 0.5, 3, 0.1, "×"),
                new("PlantScale", "植物大小", "📏", "slider", 0.5, 3, 0.1, "×"),
            },
            ["fx"] = new Feat[] {
                new("Enabled", "彩虹渐变", "🌈", "switch"),
                new("Style", "彩虹风格", "🎨", "choice", Options: new[] { "0:彩虹", "1:红金", "2:蓝紫", "3:霓虹" }),
                new("Speed", "渐变速度", "🌊", "slider", 0.1, 1, 0.05, ""),
                new("TextColorMode", "文字颜色", "🔤", "choice", Options: new[] { "0:彩虹律动", "1:纯白", "2:纯黑" }),
                new("FunEnabled", "趣味弹幕", "💬", "switch"),
                new("BGEnabled", "背景氛围", "🖼️", "switch"),
                new("DanmakuSpeed", "弹幕速度", "🚀", "slider", 0.5, 3, 0.1, "×"),
                new("ZombieColor", "僵尸变色", "🌈", "switch"),
                new("ZombieDance", "僵尸跳舞", "💃", "switch"),
                new("PlantSquash", "植物压扁", "🫓", "switch"),
                new("JellyMode", "果冻模式", "🍮", "switch"),
                new("CustomMessages", "自定义弹幕", "✏️", "text"),
            },
            ["level"] = new Feat[] {
                new("ForceRain", "强制种子雨", "🌧️", "switch"),
                new("ForceFog", "强制迷雾", "🌫️", "switch"),
                new("RainFast", "种子雨加速", "⚡", "switch"),
                new("ConveyorFast", "传送带加速", "🛷", "switch"),
                new("ClearCrater", "清除弹坑", "🕳️", "switch"),
                new("VaseRandom", "罐子随机", "🏺", "switch"),
                new("SeedBankRandom", "卡槽随机", "🃏", "switch"),
                new("SeedBankEveryFrame", "卡槽1帧1刷", "⚡", "switch"),
                new("SeedBankRandomZombie", "卡槽随机成僵尸", "🧟", "switch"),
                new("ChomperFastSwallow", "大嘴花秒吞", "😋", "switch"),
                new("CustomLevelActive", "自制关卡", "🎰", "switch"),
                new("PerfMode", "性能模式", "🚀", "switch"),
                new("GlobalOverridesEnabled", "全局属性覆盖", "📋", "switch"),
                new("BulletType", "自定义子弹", "🧨", "bullet"),
                new("InstantWinAll", "当前关卡通关", "🏁", "action"),
                new("CompleteAllLevels", "一键通关全部", "👑", "action"),
                new("CompleteAllDailyLevels", "一键通关每日挑战", "📅", "action"),
                new("InstantGrow", "成长植物秒熟", "🌱", "switch"),
            },
            ["system"] = new Feat[] {
                new("EngineLowRes", "低配渲染(1280宽)", "🖥️", "switch"),
                new("ApplyEnginePerf", "立即应用渲染优化", "⚡", "action"),
                new("GameSpeed", "游戏速度", "🎚️", "slider", 0.5, 20, 0.5, "x"),
                new("LaunchMowers", "启动全部小推车", "🚜", "action"),
                new("RestoreMowers", "恢复小推车", "🔧", "action"),
                new("ConsoleEnabled", "游戏控制台", "⌨️", "switch"),
                new("OpenConsole", "打开控制台", "⌨️", "action"),
                new("SaveSettings", "保存设置", "💾", "action"),
                new("DebugMode", "调试模式", "🐞", "switch"),
            },
            ["custom"] = new Feat[] {
                new("OpenCustomPlant", "自定义植物设计器", "🧬", "action"),
                new("CustomPlantSync", "一键注入全部", "📡", "action"),
            },
            ["trick"] = new Feat[] {
                new("TrickEnabled", "篡改总开关", "🎛️", "switch"),
                new("TrickBox", "篡改礼盒盲盒", "🎁", "switch"),
                new("TrickRain", "篡改种子雨", "🌧️", "switch"),
                new("TrickConveyor", "篡改传送带", "🛷", "switch"),
                new("TrickFixedOnly", "固定第1张卡", "📌", "switch"),
                new("TrickPoolEditor", "选择目标卡池", "🎯", "action"),
            },
            ["scripts"] = Array.Empty<Feat>(),   // 脚本分类：启用脚本动态注入
        };

        readonly Dictionary<string, ToggleSwitch> _switches = new();
        readonly List<string> _logLines = new();
        readonly DateTime _startTime = DateTime.Now;
        Dictionary<string, object> _settings = new();
        static MainWindow? _inst;
        string _activeCat = "home";
        bool _connected;
        bool _everSynced;
        bool _busy;
        bool _sliderOpen;
        int _pollFailStreak;
        TextBlock? _userUptime;
        WF.NotifyIcon? _tray;
        readonly DispatcherTimer _autoPoll;
        readonly DispatcherTimer _uptime;
        Window? _featureTip;
        DispatcherTimer? _tipTimer;
        DispatcherTimer? _tipColorTimer;
        double _tipHue;
        double _tipOpacity = 1;
        bool _debugMode;
        Window? _debugWin;
        TextBlock? _debugTb;
        ScrollViewer? _debugScroll;
        int _debugCount;
        static Action<string>? _debugSink;   // GetAsync(静态) 记录详细日志到调试终端
        SaveResource? _saveRes;
        Dictionary<object, object>? _saveUser;
        string _savePath = "";
        string _saveCat = "cards";
        readonly Dictionary<string, CheckBox> _saveChecks = new();
        TextBlock? _saveStats;
        TextBox _saveFilterBox = null!;
        WrapPanel _saveList = null!;
        DispatcherTimer? _saveFilterTimer;
        ComboBox? _saveUserCombo;
        ComboBox? _saveCurKey;

        public MainWindow()
        {
            InitializeComponent();
            _inst = this;
            ScriptStore.Init();
            LoadWallpaper();       // 背景图（exe 同目录 wallpaper.*，没有就跳过）
            BuildCats();
            ShowHome();

            SourceInitialized += (s, e) =>
            {
                // 新拟物主题：实色浅灰底，不再启用 DWM 毛玻璃
                HotKeyHelper.Register(this, Key.F8, () => Dispatcher.Invoke(ToggleVisibility));
            };
            BuildTray();
            Closed += (s, e) => { HotKeyHelper.Unregister(this); _tray?.Dispose(); };

            _uptime = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _uptime.Tick += (s, e) => { if (_userUptime != null) _userUptime.Text = "已运行 " + FormatUptime(); };
            _uptime.Start();

            _autoPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _autoPoll.Tick += async (s, e) => await OnAutoPoll();
            _autoPoll.Start();
            _debugSink = DebugLog;   // 静态 HTTP 层日志 → 实例调试终端
        }

        // ================= 分类方块 =================
        // ================= 分类导航（Win10 设置式两层） =================
        /// <summary>当前展开的父类；空 = 显示 6 个父类列表。</summary>
        string _openParent = "sys";

        void BuildCats()
        {
            LoadBackground();   // 幂等兜底：不依赖 XAML Loaded 事件也能加载背景图
            CatPanel.Children.Clear();
            if (_openParent.Length == 0)
            {
                foreach (var p in Parents)
                {
                    var b = NavButton(p.Zh, p.Icon, "\uE76C");
                    string pk = p.Key;
                    b.Click += (s, e) => OpenParent(pk);
                    CatPanel.Children.Add(b);
                }
            }
            else
            {
                var par = System.Array.Find(Parents, x => x.Key == _openParent);
                var back = NavButton(par.Zh, "", "\uE72B");
                back.Click += (s, e) => { _openParent = ""; BuildCats(); };
                CatPanel.Children.Add(back);

                foreach (var c in System.Array.FindAll(Cats, x => x.Parent == _openParent))
                {
                    var b = NavButton(c.Zh, c.Icon, "");
                    b.DataContext = c.Key;                  // ★ key 存 DataContext
                    b.Margin = new Thickness(14, 0, 0, 0);   // 子页缩进（对齐 Win10）
                    string k = c.Key;
                    b.Click += (s, e) => { HighlightCat(k); SelectCat(k); };
                    CatPanel.Children.Add(b);
                }
            }
            HighlightCat(_activeCat);
        }

        /// <summary>造一个 Win10 导航项（左图标 30px + 文字 + 右侧箭头）。</summary>
        Button NavButton(string zh, string icon, string trail)
        {
            var b = new Button { Style = (Style)FindResource("NavItem") };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var ic = new TextBlock
            {
                Text = icon, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 14,
                Foreground = (Brush)FindResource("TextBrush"), VerticalAlignment = VerticalAlignment.Center,
            };
            var tx = new TextBlock
            {
                Text = zh, FontFamily = (FontFamily)FindResource("UiFont"), FontSize = 14,
                Foreground = (Brush)FindResource("TextBrush"), VerticalAlignment = VerticalAlignment.Center,
            };
            var tr = new TextBlock
            {
                Text = trail, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11,
                Foreground = (Brush)FindResource("SubBrush"), VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(ic, 0); Grid.SetColumn(tx, 1); Grid.SetColumn(tr, 2);
            g.Children.Add(ic); g.Children.Add(tx); g.Children.Add(tr);
            b.Content = g;
            return b;
        }

        void OpenParent(string key)
        {
            _openParent = key;
            BuildCats();
            ShowParentOverview(key);
        }

        /// <summary>父类总览：把该父类下所有子页做成卡片，点卡片直接进页。</summary>
        void ShowParentOverview(string parentKey)
        {
            ContentPanel.Children.Clear();
            var par = System.Array.Find(Parents, x => x.Key == parentKey);
            if (PageTitle != null) PageTitle.Text = par.Zh;
            var wrap = new WrapPanel();
            foreach (var c in System.Array.FindAll(Cats, x => x.Parent == parentKey))
            {
                var card = new Border
                {
                    Style = (Style)FindResource("SettingsCard"),
                    Width = 210, Margin = new Thickness(0, 0, 10, 10),
                };
                var sp = new StackPanel();
                var head = new StackPanel { Orientation = Orientation.Horizontal };
                head.Children.Add(new TextBlock
                {
                    Text = c.Icon, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 16,
                    Foreground = (Brush)FindResource("AccentBrush"), VerticalAlignment = VerticalAlignment.Center,
                });
                head.Children.Add(new TextBlock
                {
                    Text = c.Zh, FontFamily = (FontFamily)FindResource("UiFont"), FontSize = 14,
                    Foreground = (Brush)FindResource("TextBrush"), Margin = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                sp.Children.Add(head);
                sp.Children.Add(new TextBlock
                {
                    Text = "打开设置", FontFamily = (FontFamily)FindResource("UiFont"),
                    FontSize = 11, Foreground = (Brush)FindResource("SubBrush"), Margin = new Thickness(0, 6, 0, 0),
                });
                card.Child = sp;
                string k = c.Key;
                card.MouseLeftButtonUp += (s, e) => { HighlightCat(k); SelectCat(k); };
                wrap.Children.Add(card);
            }
            ContentPanel.Children.Add(wrap);
        }

        void HighlightCat(string key)
        {
            _activeCat = key;
            foreach (var child in CatPanel.Children)
            {
                if (child is Button b)
                    b.Tag = ((b.DataContext as string) == key) ? "sel" : "";   // 模板据此显示竖条+灰底
            }
            var name = System.Array.Find(Cats, c => c.Key == key);
            if (name != null && PageTitle != null) PageTitle.Text = name.Zh;
        }

        // 搜索框：在 18 个子页里按中文名过滤（Win10 那个「查找设置」）
        void SearchInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            try
            {
                string q = (SearchInput.Text ?? "").Trim();
                if (q.Length == 0) { BuildCats(); return; }
                CatPanel.Children.Clear();
                foreach (var c in System.Array.FindAll(Cats,
                    x => x.Zh.IndexOf(q, System.StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    var b = NavButton(c.Zh, c.Icon, "");
                    b.DataContext = c.Key;
                    b.Margin = new Thickness(14, 0, 0, 0);
                    string k = c.Key; string pk = c.Parent;
                    b.Click += (s, ev) => { _openParent = pk; HighlightCat(k); SelectCat(k); BuildCats(); };
                    CatPanel.Children.Add(b);
                }
                if (CatPanel.Children.Count == 0)
                    CatPanel.Children.Add(new TextBlock
                    {
                        Text = "没有找到匹配的设置", Foreground = (Brush)FindResource("SubBrush"),
                        FontFamily = (FontFamily)FindResource("UiFont"), FontSize = 12, Margin = new Thickness(16, 10, 0, 0),
                    });
            }
            catch { }
        }

        void OnWindowLoaded(object sender, RoutedEventArgs e) { LoadBackground(); }

        /// <summary>背景图：exe 同目录（或 exe\bg\ 子目录）下的 bg.png/jpg/jpeg/webp，没有就不显示。
        /// ★ 这样换图只要替换文件，不用重新编译。</summary>
        static bool _bgLoaded;

        /// <summary>播放背景视频（静音、循环、铺满）。★ LoadedBehavior=Manual 时播完会停，
        /// 必须自己处理 MediaEnded 才会循环。</summary>
        void PlayBgVideo(string path)
        {
            try
            {
                BgVideo.MediaEnded -= OnBgVideoEnded;
                BgVideo.MediaEnded += OnBgVideoEnded;
                BgVideo.MediaFailed -= OnBgVideoFailed;
                BgVideo.MediaFailed += OnBgVideoFailed;
                BgVideo.Source = new Uri(path);
                BgVideo.Visibility = Visibility.Visible;
                BgVideo.Play();
                if (Wallpaper != null) Wallpaper.Source = null;   // 视频优先，清掉静态图
                if (_bgTimer != null) { _bgTimer.Stop(); _bgTimer = null; }   // 停掉图片轮播
                DebugLog("背景视频(循环): " + System.IO.Path.GetFileName(path));
            }
            catch (Exception ex) { DebugLog("背景视频失败: " + ex.Message); }
        }

        void OnBgVideoEnded(object sender, RoutedEventArgs e)
        {
            try { BgVideo.Position = TimeSpan.Zero; BgVideo.Play(); } catch { }
        }

        void OnBgVideoFailed(object sender, System.Windows.ExceptionRoutedEventArgs e)
        {
            DebugLog("背景视频播放失败: " + (e?.ErrorException?.Message ?? "未知"));
        }

        /// <summary>停掉视频（换成静态图时要调，否则视频盖着图片看不见）。</summary>
        void StopBgVideo()
        {
            try { BgVideo.Stop(); BgVideo.Source = null; BgVideo.Visibility = Visibility.Collapsed; } catch { }
        }
        readonly System.Collections.Generic.List<string> _bgPics = new System.Collections.Generic.List<string>();
        int _bgAt;
        System.Windows.Threading.DispatcherTimer _bgTimer;

        /// <summary>背景（按优先级）：
        ///   ① bg\ 里有 .mp4 → 播视频（真·律动，跟 Wallpaper Engine 一样）
        ///   ② bg\ 里有图片 → 每 8 秒淡入淡出轮播 + 缓慢放大（律动）
        ///   ③ exe 同目录的单个 bg.png/bg.jpg
        /// ★ webm 播不了（WPF MediaElement 走系统解码器，只认 mp4）；图片 jpg/png/webp 都行。
        /// ★ 换素材只要增删 bg\ 里的文件，不用重新编译。</summary>
        void LoadBackground()
        {
            if (_bgLoaded) return;      // 幂等：BuildCats 与 Loaded 谁先调到都行
            _bgLoaded = true;
            try
            {
                if (BgImage == null) return;
                string baseDir = AppContext.BaseDirectory;
                string dir = System.IO.Path.Combine(baseDir, "bg");
                var all = System.IO.Directory.Exists(dir)
                    ? System.IO.Directory.GetFiles(dir) : new string[0];
                System.Array.Sort(all);

                // ① exe 同目录的 wallpaper.mp4（用「选择背景图…」选的视频）
                string wpVid = System.IO.Path.Combine(baseDir, "wallpaper.mp4");
                if (System.IO.File.Exists(wpVid)) { PlayBgVideo(wpVid); return; }
                // ② bg\ 里的 mp4
                foreach (var f in all)
                    if (System.IO.Path.GetExtension(f).ToLowerInvariant() == ".mp4")
                    {
                        PlayBgVideo(f);
                        return;
                    }

                foreach (var f in all)
                {
                    string e = System.IO.Path.GetExtension(f).ToLowerInvariant();
                    if (e == ".jpg" || e == ".jpeg" || e == ".png" || e == ".webp") _bgPics.Add(f);
                }
                if (_bgPics.Count == 0)
                {
                    string p = System.IO.Path.Combine(baseDir, "bg.png");
                    if (!System.IO.File.Exists(p)) p = System.IO.Path.Combine(baseDir, "bg.jpg");
                    if (System.IO.File.Exists(p)) _bgPics.Add(p);
                }
                if (_bgPics.Count == 0) { DebugLog("背景：bg\\ 里没有图片或视频"); return; }

                ShowBgAt(0);
                DebugLog("背景轮播: " + _bgPics.Count + " 张");
                if (_bgPics.Count > 1)
                {
                    _bgTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
                    _bgTimer.Tick += (s, e) =>
                    {
                        _bgAt = (_bgAt + 1) % _bgPics.Count;
                        var fade = new DoubleAnimation(0.85, 0, TimeSpan.FromMilliseconds(450));
                        fade.Completed += (s2, e2) =>
                        {
                            ShowBgAt(_bgAt);
                            BgImage.BeginAnimation(UIElement.OpacityProperty,
                                new DoubleAnimation(0, 0.85, TimeSpan.FromMilliseconds(650)));
                        };
                        BgImage.BeginAnimation(UIElement.OpacityProperty, fade);
                    };
                    _bgTimer.Start();
                }
            }
            catch (Exception ex) { DebugLog("背景加载失败: " + ex.Message); }
        }

        /// <summary>换到第 i 张图，并给它一点缓慢放大的「呼吸感」。</summary>
        void ShowBgAt(int i)
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(_bgPics[i]);
            bmp.EndInit();
            BgImage.Source = bmp;
            var sc = new ScaleTransform(1, 1);
            BgImage.RenderTransform = sc;
            BgImage.RenderTransformOrigin = new Point(0.5, 0.5);
            sc.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, 1.06, TimeSpan.FromSeconds(8)));
            sc.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, 1.06, TimeSpan.FromSeconds(8)));
        }

        void SelectCat(string key)
        {
            SliderPanel.Visibility = Visibility.Collapsed;
            _sliderOpen = false;
            if (key == "home") ShowHome();
            else if (key == "spawn") ShowSpawn();
            else if (key == "net") ShowNet();
            else if (key == "save") ShowSave();
            else if (key == "entity") ShowEntities();
            else if (key == "user") ShowUserCenter();
            else if (key == "log") ShowLog();
            else ShowFeatures(key);
        }

        void AnimateIn(FrameworkElement el)
        {
            el.Opacity = 0;
            var tt = new TranslateTransform { Y = 12 };
            el.RenderTransform = tt;
            var dur = TimeSpan.FromMilliseconds(260);
            el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, dur) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        // ================= 背景图 =================
        /// <summary>加载 exe 同目录的 wallpaper.*（没有就保持白底，不报错）。
        /// 建议用横图 / 躺姿构图 —— 图层是 UniformToFill，会铺满并裁切。</summary>
        void LoadWallpaper()
        {
            try
            {
                // ★ 有 wallpaper.mp4 时视频优先：否则静态图会被设进 Wallpaper 图层，把视频盖住
                if (System.IO.File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, "wallpaper.mp4"))) return;
                foreach (var ext in new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp" })
                {
                    string p = System.IO.Path.Combine(AppContext.BaseDirectory, "wallpaper" + ext);
                    if (!System.IO.File.Exists(p)) continue;
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(p);
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    Wallpaper.Source = bmp;
                    DebugLog("背景图: " + p);
                    return;
                }
            }
            catch (Exception ex) { DebugLog("背景图加载失败: " + ex.Message); }
        }

        /// <summary>让用户挑一张背景（图片或视频）→ 复制到 exe 同目录 → 立即生效。</summary>
        void PickWallpaper()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择背景（图片或视频）",
                Filter = "图片或视频|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.mp4"
                       + "|图片|*.jpg;*.jpeg;*.png;*.webp;*.bmp"
                       + "|视频 (mp4)|*.mp4",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                string ext = System.IO.Path.GetExtension(dlg.FileName).ToLowerInvariant();
                string baseDir = AppContext.BaseDirectory;

                // ★ 视频：存成 wallpaper.mp4 → 走 PlayBgVideo（循环）
                if (ext == ".mp4")
                {
                    StopBgVideo();
                    string vdst = System.IO.Path.Combine(baseDir, "wallpaper.mp4");
                    System.IO.File.Copy(dlg.FileName, vdst, true);
                    PlayBgVideo(vdst);
                    MessageBox.Show("背景视频已设置（循环播放）。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (ext == ".webm")
                {
                    MessageBox.Show("webm 播不了：WPF 的视频播放走系统解码器，只支持 mp4。\n请先转成 mp4。", "不支持", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 图片：先停掉视频，否则视频会盖着图片
                StopBgVideo();
                // 先删掉别的扩展名，避免多个 wallpaper.* 抢着加载
                foreach (var e2 in new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp" })
                {
                    string old = System.IO.Path.Combine(baseDir, "wallpaper" + e2);
                    if (System.IO.File.Exists(old)) System.IO.File.Delete(old);
                }
                System.IO.File.Copy(dlg.FileName, System.IO.Path.Combine(baseDir, "wallpaper" + ext), true);
                LoadWallpaper();
                MessageBox.Show("背景图已设置。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { MessageBox.Show("设置背景失败: " + ex.Message, "错误"); }
        }

        // ================= 首页 =================
        void ShowHome()
        {
            ContentPanel.Children.Clear();

            var card = MakePanel();
            var sp = new StackPanel();
            sp.Children.Add(MakeText("杂交版 外置修改器", 22, FontWeights.Bold, (Brush)FindResource("TextBrush")));
            sp.Children.Add(MakeText("全部 MOD 功能桌面一键控制。", 12, FontWeights.Normal, (Brush)FindResource("SubBrush"), new Thickness(0, 8, 0, 12)));
            var wpRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            var wpBtn = MakeMiniBtn("选择背景图…");
            wpBtn.Click += (s, e) => PickWallpaper();
            wpRow.Children.Add(wpBtn);
            sp.Children.Add(wpRow);
            var rows = new[]
            {
                ("1 · 连接", "启动游戏并注入 MOD 后，点右上角 ⚡ 检测，状态灯变绿即已连接。"),
                ("2 · 使用", "左侧分类进入，功能用方块展示；点击方块右下角 开关 / 调节 / 执行。"),
                ("3 · 调节", "数值类点「调节」会展开彩色律动滑条，拖动即时生效。"),
                ("4 · 退出", "点右上角 ✕ 立即退出程序，不残留后台；Alt+F8 可临时收纳到托盘。"),
            };
            foreach (var (k, v) in rows)
            {
                var r = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 6) };
                r.Children.Add(MakeText(k, 12, FontWeights.Bold, (Brush)FindResource("AccentBrush"), new Thickness(0, 0, 12, 0)));
                r.Children.Add(MakeText(v, 12, FontWeights.Normal, (Brush)FindResource("TextBrush"), new Thickness(0, 0, 0, 0), true));
                sp.Children.Add(r);
            }
            sp.Children.Add(MakeText("本工具只连接本机 127.0.0.1 的 MOD 监听端口，数据不外传。", 10, FontWeights.Normal, (Brush)FindResource("SubBrush"), new Thickness(0, 16, 0, 0)));
            card.Child = sp;
            ContentPanel.Children.Add(card);
            AnimateIn(card);

            // 调试模式（首页快捷入口）
            var debug = MakePanel();
            debug.Margin = new Thickness(0, 12, 0, 0);
            var dsp = new StackPanel();
            var dhead = new StackPanel { Orientation = Orientation.Horizontal };
            dhead.Children.Add(MakeText("调试模式", 14, FontWeights.Bold, (Brush)FindResource("AccentBrush")));
            var dsw = new ToggleSwitch { IsOn = _debugMode, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            dsw.Command = on => _ = SetBool("DebugMode", "调试模式", on);
            dhead.Children.Add(dsw);
            dsp.Children.Add(dhead);
            dsp.Children.Add(MakeText("开启后右下角弹出调试终端，实时记录所有请求/响应/耗时，排查功能不生效、开关还原等问题。", 11, FontWeights.Normal, (Brush)FindResource("SubBrush"), new Thickness(0, 6, 0, 0), true));
            debug.Child = dsp;
            ContentPanel.Children.Add(debug);
            AnimateIn(debug);
        }

        // ================= 功能方块 =================
        void ShowFeatures(string catKey)
        {
            ContentPanel.Children.Clear();
            DebugLog("进入分类 [" + catKey + "]，当前镜像 " + _settings.Count + " 项");
            var sp = new StackPanel();
            // ★ Win10 设置：一列「行」（不是 150×150 方块网格），整组包在一张卡片里
            var wp = new StackPanel();
            if (Feats.TryGetValue(catKey, out var feats))
                foreach (var f in feats)
                    wp.Children.Add(MakeFeatTile(f));
            // 脚本分类：顶部管理入口 + 启用脚本方块
            if (catKey == "scripts")
            {
                var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
                var nb = MakeMiniBtn("新建脚本");
                nb.Margin = new Thickness(0, 0, 10, 0);
                nb.Click += (s, e) => ScriptWindow.ShowWindow();
                head.Children.Add(nb);
                var mgr = MakeMiniBtn("脚本管理");
                mgr.Margin = new Thickness(0, 0, 10, 0);
                mgr.Click += (s, e) => ScriptWindow.ShowWindow();
                head.Children.Add(mgr);
                var docs = MakeMiniBtn("开发文档");
                docs.Click += OnOpenApiDocs;
                head.Children.Add(docs);
                sp.Children.Add(head);
            }
            // 注入该分类下启用脚本（scripts 分类 + 可选其它分类）
            foreach (var d in ScriptStore.List)
                if (d.Enabled && d.Cat == catKey)
                    wp.Children.Add(MakeScriptTile(d));
            if (wp.Children.Count > 0)
            {
                var card = MakePanel();
                card.Padding = new Thickness(18, 4, 18, 4);
                card.Child = wp;
                sp.Children.Add(card);
            }
            ContentPanel.Children.Add(sp);
            AnimateIn(sp);
            // 后台刷新 MOD 状态镜像（不阻塞本次渲染，供下次进分类读取）
            _ = RefreshSettingsMirrorAsync();
        }

        // 进入功能分类前从 MOD 拉一次最新状态（只更新镜像，不 SetSilently 干扰用户正在操作的开关）
        async Task RefreshSettingsMirrorAsync()
        {
            var body = await GetAsync("/get");
            if (body == null || !body.StartsWith("{")) return;
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                var map = new Dictionary<string, object>();
                foreach (var p in root.EnumerateObject())
                {
                    map[p.Name] = p.Value.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Number when p.Value.TryGetInt32(out var i) => (object)i,
                        JsonValueKind.Number when p.Value.TryGetDouble(out var dbl) => dbl,
                        _ => p.Value.ToString(),
                    };
                }
                _settings = map;
                var dbg = new StringBuilder("镜像刷新 " + map.Count + " 项:");
                foreach (var kv in map)
                    if (kv.Value is bool) dbg.Append(' ').Append(kv.Key).Append('=').Append(kv.Value);
                DebugLog(dbg.ToString());
            }
            catch { }
        }

        /// <summary>Win10 设置行：左名称（无图标 —— Win10 设置的行本来就没图标），
        /// 右侧控件（开关/按钮）；行间 1px 细线。开关/选择类整行可点。</summary>
        Border MakeFeatTile(Feat f)
        {
            var row = new Border
            {
                Background = System.Windows.Media.Brushes.Transparent,
                BorderBrush = (Brush)FindResource("LineBrush"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 12, 0, 12),
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = MakeText(f.Zh, 14, FontWeights.Normal, (Brush)FindResource("TextBrush"));
            title.VerticalAlignment = VerticalAlignment.Center;
            title.TextWrapping = TextWrapping.Wrap;
            Grid.SetColumn(title, 0);
            grid.Children.Add(title);

            if (f.Kind == "switch")
            {
                bool init = GetSettingBool(f.Name);
                var sw = new ToggleSwitch { VerticalAlignment = VerticalAlignment.Center, IsOn = init };
                sw.Command = on => _ = SetBool(f.Name, f.Zh, on);
                _switches[f.Name] = sw;
                Grid.SetColumn(sw, 1);
                grid.Children.Add(sw);
                DebugLog("开关[" + f.Zh + "] 初始=" + init + "（镜像含该字段? " + _settings.ContainsKey(f.Name) + "）");
                // 只由开关自身控制：不注册行点击，避免 mouseup 落在行上时误翻转
            }
            else if (f.Kind == "action")
            {
                var btn = MakeMiniBtn(f.Zh);
                btn.Click += (s, e) => _ = OnAction(f.Name, f.Zh);
                Grid.SetColumn(btn, 1);
                grid.Children.Add(btn);
            }
            else
            {
                var btn = MakeMiniBtn(f.Kind == "slider" ? "调节" : (f.Kind == "choice" ? "选择" : (f.Kind == "bullet" ? "子弹" : "编辑")));
                btn.Click += (s, e) => OpenPanel(f);
                Grid.SetColumn(btn, 1);
                grid.Children.Add(btn);
                // 整行可点（Win10 的整行命中区）
                row.Cursor = System.Windows.Input.Cursors.Hand;
                row.MouseLeftButtonUp += (s, e) => OpenPanel(f);
            }
            row.Child = grid;
            return row;
        }

        Button MakeMiniBtn(string text) => new Button
        {
            Content = text,
            Style = (Style)FindResource("ActionButton"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        // ================= 脚本方块 =================

        /// <summary>Win10 设置行：脚本名 + 说明，右侧「执行」。</summary>
        Border MakeScriptTile(ScriptDef d)
        {
            var row = new Border
            {
                Background = System.Windows.Media.Brushes.Transparent,
                BorderBrush = (Brush)FindResource("LineBrush"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 12, 0, 12),
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(MakeText(d.Name, 14, FontWeights.Normal, (Brush)FindResource("TextBrush")));
            if (!string.IsNullOrWhiteSpace(d.Desc))
                left.Children.Add(MakeText(d.Desc, 12, FontWeights.Normal, (Brush)FindResource("SubBrush"), new Thickness(0, 3, 0, 0), true));
            Grid.SetColumn(left, 0);
            grid.Children.Add(left);

            var run = MakeMiniBtn("执行");
            run.Click += (s, e) => _ = RunScriptAsync(d);
            Grid.SetColumn(run, 1);
            grid.Children.Add(run);
            row.Child = grid;
            return row;
        }

        async Task RunScriptAsync(ScriptDef d)
        {
            try
            {
                var code = ScriptStore.ReadCode(d);
                DebugLog("执行脚本 [" + d.Name + "]…");
                var res = await Task.Run(() => ScriptEngine.RunAsync(d, code));
                DebugLog("脚本 [" + d.Name + "] 结果: " + res);
                if (res != null && (res.StartsWith("编译失败") || res.StartsWith("运行失败") || res.StartsWith("err")))
                    MessageBox.Show(res, "脚本 [" + d.Name + "]", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex) { DebugLog("脚本异常: " + ex.Message); }
        }

        /// <summary>打开本地开发文档（api/index.html，默认浏览器）。</summary>
        void OnOpenApiDocs(object s, RoutedEventArgs e)
        {
            try
            {
                var p = Path.Combine(AppContext.BaseDirectory, "api", "index.html");
                if (!File.Exists(p)) { MessageBox.Show("未找到开发文档：\n" + p, "提示", MessageBoxButton.OK, MessageBoxImage.Information); return; }
                Process.Start(new ProcessStartInfo { FileName = p, UseShellExecute = true });
            }
            catch (Exception ex) { MessageBox.Show("打开文档失败: " + ex.Message); }
        }

        /// <summary>脚本列表/内容变化后刷新（供 ScriptWindow 调用；当前在脚本分类则重渲染）。</summary>
        public static void ReloadScriptFeatures()
        {
            var inst = _inst;
            if (inst == null) return;
            inst.Dispatcher.Invoke(() =>
            {
                if (inst._activeCat == "scripts") inst.ShowFeatures("scripts");
            });
        }

        // ================= 滑块 / 文本面板 =================
        void OpenPanel(Feat f)
        {
            SliderTitle.Text = f.Zh;
            SliderSub.Text = f.Kind == "slider" ? "拖动下方彩色律动滑条调节" : (f.Kind == "choice" ? "选择一个选项，点击即生效" : (f.Kind == "bullet" ? "选择一种子弹类型，点击即生效" : "输入内容后点确定"));
            SliderBody.Children.Clear();
            if (f.Kind == "slider")
            {
                double cur = 1.0;
                if (_settings.TryGetValue(f.Name, out var raw))
                {
                    try { cur = Convert.ToDouble(raw, CultureInfo.InvariantCulture); }
                    catch { }
                }
                var rs = new RainbowSlider { VerticalAlignment = VerticalAlignment.Center };
                rs.SetRange(f.Min, f.Max, Math.Clamp(cur, f.Min, f.Max), f.Tick);
                rs.Suffix = f.Suffix;
                rs.ValueCommitted += async (s, e) => await SetField(f.Name, f.Zh, rs.Value);
                SliderBody.Children.Add(rs);
            }
            else if (f.Kind == "bullet")
            {
                SliderSub.Text = "选择一种子弹类型（「默认」恢复原版子弹）";
                var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 170 };
                var wp = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
                scroll.Content = wp;
                SliderBody.Children.Add(scroll);
                _sliderOpen = true;
                SliderPanel.Visibility = Visibility.Visible;
                _ = LoadBulletOptions(f.Name, f.Zh, wp);
                return;
            }
            else if (f.Kind == "choice")
            {
                var wp = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
                if (f.Options != null)
                    foreach (var opt in f.Options)
                    {
                        int i = opt.IndexOf(':');
                        string val = i >= 0 ? opt.Substring(0, i) : opt;
                        string show = i >= 0 ? opt.Substring(i + 1) : opt;
                        var b = MakeMiniBtn(show);
                        b.Margin = new Thickness(0, 0, 10, 6);
                        b.Click += async (s, e) =>
                        {
                            await SetField(f.Name, f.Zh, val);
                            SliderPanel.Visibility = Visibility.Collapsed;
                            _sliderOpen = false;
                        };
                        wp.Children.Add(b);
                    }
                SliderBody.Children.Add(wp);
            }
            else // text
            {
                string cur = "";
                if (_settings.TryGetValue(f.Name, out var raw)) cur = Convert.ToString(raw) ?? "";
                var box = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var tb = new TextBox
                {
                    Text = cur, Width = 320, FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 12,
                    Padding = new Thickness(8, 4, 8, 4), VerticalContentAlignment = VerticalAlignment.Center,
                };
                var ok = MakeMiniBtn("确定");
                ok.Margin = new Thickness(10, 0, 0, 0);
                ok.Click += async (s, e) => await SetField(f.Name, f.Zh, tb.Text);
                box.Children.Add(tb); box.Children.Add(ok);
                SliderBody.Children.Add(box);
            }
            _sliderOpen = true;
            SliderPanel.Visibility = Visibility.Visible;
        }

        // ================= 刷物 =================
        static readonly (string Id, string Zh, string Icon)[] CommonPlants =
        {
            ("PlantSunFlower", "向日葵", "🌻"), ("PlantPeaShooterSingle", "豌豆射手", "🫛"),
            ("PlantCherryBomb", "樱桃炸弹", "🍒"), ("PlantWallnut", "坚果", "🥜"),
            ("PlantSunShroom", "阳光菇", "☀️"), ("PlantPumpkin", "南瓜", "🎃"),
            ("PlantPot", "花盆", "🪴"), ("PlantLilyPad", "荷叶", "🪷"),
            ("PlantGatlingPea", "加特林", "🔫"), ("PlantDoomShroom", "毁灭菇", "💥"),
            ("PlantTallnut", "高坚果", "🛡️"), ("PlantJalapeno", "辣椒", "🌶️"),
            ("PlantSquash", "倭瓜", "🍐"), ("PlantCaltrop", "地刺", "📌"),
            ("PlantFumeShroom", "大喷菇", "🌬️"), ("PlantChomper", "大嘴花", "😋"),
            ("PlantCactus", "仙人掌", "🌵"), ("PlantBlover", "三叶草", "🍀"),
            ("PlantCornpult", "玉米投手", "🌽"), ("PlantIceTallnut", "冰高坚果", "🧊"),
            ("PlantCatTail", "香蒲", "🐱"), ("PlantPotatoMine", "土豆雷", "🥔"),
        };

        void ShowSpawn()
        {
            ContentPanel.Children.Clear();
            var page = new StackPanel();
            int rowSel = 3, colSel = 5;
            string kind = "plant";
            string filter = "";

            var ctl = MakePanel();
            var ctlSp = new StackPanel();
            ctlSp.Children.Add(MakeText("刷物 · 生成到指定格子", 16, FontWeights.Bold, (Brush)FindResource("AccentBrush")));
            ctlSp.Children.Add(MakeText("选行/列 → 点卡片「刷出」到该格；「入槽」把卡加进卡槽", 11, FontWeights.Normal, (Brush)FindResource("SubBrush"), new Thickness(0, 6, 0, 0), true));

            // 行/列按钮组
            var rowBtns = new List<(Button B, int V)>();
            var rowLine = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            rowLine.Children.Add(MakeText("行:", 13, FontWeights.Bold, (Brush)FindResource("TextBrush"), new Thickness(0, 0, 8, 0)));
            var rowWrap = new WrapPanel { Orientation = Orientation.Horizontal };
            void RefRows() { var on = (Brush)FindResource("AccentBrush"); var off = (Brush)FindResource("TextBrush"); foreach (var (b, v) in rowBtns) { bool s = v == rowSel; b.Foreground = s ? on : off; b.FontWeight = s ? FontWeights.Bold : FontWeights.Normal; } }
            for (int i = 1; i <= 6; i++) { int r = i; var b = MakeOptionBtn("第" + i + "行", i == rowSel); b.Click += (s, e) => { rowSel = r; RefRows(); }; rowBtns.Add((b, r)); rowWrap.Children.Add(b); }
            rowLine.Children.Add(rowWrap); ctlSp.Children.Add(rowLine);

            var colBtns = new List<(Button B, int V)>();
            var colLine = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            colLine.Children.Add(MakeText("列:", 13, FontWeights.Bold, (Brush)FindResource("TextBrush"), new Thickness(0, 0, 8, 0)));
            var colWrap = new WrapPanel { Orientation = Orientation.Horizontal };
            void RefCols() { var on = (Brush)FindResource("AccentBrush"); var off = (Brush)FindResource("TextBrush"); foreach (var (b, v) in colBtns) { bool s = v == colSel; b.Foreground = s ? on : off; b.FontWeight = s ? FontWeights.Bold : FontWeights.Normal; } }
            for (int i = 1; i <= 9; i++) { int c = i; var b = MakeOptionBtn("第" + i + "列", i == colSel); b.Click += (s, e) => { colSel = c; RefCols(); }; colBtns.Add((b, c)); colWrap.Children.Add(b); }
            colLine.Children.Add(colWrap); ctlSp.Children.Add(colLine);

            // 类型切换 + 搜索
            var typeLine = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            var plantBtn = MakeOptionBtn("植物", true);
            var zombieBtn = MakeOptionBtn("僵尸", false);
            var search = new TextBox { Style = (Style)FindResource("GlassTextBox"), Width = 240, Margin = new Thickness(12, 0, 0, 0), FontFamily = (FontFamily)FindResource("MonoFont") };
            typeLine.Children.Add(plantBtn); typeLine.Children.Add(zombieBtn); typeLine.Children.Add(search);
            var refreshBtn = MakeMiniBtn("刷新列表");
            refreshBtn.Margin = new Thickness(8, 0, 0, 0);
            typeLine.Children.Add(refreshBtn);
            ctlSp.Children.Add(typeLine);
            ctl.Child = ctlSp;
            page.Children.Add(ctl);

            // 结果列表
            var result = MakePanel();
            var resultSp = new StackPanel();
            resultSp.Children.Add(MakeText("卡牌列表（点「刷出」到格子 · 点「入槽」进卡槽）", 14, FontWeights.Bold, (Brush)FindResource("AccentBrush")));
            var listHost = new ScrollViewer { MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var listWp = new WrapPanel { Orientation = Orientation.Horizontal };
            listHost.Content = listWp;
            resultSp.Children.Add(listHost);
            result.Child = resultSp;
            page.Children.Add(result);

            ContentPanel.Children.Add(page);
            AnimateIn(page);

            void RefreshList()
            {
                listWp.Children.Clear();
                _ = LoadPacketsAsync(kind, filter, listWp, () => rowSel, () => colSel);
            }
            var onB = new SolidColorBrush(Color.FromArgb(0x2E, 0x34, 0xC7, 0x59));
            var offB = (Brush)FindResource("CardBrushW");
            plantBtn.Click += (s, e) => { kind = "plant"; plantBtn.Background = onB; zombieBtn.Background = offB; RefreshList(); };
            zombieBtn.Click += (s, e) => { kind = "zombie"; zombieBtn.Background = onB; plantBtn.Background = offB; RefreshList(); };
            DispatcherTimer? debounce = null;
            search.TextChanged += (s, e) =>
            {
                filter = search.Text.Trim().ToLower();
                debounce?.Stop();
                debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
                debounce.Tick += (s2, e2) => { debounce!.Stop(); RefreshList(); };
                debounce.Start();
            };
            refreshBtn.Click += (s, e) => { _packetCache.Clear(); RefreshList(); };
            RefreshList();
        }

        static readonly Dictionary<string, (DateTime T, List<(string Id, string Name)> Items)> _packetCache = new();

        async Task<List<(string Id, string Name)>?> GetPacketsAsync(string kind)
        {
            if (_packetCache.TryGetValue(kind, out var c) && (DateTime.Now - c.T).TotalSeconds < 120)
                return c.Items;
            var r = await GetAsync("/packets?type=" + kind);
            var items = new List<(string, string)>();
            if (r != null && r.StartsWith("["))
            {
                try
                {
                    using var doc = JsonDocument.Parse(r);
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        if (el.ValueKind == JsonValueKind.String)
                        {
                            var s = el.GetString() ?? "";
                            items.Add((s, s));
                        }
                        else
                        {
                            var id = el.TryGetProperty("id", out var idp) ? idp.GetString() ?? "" : "";
                            var nm = el.TryGetProperty("name", out var nmp) ? nmp.GetString() ?? "" : "";
                            if (string.IsNullOrEmpty(id)) continue;
                            items.Add((id, string.IsNullOrEmpty(nm) ? id : nm));
                        }
                    }
                }
                catch { }
            }
            if (items.Count > 0) _packetCache[kind] = (DateTime.Now, items);
            return items.Count > 0 ? items : null;
        }

        async Task LoadPacketsAsync(string kind, string filter, WrapPanel wp, Func<int> getRow, Func<int> getCol)
        {
            var items = await GetPacketsAsync(kind);
            if (items == null)
            {
                wp.Children.Add(MakeText("未连接或无法获取卡牌列表", 12, FontWeights.Normal, (Brush)FindResource("SubBrush")));
                return;
            }
            int shown = 0;
            foreach (var (id, nm) in items)
            {
                if (filter.Length > 0 && !id.ToLower().Contains(filter) && !nm.ToLower().Contains(filter)) continue;
                if (shown >= 80)
                {
                    wp.Children.Add(MakeText("…共 " + items.Count + " 项，输入搜索查看更多", 11, FontWeights.Normal, (Brush)FindResource("SubBrush"), new Thickness(0, 6, 0, 0)));
                    break;
                }
                wp.Children.Add(MakeSpawnCard(id, nm, getRow, getCol));
                shown++;
            }
            if (shown == 0) wp.Children.Add(MakeText("无匹配结果", 12, FontWeights.Normal, (Brush)FindResource("SubBrush")));
        }

        Border MakeSpawnCard(string id, string name, Func<int> getRow, Func<int> getCol)
        {
            var card = new Border
            {
                Width = 200, Height = 74, CornerRadius = new CornerRadius(12),
                Background = (Brush)FindResource("CardBrushW"),
                BorderBrush = (Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 8, 8),
            };
            var g = new Grid();
            var nm = new TextBlock
            {
                Text = name, Foreground = (Brush)FindResource("TextBrush"),
                FontFamily = (FontFamily)FindResource("AppFont"), FontSize = 13,
                Margin = new Thickness(12, 10, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 158,
                ToolTip = id,
            };
            var sub = new TextBlock
            {
                Text = id, Foreground = (Brush)FindResource("SubBrush"),
                FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 9,
                Margin = new Thickness(12, 34, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 158,
            };
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 10, 8) };
            var b1 = MakeMiniBtn("刷出"); b1.Margin = new Thickness(0, 0, 6, 0);
            b1.Click += async (s, e) => await SendSpawn(id, getRow(), getCol());
            var b2 = MakeMiniBtn("入槽");
            b2.Click += async (s, e) => await AddPacket(id);
            btns.Children.Add(b1); btns.Children.Add(b2);
            g.Children.Add(nm); g.Children.Add(sub); g.Children.Add(btns);
            card.Child = g;
            return card;
        }

        async Task AddPacket(string id)
        {
            var r = await GetAsync("/addpacket?id=" + Uri.EscapeDataString(id));
            if (r == "ok") { Log("已加入卡槽 " + id); SetStatus(true, "已连接"); }
            else { Log("加入卡槽失败: " + id); }
        }

        async Task LoadBulletOptions(string name, string zh, WrapPanel wp)
        {
            var r = await GetAsync("/bullettypes");
            if (r == null || !r.StartsWith("["))
            {
                wp.Children.Add(MakeText("无法获取子弹列表（未连接）", 12, FontWeights.Normal, (Brush)FindResource("SubBrush")));
                return;
            }
            try
            {
                var arr = JsonSerializer.Deserialize<JsonElement>(r);
                wp.Children.Add(MakeBulletCard("默认", "", name, zh, true));
                foreach (var el in arr.EnumerateArray())
                {
                    string k = el.GetProperty("key").GetString() ?? "";
                    string n = el.GetProperty("name").GetString() ?? k;
                    wp.Children.Add(MakeBulletCard(n, k, name, zh, false));
                }
            }
            catch { }
        }

        // 子弹选择：独立卡片网格（中文名大标题 + key 小字）
        Border MakeBulletCard(string display, string key, string field, string zh, bool isDefault)
        {
            var card = new Border
            {
                Background = (Brush)FindResource("CardBrushW"),
                BorderBrush = (Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 0, 8, 8),
                Cursor = System.Windows.Input.Cursors.Hand,
                Width = 168,
                Effect = FindResource("SoftShadow") as System.Windows.Media.Effects.Effect,
            };
            var sp = new StackPanel();
            var nm = MakeText(display, 13, FontWeights.Bold, (Brush)FindResource("TextBrush"));
            nm.TextTrimming = TextTrimming.CharacterEllipsis;
            sp.Children.Add(nm);
            if (!isDefault)
                sp.Children.Add(MakeText(key, 9, FontWeights.Normal, (Brush)FindResource("SubBrush"), new Thickness(0, 2, 0, 0)));
            card.Child = sp;
            string val = isDefault ? "" : key;
            card.MouseLeftButtonUp += async (s, e) =>
            {
                await SetField(field, zh, val);
                SliderPanel.Visibility = Visibility.Collapsed;
                _sliderOpen = false;
            };
            return card;
        }

        Button MakeOptionBtn(string text, bool selected)
        {
            var b = new Button
            {
                Content = text, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(8, 4, 8, 4), FontSize = 11,
                Style = (Style)FindResource("ActionButton"),
                FontWeight = selected ? FontWeights.Bold : FontWeights.Normal,
                Foreground = selected ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("TextBrush"),
            };
            return b;
        }

        async Task SendSpawn(string id, int row, int col)
        {
            var r = await GetAsync("/spawn?id=" + Uri.EscapeDataString(id) + "&row=" + row + "&col=" + col);
            if (r == "ok") { Log("已刷出 " + id + "（第" + row + "行 第" + col + "列）"); SetStatus(true, "已连接"); }
            else { Log("刷物失败: " + id + "（需在战斗中 / ID 无效 / 未连接）"); }
        }

        // ================= 用户中心（完整页面） =================
        void ShowUserCenter()
        {
            ContentPanel.Children.Clear();
            var page = new StackPanel { Margin = new Thickness(8, 4, 8, 4) };

            // 头部
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
            var avatar = new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(32), Background = new SolidColorBrush(Color.FromArgb(0x22, 0x34, 0xC7, 0x59)) };
            avatar.Child = new TextBlock { Text = "M", FontSize = 28, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var headText = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
            headText.Children.Add(MakeText("用户中心", 20, FontWeights.Bold, (Brush)FindResource("TextBrush")));
            headText.Children.Add(MakeText("存档状态 · 使用时长 · 连接信息", 11, FontWeights.Normal, (Brush)FindResource("SubBrush"), new Thickness(0, 4, 0, 0)));
            head.Children.Add(avatar); head.Children.Add(headText);
            page.Children.Add(head);

            // 连接信息卡
            var conn = MakePanel();
            var connSp = new StackPanel();
            connSp.Children.Add(MakeText("连接信息", 14, FontWeights.Bold, (Brush)FindResource("AccentBrush")));
            connSp.Children.Add(InfoRow("连接状态", _connected ? "已连接" : "未连接", _connected ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("SubBrush")));
            connSp.Children.Add(InfoRow("已开功能", _settings.Count == 0 ? "—" : CountOn(_settings).ToString()));
            connSp.Children.Add(InfoRow("本地端口", "127.0.0.1:28999"));
            conn.Child = connSp;
            page.Children.Add(conn);

            // 存档卡
            var save = MakePanel();
            var saveSp = new StackPanel();
            saveSp.Children.Add(MakeText("存档状态", 14, FontWeights.Bold, (Brush)FindResource("AccentBrush")));
            string savePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Godot", "app_userdata", "植物大战僵尸杂交版", "Csharp", "save.res");
            if (File.Exists(savePath))
            {
                var fi = new FileInfo(savePath);
                saveSp.Children.Add(InfoRow("存档文件", "存在 · " + (fi.Length / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB"));
                saveSp.Children.Add(InfoRow("最后修改", fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm")));
                saveSp.Children.Add(InfoRow("位置", savePath, (Brush)FindResource("SubBrush")));
            }
            else
            {
                saveSp.Children.Add(InfoRow("存档文件", "未找到"));
                saveSp.Children.Add(InfoRow("位置", savePath, (Brush)FindResource("SubBrush")));
            }
            save.Child = saveSp;
            page.Children.Add(save);

            // 使用时长卡
            var time = MakePanel();
            var timeSp = new StackPanel();
            timeSp.Children.Add(MakeText("使用时长", 14, FontWeights.Bold, (Brush)FindResource("AccentBrush")));
            _userUptime = MakeText("已运行 " + FormatUptime(), 22, FontWeights.Bold, (Brush)FindResource("AccentBrush"), new Thickness(0, 8, 0, 0));
            timeSp.Children.Add(_userUptime);
            timeSp.Children.Add(MakeText("本次程序自启动以来累计运行时间", 11, FontWeights.Normal, (Brush)FindResource("SubBrush"), new Thickness(0, 4, 0, 0)));
            time.Child = timeSp;
            page.Children.Add(time);

            // 关于（对应游戏内 MOD 的“关于”内容）
            var about = MakePanel();
            var aboutSp = new StackPanel();
            aboutSp.Children.Add(MakeText("关于", 14, FontWeights.Bold, (Brush)FindResource("AccentBrush")));
            aboutSp.Children.Add(MakeText("DLL 注入型辅助 Mod · 植物大战僵尸杂交版 0.26.1", 11, FontWeights.Normal, (Brush)FindResource("SubBrush"), new Thickness(0, 6, 0, 0)));
            // ★ 版本号约定：用**当天日期**，格式 yy.M.d（如 26.9.14）。
            //   每次发布都改成当天日期，方便用户对版本（之前写死 "1.4" 看不出来是哪天的）。
            aboutSp.Children.Add(InfoRow("Mod 版本", "外置修改器 26.9.15"));
            aboutSp.Children.Add(InfoRow("作者", "小晓Air"));
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            var biliBtn = MakeMiniBtn("B 站主页");
            // 作者主页（b23.tv/ZOtHAFF 解析后的正式地址，UID 3546789573561037）
            biliBtn.Click += (s, e) => OpenUrl("https://space.bilibili.com/3546789573561037");
            btnRow.Children.Add(biliBtn);
            aboutSp.Children.Add(btnRow);
            about.Child = aboutSp;
            page.Children.Add(about);

            var note = MakeText("提示：用满级存档制作器可直接修改存档数值；这里显示存档文件状态。", 10, FontWeights.Normal, (Brush)FindResource("SubBrush"), new Thickness(0, 10, 0, 0));
            page.Children.Add(note);

            ContentPanel.Children.Add(page);
            AnimateIn(page);
        }

        /// <summary>Win10 设置卡片：白底 + 1px 细线 + 圆角 4，无阴影。
        /// ★ 18 个页面的卡片全走这里（14 处调用），改这一处全都变。</summary>
        Border MakePanel()
        {
            var bd = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)),   // 85% 白：能透出一点背景又保证可读
                BorderBrush = (Brush)FindResource("LineBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(18, 14, 18, 14),
                Margin = new Thickness(0, 0, 0, 10),
            };
            return bd;
        }

        /// <summary>排版助手。
        /// ★ size==20 且 Bold 是旧版「页内大标题」写法 → 统一降成 Win10 分组标题（14 SemiBold），
        ///   因为页面名已由顶部 28px 的 PageTitle 承担，否则会重复两个大标题。</summary>
        TextBlock MakeText(string text, double size, FontWeight weight, Brush fg, Thickness? margin = null, bool wrap = false)
        {
            if (size == 20 && weight == FontWeights.Bold) { size = 14; weight = FontWeights.SemiBold; }
            return new TextBlock
            {
                Text = text,
                FontFamily = (FontFamily)FindResource("UiFont"),
                FontSize = size,
                FontWeight = weight,
                Foreground = fg,
                Margin = margin ?? new Thickness(0),
                TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            };
        }

        StackPanel InfoRow(string k, string v, Brush? valBrush = null)
        {
            var r = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
            r.Children.Add(MakeText(k, 11, FontWeights.Normal, (Brush)FindResource("SubBrush"), new Thickness(0, 0, 14, 0)));
            var vt = MakeText(v, 11, FontWeights.Bold, valBrush ?? (Brush)FindResource("TextBrush"));
            vt.TextWrapping = TextWrapping.Wrap;
            r.Children.Add(vt);
            return r;
        }

        // ================= 存档管理 =================
        void ShowSave()
        {
            ContentPanel.Children.Clear();
            _savePath = SaveTools.FindSavePath();
            var page = new StackPanel { Margin = new Thickness(8, 4, 8, 4) };

            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
            var headText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            headText.Children.Add(MakeText("存档管理", 20, FontWeights.Bold, (Brush)FindResource("TextBrush")));
            headText.Children.Add(MakeText("直接读写 save.res · 写前自动备份 · 建议先关闭游戏", 11, FontWeights.Normal, (Brush)FindResource("SubBrush"), new Thickness(0, 4, 0, 0)));
            head.Children.Add(headText);
            page.Children.Add(head);

            // 路径卡
            var pathCard = MakePanel();
            var pathSp = new StackPanel();
            pathSp.Children.Add(MakeText("存档位置", 14, FontWeights.Bold, (Brush)FindResource("AccentBrush")));
            pathSp.Children.Add(InfoRow("路径", _savePath, (Brush)FindResource("SubBrush")));
            var pathBtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            var openBtn = MakeMiniBtn("打开目录");
            openBtn.Click += (s, e) => OpenFolder(_savePath);
            var loadBtn = MakeMiniBtn("加载存档");
            loadBtn.Margin = new Thickness(8, 0, 0, 0);
            loadBtn.Click += (s, e) => LoadSave();
            pathBtns.Children.Add(openBtn); pathBtns.Children.Add(loadBtn);
            pathSp.Children.Add(pathBtns);
            pathCard.Child = pathSp;
            page.Children.Add(pathCard);

            // 一键操作卡
            var opCard = MakePanel();
            var opSp = new StackPanel();
            opSp.Children.Add(MakeText("一键操作", 14, FontWeights.Bold, (Brush)FindResource("AccentBrush")));
            _saveStats = MakeText("未加载存档", 12, FontWeights.Normal, (Brush)FindResource("SubBrush"), new Thickness(0, 6, 0, 0));
            opSp.Children.Add(_saveStats);
            var opBtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            var fullBtn = MakeMiniBtn("一键全满");
            fullBtn.Click += (s, e) => FullSave();
            var backBtn = MakeMiniBtn("备份存档");
            backBtn.Margin = new Thickness(8, 0, 0, 0);
            backBtn.Click += (s, e) => DoBackup();
            opBtns.Children.Add(fullBtn); opBtns.Children.Add(backBtn);
            opSp.Children.Add(opBtns);
            opCard.Child = opSp;
            page.Children.Add(opCard);

            // 多用户管理卡
            var userCard = MakePanel();
            var userSp = new StackPanel();
            userSp.Children.Add(MakeText("多用户管理", 14, FontWeights.Bold, (Brush)FindResource("AccentBrush")));
            userSp.Children.Add(MakeText("切换 / 新建 / 删除用户（删除前先切到别的用户）", 10, FontWeights.Normal, (Brush)FindResource("SubBrush"), new Thickness(0, 3, 0, 0)));
            var userRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            var userCombo = new ComboBox
            {
                FontFamily = (FontFamily)FindResource("AppFont"),
                FontSize = 12,
                MinWidth = 220,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _saveUserCombo = userCombo;
            userCombo.SelectionChanged += (s, e) =>
            {
                if (_saveRes == null || userCombo.SelectedItem is not string nm) return;
                if (SaveTools.GetUser(_saveRes, out var cur) != null && cur == nm) return;
                SaveTools.SwitchUser(_saveRes, nm);
                _saveUser = SaveTools.GetUser(_saveRes, out _);
                Log("已切换到用户 " + nm);
                RefreshSaveStats();
                PopulateSaveChecks();
                RefreshCurKeys();
            };
            var userNew = MakeMiniBtn("新建");
            userNew.Margin = new Thickness(8, 0, 0, 0);
            userNew.Click += (s, e) =>
            {
                var input = new TextBox { Text = "user" + DateTime.Now.ToString("HHmmss"), Width = 220, FontSize = 12 };
                var w = new Window { WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Width = 300, Height = 130, WindowStartupLocation = WindowStartupLocation.CenterScreen, Topmost = true };
                var bd = new Border { Background = (Brush)FindResource("GlassBrush"), BorderBrush = (Brush)FindResource("BorderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(16) };
                var sp = new StackPanel();
                sp.Children.Add(MakeText("新用户名", 13, FontWeights.Bold, (Brush)FindResource("TextBrush")));
                sp.Children.Add(input);
                var ok = MakeMiniBtn("创建");
                ok.Margin = new Thickness(0, 10, 0, 0);
                ok.Click += (s2, e2) =>
                {
                    string nm = input.Text.Trim();
                    if (_saveRes != null && SaveTools.AddUser(_saveRes, nm)) { WriteSaveSafe(); RefreshSaveUsers(); Log("已新建用户 " + nm); }
                    else Log("新建失败（空名或已存在）");
                    w.Close();
                };
                sp.Children.Add(ok);
                bd.Child = sp; w.Content = bd; w.Show();
            };
            var userDel = MakeMiniBtn("删除");
            userDel.Margin = new Thickness(8, 0, 0, 0);
            userDel.Click += (s, e) =>
            {
                if (_saveRes == null || _saveUserCombo.SelectedItem is not string nm) { Log("请先加载存档并选择用户"); return; }
                if (_saveUser != null && SaveTools.GetUser(_saveRes, out var cur) == _saveUser && cur == nm) { Log("不能删除当前用户"); return; }
                if (SaveTools.DeleteUser(_saveRes, nm)) { WriteSaveSafe(); RefreshSaveUsers(); Log("已删除用户 " + nm); }
            };
            userRow.Children.Add(userCombo); userRow.Children.Add(userNew); userRow.Children.Add(userDel);
            userSp.Children.Add(userRow);
            userCard.Child = userSp;
            page.Children.Add(userCard);

            // 货币/数值直接编辑卡
            var curCard = MakePanel();
            var curSp = new StackPanel();
            curSp.Children.Add(MakeText("货币 / 数值直接编辑", 14, FontWeights.Bold, (Brush)FindResource("AccentBrush")));
            curSp.Children.Add(MakeText("输入数字点「应用」直接写存档（0.27 金币/钻石/水晶等任意 int 值）", 10, FontWeights.Normal, (Brush)FindResource("SubBrush"), new Thickness(0, 3, 0, 0)));
            var curRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            var curKey = new ComboBox { FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 12, MinWidth = 170, VerticalAlignment = VerticalAlignment.Center };
            _saveCurKey = curKey;
            var curVal = new TextBox { Style = (Style)FindResource("GlassTextBox"), Width = 130, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Text = "999999999" };
            var curApply = MakeMiniBtn("应用");
            curApply.Margin = new Thickness(8, 0, 0, 0);
            curApply.Click += (s, e) =>
            {
                if (_saveUser == null || curKey.SelectedItem is not string kk) { Log("请先加载存档并选一个数值项"); return; }
                if (SaveTools.SetKeyValue(_saveUser, kk, curVal.Text)) { WriteSaveSafe(); RefreshSaveStats(); Log("已设置 " + SaveTools.KeyDisplay(kk) + " = " + curVal.Text); }
                else Log("设置失败（值格式不对或不存在）");
            };
            var curRefresh = MakeMiniBtn("刷新列表");
            curRefresh.Margin = new Thickness(8, 0, 0, 0);
            curRefresh.Click += (s, e) => RefreshCurKeys();
            curRow.Children.Add(curKey); curRow.Children.Add(curVal); curRow.Children.Add(curApply); curRow.Children.Add(curRefresh);
            curSp.Children.Add(curRow);
            curCard.Child = curSp;
            page.Children.Add(curCard);

            // 自定义解锁卡
            var cust = MakePanel();
            var custSp = new StackPanel();
            custSp.Children.Add(MakeText("自定义解锁", 14, FontWeights.Bold, (Brush)FindResource("AccentBrush")));
            var tabRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            foreach (var (key, label) in new[] { ("cards", "卡牌"), ("levels", "关卡"), ("features", "功能"), ("tutorials", "教程"), ("keys", "货币/设置") })
            {
                var tb = MakeMiniBtn(label);
                tb.Tag = key;
                var kk = key;
                tb.Click += (s, e) => { _saveCat = kk; PopulateSaveChecks(); };
                tb.Margin = new Thickness(tabRow.Children.Count > 0 ? 8 : 0, 0, 0, 0);
                tabRow.Children.Add(tb);
            }
            custSp.Children.Add(tabRow);

            _saveFilterBox = new TextBox { Style = (Style)FindResource("GlassTextBox"), Margin = new Thickness(0, 10, 0, 0), FontSize = 12 };
            _saveFilterBox.TextChanged += (s, e) => DebounceSaveFilter();
            custSp.Children.Add(_saveFilterBox);

            var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 300, Margin = new Thickness(0, 10, 0, 0) };
            _saveList = new WrapPanel();
            sv.Content = _saveList;
            custSp.Children.Add(sv);

            var custBtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            var unlockBtn = MakeMiniBtn("解锁勾选项");
            unlockBtn.Click += (s, e) => ApplySelectedSave();
            var allBtn = MakeMiniBtn("全选");
            allBtn.Margin = new Thickness(8, 0, 0, 0);
            allBtn.Click += (s, e) => SetAllChecks(true);
            var noneBtn = MakeMiniBtn("全不选");
            noneBtn.Margin = new Thickness(8, 0, 0, 0);
            noneBtn.Click += (s, e) => SetAllChecks(false);
            custBtns.Children.Add(unlockBtn); custBtns.Children.Add(allBtn); custBtns.Children.Add(noneBtn);
            custSp.Children.Add(custBtns);
            cust.Child = custSp;
            page.Children.Add(cust);

            ContentPanel.Children.Add(page);
            AnimateIn(page);
            PopulateSaveChecks();
        }

        void LoadSave()
        {
            try
            {
                if (!File.Exists(_savePath)) { Log("存档不存在: " + _savePath); if (_saveStats != null) _saveStats.Text = "未找到存档文件"; return; }
                byte[] data = File.ReadAllBytes(_savePath);
                _saveRes = SaveTools.Parse(data);
                _saveUser = SaveTools.GetUser(_saveRes, out _);
                if (_saveUser == null) { Log("存档加载失败（未找到当前用户存档）"); if (_saveStats != null) _saveStats.Text = "加载失败（未找到当前用户）"; return; }
                Log("存档已加载: " + _savePath);
                RefreshSaveStats();
                PopulateSaveChecks();
                RefreshSaveUsers();
                RefreshCurKeys();
            }
            catch (Exception ex) { Log("存档加载失败: " + ex.Message); if (_saveStats != null) _saveStats.Text = "加载失败"; }
        }

        void RefreshSaveStats()
        {
            if (_saveUser == null) { if (_saveStats != null) _saveStats.Text = "未加载存档"; return; }
            int cards = 0, levels = 0;
            if (_saveUser.TryGetValue("TowerDefensePacket", out var pk) && pk is Dictionary<object, object> pkd) cards = pkd.Count;
            if (_saveUser.TryGetValue("Level", out var lv) && lv is Dictionary<object, object> lvd) levels = lvd.Count;
            string coin = "?", crystal = "?";
            if (_saveUser.TryGetValue("Key", out var kk) && kk is Dictionary<object, object> key)
            {
                if (key.TryGetValue("CoinNum", out var cn) && cn is int cni) coin = cni.ToString("N0");
                if (key.TryGetValue("CrystalNum", out var cr) && cr is int cri) crystal = cri.ToString("N0");
            }
            if (_saveStats != null)
                _saveStats.Text = "金币 " + coin + " · 钻石 " + crystal + " · 卡牌 " + cards + " · 关卡 " + levels;
        }

        void PopulateSaveChecks()
        {
            _saveList.Children.Clear();
            _saveChecks.Clear();
            if (_saveUser == null)
            {
                _saveList.Children.Add(MakeText("未加载存档，请先点击「加载存档」。", 12, FontWeights.Normal, (Brush)FindResource("SubBrush")));
                return;
            }
            string filter = _saveFilterBox.Text.Trim();
            var items = new List<string>();
            if (_saveCat == "cards")
            {
                if (_saveUser.TryGetValue("TowerDefensePacket", out var pk) && pk is Dictionary<object, object> pkd)
                    items = pkd.Keys.OfType<string>().ToList();
                items.Sort();
                int shown = 0;
                foreach (var id in items)
                {
                    if (filter.Length > 0 && !id.Contains(filter)) continue;
                    if (shown >= 150) break;
                    var cb = MakeSaveCheckBox(SaveTools.CardDisplay(id), id);
                    _saveList.Children.Add(cb);
                    _saveChecks[id] = cb;
                    shown++;
                }
            }
            else if (_saveCat == "levels")
            {
                if (_saveUser.TryGetValue("Level", out var lv) && lv is Dictionary<object, object> lvd)
                    items = lvd.Keys.OfType<string>().ToList();
                items.Sort();
                foreach (var id in items)
                {
                    if (filter.Length > 0 && !id.Contains(filter)) continue;
                    var cb = MakeSaveCheckBox(SaveTools.LevelDisplay(id), id);
                    _saveList.Children.Add(cb);
                    _saveChecks[id] = cb;
                }
            }
            else if (_saveCat == "features")
            {
                if (_saveUser.TryGetValue("Feature", out var ft) && ft is Dictionary<object, object> ftd)
                    items = ftd.Keys.OfType<string>().ToList();
                items.Sort();
                foreach (var id in items)
                {
                    if (filter.Length > 0 && !id.Contains(filter)) continue;
                    var cb = MakeSaveCheckBox(SaveTools.FeatureDisplay(id), id);
                    _saveList.Children.Add(cb);
                    _saveChecks[id] = cb;
                }
            }
            else if (_saveCat == "tutorials")
            {
                if (_saveUser.TryGetValue("Tutorial", out var tu) && tu is Dictionary<object, object> tud)
                    items = tud.Keys.OfType<string>().ToList();
                items.Sort();
                foreach (var id in items)
                {
                    if (filter.Length > 0 && !id.Contains(filter)) continue;
                    var cb = MakeSaveCheckBox(id, id);
                    _saveList.Children.Add(cb);
                    _saveChecks[id] = cb;
                }
            }
            else // keys
            {
                if (_saveUser.TryGetValue("Key", out var kk) && kk is Dictionary<object, object> key)
                    items = key.Keys.OfType<string>().ToList();
                items.Sort();
                foreach (var id in items)
                {
                    if (filter.Length > 0 && !id.Contains(filter)) continue;
                    var cb = MakeSaveCheckBox(SaveTools.KeyDisplay(id), id);
                    _saveList.Children.Add(cb);
                    _saveChecks[id] = cb;
                }
            }
        }

        CheckBox MakeSaveCheckBox(string display, string id) => new CheckBox
        {
            Content = display,
            Tag = id,
            FontFamily = (FontFamily)FindResource("AppFont"),
            FontSize = 12,
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 4, 18, 4),
            ToolTip = id,
        };

        void DebounceSaveFilter()
        {
            if (_saveFilterTimer == null)
            {
                _saveFilterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
                _saveFilterTimer.Tick += (s, e) => { _saveFilterTimer.Stop(); PopulateSaveChecks(); };
            }
            _saveFilterTimer.Stop();
            _saveFilterTimer.Start();
        }

        void FullSave()
        {
            if (_saveUser == null || _saveRes == null) { Log("请先加载存档"); return; }
            if (IsGameRunning() && MessageBox.Show("检测到游戏正在运行！\n写入的存档会在游戏退出时被覆盖。\n\n建议先关闭游戏再继续。\n是否仍要继续？", "提示", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            int ops = SaveTools.ApplyFull(_saveUser);
            WriteSaveSafe();
            Log("一键全满完成，操作 " + ops + " 处");
            RefreshSaveStats();
            PopulateSaveChecks();
        }

        void ApplySelectedSave()
        {
            if (_saveUser == null || _saveRes == null) { Log("请先加载存档"); return; }
            var sel = _saveChecks.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList();
            if (sel.Count == 0) { Log("未勾选任何项"); return; }
            if (IsGameRunning() && MessageBox.Show("检测到游戏正在运行！\n写入的存档会在游戏退出时被覆盖。\n\n建议先关闭游戏再继续。\n是否仍要继续？", "提示", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            int ops;
            if (_saveCat == "cards")
                ops = SaveTools.ApplySelected(_saveUser, sel, Enumerable.Empty<string>(), Enumerable.Empty<string>());
            else if (_saveCat == "levels")
                ops = SaveTools.ApplySelected(_saveUser, Enumerable.Empty<string>(), sel, Enumerable.Empty<string>());
            else if (_saveCat == "features")
                ops = SaveTools.ApplySelected(_saveUser, Enumerable.Empty<string>(), Enumerable.Empty<string>(), Enumerable.Empty<string>(), sel);
            else if (_saveCat == "tutorials")
                ops = SaveTools.ApplySelected(_saveUser, Enumerable.Empty<string>(), Enumerable.Empty<string>(), Enumerable.Empty<string>(), null, sel);
            else
                ops = SaveTools.ApplySelected(_saveUser, Enumerable.Empty<string>(), Enumerable.Empty<string>(), sel);
            WriteSaveSafe();
            Log("自定义解锁完成，操作 " + ops + " 处");
            RefreshSaveStats();
        }

        // 刷新用户下拉 + 切换当前用户
        void RefreshSaveUsers()
        {
            if (_saveRes == null || _saveUserCombo == null) return;
            var users = SaveTools.GetUsers(_saveRes);
            _saveUserCombo.ItemsSource = users;
            _saveUserCombo.SelectedItem = SaveTools.GetUser(_saveRes, out var cur) != null ? cur : (users.Count > 0 ? users[0] : null);
        }

        // 刷新货币/数值下拉（Key 字典里所有 int 数值项）
        void RefreshCurKeys()
        {
            if (_saveUser == null || _saveCurKey == null) return;
            var list = new List<string>();
            if (_saveUser.TryGetValue("Key", out var kk) && kk is Dictionary<object, object> key)
                list = key.Where(kv => kv.Value is int).Select(kv => kv.Key.ToString()).ToList();
            list.Sort();
            _saveCurKey.ItemsSource = list;
            _saveCurKey.SelectedItem = list.Contains("CoinNum") ? "CoinNum" : (list.Count > 0 ? list[0] : null);
        }

        void WriteSaveSafe()
        {
            if (_saveRes == null) return;
            string bak = SaveTools.Backup(_savePath);
            SaveTools.WriteSave(_savePath, _saveRes);
            Log("已写入存档（备份: " + Path.GetFileName(bak) + "）");
        }

        void DoBackup()
        {
            if (!File.Exists(_savePath)) { Log("存档不存在"); return; }
            string bak = SaveTools.Backup(_savePath);
            Log("已备份存档: " + Path.GetFileName(bak));
        }

        void SetAllChecks(bool on) { foreach (var cb in _saveChecks.Values) cb.IsChecked = on; }

        static bool IsGameRunning()
        {
            try { return Process.GetProcesses().Any(p => p.ProcessName.Contains("植物大战僵尸杂交版")); }
            catch { return false; }
        }

        static void OpenFolder(string filePath)
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + filePath + "\"") { UseShellExecute = true }); }
            catch { }
        }

        // ================= 实体属性（场上植物/僵尸，动态反射全部可改字段） =================
        void ShowEntities()
        {
            ContentPanel.Children.Clear();
            var page = new StackPanel { Margin = new Thickness(8, 4, 8, 4) };
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            head.Children.Add(MakeText("实体属性", 20, FontWeights.Bold, (Brush)FindResource("TextBrush")));
            var refresh = MakeMiniBtn("刷新列表");
            refresh.Margin = new Thickness(16, 0, 0, 0);
            refresh.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(refresh);
            page.Children.Add(head);

            var t1 = MakeText("全部类型（点击调节该类全局属性，作用于所有该类实例）", 13, FontWeights.Bold, (Brush)FindResource("AccentBrush"), new Thickness(0, 0, 0, 8));
            page.Children.Add(t1);
            var typeWp = new WrapPanel { VerticalAlignment = VerticalAlignment.Top };
            page.Children.Add(typeWp);

            var t2 = MakeText("场上实体（点击改该实例属性，即时生效；进战斗后才有）", 13, FontWeights.Bold, (Brush)FindResource("AccentBrush"), new Thickness(0, 14, 0, 8));
            page.Children.Add(t2);
            var entWp = new WrapPanel { VerticalAlignment = VerticalAlignment.Top };
            page.Children.Add(entWp);

            ContentPanel.Children.Add(page);
            AnimateIn(page);
            refresh.Click += async (s, e) => { await LoadTypeList(typeWp); await LoadEntities(entWp); };
            _ = LoadTypeList(typeWp);
            _ = LoadEntities(entWp);
        }

        async Task LoadTypeList(WrapPanel wp)
        {
            var r = await GetAsync("/types");
            wp.Children.Clear();
            if (r == null || !r.StartsWith("["))
            {
                wp.Children.Add(MakeText("无法获取类型列表（未连接）", 12, FontWeights.Normal, (Brush)FindResource("SubBrush")));
                return;
            }
            int n = 0;
            try
            {
                foreach (var el in JsonSerializer.Deserialize<JsonElement>(r).EnumerateArray())
                {
                    string id = el.GetProperty("id").GetString() ?? "";
                    string nm = el.GetProperty("name").GetString() ?? id;
                    wp.Children.Add(MakeTypeCard(id, nm, ++n));
                }
            }
            catch { }
        }

        Border MakeTypeCard(string id, string name, int idx)
        {
            var card = new Border
            {
                Background = (Brush)FindResource("CardBrushW"),
                BorderBrush = (Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 0, 10, 10),
                Cursor = System.Windows.Input.Cursors.Hand,
                Width = 190,
                Effect = FindResource("SoftShadow") as System.Windows.Media.Effects.Effect,
            };
            var sp = new StackPanel();
            var nm = MakeText(name, 13, FontWeights.Bold, (Brush)FindResource("TextBrush"));
            nm.TextTrimming = TextTrimming.CharacterEllipsis;
            sp.Children.Add(nm);
            sp.Children.Add(MakeText(id, 9, FontWeights.Normal, (Brush)FindResource("SubBrush"), new Thickness(0, 3, 0, 0)));
            card.Child = sp;
            card.MouseLeftButtonUp += (s, e) => OpenTypeProps(id, name);
            return card;
        }

        void OpenTypeProps(string id, string name)
        {
            var w = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true,
                ShowInTaskbar = false,
                Width = 380,
                Height = 560,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
            };
            var card = new Border
            {
                Background = (Brush)FindResource("GlassBrush"),
                BorderBrush = (Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(18),
            };
            var outer = new Grid();
            outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = MakeText(name + "（" + id + "）", 14, FontWeights.Bold, (Brush)FindResource("TextBrush"));
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            head.Children.Add(title);
            var close = new Button { Content = "✕", Style = (Style)FindResource("CloseButton"), Width = 28, Height = 28 };
            Grid.SetColumn(close, 1);
            head.Children.Add(close);
            head.MouseLeftButtonDown += (s, e) => { try { w.DragMove(); } catch { } };
            outer.Children.Add(head);
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var content = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            scroll.Content = content;
            Grid.SetRow(scroll, 1);
            outer.Children.Add(scroll);
            card.Child = outer;
            w.Content = card;
            w.Show();
            close.Click += (s, e) => w.Close();
            _ = LoadTypeProps(content, id, name);
        }

        // 常见字段名 → 中文（专属字段显示用）
        static readonly Dictionary<string, string> FieldZh = new(StringComparer.OrdinalIgnoreCase)
        {
            { "_fireInterval", "攻击间隔" }, { "fireInterval", "攻击间隔" }, { "_fireNum", "发射数量" }, { "fireNum", "发射数量" },
            { "_projectileName", "子弹类型" }, { "projectileName", "子弹类型" }, { "attack", "伤害" }, { "damage", "伤害" },
            { "_sunNum", "产阳光数" }, { "sunNum", "产阳光数" }, { "_produceInterval", "产阳光间隔" }, { "produceInterval", "产阳光间隔" },
            { "_readyTime", "准备时间" }, { "readyTime", "准备时间" }, { "_growUpTime", "成长时间" }, { "speed", "移动速度" },
            { "hp", "血量" }, { "maxHp", "最大血量" }, { "currentHp", "当前血量" }, { "invincible", "无敌" },
            { "cost", "价格" }, { "cooldown", "冷却" }, { "range", "射程" }, { "scale", "大小" },
            { "over", "已结束" }, { "run", "运行中" }, { "isRun", "奔跑" }, { "angry", "愤怒" }, { "isJump", "跳跃" },
            { "hasAxe", "有斧子" }, { "hasPogo", "有弹簧" }, { "isAttack", "攻击中" }, { "isHack", "破解" },
            // 底层：角色/卡牌配置字段
            { "hitpoints", "血量" }, { "hitpointsNearDeath", "濒死血量" },
            { "explosionHurt", "爆炸伤害" }, { "smashHurt", "砸击伤害" }, { "dragHurt", "拖拽伤害" },
            { "spikeHurt", "尖刺伤害" }, { "biteHurt", "撕咬伤害" },
            { "costNight", "夜间价格" }, { "costRise", "涨价" }, { "costMultiple", "价格倍数" },
            { "packetCooldown", "卡牌冷却" }, { "startingCooldown", "初始冷却" },
            { "sleepTime", "睡眠时间" }, { "canDragIntoWater", "可拖入水" }, { "canImitate", "可模仿" }, { "canCopy", "可复制" },
            { "collisionFlags", "碰撞标志" }, { "maskFlags", "掩码标志" }, { "unUseBuffFlags", "禁用Buff标志" },
            { "physiqueTypeFlags", "体型标志" }, { "elementFlags", "元素标志" },
            { "overrideCost", "覆盖价格" }, { "overrideCostRise", "覆盖涨价" },
            { "overridePacketCooldown", "覆盖卡牌冷却" }, { "overrideStartingCooldown", "覆盖初始冷却" },
            { "overrideWeight", "覆盖权重" }, { "overrideWavePointCost", "覆盖波次消耗" },
            { "overrideHypnoses", "覆盖魅惑" },
        };
        static string FieldLabel(string field)
        {
            if (string.IsNullOrEmpty(field)) return field;
            if (FieldZh.TryGetValue(field, out var zh)) return zh;
            return TranslateField(field);
        }

        // 英文词根 → 中文：camelCase 拆词后逐词翻译，未覆盖的词保留原文
        static readonly Dictionary<string, string> WordZh = new(StringComparer.OrdinalIgnoreCase)
        {
            // 数量/数值
            { "num", "数量" }, { "count", "数量" }, { "amount", "数量" }, { "max", "最大" }, { "min", "最小" },
            { "current", "当前" }, { "base", "基础" }, { "value", "值" }, { "total", "总" }, { "limit", "上限" },
            { "rate", "速率" }, { "percent", "百分比" }, { "chance", "概率" }, { "probability", "概率" }, { "power", "强度" },
            { "level", "等级" }, { "size", "大小" }, { "scale", "缩放" }, { "height", "高度" }, { "width", "宽度" },
            { "length", "长度" }, { "radius", "半径" }, { "angle", "角度" }, { "direction", "方向" }, { "position", "位置" },
            { "offset", "偏移" }, { "distance", "距离" }, { "range", "射程" }, { "weight", "权重" }, { "multiple", "倍数" },
            // 时间
            { "time", "时间" }, { "duration", "时长" }, { "interval", "间隔" }, { "delay", "延迟" }, { "cooldown", "冷却" },
            { "start", "开始" }, { "starting", "初始" }, { "cool", "冷却" }, { "cd", "冷却" }, { "ready", "就绪" },
            // 战斗/行为
            { "attack", "攻击" }, { "damage", "伤害" }, { "hurt", "伤害" }, { "hp", "血量" }, { "health", "血量" },
            { "hitpoints", "血量" }, { "speed", "速度" }, { "move", "移动" }, { "fire", "发射" }, { "shoot", "射击" },
            { "bullet", "子弹" }, { "projectile", "子弹" }, { "target", "目标" }, { "hit", "命中" }, { "critical", "暴击" },
            { "crit", "暴击" }, { "armor", "护甲" }, { "defense", "防御" }, { "shield", "护盾" }, { "dodge", "闪避" },
            { "lifesteal", "吸血" }, { "splash", "溅射" }, { "explosion", "爆炸" }, { "smash", "砸击" }, { "drag", "拖拽" },
            { "spike", "尖刺" }, { "bite", "撕咬" }, { "burn", "燃烧" }, { "freeze", "冰冻" }, { "frozen", "冰冻" },
            { "poison", "中毒" }, { "electric", "电击" }, { "stun", "眩晕" }, { "slow", "减速" }, { "knockback", "击退" },
            { "grab", "抓取" }, { "pogo", "弹簧" }, { "jump", "跳跃" }, { "rise", "升起" }, { "drop", "掉落" },
            { "summon", "召唤" }, { "spawn", "生成" }, { "produce", "生产" }, { "grow", "成长" }, { "recharge", "充能" },
            { "reload", "装填" }, { "wave", "波次" }, { "point", "点数" }, { "price", "价格" }, { "cost", "价格" },
            { "sun", "阳光" }, { "gold", "金币" }, { "energy", "能量" }, { "money", "钱" },
            // 状态/属性
            { "is", "是否" }, { "can", "可" }, { "has", "有" }, { "enable", "启用" }, { "enabled", "启用" },
            { "active", "激活" }, { "run", "运行" }, { "running", "运行中" }, { "over", "结束" }, { "done", "完成" },
            { "sleep", "睡眠" }, { "angry", "愤怒" }, { "invincible", "无敌" }, { "alive", "存活" }, { "dead", "死亡" },
            { "near", "近" }, { "death", "死亡" }, { "flag", "标志" }, { "flags", "标志" }, { "type", "类型" },
            { "id", "编号" }, { "mode", "模式" }, { "state", "状态" }, { "phase", "阶段" }, { "tag", "标签" },
            { "index", "索引" }, { "layer", "层" }, { "group", "组" }, { "camp", "阵营" }, { "into", "入" }, { "water", "水" },
            // 卡牌/配置
            { "packet", "卡牌" }, { "character", "角色" }, { "config", "配置" }, { "override", "覆盖" }, { "night", "夜间" },
            { "imitate", "模仿" }, { "copy", "复制" }, { "collision", "碰撞" }, { "mask", "掩码" }, { "buff", "增益" },
            { "physique", "体型" }, { "element", "元素" }, { "hypnoses", "魅惑" }, { "hypnosis", "魅惑" },
            { "turn", "回合" }, { "round", "回合" }, { "stage", "阶段" }, { "chapter", "章节" }, { "main", "主" },
            // 方位/显示/杂项
            { "body", "身体" }, { "head", "头部" }, { "left", "左" }, { "right", "右" }, { "top", "上" },
            { "bottom", "下" }, { "front", "前" }, { "back", "后" }, { "center", "中心" }, { "color", "颜色" },
            { "alpha", "透明度" }, { "opacity", "透明度" }, { "visible", "可见" }, { "show", "显示" }, { "hide", "隐藏" },
            { "name", "名称" }, { "desc", "描述" }, { "text", "文本" }, { "title", "标题" }, { "key", "键" },
            { "data", "数据" }, { "info", "信息" }, { "last", "最后" }, { "first", "第一" }, { "next", "下一个" },
            { "prev", "上一个" }, { "once", "一次" }, { "again", "再次" }, { "use", "使用" }, { "un", "未" },
            { "component", "组件" }, { "texture", "贴图" }, { "sprite", "精灵图" }, { "path", "路径" }, { "node", "节点" },
            { "area", "区域" }, { "cell", "格子" }, { "row", "行" }, { "column", "列" }, { "grid", "网格" },
            { "team", "队伍" }, { "friendly", "友方" }, { "enemy", "敌方" }, { "charge", "充能" }, { "mana", "蓝量" },
            { "stamina", "体力" }, { "magic", "魔法" }, { "slot", "槽位" }, { "icon", "图标" }, { "sound", "音效" },
            { "music", "音乐" }, { "font", "字体" }, { "effect", "特效" }, { "particle", "粒子" }, { "animation", "动画" },
            { "anim", "动画" }, { "frame", "帧" }, { "fps", "帧率" }, { "startup", "启动" }, { "refresh", "刷新" },
        };

        static string TranslateField(string field)
        {
            string name = field;
            while (name.StartsWith("_")) name = name.Substring(1);
            if (string.IsNullOrEmpty(name)) return field;
            var parts = SplitWords(name);
            var sb = new StringBuilder();
            int translated = 0;
            foreach (var p in parts)
            {
                if (WordZh.TryGetValue(p, out var z)) { sb.Append(z); translated++; }
                else sb.Append(p);
            }
            if (translated == 0) return field;  // 一个词都没翻译 → 原样返回
            return sb.ToString();
        }

        // camelCase / 大写缩写 / 数字 拆词：AttackSpeed→Attack|Speed, HP→HP, grabOver→grab|Over
        static List<string> SplitWords(string s)
        {
            var list = new List<string>();
            var cur = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsUpper(c) && cur.Length > 0)
                {
                    char prev = cur[cur.Length - 1];
                    if (char.IsLower(prev) || char.IsDigit(prev))
                    {
                        list.Add(cur.ToString());
                        cur.Clear();
                    }
                }
                else if (char.IsDigit(c) && cur.Length > 0 && !char.IsDigit(cur[cur.Length - 1]))
                {
                    list.Add(cur.ToString());
                    cur.Clear();
                }
                cur.Append(c);
            }
            if (cur.Length > 0) list.Add(cur.ToString());
            return list;
        }

        // 字段类型名中文化
        static string TypeLabel(string t) => t switch
        {
            "float" or "double" => "小数",
            "int" or "uint" or "long" or "byte" or "short" => "整数",
            "bool" => "开关",
            "string" => "文本",
            _ => t,
        };

        async Task LoadTypeProps(StackPanel content, string id, string name)
        {
            var cfgR = await GetAsync("/cfgprops?id=" + Uri.EscapeDataString(id));
            var propsR = await GetAsync("/tprops?type=" + Uri.EscapeDataString(id));
            var g = await GetAsync("/gget");
            var overrides = new Dictionary<string, string>();
            if (g != null && g.StartsWith("["))
            {
                try
                {
                    foreach (var el in JsonSerializer.Deserialize<JsonElement>(g).EnumerateArray())
                        if (el.GetProperty("type").GetString() == id)
                            overrides[el.GetProperty("p").GetString() ?? ""] = el.GetProperty("v").ToString();
                }
                catch { }
            }
            content.Children.Clear();
            content.Children.Add(MakeText(name + "（" + id + "）", 13, FontWeights.Bold, (Brush)FindResource("TextBrush")));

            // 区块1：卡牌/角色配置（改配置 → 影响该类新生成的角色）
            content.Children.Add(MakeText("卡牌/角色配置（影响新生成）", 12, FontWeights.Bold, (Brush)FindResource("AccentBrush"), new Thickness(0, 6, 0, 6)));
            var cfgItems = new List<(string F, string T, string V)>();
            if (cfgR != null && cfgR.StartsWith("["))
            {
                try
                {
                    int n = 0;
                    foreach (var el in JsonSerializer.Deserialize<JsonElement>(cfgR).EnumerateArray())
                    {
                        string f = el.GetProperty("f").GetString() ?? "";
                        string ft = el.GetProperty("t").GetString() ?? "";
                        string v = el.GetProperty("v").ValueKind == JsonValueKind.String ? el.GetProperty("v").GetString() ?? "" : el.GetProperty("v").ToString();
                        cfgItems.Add((f, ft, v));
                        if (++n >= 40) break;
                    }
                }
                catch { }
            }
            else
            {
                content.Children.Add(MakeText("无法读取该类型配置（未连接或该类型无配置）", 11, FontWeights.Normal, (Brush)FindResource("SubBrush")));
            }
            for (int i = 0; i < cfgItems.Count; i += 20)
            {
                int end = Math.Min(i + 20, cfgItems.Count);
                for (int j = i; j < end; j++)
                    content.Children.Add(MakeCfgPropRow(id, cfgItems[j].F, FieldLabel(cfgItems[j].F), cfgItems[j].T, cfgItems[j].V));
                await Task.Delay(1);
            }

            // 区块2：专属字段（当前场上该类实例，即时生效）
            content.Children.Add(MakeText("专属字段（当前场上该类实例）", 12, FontWeights.Bold, (Brush)FindResource("AccentBrush"), new Thickness(0, 10, 0, 6)));
            var propItems = new List<(string F, string T, string V)>();
            if (propsR != null && propsR.StartsWith("["))
            {
                try
                {
                    int n = 0;
                    foreach (var el in JsonSerializer.Deserialize<JsonElement>(propsR).EnumerateArray())
                    {
                        string f = el.GetProperty("f").GetString() ?? "";
                        string ft = el.GetProperty("t").GetString() ?? "";
                        overrides.TryGetValue(f, out var cur);
                        propItems.Add((f, ft, cur ?? ""));
                        if (++n >= 60) break;
                    }
                }
                catch { }
            }
            for (int i = 0; i < propItems.Count; i += 20)
            {
                int end = Math.Min(i + 20, propItems.Count);
                for (int j = i; j < end; j++)
                    content.Children.Add(MakeTypePropRow(id, propItems[j].F, FieldLabel(propItems[j].F), propItems[j].T, propItems[j].V));
                await Task.Delay(1);
            }
        }

        // 配置层属性行：改 TowerDefensePacketConfig / CharacterConfig 字段（影响新生成），无恢复（直接改配置对象）
        Border MakeCfgPropRow(string id, string prop, string label, string fieldType, string curVal)
        {
            var row = new Border
            {
                Background = (Brush)FindResource("CardBrushW"),
                BorderBrush = (Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 0, 6),
            };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var sp = new StackPanel();
            var nm = MakeText(label, 12, FontWeights.Bold, (Brush)FindResource("TextBrush"));
            nm.TextTrimming = TextTrimming.CharacterEllipsis;
            sp.Children.Add(nm);
            sp.Children.Add(MakeText(prop + "  ·  " + TypeLabel(fieldType), 9, FontWeights.Normal, (Brush)FindResource("SubBrush"), new Thickness(0, 1, 0, 0)));
            g.Children.Add(sp);
            if (fieldType == "bool")
            {
                var sw = new ToggleSwitch { IsOn = curVal == "true" || curVal == "1", VerticalAlignment = VerticalAlignment.Center };
                sw.Command = on => _ = SendCfgSet(id, prop, on ? "1" : "0");
                Grid.SetColumn(sw, 1);
                g.Children.Add(sw);
            }
            else
            {
                var tb = new TextBox { Text = curVal, Style = (Style)FindResource("GlassTextBox"), VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
                Grid.SetColumn(tb, 1);
                g.Children.Add(tb);
                var ok = MakeMiniBtn("应用");
                ok.Margin = new Thickness(6, 0, 0, 0);
                ok.Click += async (s, e) => await SendCfgSet(id, prop, tb.Text);
                Grid.SetColumn(ok, 2);
                g.Children.Add(ok);
            }
            row.Child = g;
            return row;
        }

        async Task SendCfgSet(string id, string field, string val)
        {
            var r = await GetAsync("/cfgset?id=" + Uri.EscapeDataString(id) + "&f=" + Uri.EscapeDataString(field) + "&v=" + Uri.EscapeDataString(val));
            if (r == "ok") { Log("配置 " + id + " " + FieldLabel(field) + " = " + val); SetStatus(true, "已连接"); }
            else { Log("配置修改失败: " + id + " " + field); }
        }

        Border MakeTypePropRow(string id, string prop, string label, string fieldType, string curVal)
        {
            var row = new Border
            {
                Background = (Brush)FindResource("CardBrushW"),
                BorderBrush = (Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 0, 6),
            };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var sp = new StackPanel();
            var nm = MakeText(label, 12, FontWeights.Bold, (Brush)FindResource("TextBrush"));
            nm.TextTrimming = TextTrimming.CharacterEllipsis;
            sp.Children.Add(nm);
            sp.Children.Add(MakeText(prop + "  ·  " + TypeLabel(fieldType), 9, FontWeights.Normal, (Brush)FindResource("SubBrush"), new Thickness(0, 1, 0, 0)));
            g.Children.Add(sp);
            if (fieldType == "bool")
            {
                // 布尔型属性：用开关
                var sw = new ToggleSwitch { IsOn = curVal == "1", VerticalAlignment = VerticalAlignment.Center };
                sw.Command = on => _ = SendGlobalSet(id, prop, on ? "1" : "0");
                Grid.SetColumn(sw, 1);
                g.Children.Add(sw);
                var rm2 = MakeMiniBtn("恢复");
                rm2.Margin = new Thickness(6, 0, 0, 0);
                rm2.Click += async (s, e) => await SendGlobalRemove(id, prop);
                Grid.SetColumn(rm2, 2);
                g.Children.Add(rm2);
            }
            else
            {
                var tb = new TextBox { Text = curVal, Style = (Style)FindResource("GlassTextBox"), VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
                Grid.SetColumn(tb, 1);
                g.Children.Add(tb);
                var ok = MakeMiniBtn("应用");
                ok.Margin = new Thickness(6, 0, 0, 0);
                ok.Click += async (s, e) => await SendGlobalSet(id, prop, tb.Text);
                Grid.SetColumn(ok, 2);
                g.Children.Add(ok);
                var rm = MakeMiniBtn("恢复");
                rm.Margin = new Thickness(6, 0, 0, 0);
                rm.Click += async (s, e) => await SendGlobalRemove(id, prop);
                Grid.SetColumn(rm, 3);
                g.Children.Add(rm);
            }
            row.Child = g;
            return row;
        }

        async Task SendGlobalSet(string type, string prop, string val)
        {
            var r = await GetAsync("/gset?type=" + Uri.EscapeDataString(type) + "&prop=" + prop + "&val=" + Uri.EscapeDataString(val));
            if (r == "ok") { Log("全局覆盖 " + type + " " + prop + " = " + val); SetStatus(true, "已连接"); }
            else { Log("全局覆盖设置失败: " + type + " " + prop); }
        }

        async Task SendGlobalRemove(string type, string prop)
        {
            var r = await GetAsync("/gremove?type=" + Uri.EscapeDataString(type) + "&prop=" + prop);
            if (r == "ok") { Log("已恢复 " + type + " " + prop); SetStatus(true, "已连接"); }
            else { Log("恢复失败: " + type + " " + prop); }
        }

        async Task LoadEntities(WrapPanel wp)
        {
            var r = await GetAsync("/entities");
            wp.Children.Clear();
            if (r == null || !r.StartsWith("["))
            {
                wp.Children.Add(MakeText("无法获取实体列表（未连接）", 12, FontWeights.Normal, (Brush)FindResource("SubBrush")));
                return;
            }
            if (r == "[]")
            {
                wp.Children.Add(MakeText("场上暂无植物/僵尸（进入战斗后这里会显示）", 12, FontWeights.Normal, (Brush)FindResource("SubBrush")));
                return;
            }
            try
            {
                var arr = JsonSerializer.Deserialize<JsonElement>(r);
                int i = 0;
                foreach (var el in arr.EnumerateArray())
                {
                    long id = el.GetProperty("id").GetInt64();
                    string name = el.GetProperty("name").GetString() ?? "";
                    string camp = el.GetProperty("camp").GetString() ?? "";
                    string x = el.GetProperty("x").GetString() ?? "0";
                    string y = el.GetProperty("y").GetString() ?? "0";
                    wp.Children.Add(MakeEntityCard(id, name, camp, x, y, ++i));
                }
            }
            catch { }
        }

        Border MakeEntityCard(long id, string name, string camp, string x, string y, int idx)
        {
            var card = new Border
            {
                Background = (Brush)FindResource("CardBrushW"),
                BorderBrush = (Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 0, 10, 10),
                Cursor = System.Windows.Input.Cursors.Hand,
                Width = 215,
                Effect = FindResource("SoftShadow") as System.Windows.Media.Effects.Effect,
            };
            var sp = new StackPanel();
            sp.Children.Add(MakeText(name, 13, FontWeights.Bold, (Brush)FindResource("TextBrush")));
            sp.Children.Add(MakeText((camp == "Plant" ? "植物" : "僵尸") + "  #" + idx + "  (" + x + ", " + y + ")", 10, FontWeights.Normal, (Brush)FindResource("SubBrush"), new Thickness(0, 3, 0, 0)));
            card.Child = sp;
            card.MouseLeftButtonUp += (s, e) => OpenEntityProps(id, name, camp, x, y);
            return card;
        }

        void OpenEntityProps(long id, string name, string camp, string x, string y)
        {
            var w = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true,
                ShowInTaskbar = false,
                Width = 440,
                Height = 600,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
            };
            var card = new Border
            {
                Background = (Brush)FindResource("GlassBrush"),
                BorderBrush = (Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(18),
            };
            var outer = new Grid();
            outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = MakeText(name + "  ·  " + (camp == "Plant" ? "植物" : "僵尸") + "  (" + x + ", " + y + ")", 14, FontWeights.Bold, (Brush)FindResource("TextBrush"));
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            head.Children.Add(title);
            var close = new Button { Content = "✕", Style = (Style)FindResource("CloseButton"), Width = 28, Height = 28 };
            Grid.SetColumn(close, 1);
            head.Children.Add(close);
            head.MouseLeftButtonDown += (s, e) => { try { w.DragMove(); } catch { } };
            outer.Children.Add(head);
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var content = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            scroll.Content = content;
            Grid.SetRow(scroll, 1);
            outer.Children.Add(scroll);
            card.Child = outer;
            w.Content = card;
            w.Show();
            close.Click += (s, e) => w.Close();
            _ = LoadEntityProps(w, content, id);
        }

        async Task LoadEntityProps(Window w, StackPanel content, long id)
        {
            var r = await GetAsync("/eprops?id=" + id);
            content.Children.Clear();
            if (r == null || !r.StartsWith("["))
            {
                content.Children.Add(MakeText(r != null && r.Contains("not-found") ? "该实体已消失，请返回刷新列表" : "无法获取属性（未连接）", 12, FontWeights.Normal, (Brush)FindResource("SubBrush")));
                return;
            }
            var items = new List<(string F, string T, string V)>();
            try
            {
                var arr = JsonSerializer.Deserialize<JsonElement>(r);
                int rows = 0;
                foreach (var el in arr.EnumerateArray())
                {
                    string f = el.GetProperty("f").GetString() ?? "";
                    string t = el.GetProperty("t").GetString() ?? "";
                    string v = el.GetProperty("v").ValueKind == JsonValueKind.String ? el.GetProperty("v").GetString() ?? "" : el.GetProperty("v").ToString();
                    items.Add((f, t, v));
                    if (++rows >= 250) break;
                }
            }
            catch { }
            // 分批渲染：一次性创建几百个控件会把 UI 卡死，每批 20 行让界面保持响应
            for (int i = 0; i < items.Count; i += 20)
            {
                int end = Math.Min(i + 20, items.Count);
                for (int j = i; j < end; j++)
                    content.Children.Add(MakeEntityPropRow(id, items[j].F, items[j].T, items[j].V));
                await Task.Delay(1);
            }
        }

        Border MakeEntityPropRow(long id, string field, string type, string value)
        {
            var row = new Border
            {
                Background = (Brush)FindResource("CardBrushW"),
                BorderBrush = (Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 0, 6),
            };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var sp = new StackPanel();
            var nm = MakeText(field, 12, FontWeights.Bold, (Brush)FindResource("TextBrush"));
            nm.TextTrimming = TextTrimming.CharacterEllipsis;
            sp.Children.Add(nm);
            sp.Children.Add(MakeText(type, 9, FontWeights.Normal, (Brush)FindResource("SubBrush"), new Thickness(0, 1, 0, 0)));
            g.Children.Add(sp);
            if (type == "bool")
            {
                var sw = new ToggleSwitch { IsOn = value == "true" || value == "1", VerticalAlignment = VerticalAlignment.Center };
                sw.Command = on => _ = SendEntitySet(id, field, on ? "1" : "0");
                Grid.SetColumn(sw, 1);
                g.Children.Add(sw);
            }
            else
            {
                var eb = new Grid();
                eb.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
                eb.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var tb = new TextBox { Text = value, Style = (Style)FindResource("GlassTextBox"), VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
                Grid.SetColumn(tb, 0);
                eb.Children.Add(tb);
                var ok = MakeMiniBtn("应用");
                ok.Margin = new Thickness(6, 0, 0, 0);
                ok.Click += async (s, e) => await SendEntitySet(id, field, tb.Text);
                Grid.SetColumn(ok, 1);
                eb.Children.Add(ok);
                Grid.SetColumn(eb, 1);
                g.Children.Add(eb);
            }
            row.Child = g;
            return row;
        }

        async Task SendEntitySet(long id, string field, string value)
        {
            var r = await GetAsync("/eset?id=" + id + "&f=" + Uri.EscapeDataString(field) + "&v=" + Uri.EscapeDataString(value));
            if (r == "ok") { Log("实体属性 " + field + " = " + value); SetStatus(true, "已连接"); }
            else { Log("实体属性设置失败: " + field); }
        }

        // ================= 日志 =================
        void ShowLog()
        {
            ContentPanel.Children.Clear();
            var box = new Border
            {
                Background = (Brush)FindResource("CardBrushW"),
                BorderBrush = (Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(18),
                Padding = new Thickness(18),
            };
            var tb = new TextBlock
            {
                Text = _logLines.Count == 0 ? "（暂无日志）" : string.Join("\n", _logLines),
                Foreground = (Brush)FindResource("TextBrush"),
                FontFamily = (FontFamily)FindResource("MonoFont"),
                FontSize = 11, TextWrapping = TextWrapping.Wrap,
            };
            var scroll = new ScrollViewer { Content = tb, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 540 };
            box.Child = scroll;
            ContentPanel.Children.Add(box);
            AnimateIn(box);
        }

        // 联机界面见 MainWindow.Net.cs（大厅 / 创建房间 / 加入房间 / 我的房间）

        // （旧版单页联机面板已删除；新实现见 MainWindow.Net.cs）

        // ================= HTTP / 状态 =================
        // 全局串行化：所有 HTTP 请求一次只发一个。
        // MOD 端是单连接、响应后即断（Connection: close），
        // 若 /status 轮询与用户 /set 并发，请求会互相干扰导致 /set 丢失、开关被回滚弹回。
        static readonly SemaphoreSlim _httpGate = new SemaphoreSlim(1, 1);

        internal static async Task<string?> GetAsync(string path)
        {
            await _httpGate.WaitAsync();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var r = await _http.GetAsync(BASE + path);
                var s = await r.Content.ReadAsStringAsync();
                sw.Stop();
                try
                {
                    var brief = s;
                    if (brief != null && brief.Length > 160) brief = brief.Substring(0, 160) + "…";
                    _debugSink?.Invoke(path + "  [" + sw.ElapsedMilliseconds + "ms] 响应=" +
                        (r.StatusCode == System.Net.HttpStatusCode.OK ? "OK" : r.StatusCode.ToString()) + " " + brief);
                }
                catch { }
                return s;
            }
            catch (Exception ex)
            {
                sw.Stop();
                try { _debugSink?.Invoke(path + "  [失败 " + sw.ElapsedMilliseconds + "ms] " + ex.Message); } catch { }
                return null;
            }
            finally { _httpGate.Release(); }
        }

        void SetStatus(bool ok, string text)
        {
            _connected = ok;
            StatusDot.Fill = ok ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("DangerBrush");
            StatusText.Text = text;
            StatusText.Foreground = ok ? (Brush)FindResource("TextBrush") : (Brush)FindResource("SubBrush");
        }

        static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { }
        }

        // ================= 游戏窗口外功能提示（独立小窗，不遮挡游戏画面） =================
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        void EnsureTipWindow()
        {
            if (_featureTip != null) return;
            _featureTip = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true,
                ShowInTaskbar = false,
                ShowActivated = false,
                IsHitTestVisible = false,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
            };
            _featureTip.Content = new TextBlock
            {
                FontFamily = (FontFamily)FindResource("AppFont"),
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Padding = new Thickness(10, 5, 10, 5),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = 0.8, Color = Colors.Black },
            };
            _tipTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
            _tipTimer.Tick += (s, e) => { _tipTimer.Stop(); FadeOutTip(); };
            // 文字炫彩律动（色相轮转，无外框纯文字）
            _tipColorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _tipColorTimer.Tick += (s, e) =>
            {
                if (_featureTip == null || !(_featureTip.Content is TextBlock tb)) return;
                _tipHue += 0.018f; if (_tipHue > 1f) _tipHue -= 1f;
                tb.Foreground = new SolidColorBrush(HsvToRgb(_tipHue, 0.85f, 1f));
            };
        }

        static Rect FindGameWindow()
        {
            Rect result = Rect.Empty;
            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h)) return true;
                var sb = new StringBuilder(256);
                GetWindowText(h, sb, 256);
                if (sb.ToString().IndexOf("植物大战僵尸", StringComparison.Ordinal) >= 0)
                {
                    RECT r;
                    if (GetWindowRect(h, out r))
                        result = new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                }
                return true;
            }, IntPtr.Zero);
            return result;
        }

        void ShowFeatureToast(string text)
        {
            EnsureTipWindow();
            if (_featureTip == null) return;
            if (_featureTip.Content is TextBlock tb) tb.Text = text;
            // 固定显示在屏幕右上角（游戏外，不遮挡游戏画面）
            double w = 300, h = 44;
            double x = SystemParameters.WorkArea.Right - w - 20;
            double y = SystemParameters.WorkArea.Top + 18;
            _featureTip.Left = x;
            _featureTip.Top = y;
            _featureTip.Opacity = 1;
            if (!_featureTip.IsVisible) _featureTip.Show();
            _featureTip.Topmost = true;
            _tipTimer?.Stop();
            _tipTimer?.Start();
            _tipColorTimer?.Stop();
            _tipColorTimer?.Start();
        }

        void FadeOutTip()
        {
            try
            {
                if (_featureTip == null || !_featureTip.IsVisible) return;
                _tipColorTimer?.Stop();
                var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(350));
                anim.Completed += (s, e) => { if (_featureTip != null) _featureTip.Hide(); };
                _featureTip.BeginAnimation(UIElement.OpacityProperty, anim);
            }
            catch { }
        }

        static Color HsvToRgb(double h, double s, double v)
        {
            int hi = (int)(h * 6) % 6;
            double f = h * 6 - Math.Floor(h * 6);
            double p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
            double r, g, b;
            switch (hi)
            {
                case 0: r = v; g = t; b = p; break;
                case 1: r = q; g = v; b = p; break;
                case 2: r = p; g = v; b = t; break;
                case 3: r = p; g = q; b = v; break;
                case 4: r = t; g = p; b = v; break;
                default: r = v; g = p; b = q; break;
            }
            return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        }

        void Log(string msg)
        {
            _logLines.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + msg);
            if (_logLines.Count > 300) _logLines.RemoveAt(0);
        }

        // ================= 调试模式（终端窗口） =================
        void ToggleDebug(bool on)
        {
            _debugMode = on;
            if (on) ShowDebugWindow();
            else CloseDebugWindow();
        }

        void ShowDebugWindow()
        {
            if (_debugWin != null) { _debugWin.Show(); _debugWin.Activate(); return; }
            var w = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true,
                ShowInTaskbar = false,
                Width = 580,
                Height = 400,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = SystemParameters.WorkArea.Right - 600,
                Top = SystemParameters.WorkArea.Bottom - 420,
            };
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x11, 0x15, 0x1B)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x35, 0x3F)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(12),
            };
            var outer = new Grid();
            outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = MakeText("🐞 调试终端（Debug）", 13, FontWeights.Bold, new SolidColorBrush(Color.FromRgb(0x7A, 0xE0, 0x9A)));
            head.Children.Add(title);
            var clearBtn = MakeMiniBtn("清空");
            clearBtn.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(clearBtn, 1);
            head.Children.Add(clearBtn);
            var closeBtn = MakeMiniBtn("关闭");
            closeBtn.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(closeBtn, 2);
            head.Children.Add(closeBtn);
            head.MouseLeftButtonDown += (s, e) => { try { w.DragMove(); } catch { } };
            outer.Children.Add(head);
            _debugTb = new TextBlock
            {
                FontFamily = (FontFamily)FindResource("MonoFont"),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0xD8, 0xE4, 0xEE)),
                TextWrapping = TextWrapping.NoWrap,
            };
            _debugScroll = new ScrollViewer
            {
                Content = _debugTb,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 10, 0, 0),
            };
            Grid.SetRow(_debugScroll, 1);
            outer.Children.Add(_debugScroll);
            card.Child = outer;
            w.Content = card;
            _debugWin = w;
            w.Show();
            closeBtn.Click += (s, e) => ToggleDebug(false);
            clearBtn.Click += (s, e) => { if (_debugTb != null) { _debugTb.Text = ""; _debugCount = 0; } };
            w.Closed += (s, e) =>
            {
                _debugWin = null; _debugTb = null; _debugScroll = null;
                if (_debugMode) { _debugMode = false; }
            };
            DebugLog("调试模式已开启（关闭需回到外置修改器关闭「调试模式」开关）");
        }

        void CloseDebugWindow()
        {
            _debugWin?.Close();
            _debugWin = null;
            _debugTb = null;
            _debugScroll = null;
        }

        // 详细日志：普通 Log + 追加到调试终端
        void DebugLog(string msg)
        {
            Log(msg);
            if (_debugWin == null || _debugTb == null) return;
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_debugTb == null) return;
                    _debugTb.Text += DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + "\n";
                    _debugScroll?.ScrollToEnd();
                    if (++_debugCount > 400)
                    {
                        int cut = _debugTb.Text.IndexOf('\n');
                        if (cut > 0) { _debugTb.Text = _debugTb.Text.Substring(cut + 1); _debugCount--; }
                    }
                }));
            }
            catch { }
        }














        // 冬天功能块顶部积雪（已删除：四季系统移除）
        int CountOn(Dictionary<string, object> d)
        {
            int n = 0;
            foreach (var v in d.Values)
                if (v is bool b && b) n++;
            return n;
        }

        // M5 在线统计（-1 = 未知）。数据源：游戏内 MOD 的 /netstatus ——
        // 它每 15 秒向中继刷一次并缓存，这里只是读缓存，不会增加中继负担。
        int _liveCount = -1, _todayCount = -1;

        /// <summary>刷新在线统计并更新状态栏。
        /// ★ 状态栏文案必须由这里统一写：OnAutoPoll 每 2.5 秒也会 SetStatus，
        ///   两边都写会把在线数冲掉（表现为在线数一闪就没了）。</summary>
        async Task RefreshOnlineAsync()
        {
            var raw = await GetAsync("/netstatus");
            if (string.IsNullOrEmpty(raw)) { SetStatus(true, "已连接"); return; }
            _liveCount = ParseIntField(raw, "live", _liveCount);
            _todayCount = ParseIntField(raw, "today", _todayCount);
            SetStatus(true, _liveCount >= 0
                ? "已连接 · 在线 " + _liveCount + " / 今日 " + _todayCount
                : "已连接");
        }

        /// <summary>从 "|key=value|key2=..." 形式的字段串里取整数值。</summary>
        static int ParseIntField(string raw, string key, int fallback)
        {
            string needle = "|" + key + "=";
            int i = raw.IndexOf(needle, StringComparison.Ordinal);
            if (i < 0) return fallback;
            int start = i + needle.Length;
            int end = raw.IndexOf('|', start);
            string v = end < 0 ? raw.Substring(start) : raw.Substring(start, end - start);
            return int.TryParse(v, out int n) ? n : fallback;
        }

        async Task OnAutoPoll()
        {
            var body = await GetAsync("/status");
            if (body == null || !body.StartsWith("{"))
            {
                // 只连续失败 2 次才显示未连接，避免单次瞬时超时误报（明明连着却提示未连接）
                if (++_pollFailStreak >= 2) SetStatus(false, "未连接");
                return;
            }
            _pollFailStreak = 0;
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (!_everSynced) { _everSynced = true; await SyncAllAsync(); }
            }
            catch { }
            // 连接成功的状态文案交给 RefreshOnlineAsync（它会带上在线数）
            await RefreshOnlineAsync();
        }

        async Task SyncAllAsync()
        {
            var body = await GetAsync("/get");
            if (body == null || !body.StartsWith("{")) { SetStatus(false, "未连接"); return; }
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                var map = new Dictionary<string, object>();
                foreach (var p in root.EnumerateObject())
                {
                    map[p.Name] = p.Value.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Number when p.Value.TryGetInt32(out var i) => (object)i,
                        JsonValueKind.Number when p.Value.TryGetDouble(out var dbl) => dbl,
                        _ => p.Value.ToString(),
                    };
                }
                _settings = map;
                foreach (var (name, sw) in _switches)
                {
                    if (map.TryGetValue(name, out var v) && v is bool b) sw.SetSilently(b);
                }
                _everSynced = true;
                var sdbg = new StringBuilder("首次同步 " + map.Count + " 项:");
                foreach (var kv in map)
                    if (kv.Value is bool) sdbg.Append(' ').Append(kv.Key).Append('=').Append(kv.Value);
                DebugLog(sdbg.ToString());
                Log("已从游戏同步 " + _switches.Count + " 项开关");
            }
            catch { }
        }

        async Task<bool> PingAsync()
        {
            if (_busy) return _connected;
            _busy = true;
            try
            {
                var r = await GetAsync("/ping");
                bool ok = r == "ok";
                if (ok && !_everSynced) { _everSynced = true; await SyncAllAsync(); }
                if (ok) await RefreshOnlineAsync();   // 手动检测也顺带刷新在线数
                else SetStatus(false, "未连接");
                return ok;
            }
            finally { _busy = false; }
        }

        async Task SetBool(string name, string zh, bool on)
        {
            // 调试模式是纯外置本地功能：不发给 MOD，直接切换终端窗口
            if (name == "DebugMode")
            {
                _settings[name] = on;
                if (_switches.TryGetValue(name, out var sw)) sw.SetSilently(on);
                ToggleDebug(on);
                return;
            }
            var r = await GetAsync($"/set?name={name}&val={(on ? "1" : "0")}");
            if (r == "queued")
            {
                // 成功后同步本地镜像：避免切分类重建时开关/滑块回退到旧值（“都变成关/都变成1”）。
                _settings[name] = on;
                if (_switches.TryGetValue(name, out var sw)) sw.SetSilently(on);
                Log(zh + " → " + (on ? "开" : "关")); SetStatus(true, "已连接");
                if (on) ShowFeatureToast(zh + " 已开启");   // 在游戏窗口外面弹出小提示
            }
            else
            {
                // 不再回滚弹回：保持用户选择的状态，只提示未连接，避免开关“自己又开了”
                Log("未连接，" + zh + " 未生效");
                SetStatus(false, "未连接");
            }
        }

        // 从本地镜像读取开关初始状态（进入分类重建界面时用，反映 MOD 实际开关）
        bool GetSettingBool(string name)
        {
            if (name == "DebugMode") return _debugMode;   // 调试模式是外置本地状态，MOD 不返回
            if (_settings.TryGetValue(name, out var v))
            {
                if (v is bool b) return b;
                var s = Convert.ToString(v);
                return s == "1" || s == "true" || s == "True";
            }
            return false;
        }

        async Task SetField(string name, string zh, double v)
        {
            var r = await GetAsync($"/set?name={name}&val={v.ToString("0.##", CultureInfo.InvariantCulture)}");
            if (r == "queued") { _settings[name] = v; Log(zh + " → " + v.ToString("0.##", CultureInfo.InvariantCulture)); SetStatus(true, "已连接"); }
            else { Log("发送失败（未连接）"); SetStatus(false, "未连接"); }
        }

        async Task SetField(string name, string zh, string v)
        {
            var r = await GetAsync("/set?name=" + name + "&val=" + Uri.EscapeDataString(v));
            if (r == "queued") { _settings[name] = v; Log(zh + " 已保存"); SetStatus(true, "已连接"); }
            else { Log("发送失败（未连接）"); SetStatus(false, "未连接"); }
        }

        async Task OnAction(string name, string zh)
        {
            if (name == "TrickPoolEditor") { TrickPoolWindow.Show(GetAsync); return; }
            if (name == "OpenCustomPlant") { CustomPlantWindow.ShowCustomPlant(GetAsync); return; }
            if (name == "CustomPlantSync") { _ = SyncAllCustomPlantsAsync(); return; }
            var r = await GetAsync("/cmd?name=" + name);
            if (r != null) { Log(zh + " 已执行"); SetStatus(true, "已连接"); }
            else { Log("执行失败（未连接）"); SetStatus(false, "未连接"); }
        }

        async Task SyncAllCustomPlantsAsync()
        {
            var dir = @"custom_plants";
            if (!System.IO.Directory.Exists(dir)) { DebugLog("自定义植物目录不存在: " + dir); return; }
            foreach (var cfg in System.IO.Directory.GetFiles(dir, "config.json", System.IO.SearchOption.AllDirectories))
            {
                var r = await GetAsync("/cmd?name=CustomPlantLoad&file=" + Uri.EscapeDataString(cfg));
                DebugLog("注入 " + cfg + " → " + r);
            }
        }

        // ================= 窗口 / 托盘 =================
        void ToggleVisibility()
        {
            if (IsVisible) Hide();
            else { Show(); Activate(); }
        }

        void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        void OnDetect(object sender, RoutedEventArgs e) => _ = PingAsync();
        void OnClose(object sender, RoutedEventArgs e) => ExitApp();   // 点 ✕ = 真正退出程序（不再隐藏到后台）
        void OnUserCenter(object sender, RoutedEventArgs e) { HighlightCat("user"); SelectCat("user"); }

        void BuildTray()
        {
            _tray = new WF.NotifyIcon
            {
                Text = "杂交版 外置修改器",
                Icon = System.Drawing.SystemIcons.Application,
                Visible = true,
            };
            var menu = new WF.ContextMenuStrip();
            menu.Items.Add("显示 / 隐藏", null, (s, e) => Dispatcher.Invoke(ToggleVisibility));
            menu.Items.Add("检测连接", null, (s, e) => Dispatcher.Invoke(async () => await PingAsync()));
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add("退出", null, (s, e) => Dispatcher.Invoke(ExitApp));
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (s, e) => Dispatcher.Invoke(ToggleVisibility);
        }

        void ExitApp()
        {
            _tray?.Dispose();
            Close();
            Application.Current.Shutdown();
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            if (WindowState == WindowState.Minimized) { Hide(); WindowState = WindowState.Normal; }
        }

        string FormatUptime()
        {
            var t = DateTime.Now - _startTime;
            if (t.TotalHours >= 1) return (int)t.TotalHours + " 小时 " + t.Minutes + " 分";
            if (t.TotalMinutes >= 1) return (int)t.TotalMinutes + " 分 " + t.Seconds + " 秒";
            return t.Seconds + " 秒";
        }
    }
}
