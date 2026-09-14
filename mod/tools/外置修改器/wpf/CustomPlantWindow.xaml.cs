using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace PvzheRemote;

// ============ 数据类（字段名与 MOD 侧 CustomPlantManager.ParseDef 严格一致，键大小写不敏感） ============

sealed class CustomPlantDef
{
    public string Name { get; set; } = "";
    public string Template { get; set; } = "shooter";
    public int Cost { get; set; } = 125;
    public double Cooldown { get; set; } = 7.5;
    public double Hp { get; set; } = 300;
    public double Scale { get; set; } = 1.0;
    public CustomAttackDef Attack { get; set; } = new();
    public CustomFrameDef Frame { get; set; } = new();
    // Task 5 绑定：图鉴名（默认=项目名）+ 关卡限制（逗号分隔；MOD 生效留验证，WPF 只存字段）
    public string DisplayName { get; set; } = "";
    public string LevelRestrict { get; set; } = "";
    // SP4 被动能力（MOD 读 passive 子对象：kind/value，键大小写不敏感）
    public PassiveDef Passive { get; set; } = new();
}

sealed class CustomAttackDef
{
    public double Damage { get; set; } = 40;
    public double Interval { get; set; } = 1.5;
    public double Range { get; set; } = 2000;
    public string Bullet { get; set; } = "Pea";
    public string Targets { get; set; } = "single";
    public int SunAmount { get; set; } = 25;
}

sealed class CustomFrameDef
{
    public string Idle { get; set; } = "idle.png";
    public string Attack { get; set; } = "attack.png";
}

/// <summary>植物被动能力（SP4）：Kind = none/sun/shield/slowaura/coin/buffaura；Value = 周期秒/半径。</summary>
sealed class PassiveDef
{
    public string Kind { get; set; } = "none";
    public double Value { get; set; } = 0;
}

// ============ 自制子弹数据类（字段名与 MOD 侧 CustomBulletDef 严格一致） ============

sealed class CustomBulletDef
{
    public string Name { get; set; } = "";
    public string Template { get; set; } = "Pea";
    public double Damage { get; set; } = 40;
    public double Speed { get; set; } = 600;
    public double Range { get; set; } = 2000;
    public int Penetrate { get; set; } = 0;
    public double Scale { get; set; } = 1.0;
    public string Effect { get; set; } = "none";
    public double EffectValue { get; set; } = 2.0;
    public string Audio { get; set; } = "Ignite";
    // SP8 命中特效：none/explode/freeze/lightning/fire（MOD 解析 splat 键）
    public string Splat { get; set; } = "none";
}

// ============ 自定义僵尸数据类（字段名与 MOD 侧 CustomZombieDef 严格一致；Template 存 MOD TEMPLATES 键） ============

sealed class CustomZombieDef
{
    public string Name { get; set; } = "";
    public string Template { get; set; } = "zombie_normal";   // 见 ZombieTemplates：显示键→存储键映射
    public double Hp { get; set; } = 200;
    public double Speed { get; set; } = 0.3;
    public double Damage { get; set; } = 100;
    public CustomFrameDef Frame { get; set; } = new();
    public double Scale { get; set; } = 1.0;
    public string DisplayName { get; set; } = "";   // Task 5 图鉴名
    // SP4 僵尸行为（MOD 读顶层 move/attack/rangedBullet/special/specialValue，键大小写不敏感）
    public string Move { get; set; } = "walk";          // walk/flight/burrow/dash
    public string Attack { get; set; } = "bite";        // bite/ranged/selfdestruct
    public string RangedBullet { get; set; } = "Pea";   // 远程子弹名（attack=ranged 时生效）
    public string Special { get; set; } = "none";       // none/summon/buff/split
    public double SpecialValue { get; set; } = 2.0;     // 技能数值（召唤间隔/分裂数等）
}

// ============ 自定义卡牌数据类（字段名与 MOD 侧 CustomCardDef 严格一致） ============

sealed class CustomCardDef
{
    public string Name { get; set; } = "";
    public int Cost { get; set; } = 125;
    public double Cooldown { get; set; } = 7.5;
    public string DisplayName { get; set; } = "";   // 图鉴名（默认=项目名）
    public string Desc { get; set; } = "";          // 图鉴描述
    public CustomFrameDef Frame { get; set; } = new();
}

public partial class CustomPlantWindow : Window
{
    readonly Func<string, Task<string?>> _get;
    static CustomPlantWindow _inst;

    // ---- 画板 ----
    readonly PixelCanvas _canvas = new();
    bool _frameAttack;
    // ---- 画布 chrome：分辨率 / 网格 / 标题 / 状态栏 / 自动 Fit ----
    int _lastRes = 64;
    bool _autoFitPending = true;
    (int X, int Y) _lastHover = (0, 0);
    System.Windows.Shapes.Rectangle _hoverCell = null!;
    // ---- 帧生成：结果缓存与预览 ----
    byte[][] _generatedFrames = Array.Empty<byte[]>();
    string _generatedFolder = "";

    // ---- 参数表单控件 ----
    TextBox _tbName = null!, _tbCost = null!, _tbCooldown = null!, _tbHp = null!,
            _tbDamage = null!, _tbInterval = null!, _tbRange = null!, _tbSun = null!,
            _tbDisplayName = null!, _tbLevelRestrict = null!, _tbPassiveValue = null!;
    ComboBox _cbTemplate = null!, _cbBullet = null!, _cbPassive = null!, _cbBTemplate = null!;
    TextBlock StatusText = null!;
    Panel _attackPanel = null!, _sunPanel = null!;
    Panel _plantPanel = null!, _bulletPanel = null!;
    string _editingPath = "";   // 当前载入 config 路径（用于重名覆盖确认）

    // ---- 类型系统（Task 4）：plant/zombie/bullet/card ----
    string _type = "plant";

    // ---- 子弹表单（Task 3 + SP8 特效音效） ----
    TextBox _tbBName = null!, _tbBDamage = null!, _tbBSpeed = null!, _tbBRange = null!,
            _tbBPenetrate = null!, _tbBScale = null!;
    ComboBox _cbEffect = null!, _cbSplat = null!, _cbAudio = null!;

    // ---- 僵尸表单（Task 4 + SP4 行为） ----
    Panel _zombiePanel = null!, _zBulletPanel = null!;
    TextBox _tbZName = null!, _tbZHp = null!, _tbZSpeed = null!, _tbZDamage = null!, _tbZDisplayName = null!,
            _tbZSpecialValue = null!;
    ComboBox _cbZTemplate = null!, _cbZMove = null!, _cbZAtk = null!, _cbZSpecial = null!, _cbZBullet = null!;

    // ---- 卡牌表单（Task 4） ----
    Panel _cardPanel = null!;
    TextBox _tbCName = null!, _tbCCost = null!, _tbCCooldown = null!, _tbCDisplayName = null!, _tbCDesc = null!;

    // ---- SP8 卡牌图标（四类型编辑面板各一个状态文字，key=type） ----
    readonly Dictionary<string, TextBlock> _iconStatus = new();

    const string BaseRoot = @".";
    const string PlantRoot = @"custom_plants";

    /// <summary>导出目录：custom_projects\导出（.cproj 包）。</summary>
    static string ExportRoot => Path.Combine(BaseRoot, "custom_projects", "导出");

    // ---- 分享分类页（SP7）：导出包列表 ----
    WrapPanel _shareList = null!;
    TextBlock _shareHint = null!;

    // 僵尸模板：Combo 项文本 = key:中文名（zombie_normal:普通僵尸…）；存储值 = 游戏模板 id（MOD TEMPLATES 键，PascalCase）。
    // 注意：MOD CustomZombieManager.TEMPLATES 键是 ZombieNormal/ZombieConehead/…（PascalCase），
    // CustomZombieLoad 若收到未知模板会返回 err:bad-template，故保存时必须映射为 PascalCase 存储值。
    static readonly (string Key, string Stored, string Label)[] ZombieTemplates = new[]
    {
        ("zombie_normal", "ZombieNormal", "普通僵尸"),
        ("zombie_conehead", "ZombieConehead", "路障"),
        ("zombie_buckethead", "ZombieBuckethead", "铁桶"),
        ("zombie_giant", "ZombieGiant", "巨人"),
        ("zombie_runner", "ZombieRunner", "奔跑"),
        ("zombie_bungi", "ZombieBungi", "飞贼"),
    };

    // 植物模板：内置攻击方式 key（snake_case）→ 游戏植物 id（与 MOD TEMPLATES 一致）；全量加载后用于保留当前选中
    static readonly (string Key, string Id)[] PlantTemplateMap = new[]
    {
        ("shooter", "PlantPeaShooter"), ("catapult", "PlantCabbagepult"), ("cannon", "PlantCobCannon"),
        ("melee", "PlantChomper"), ("passive", "PlantSunFlower"), ("spike", "PlantCaltrop"),
        ("umbrella", "PlantUmbreallleaf"), ("magnet", "PlantMagnetShroom"), ("squash", "PlantSquash"),
        ("iceberg", "PlantIcePea"), ("doom", "PlantDoomShroom"), ("fume", "PlantFumeShroom"), ("puff", "PlantPuffShroom"),
    };

    /// <summary>目录约定（与 MOD CustomProjectManager.GetTypeDir 一致）：plant 兼容旧 custom_plants。</summary>
    static string GetTypeDir(string type) => type switch
    {
        "plant" => Directory.Exists(PlantRoot) ? PlantRoot : System.IO.Path.Combine(BaseRoot, "custom_projects", "plant"),
        "zombie" => System.IO.Path.Combine(BaseRoot, "custom_projects", "zombie"),
        "bullet" => System.IO.Path.Combine(BaseRoot, "custom_projects", "bullet"),
        "card" => System.IO.Path.Combine(BaseRoot, "custom_projects", "card"),
        _ => System.IO.Path.Combine(BaseRoot, "custom_projects", type),
    };

    public CustomPlantWindow(Func<string, Task<string?>> get)
    {
        InitializeComponent();
        _get = get;
        SetupCanvas();
        InitCanvasChrome();
        BuildColorBar();
        BuildParamForm(ParamPanel);
        UpdatePassiveVisibility();
        SetProjectType("plant");   // 统一入口：左栏列表 + 右栏面板 + 画板帧模式
        _ = LoadBulletTypesAsync();   // Task 1：fire-and-forget 拉全量子弹库（失败回退内置 5 个）
        _ = LoadZombieTemplatesAsync();   // SP6：fire-and-forget 拉全量僵尸模板（失败回退内置 6 个）
        _ = LoadPlantTemplatesAsync();    // 全量游戏植物模板（任意游戏内植物可作自定义植物模板）
        BuildShareView();   // SP7：分享分类页（导出包列表 + 导入/导出全部）
    }

    public static void ShowCustomPlant(Func<string, Task<string?>> get)
    {
        if (_inst == null || !_inst.IsLoaded) _inst = new CustomPlantWindow(get);
        _inst.Show();
        _inst.Activate();
    }

    // ================= 窗口：拖动 / 关闭 =================

    void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    void OnWinClose(object sender, RoutedEventArgs e) => Close();

    // ================= 画板：输入转发 =================

    void SetupCanvas()
    {
        _canvas.Width = 480;
        _canvas.Height = 480;
        _canvas.PixelChanged += (x, y) => { CanvasImage.Source = _canvas.Bitmap; UpdatePreview(); };
        CanvasImage.Source = _canvas.Bitmap;
        // 输入表面是 CanvasImage（PixelCanvas 仅作位图引擎，不在可视树）；坐标经公开 API 转发给画板
        // 笔画级撤销：按下压快照、抬起结束，拖动不重复压栈（对应 PixelCanvas.BeginStroke/EndStroke）
        CanvasImage.MouseLeftButtonDown += (s, e) => { _canvas.BeginStroke(); _canvas.ResetStroke(); OnCanvasMouse(s, e); };
        CanvasImage.MouseMove += (s, e) => { if (e.LeftButton == MouseButtonState.Pressed) OnCanvasMouse(s, e); };
        CanvasImage.MouseRightButtonDown += (s, e) => { _canvas.BeginStroke(); _canvas.ResetStroke(); PaintAt(e.GetPosition(CanvasImage), Colors.Transparent); };
        CanvasImage.MouseLeftButtonUp += (s, e) => { _canvas.EndStroke(); _canvas.ResetStroke(); };
        CanvasImage.MouseRightButtonUp += (s, e) => { _canvas.EndStroke(); _canvas.ResetStroke(); };
        // 初始化笔刷大小与工具按钮 UI
        try { _canvas.BrushSize = (int)PenSizeSlider.Value; } catch { _canvas.BrushSize = 1; }
        UpdateToolButtons();
    }

    void OnCanvasMouse(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(CanvasImage);
        if (CanvasImage.ActualWidth <= 0 || CanvasImage.ActualHeight <= 0) return;
        var hg = ToGrid(pos); _lastHover = (hg.x, hg.y); UpdateHoverCell();
        if (_canvas.Tool == CanvasTool.Eyedrop)
        {
            var g = ToGrid(pos);
            _canvas.CurrentColor = _canvas.Pick(g.x, g.y);
            var c = _canvas.CurrentColor;
            CanvasHint.Text = "已吸色 #" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2") + " · 左键画 · 右键擦";
            return;
        }
        if (_canvas.Tool == CanvasTool.Fill)
        {
            var g = ToGrid(pos);
            _canvas.FillRegion(g.x, g.y, _canvas.CurrentColor);
            return;
        }
        PaintAt(pos, _canvas.Tool == CanvasTool.Eraser ? Colors.Transparent : _canvas.CurrentColor);
    }

    void PaintAt(Point pos, Color color)
    {
        var g = ToGrid(pos);
        if (g.x < 0 || g.y < 0 || g.x >= _canvas.GridSize || g.y >= _canvas.GridSize) { _canvas.ResetStroke(); return; }
        _canvas.StrokeTo(g.x, g.y, color);
    }

    (int x, int y) ToGrid(Point pos)
    {
        int gx = (int)(pos.X / CanvasImage.ActualWidth * _canvas.GridSize);
        int gy = (int)(pos.Y / CanvasImage.ActualHeight * _canvas.GridSize);
        return (gx, gy);
    }

    // ================= 帧切换 / 工具栏 =================

    void BuildColorBar()
    {
        var swatches = new (string, Color)[]
        {
            ("红", Colors.Red), ("橙", Colors.Orange), ("金", Colors.Gold), ("绿", Colors.LimeGreen),
            ("青", Colors.Cyan), ("蓝", Colors.DodgerBlue), ("紫", Colors.Purple), ("粉", Colors.DeepPink),
            ("棕", Colors.SaddleBrown), ("黑", Colors.Black), ("白", Colors.White), ("灰", Colors.Gray),
        };
        foreach (var (name, c) in swatches)
        {
            var b = new Border
            {
                Width = 26, Height = 26, Margin = new Thickness(3, 0, 3, 0),
                Background = new SolidColorBrush(c),
                BorderBrush = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(5),
                Cursor = Cursors.Hand,
                Tag = c,
                ToolTip = name,
            };
            b.MouseLeftButtonDown += (s, e) => { _canvas.CurrentColor = (Color)((Border)s).Tag; UpdateColorBarHighlight(); CanvasHint.Text = "画笔 · 当前颜色 " + name; };
            ColorBar.Children.Add(b);
        }
        UpdateColorBarHighlight();
    }
    void OnPenSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _canvas.BrushSize = Math.Max(1, (int)PenSizeSlider.Value);
        CanvasHint.Text = $"当前笔刷: {_canvas.BrushSize} px · 左键画 · 右键擦";
    }

    void UpdateColorBarHighlight()
    {
        foreach (var child in ColorBar.Children)
        {
            if (child is Border b && b.Tag is Color c)
                b.BorderBrush = new SolidColorBrush(c == _canvas.CurrentColor ? Colors.White : Colors.Transparent);
        }
        try { ColorPreview.Background = new SolidColorBrush(_canvas.CurrentColor); } catch { }
    }

    void UpdateToolButtons()
    {
        Brush act = (Brush)FindResource("AccentBrush");
        Brush none = new SolidColorBrush(Colors.Transparent);
        BtnPen.BorderBrush = _canvas.Tool == CanvasTool.Pen ? act : none;
        BtnEraser.BorderBrush = _canvas.Tool == CanvasTool.Eraser ? act : none;
        BtnEyedrop.BorderBrush = _canvas.Tool == CanvasTool.Eyedrop ? act : none;
        BtnFill.BorderBrush = _canvas.Tool == CanvasTool.Fill ? act : none;
        try { UpdateCanvasChrome(); } catch { }
    }

    void OnFrameIdle(object sender, RoutedEventArgs e) { _frameAttack = false; UpdateFrameUi(); UpdatePreview(); }
    void OnFrameAttack(object sender, RoutedEventArgs e) { _frameAttack = true; UpdateFrameUi(); UpdatePreview(); }

    void UpdateFrameUi()
    {
        FrameIdleBtn.Style = (Style)FindResource(_frameAttack ? "CardBtn" : "CardBtnActive");
        FrameAttackBtn.Style = (Style)FindResource(_frameAttack ? "CardBtnActive" : "CardBtn");
        bool single = _type == "bullet" || _type == "card";
        CanvasHint.Text = single ? "单帧图 · 左键画 · 右键擦"
            : (_frameAttack ? "攻击帧" : "待机帧") + " · 左键画 · 右键擦";
        try { UpdateCanvasChrome(); } catch { }
    }

    void OnToolPen(object s, RoutedEventArgs e) { _canvas.Tool = CanvasTool.Pen; CanvasHint.Text = "画笔 · 左键画 · 右键擦"; }
    void OnToolEraser(object s, RoutedEventArgs e) { _canvas.Tool = CanvasTool.Eraser; CanvasHint.Text = "橡皮 · 左键擦除"; }
    void OnToolEyedrop(object s, RoutedEventArgs e) { _canvas.Tool = CanvasTool.Eyedrop; CanvasHint.Text = "吸色 · 点击画布取色"; }
    void OnToolFill(object s, RoutedEventArgs e) { _canvas.Tool = CanvasTool.Fill; CanvasHint.Text = "填充 · 点击填充同色区域"; }
    
    void OnToolPenUpdated(object s, RoutedEventArgs e) { OnToolPen(s, e); UpdateToolButtons(); }
    void OnToolEraserUpdated(object s, RoutedEventArgs e) { OnToolEraser(s, e); UpdateToolButtons(); }
    void OnToolEyedropUpdated(object s, RoutedEventArgs e) { OnToolEyedrop(s, e); UpdateToolButtons(); }
    void OnToolFillUpdated(object s, RoutedEventArgs e) { OnToolFill(s, e); UpdateToolButtons(); }

    // ================ 缩放 ================
    void OnZoomChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        try
        {
            double v = ZoomSlider.Value;
            CanvasScale.ScaleX = v; CanvasScale.ScaleY = v;
            if (GridScale != null) { GridScale.ScaleX = v; GridScale.ScaleY = v; }
            if (CheckerScale != null) { CheckerScale.ScaleX = v; CheckerScale.ScaleY = v; }
            ZoomLabel.Text = ((int)(v * 100)).ToString() + "%";
            DrawGridOverlay();
            UpdateCanvasChrome();
            UpdateCanvasHostSize();
        }
        catch { }
    }

    /// <summary>让画布宿主的布局尺寸跟随缩放（RenderTransform 不影响布局，需手动同步，ScrollViewer 才能正确居中/滚动）。</summary>
    void UpdateCanvasHostSize()
    {
        try
        {
            double zoom = CanvasScale.ScaleX;
            double sz = _canvas.GridSize * zoom;
            CanvasHost.Width = sz;
            CanvasHost.Height = sz;
        }
        catch { }
    }

    // ================ 画布 chrome：分辨率 / 网格 / 标题 / 状态栏 / 自动 Fit ================

    void InitCanvasChrome()
    {
        try
        {
            if (_canvas.GridSize < 64) _canvas.Resize(128);   // 默认 128×128
            for (int i = 0; i < ResolutionBox.Items.Count; i++)
            {
                if (ResolutionBox.Items[i] is ComboBoxItem cbi && (string)cbi.Content == _canvas.GridSize.ToString())
                { ResolutionBox.SelectedIndex = i; break; }
            }
            _lastRes = _canvas.GridSize;
            try { CheckerRect.Width = CheckerRect.Height = _canvas.GridSize; } catch { }
            DrawGridOverlay();
            UpdateCanvasChrome();
            UpdateCanvasHostSize();
        }
        catch { }
    }

    void OnResolutionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResolutionBox.SelectedItem is not ComboBoxItem cbi) return;
        if (!int.TryParse((string)cbi.Content, out var n)) return;
        if (n == _lastRes) return;
        _lastRes = n;
        try
        {
            _canvas.Resize(n);
            CanvasImage.Source = _canvas.Bitmap;
            try { CheckerRect.Width = CheckerRect.Height = n; } catch { }
            DrawGridOverlay();
            UpdateCanvasChrome();
            UpdateCanvasHostSize();
            CanvasHint.Text = "分辨率已切换为 " + n + "×" + n + " · 左键画 · 右键擦";
            _autoFitPending = true;
        }
        catch (Exception ex) { StatusText.Text = "分辨率切换失败: " + ex.Message; }
    }

    void DrawGridOverlay()
    {
        try
        {
            if (GridOverlay == null) return;
            GridOverlay.Children.Clear();
            int n = _canvas.GridSize;
            GridOverlay.Width = GridOverlay.Height = n;
            double zoom = 0; try { zoom = CanvasScale.ScaleX; } catch { }
            bool showGrid = zoom >= 6.0 && n <= 256;   // 放大到很大才显示像素网格（Aseprite 风格）
            if (showGrid)
            {
                var line = new SolidColorBrush(Color.FromArgb(45, 0, 0, 0));
                for (int i = 1; i < n; i++)
                {
                    GridOverlay.Children.Add(new System.Windows.Shapes.Line
                    { X1 = i, Y1 = 0, X2 = i, Y2 = n, Stroke = line, StrokeThickness = 0.6 });
                    GridOverlay.Children.Add(new System.Windows.Shapes.Line
                    { X1 = 0, Y1 = i, X2 = n, Y2 = i, Stroke = line, StrokeThickness = 0.6 });
                }
            }
            if (_hoverCell == null)
            {
                _hoverCell = new System.Windows.Shapes.Rectangle
                {
                    Width = 1, Height = 1,
                    Stroke = new SolidColorBrush(Color.FromArgb(0x66, 0x34, 0xC7, 0x59)),
                    StrokeThickness = 1.2,
                    IsHitTestVisible = false,
                };
            }
            GridOverlay.Children.Add(_hoverCell);
            GridScale.ScaleX = CanvasScale.ScaleX;
            GridScale.ScaleY = CanvasScale.ScaleY;
            UpdateHoverCell();
        }
        catch { }
    }

    void UpdateHoverCell()
    {
        try
        {
            if (_hoverCell == null) return;
            int n = _canvas.GridSize;
            if (_lastHover.X < 0 || _lastHover.Y < 0 || _lastHover.X >= n || _lastHover.Y >= n)
            { _hoverCell.Visibility = Visibility.Collapsed; return; }
            Canvas.SetLeft(_hoverCell, _lastHover.X);
            Canvas.SetTop(_hoverCell, _lastHover.Y);
            _hoverCell.Visibility = Visibility.Visible;
        }
        catch { }
    }

    void UpdateCanvasChrome()
    {
        try
        {
            if (CanvasTitleText == null || StatusBarText == null) return;
            string frm = _type is "bullet" or "card" ? "单帧" : (_frameAttack ? "攻击帧" : "待机帧");
            CanvasTitleText.Text = $"画布 · {_canvas.GridSize}×{_canvas.GridSize} · {frm}";
            double z = 0; try { z = CanvasScale.ScaleX; } catch { }
            var c = _canvas.CurrentColor;
            StatusBarText.Text = $"坐标 ({_lastHover.X},{_lastHover.Y}) · 缩放 {(int)(z * 100)}% · 工具:{ToolZh(_canvas.Tool)} · 颜色 #{c.R:X2}{c.G:X2}{c.B:X2}";
        }
        catch { }
    }

    static string ToolZh(CanvasTool t) => t switch
    {
        CanvasTool.Pen => "画笔",
        CanvasTool.Eraser => "橡皮",
        CanvasTool.Eyedrop => "吸色",
        _ => "填充",
    };

    void OnCanvasScrollerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_autoFitPending) return;
        DoAutoFit();
    }

    void DoAutoFit()
    {
        try
        {
            var sv = CanvasScroller;
            double availW = sv.ActualWidth - 24, availH = sv.ActualHeight - 24;
            if (availW <= 0 || availH <= 0) { _autoFitPending = true; return; }
            double s = Math.Min(availW / _canvas.GridSize, availH / _canvas.GridSize);
            s = Math.Min(s, 8.0);
            if (s < 0.25) s = 0.25;
            ZoomSlider.Value = s;
            _autoFitPending = false;
        }
        catch { _autoFitPending = true; }
    }

    // ================ 自动生成帧（统一渲染核心 + 三模式） ================

    /// <summary>中心变换渲染一帧：translate(tx,ty)+rotate(angle)+scale(sx,sy)，输出 PNG bytes。</summary>
    static byte[] RenderFramePng(byte[] srcPng, int size,
        double tx, double ty, double angle, double sx, double sy)
    {
        var img = new BitmapImage();
        using (var ms = new MemoryStream(srcPng))
        {
            img.BeginInit(); img.CacheOption = BitmapCacheOption.OnLoad; img.StreamSource = ms; img.EndInit();
        }
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var c = new Point(size / 2.0, size / 2.0);
            dc.PushTransform(new TranslateTransform(c.X + tx, c.Y + ty));
            if (Math.Abs(angle) > 0.0001) dc.PushTransform(new RotateTransform(angle));
            if (Math.Abs(sx - 1.0) > 0.0001 || Math.Abs(sy - 1.0) > 0.0001)
                dc.PushTransform(new ScaleTransform(sx, sy));
            dc.PushTransform(new TranslateTransform(-c.X, -c.Y));
            dc.DrawImage(img, new Rect(0, 0, size, size));
            dc.Pop(); if (Math.Abs(sx - 1.0) > 0.0001 || Math.Abs(sy - 1.0) > 0.0001) dc.Pop();
            if (Math.Abs(angle) > 0.0001) dc.Pop(); dc.Pop();
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Default);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var ms2 = new MemoryStream();
        enc.Save(ms2);
        return ms2.ToArray();
    }

    /// <summary>插值帧：当前帧按 t∈[-0.5,0.5] 线性变换生成 n 帧 PNG。</summary>
    byte[][] GenInterpolatedFrames(byte[] srcPng, int n,
        string mode, double translateFactor, double rotateDegree, double scaleFactor)
    {
        int size = _canvas.GridSize;
        var frames = new byte[n][];
        for (int i = 0; i < n; i++)
        {
            double t = n == 1 ? 0 : (i - (n - 1) / 2.0) / (n - 1);
            double px = 0, py = 0, ang = 0, sx = 1, sy = 1;
            switch (mode)
            {
                case "translate": px = t * size * translateFactor; break;
                case "rotate": ang = t * rotateDegree; break;
                case "scale":
                    double s = Math.Pow(scaleFactor, t * 2); sx = s; sy = s; break;
                case "combo":
                    px = t * size * translateFactor; ang = t * rotateDegree;
                    double sc = Math.Pow(scaleFactor, t * 2); sx = sc; sy = sc; break;
                default: px = t * size * translateFactor; break;
            }
            frames[i] = RenderFramePng(srcPng, size, px, py, ang, sx, sy);
        }
        return frames;
    }

    /// <summary>关键帧补间：待机帧→攻击帧。帧 i：idle 变换(t) 与 attack 变换(t) 交叉淡化（t=i/(n-1)）。</summary>
    byte[][] GenTweenFrames(byte[] idlePng, int n, double tf, double rd, double sf)
    {
        int size = _canvas.GridSize;
        byte[] atk = null!;
        var dir = CurrentProjectDir();
        if (dir != null)
        {
            var p = Path.Combine(dir, "attack.png");
            if (File.Exists(p)) atk = File.ReadAllBytes(p);
        }
        if (atk == null)
            return GenInterpolatedFrames(idlePng, n, "translate", tf, rd, sf);
        var frames = new byte[n][];
        for (int i = 0; i < n; i++)
        {
            double t = n == 1 ? 0 : (double)i / (n - 1);
            double px = t * size * tf, ang = t * rd, s = 1 + (sf - 1) * t;
            var a = RenderFramePng(idlePng, size, px, 0, ang, s, s);
            var b = RenderFramePng(atk, size, px, 0, ang, s, s);
            frames[i] = BlendPng(a, b, size, t);
        }
        return frames;
    }

    /// <summary>两帧 PNG 按 alpha 比例混合（t=0 纯 a，t=1 纯 b）。</summary>
    static byte[] BlendPng(byte[] aPng, byte[] bPng, int size, double t)
    {
        byte[] A(byte[] png)
        {
            var img = new BitmapImage();
            using (var ms = new MemoryStream(png))
            {
                img.BeginInit(); img.CacheOption = BitmapCacheOption.OnLoad; img.StreamSource = ms; img.EndInit();
            }
            var src = new FormatConvertedBitmap(img, PixelFormats.Bgra32, null, 0);
            var px = new byte[size * size * 4];
            src.CopyPixels(px, size * 4, 0);
            return px;
        }
        var pa = A(aPng); var pb = A(bPng);
        var outPx = new byte[pa.Length];
        for (int i = 0; i < pa.Length; i += 4)
        {
            for (int k = 0; k < 3; k++)
                outPx[i + k] = (byte)(pa[i + k] * (1 - t) + pb[i + k] * t);
            outPx[i + 3] = Math.Max(pa[i + 3], pb[i + 3]);
        }
        var wb = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, size, size), outPx, size * 4, 0);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(wb));
        using var ms2 = new MemoryStream();
        enc.Save(ms2);
        return ms2.ToArray();
    }

    /// <summary>动作模板：基于当前帧生成 n 帧。breath=呼吸缩放, swing=摆动旋转, shake=抖动, attack=攻击挥动。</summary>
    byte[][] GenTemplateFrames(byte[] srcPng, int n, string template)
    {
        int size = _canvas.GridSize;
        var frames = new byte[n][];
        var rnd = new Random(12345);
        for (int i = 0; i < n; i++)
        {
            double f = n == 1 ? 0 : (double)i / (n - 1);   // 0..1
            double phase = 2.0 * Math.PI * f;
            double px = 0, py = 0, ang = 0, sx = 1, sy = 1;
            switch (template)
            {
                case "breath":
                    double b = 1 + 0.04 * Math.Sin(phase);
                    sx = b; sy = 1 + 0.10 * Math.Sin(phase);   // 主要垂直呼吸
                    break;
                case "swing":
                    ang = 10.0 * Math.Sin(2.0 * Math.PI * f);
                    break;
                case "shake":
                    px = (rnd.NextDouble() - 0.5) * size * 0.06;
                    py = (rnd.NextDouble() - 0.5) * size * 0.06;
                    break;
                default: // attack：前冲 + 旋转单程
                    px = f * size * 0.18;
                    ang = 12.0 * f;
                    sx = 1 - 0.08 * f; sy = 1 - 0.08 * f;
                    break;
            }
            frames[i] = RenderFramePng(srcPng, size, px, py, ang, sx, sy);
        }
        return frames;
    }

    void OnAutoGenerateFrames(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_canvas.HasContent()) { StatusText.Text = "当前帧为空，先画点东西再生成"; return; }
            int frames = 6;
            if (!int.TryParse(AutoFramesBox.Text, out frames) || frames < 1) frames = 6;
            if (frames > 60) frames = 60;
            var mode = (AutoMode.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "translate";
            if (!double.TryParse(AutoTranslateBox.Text, out double translateFactor)) translateFactor = 0.12;
            if (!double.TryParse(AutoRotateBox.Text, out double rotateDegree)) rotateDegree = 8;
            if (!double.TryParse(AutoScaleBox.Text, out double scaleFactor)) scaleFactor = 1.05;

            var src = _canvas.ToPng();
            byte[][] framesArr;
            string label;
            switch (mode)
            {
                case "tween":
                    framesArr = GenTweenFrames(src, frames, translateFactor, rotateDegree, scaleFactor);
                    label = "关键帧补间";
                    break;
                case "template":
                    framesArr = GenTemplateFrames(src, frames, (AutoTemplateBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "breath");
                    label = "动作模板";
                    break;
                default:
                    framesArr = GenInterpolatedFrames(src, frames, mode, translateFactor, rotateDegree, scaleFactor);
                    label = "插值";
                    break;
            }

            var folder = Path.Combine(ExportRoot, "auto_frames", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Directory.CreateDirectory(folder);
            for (int i = 0; i < framesArr.Length; i++)
                File.WriteAllBytes(Path.Combine(folder, $"frame_{i:D2}.png"), framesArr[i]);
            _generatedFrames = framesArr;
            _generatedFolder = folder;
            ShowFramePreview(framesArr);
            StatusText.Text = $"已生成 {label} {framesArr.Length} 帧 → {folder}";
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = folder, UseShellExecute = true }); } catch { }
        }
        catch (Exception ex)
        {
            StatusText.Text = "自动生成帧失败: " + ex.Message;
        }
    }

    void ShowFramePreview(byte[][] frames)
    {
        FramePreviewSlider.Maximum = Math.Max(0, frames.Length - 1);
        FramePreviewSlider.Value = 0;
        FramePreviewPanel.Visibility = Visibility.Visible;
        SetFramePreview(0);
    }

    void SetFramePreview(int i)
    {
        if (i < 0 || i >= _generatedFrames.Length) return;
        var bi = new BitmapImage();
        using (var ms = new MemoryStream(_generatedFrames[i]))
        {
            bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.StreamSource = ms; bi.EndInit();
        }
        FramePreviewImage.Source = bi;
        FramePreviewIdx.Text = i + "/" + Math.Max(0, _generatedFrames.Length - 1);
    }

    void OnFramePreviewChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => SetFramePreview((int)Math.Round(e.NewValue));

    void OnApplyFrame(object sender, RoutedEventArgs e)
    {
        int i = (int)Math.Round(FramePreviewSlider.Value);
        if (i < 0 || i >= _generatedFrames.Length) return;
        try
        {
            _canvas.FromPng(_generatedFrames[i]);
            CanvasImage.Source = _canvas.Bitmap;
            UpdatePreview();
            StatusText.Text = "已套用第 " + i + " 帧到当前画布（保存后生效）";
        }
        catch (Exception ex) { StatusText.Text = "套用失败: " + ex.Message; }
    }

    void OnExportSpriteSheet(object sender, RoutedEventArgs e)
    {
        try
        {
            // 如果之前已生成目录，优先使用最新目录；否则用即时渲染帧
            var baseDir = Path.Combine(ExportRoot, "auto_frames");
            string? folder = null;
            if (Directory.Exists(baseDir))
            {
                // 取最近创建的子目录
                var dir = new DirectoryInfo(baseDir).GetDirectories().OrderByDescending(d => d.CreationTime).FirstOrDefault();
                if (dir != null) folder = dir.FullName;
            }
            if (folder == null)
            {
                StatusText.Text = "未找到已生成帧，先使用自动生成或手动生成帧";
                return;
            }
            var files = Directory.GetFiles(folder, "frame_*.png").OrderBy(f => f).ToArray();
            if (files.Length == 0) { StatusText.Text = "在生成目录中找不到帧图片"; return; }
            // 读取第一张确定尺寸
            var first = new BitmapImage(new Uri(files[0]));
            int w = first.PixelWidth, h = first.PixelHeight;
            var target = new RenderTargetBitmap(w * files.Length, h, 96, 96, PixelFormats.Default);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                for (int i = 0; i < files.Length; i++)
                {
                    var bi = new BitmapImage(new Uri(files[i]));
                    dc.DrawImage(bi, new Rect(i * w, 0, w, h));
                }
            }
            target.Render(dv);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(target));
            var outPath = Path.Combine(folder, "sprite_sheet.png");
            using (var fs = File.OpenWrite(outPath)) encoder.Save(fs);
            StatusText.Text = "已导出精灵表: " + outPath;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = folder, UseShellExecute = true }); } catch { }
        }
        catch (Exception ex)
        {
            StatusText.Text = "导出精灵表失败: " + ex.Message;
        }
    }

    void OnExportGif(object sender, RoutedEventArgs e)
    {
        try
        {
            var baseDir = Path.Combine(ExportRoot, "auto_frames");
            string? folder = null;
            if (Directory.Exists(baseDir))
            {
                var dir = new DirectoryInfo(baseDir).GetDirectories().OrderByDescending(d => d.CreationTime).FirstOrDefault();
                if (dir != null) folder = dir.FullName;
            }
            if (folder == null)
            {
                StatusText.Text = "未找到已生成帧，先使用自动生成或手动生成帧";
                return;
            }
            var files = Directory.GetFiles(folder, "frame_*.png").OrderBy(f => f).ToArray();
            if (files.Length == 0) { StatusText.Text = "在生成目录中找不到帧图片"; return; }

            // 使用 GifBitmapEncoder 写入带延迟的 GIF（延迟单位：1/100秒）
            var encoder = new GifBitmapEncoder();
            int delayCs = 10; // 默认每帧 100ms
            foreach (var f in files)
            {
                var bi = new BitmapImage();
                using (var fs = File.OpenRead(f))
                {
                    bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.StreamSource = fs; bi.EndInit();
                }
                // 为每帧创建 metadata，设置延迟
                var meta = new BitmapMetadata("gif");
                try { meta.SetQuery("/grctlext/Delay", (ushort)delayCs); } catch { }
                var frame = BitmapFrame.Create(bi, null, meta, null);
                encoder.Frames.Add(frame);
            }
            var outPath = Path.Combine(folder, "preview.gif");
            using (var fs = File.OpenWrite(outPath)) encoder.Save(fs);
            StatusText.Text = "已导出 GIF: " + outPath;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = folder, UseShellExecute = true }); } catch { }
        }
        catch (Exception ex)
        {
            StatusText.Text = "导出 GIF 失败: " + ex.Message;
        }
    }

    void OnImport(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "导入 PNG 到当前帧", Filter = "PNG 图片 (*.png)|*.png" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            _canvas.FromPng(File.ReadAllBytes(dlg.FileName));
            CanvasImage.Source = _canvas.Bitmap;
            StatusText.Text = "已导入 " + Path.GetFileName(dlg.FileName) + " 到当前帧";
        }
        catch (Exception ex) { StatusText.Text = "导入失败: " + ex.Message; }
    }

    void OnUndo(object sender, RoutedEventArgs e) { _canvas.Undo(); }
    void OnClear(object sender, RoutedEventArgs e) { _canvas.Clear(); }

    // ================= 右栏参数表单 =================

    void BuildParamForm(Panel panel)
    {
        StatusText = new TextBlock
        {
            Text = "尚未连接 / 画完当前帧后保存",
            Foreground = (Brush)FindResource("SubBrush"),
            Margin = new Thickness(0, 0, 0, 10),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(StatusText);

        // ---- 植物表单（整体放入 _plantPanel，便于类型切换时整体显隐） ----
        _plantPanel = new StackPanel();
        _tbName = AddRow(_plantPanel, "植物名称", "");
        _tbName.TextChanged += (s, e) => { UpdatePreview(); UpdateIconStatus(); };
        _tbCost = AddRow(_plantPanel, "阳光", "125");
        _tbCost.TextChanged += (s, e) => UpdatePreview();
        _tbCooldown = AddRow(_plantPanel, "冷却(秒)", "7.5");
        _tbCooldown.TextChanged += (s, e) => UpdatePreview();
        _tbHp = AddRow(_plantPanel, "血量", "300");
        _tbHp.TextChanged += (s, e) => UpdatePreview();

        _plantPanel.Children.Add(AddLabel("攻击方式(模板)"));
        _cbTemplate = NewCombo();
        foreach (var kv in new[] {
            ("shooter", "直线射击"), ("catapult", "抛物线投掷"), ("cannon", "定点轰炸"),
            ("melee", "近战"), ("passive", "被动"), ("spike", "地刺"), ("umbrella", "保护伞"),
            ("magnet", "磁力"), ("squash", "倭瓜"), ("iceberg", "寒冰"), ("doom", "毁灭"),
            ("fume", "喷气"), ("puff", "小喷") })
            _cbTemplate.Items.Add(kv.Item1 + ":" + kv.Item2);
        _cbTemplate.SelectedIndex = 0;
        _cbTemplate.SelectionChanged += (s, e) => { UpdatePassiveVisibility(); UpdatePreview(); };
        _plantPanel.Children.Add(_cbTemplate);

        _attackPanel = new StackPanel();
        _tbDamage = AddRow(_attackPanel, "伤害", "40");
        _tbDamage.TextChanged += (s, e) => UpdatePreview();
        _tbInterval = AddRow(_attackPanel, "攻速(秒)", "1.5");
        _tbInterval.TextChanged += (s, e) => UpdatePreview();
        _tbRange = AddRow(_attackPanel, "射程", "2000");
        _tbRange.TextChanged += (s, e) => UpdatePreview();
        _attackPanel.Children.Add(AddLabel("子弹类型"));
        _cbBullet = NewCombo();
        foreach (var b in new[] { "Pea:豌豆", "SnowPea:寒冰", "FirePea:火焰", "Star:星星", "Cabbage:卷心菜" })
            _cbBullet.Items.Add(b);
        _cbBullet.SelectedIndex = 0;
        _cbBullet.SelectionChanged += (s, e) => UpdatePreview();
        _attackPanel.Children.Add(_cbBullet);
        _plantPanel.Children.Add(_attackPanel);

        _sunPanel = new StackPanel { Visibility = Visibility.Collapsed };
        _tbSun = AddRow(_sunPanel, "产阳光量", "25");
        _tbSun.TextChanged += (s, e) => UpdatePreview();
        _plantPanel.Children.Add(_sunPanel);

        // ---- SP4 被动能力 ----
        _plantPanel.Children.Add(AddLabel("被动能力"));
        _cbPassive = NewCombo();
        foreach (var kv in new[] {
            ("none", "无"), ("sun", "产阳光"), ("shield", "护盾防御"),
            ("slowaura", "减速光环"), ("coin", "产金币"), ("buffaura", "增益光环"),
            ("reflect", "反伤"), ("lifesteal", "吸血"), ("crit", "暴击"), ("rangebonus", "射程加成") })
            _cbPassive.Items.Add(kv.Item1 + ":" + kv.Item2);
        _cbPassive.SelectedIndex = 0;
        _cbPassive.SelectionChanged += (s, e) => UpdatePreview();
        _plantPanel.Children.Add(_cbPassive);
        _tbPassiveValue = AddRow(_plantPanel, "被动数值(周期秒/半径)", "5");
        _tbPassiveValue.TextChanged += (s, e) => UpdatePreview();

        // ---- Task 5 绑定：图鉴名 + 关卡限制 ----
        _tbDisplayName = AddRow(_plantPanel, "图鉴名（默认=项目名）", "");
        _tbDisplayName.TextChanged += (s, e) => UpdatePreview();
        _tbLevelRestrict = AddRow(_plantPanel, "关卡限制（逗号分隔，留空=不限）", "");
        _tbLevelRestrict.TextChanged += (s, e) => UpdatePreview();

        AddIconSection(_plantPanel, "plant");   // SP8 卡牌图标

        var save = new Button { Content = "保存定义（当前帧）", Height = 40, Margin = new Thickness(0, 12, 0, 6) };
        save.Style = (Style)FindResource("CardBtn");
        save.Click += OnSave;
        _plantPanel.Children.Add(save);

        var deploy = new Button { Content = "生成到游戏", Height = 40, Margin = new Thickness(0, 0, 0, 6) };
        deploy.Style = (Style)FindResource("CardBtn");
        deploy.Click += OnDeploy;
        _plantPanel.Children.Add(deploy);

        var del = new Button { Content = "删除当前植物", Height = 40 };
        del.Style = (Style)FindResource("CardBtn");
        del.Click += OnDelete;
        _plantPanel.Children.Add(del);
        panel.Children.Add(_plantPanel);

        // ---- 子弹表单（Task 3，默认隐藏，切「子弹」后显示） ----
        _bulletPanel = new StackPanel { Visibility = Visibility.Collapsed };
        _tbBName = AddRow(_bulletPanel, "子弹名称", "");
        _tbBName.TextChanged += (s, e) => { UpdatePreview(); UpdateIconStatus(); };
        _bulletPanel.Children.Add(AddLabel("克隆模板"));
        _cbBTemplate = NewCombo();
        foreach (var b in new[] { "Pea:豌豆", "SnowPea:寒冰", "FirePea:火焰", "Star:星星", "Cabbage:卷心菜" })
            _cbBTemplate.Items.Add(b);
        _cbBTemplate.SelectedIndex = 0;
        _cbBTemplate.SelectionChanged += (s, e) => UpdatePreview();
        _bulletPanel.Children.Add(_cbBTemplate);
        _tbBDamage = AddRow(_bulletPanel, "伤害", "40");
        _tbBDamage.TextChanged += (s, e) => UpdatePreview();
        _tbBSpeed = AddRow(_bulletPanel, "速度", "600");
        _tbBSpeed.TextChanged += (s, e) => UpdatePreview();
        _tbBRange = AddRow(_bulletPanel, "射程", "2000");
        _tbBRange.TextChanged += (s, e) => UpdatePreview();
        _tbBPenetrate = AddRow(_bulletPanel, "穿透数", "0");
        _tbBPenetrate.TextChanged += (s, e) => UpdatePreview();
        _tbBScale = AddRow(_bulletPanel, "大小", "1.0");
        _tbBScale.TextChanged += (s, e) => UpdatePreview();
        _bulletPanel.Children.Add(AddLabel("命中效果"));
        _cbEffect = NewCombo();
        foreach (var kv in new[] {
            ("none", "无"), ("burn", "燃烧"), ("freeze", "冰冻"),
            ("stun", "眩晕"), ("explode", "爆炸") })
            _cbEffect.Items.Add(kv.Item1 + ":" + kv.Item2);
        _cbEffect.SelectedIndex = 0;
        _cbEffect.SelectionChanged += (s, e) => UpdatePreview();
        _bulletPanel.Children.Add(_cbEffect);

        // ---- SP8 命中特效 + 音效下拉 ----
        _bulletPanel.Children.Add(AddLabel("命中特效"));
        _cbSplat = NewCombo();
        foreach (var kv in new[] {
            ("none", "无"), ("explode", "爆炸"), ("freeze", "冰冻"),
            ("lightning", "闪电"), ("fire", "火焰") })
            _cbSplat.Items.Add(kv.Item1 + ":" + kv.Item2);
        _cbSplat.SelectedIndex = 0;   // 默认 none
        _cbSplat.SelectionChanged += (s, e) => UpdatePreview();
        _bulletPanel.Children.Add(_cbSplat);

        _bulletPanel.Children.Add(AddLabel("命中音效"));
        _cbAudio = NewCombo();
        foreach (var kv in new[] {
            ("Ignite", "点燃"), ("Freeze", "冰冻"), ("Explosion", "爆炸"),
            ("Splat", "溅射"), ("None", "无") })
            _cbAudio.Items.Add(kv.Item1 + ":" + kv.Item2);
        _cbAudio.SelectedIndex = 0;   // 默认 Ignite
        _cbAudio.SelectionChanged += (s, e) => UpdatePreview();
        _bulletPanel.Children.Add(_cbAudio);

        AddIconSection(_bulletPanel, "bullet");   // SP8 卡牌图标

        var bSave = new Button { Content = "保存子弹", Height = 40, Margin = new Thickness(0, 12, 0, 6) };
        bSave.Style = (Style)FindResource("CardBtn");
        bSave.Click += OnBulletSave;
        _bulletPanel.Children.Add(bSave);

        var bDeploy = new Button { Content = "生成到游戏", Height = 40, Margin = new Thickness(0, 0, 0, 6) };
        bDeploy.Style = (Style)FindResource("CardBtn");
        bDeploy.Click += OnBulletDeploy;
        _bulletPanel.Children.Add(bDeploy);

        var bDel = new Button { Content = "删除当前子弹", Height = 40 };
        bDel.Style = (Style)FindResource("CardBtn");
        bDel.Click += OnBulletDelete;
        _bulletPanel.Children.Add(bDel);
        panel.Children.Add(_bulletPanel);

        // ---- 僵尸表单（Task 4，默认隐藏；双帧画板） ----
        _zombiePanel = new StackPanel { Visibility = Visibility.Collapsed };
        _tbZName = AddRow(_zombiePanel, "僵尸名称", "");
        _tbZName.TextChanged += (s, e) => { UpdatePreview(); UpdateIconStatus(); };
        _zombiePanel.Children.Add(AddLabel("模板"));
        _cbZTemplate = NewCombo();
        foreach (var t in ZombieTemplates)
            _cbZTemplate.Items.Add(t.Key + ":" + t.Label);
        _cbZTemplate.SelectedIndex = 0;
        _cbZTemplate.SelectionChanged += (s, e) => UpdatePreview();
        _zombiePanel.Children.Add(_cbZTemplate);
        _tbZHp = AddRow(_zombiePanel, "血量", "200");
        _tbZHp.TextChanged += (s, e) => UpdatePreview();
        _tbZSpeed = AddRow(_zombiePanel, "移速", "0.3");
        _tbZSpeed.TextChanged += (s, e) => UpdatePreview();
        _tbZDamage = AddRow(_zombiePanel, "伤害", "100");
        _tbZDamage.TextChanged += (s, e) => UpdatePreview();

        // ---- SP4 僵尸行为：移动 / 攻击 / 技能 ----
        _zombiePanel.Children.Add(AddLabel("移动方式"));
        _cbZMove = NewCombo();
        foreach (var kv in new[] {
            ("walk", "步行"), ("flight", "飞行"), ("burrow", "遁地"), ("dash", "冲刺") })
            _cbZMove.Items.Add(kv.Item1 + ":" + kv.Item2);
        _cbZMove.SelectedIndex = 0;
        _cbZMove.SelectionChanged += (s, e) => UpdatePreview();
        _zombiePanel.Children.Add(_cbZMove);

        _zombiePanel.Children.Add(AddLabel("攻击方式"));
        _cbZAtk = NewCombo();
        foreach (var kv in new[] {
            ("bite", "近战啃食"), ("ranged", "远程"), ("selfdestruct", "自爆") })
            _cbZAtk.Items.Add(kv.Item1 + ":" + kv.Item2);
        _cbZAtk.SelectedIndex = 0;
        _cbZAtk.SelectionChanged += (s, e) => { UpdateZBulletVisibility(); UpdatePreview(); };
        _zombiePanel.Children.Add(_cbZAtk);

        // 远程子弹（攻击方式 = 远程 时显示；复用全量子弹库 + ★自制）
        _zBulletPanel = new StackPanel();
        _zBulletPanel.Children.Add(AddLabel("远程子弹"));
        _cbZBullet = NewCombo();
        foreach (var b in new[] { "Pea:豌豆", "SnowPea:寒冰", "FirePea:火焰", "Star:星星", "Cabbage:卷心菜" })
            _cbZBullet.Items.Add(b);
        _cbZBullet.SelectedIndex = 0;
        _cbZBullet.SelectionChanged += (s, e) => UpdatePreview();
        _zBulletPanel.Children.Add(_cbZBullet);
        _zombiePanel.Children.Add(_zBulletPanel);

        _zombiePanel.Children.Add(AddLabel("技能"));
        _cbZSpecial = NewCombo();
        foreach (var kv in new[] {
            ("none", "无"), ("summon", "召唤"), ("buff", "增益"), ("split", "死后分裂"),
            ("freezeaura", "冰霜光环"), ("heal", "治疗") })
            _cbZSpecial.Items.Add(kv.Item1 + ":" + kv.Item2);
        _cbZSpecial.SelectedIndex = 0;
        _cbZSpecial.SelectionChanged += (s, e) => UpdatePreview();
        _zombiePanel.Children.Add(_cbZSpecial);
        _tbZSpecialValue = AddRow(_zombiePanel, "技能数值", "2");
        _tbZSpecialValue.TextChanged += (s, e) => UpdatePreview();
        UpdateZBulletVisibility();   // 初始攻击=bite → 隐藏远程子弹

        _tbZDisplayName = AddRow(_zombiePanel, "图鉴名（默认=项目名）", "");   // Task 5
        _tbZDisplayName.TextChanged += (s, e) => UpdatePreview();

        AddIconSection(_zombiePanel, "zombie");   // SP8 卡牌图标

        var zSave = new Button { Content = "保存僵尸", Height = 40, Margin = new Thickness(0, 12, 0, 6) };
        zSave.Style = (Style)FindResource("CardBtn");
        zSave.Click += OnZombieSave;
        _zombiePanel.Children.Add(zSave);

        var zDeploy = new Button { Content = "生成到游戏", Height = 40, Margin = new Thickness(0, 0, 0, 6) };
        zDeploy.Style = (Style)FindResource("CardBtn");
        zDeploy.Click += OnZombieDeploy;
        _zombiePanel.Children.Add(zDeploy);

        var zDel = new Button { Content = "删除当前僵尸", Height = 40 };
        zDel.Style = (Style)FindResource("CardBtn");
        zDel.Click += OnZombieDelete;
        _zombiePanel.Children.Add(zDel);
        panel.Children.Add(_zombiePanel);

        // ---- 卡牌表单（Task 4，默认隐藏；单帧：保存导出 idle.png） ----
        _cardPanel = new StackPanel { Visibility = Visibility.Collapsed };
        _tbCName = AddRow(_cardPanel, "卡牌名称", "");
        _tbCName.TextChanged += (s, e) => { UpdatePreview(); UpdateIconStatus(); };
        _tbCCost = AddRow(_cardPanel, "阳光", "125");
        _tbCCost.TextChanged += (s, e) => UpdatePreview();
        _tbCCooldown = AddRow(_cardPanel, "冷却(秒)", "7.5");
        _tbCCooldown.TextChanged += (s, e) => UpdatePreview();
        _tbCDisplayName = AddRow(_cardPanel, "图鉴名（默认=项目名）", "");
        _tbCDisplayName.TextChanged += (s, e) => UpdatePreview();
        _tbCDesc = AddRow(_cardPanel, "图鉴描述", "");
        _tbCDesc.TextChanged += (s, e) => UpdatePreview();

        AddIconSection(_cardPanel, "card");   // SP8 卡牌图标

        var cSave = new Button { Content = "保存卡牌", Height = 40, Margin = new Thickness(0, 12, 0, 6) };
        cSave.Style = (Style)FindResource("CardBtn");
        cSave.Click += OnCardSave;
        _cardPanel.Children.Add(cSave);

        var cDeploy = new Button { Content = "生成到游戏", Height = 40, Margin = new Thickness(0, 0, 0, 6) };
        cDeploy.Style = (Style)FindResource("CardBtn");
        cDeploy.Click += OnCardDeploy;
        _cardPanel.Children.Add(cDeploy);

        var cDel = new Button { Content = "删除当前卡牌", Height = 40 };
        cDel.Style = (Style)FindResource("CardBtn");
        cDel.Click += OnCardDelete;
        _cardPanel.Children.Add(cDel);
        panel.Children.Add(_cardPanel);
    }

    // ================= 实时预览 =================

    void UpdatePreview()
    {
        if (PreviewImage != null) PreviewImage.Source = _canvas.Bitmap;
        if (PreviewText == null) return;
        if (_type == "bullet")
        {
            var bname = string.IsNullOrWhiteSpace(_tbBName?.Text) ? "未命名" : _tbBName.Text.Trim();
            var eff = _cbEffect?.SelectedItem?.ToString() ?? "none";
            var btmpl = _cbBTemplate?.SelectedItem?.ToString() ?? "Pea";
            var splat = _cbSplat?.SelectedItem?.ToString() ?? "none";
            var audio = _cbAudio?.SelectedItem?.ToString() ?? "Ignite";
            PreviewText.Text = $"{bname}（子弹）\n模板:{btmpl} 效果:{eff} 特效:{splat}\n音效:{audio} 伤害:{_tbBDamage?.Text} 速度:{_tbBSpeed?.Text}\n射程:{_tbBRange?.Text} 穿透:{_tbBPenetrate?.Text} 大小:{_tbBScale?.Text}";
            return;
        }
        if (_type == "zombie")
        {
            var zname = string.IsNullOrWhiteSpace(_tbZName?.Text) ? "未命名" : _tbZName.Text.Trim();
            var ztmpl = _cbZTemplate?.SelectedItem?.ToString() ?? "zombie_normal";
            var zmove = _cbZMove?.SelectedItem?.ToString() ?? "walk";
            var zatk = _cbZAtk?.SelectedItem?.ToString() ?? "bite";
            var zspec = _cbZSpecial?.SelectedItem?.ToString() ?? "none";
            PreviewText.Text = $"{zname}（僵尸）\n模板:{ztmpl} 血量:{_tbZHp?.Text}\n移速:{_tbZSpeed?.Text} 伤害:{_tbZDamage?.Text}\n移动:{zmove} 攻击:{zatk} 技能:{zspec}";
            return;
        }
        if (_type == "card")
        {
            var cname = string.IsNullOrWhiteSpace(_tbCName?.Text) ? "未命名" : _tbCName.Text.Trim();
            PreviewText.Text = $"{cname}（卡牌）\n阳光:{_tbCCost?.Text} 冷却:{_tbCCooldown?.Text}\n图鉴名:{_tbCDisplayName?.Text}";
            return;
        }
        var name = string.IsNullOrWhiteSpace(_tbName?.Text) ? "未命名" : _tbName.Text.Trim();
        var tmpl = _cbTemplate?.SelectedItem?.ToString() ?? "shooter";
        var pass = _cbPassive?.SelectedItem?.ToString() ?? "none";
        PreviewText.Text = $"{name}\n类型:{tmpl}\n阳光:{_tbCost?.Text} 冷却:{_tbCooldown?.Text}\n血量:{_tbHp?.Text} 伤害:{_tbDamage?.Text}\n被动:{pass}";
    }

    ComboBox NewCombo()
    {
        var cb = new ComboBox();
        cb.Style = (Style)FindResource("CardComboBox");
        return cb;
    }

    /// <summary>按 key 匹配 combo 项（key:中文；starAllowed 时也匹配 ★key:），未知 key 追加并选中。</summary>
    void SelectComboByKey(ComboBox cb, string key, bool starAllowed = false)
    {
        if (cb == null) return;
        var idx = -1;
        for (int i = 0; i < cb.Items.Count; i++)
        {
            var item = (string)cb.Items[i];
            if (item.StartsWith(key + ":") || (starAllowed && item.StartsWith("★" + key + ":"))) { idx = i; break; }
        }
        if (idx < 0) { cb.Items.Add(key + ":" + key); idx = cb.Items.Count - 1; }
        cb.SelectedIndex = idx;
    }

    TextBox AddRow(Panel panel, string title, string def)
    {
        panel.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 6, 0, 2),
            FontSize = 12,
        });
        var t = new TextBox { Text = def };
        t.Style = (Style)FindResource("CardTextBox");
        panel.Children.Add(t);
        return t;
    }

    TextBlock AddLabel(string s) => new()
    {
        Text = s,
        Foreground = (Brush)FindResource("TextBrush"),
        Margin = new Thickness(0, 10, 0, 2),
        FontSize = 12,
    };

    // ================= SP8 卡牌图标区（四类型共用） =================

    /// <summary>在编辑面板加「卡牌图标」区：上传 PNG→icon.png / 清除 / 状态文字（默认用待机帧）。</summary>
    void AddIconSection(Panel panel, string type)
    {
        panel.Children.Add(AddLabel("卡牌图标"));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
        var upload = new Button
        {
            Content = "上传图标",
            Style = (Style)FindResource("CardMiniBtn"),
            Margin = new Thickness(0, 0, 6, 0),
        };
        upload.Click += OnUploadIcon;
        var clear = new Button { Content = "清除图标", Style = (Style)FindResource("CardMiniBtn") };
        clear.Click += OnClearIcon;
        row.Children.Add(upload);
        row.Children.Add(clear);
        panel.Children.Add(row);
        var st = new TextBlock
        {
            Foreground = (Brush)FindResource("SubBrush"),
            Margin = new Thickness(0, 2, 0, 4),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(st);
        _iconStatus[type] = st;
    }

    /// <summary>当前编辑项目的项目目录（按当前类型 + 名称文本框；未命名返回 null）。</summary>
    string? CurrentProjectDir() => _type switch
    {
        "plant" => string.IsNullOrWhiteSpace(_tbName?.Text) ? null : Path.Combine(GetTypeDir("plant"), _tbName.Text.Trim()),
        "zombie" => string.IsNullOrWhiteSpace(_tbZName?.Text) ? null : Path.Combine(GetTypeDir("zombie"), _tbZName.Text.Trim()),
        "bullet" => string.IsNullOrWhiteSpace(_tbBName?.Text) ? null : Path.Combine(GetTypeDir("bullet"), _tbBName.Text.Trim()),
        "card" => string.IsNullOrWhiteSpace(_tbCName?.Text) ? null : Path.Combine(GetTypeDir("card"), _tbCName.Text.Trim()),
        _ => null,
    };

    /// <summary>刷新当前类型面板的卡牌图标状态（项目目录是否已有 icon.png）。</summary>
    void UpdateIconStatus()
    {
        if (!_iconStatus.TryGetValue(_type, out var st)) return;
        var dir = CurrentProjectDir();
        bool has = dir != null && File.Exists(Path.Combine(dir, "icon.png"));
        st.Text = has
            ? "✓ 已有专属图标（icon.png）"
            : "（未设置图标，默认使用待机帧）";
    }

    /// <summary>上传 PNG → 当前项目目录 icon.png（MOD 注册时自动应用为卡牌图标）。</summary>
    void OnUploadIcon(object sender, RoutedEventArgs e)
    {
        var dir = CurrentProjectDir();
        if (dir == null) { StatusText.Text = "请先填写名称再上传图标"; return; }
        var dlg = new OpenFileDialog { Title = "选择卡牌图标 (PNG)", Filter = "PNG 图片 (*.png)|*.png" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            Directory.CreateDirectory(dir);
            File.Copy(dlg.FileName, Path.Combine(dir, "icon.png"), true);
            StatusText.Text = "已上传卡牌图标: " + Path.GetFileName(dlg.FileName);
            UpdateIconStatus();
        }
        catch (Exception ex) { StatusText.Text = "上传图标失败: " + ex.Message; }
    }

    /// <summary>删除当前项目 icon.png，恢复默认待机帧。</summary>
    void OnClearIcon(object sender, RoutedEventArgs e)
    {
        var dir = CurrentProjectDir();
        if (dir == null) { StatusText.Text = "请先填写名称"; return; }
        var p = Path.Combine(dir, "icon.png");
        if (!File.Exists(p)) { StatusText.Text = "当前没有卡牌图标"; return; }
        try
        {
            File.Delete(p);
            StatusText.Text = "已清除卡牌图标（默认使用待机帧）";
            UpdateIconStatus();
        }
        catch (Exception ex) { StatusText.Text = "清除图标失败: " + ex.Message; }
    }

    void UpdatePassiveVisibility()
    {
        bool passive = _cbTemplate != null && _cbTemplate.SelectedItem != null &&
                       ((string)_cbTemplate.SelectedItem).StartsWith("passive:");
        if (_attackPanel != null) _attackPanel.Visibility = passive ? Visibility.Collapsed : Visibility.Visible;
        if (_sunPanel != null) _sunPanel.Visibility = passive ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>攻击方式 = 远程 时显示远程子弹下拉。</summary>
    void UpdateZBulletVisibility()
    {
        if (_zBulletPanel == null || _cbZAtk == null) return;
        bool ranged = _cbZAtk.SelectedItem != null && ((string)_cbZAtk.SelectedItem).StartsWith("ranged:");
        _zBulletPanel.Visibility = ranged ? Visibility.Visible : Visibility.Collapsed;
    }

    CustomPlantDef BuildDef()
    {
        double D(TextBox t, double d) => double.TryParse(t.Text, out var v) ? v : d;
        return new CustomPlantDef
        {
            Name = _tbName.Text?.Trim() ?? "",
            Template = ((string)_cbTemplate.SelectedItem).Split(':')[0],
            Cost = (int)D(_tbCost, 125),
            Cooldown = D(_tbCooldown, 7.5),
            Hp = D(_tbHp, 300),
            Scale = 1.0,
            DisplayName = string.IsNullOrWhiteSpace(_tbDisplayName?.Text) ? _tbName.Text.Trim() : _tbDisplayName.Text.Trim(),
            LevelRestrict = (_tbLevelRestrict?.Text ?? "").Trim(),
            Attack = new CustomAttackDef
            {
                Damage = D(_tbDamage, 40),
                Interval = D(_tbInterval, 1.5),
                Range = D(_tbRange, 2000),
                // I1: 自制子弹项是 ★名:名，Split 取 key 后必须 TrimStart('★')，否则 ★ 泄漏进存储值、MOD 按名解析不到
                Bullet = ((string)_cbBullet.SelectedItem).Split(':')[0].TrimStart('★'),
                Targets = "single",
                SunAmount = (int)D(_tbSun, 25),
            },
            Frame = new CustomFrameDef { Idle = "idle.png", Attack = "attack.png" },
            Passive = new PassiveDef
            {
                Kind = ((string)_cbPassive.SelectedItem).Split(':')[0],
                Value = D(_tbPassiveValue, 5),
            },
        };
    }

    // ================= 保存 / 部署 / 删除 =================

    void OnSave(object sender, RoutedEventArgs e)
    {
        // ---- 保存校验（任一失败即提示并中断） ----
        if (!_canvas.HasContent()) { MessageBox.Show("画板为空，请先画图"); return; }
        if (string.IsNullOrWhiteSpace(_tbName.Text)) { MessageBox.Show("名称不能为空"); return; }
        if (!double.TryParse(_tbCost.Text, out var cost) || cost < 0) { MessageBox.Show("阳光需 ≥0"); return; }
        if (!double.TryParse(_tbHp.Text, out var hp) || hp < 0) { MessageBox.Show("血量需 ≥0"); return; }
        if (!double.TryParse(_tbInterval.Text, out var iv) || iv <= 0) { MessageBox.Show("攻速需 >0"); return; }
        if (!double.TryParse(_tbDamage.Text, out var dmg) || dmg < 0) { MessageBox.Show("伤害需 ≥0"); return; }
        if (!double.TryParse(_tbCooldown.Text, out var cd) || cd < 0) { MessageBox.Show("冷却需 ≥0"); return; }

        // ---- 重名覆盖确认（目标目录已存在且不是当前正在编辑的植物） ----
        var dir = Path.Combine(GetTypeDir("plant"), _tbName.Text.Trim());
        var targetCfg = Path.Combine(dir, "config.json");
        if (Directory.Exists(dir) && _editingPath != targetCfg)
            if (MessageBox.Show("同名项目已存在，覆盖？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var def = BuildDef();
        Directory.CreateDirectory(dir);
        File.WriteAllText(targetCfg,
            JsonSerializer.Serialize(def, new JsonSerializerOptions { WriteIndented = true }));
        var file = _frameAttack ? "attack.png" : "idle.png";
        File.WriteAllBytes(Path.Combine(dir, file), _canvas.ToPng());
        _editingPath = targetCfg;   // 保存后标记当前编辑路径，避免再次覆盖确认
        StatusText.Text = "已保存（" + file + "）。" +
            (_frameAttack ? "再切「待机帧」画完保存一次" : "再切「攻击帧」画完保存一次");
        RefreshLeftList();
    }

    async void OnDeploy(object sender, RoutedEventArgs e)
    {
        var def = BuildDef();
        if (string.IsNullOrWhiteSpace(def.Name)) { StatusText.Text = "名称不能为空"; return; }
        var cfgPath = Path.Combine(GetTypeDir("plant"), def.Name, "config.json");
        if (!File.Exists(cfgPath)) { StatusText.Text = "请先保存定义"; return; }
        StatusText.Text = "注入中…";
        var r = await _get("/cmd?name=CustomPlantLoad&file=" + Uri.EscapeDataString(cfgPath));
        var txt = r == "ok" ? "已注入游戏" : (r ?? "未连接游戏");
        StatusText.Text = txt;
        RefreshLeftList();
    }

    async void OnDelete(object sender, RoutedEventArgs e)
    {
        var name = _tbName.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name)) { StatusText.Text = "名称为空，无法删除"; return; }
        var dir = Path.Combine(GetTypeDir("plant"), name);
        if (!Directory.Exists(dir)) { StatusText.Text = "目录不存在: " + dir; return; }
        if (MessageBox.Show(this, "确定删除自定义植物「" + name + "」？\n（删除本地文件并通知游戏移除）",
                "删除确认", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try { Directory.Delete(dir, true); }
        catch (Exception ex) { StatusText.Text = "删除文件失败: " + ex.Message; return; }
        var r = await _get("/cmd?name=CustomPlantRemove&name=" + Uri.EscapeDataString(name));
        var txt = r == "ok" ? "已删除 " + name : (r ?? "未连接游戏");
        StatusText.Text = txt;
        _tbName.Text = "";
        _canvas.Clear();
        RefreshLeftList();
    }

    // ================= 列表视图（卡片网格，按 _type 扫描对应目录） =================

    /// <summary>刷新列表（保留原方法名兼容既有调用点）。</summary>
    void RefreshLeftList() => RefreshListView();

    void RefreshListView()
    {
        CardGrid.Children.Clear();
        int n = 0;
        var root = GetTypeDir(_type);
        if (Directory.Exists(root))
        {
            foreach (var cfg in Directory.GetFiles(root, "config.json", SearchOption.AllDirectories))
            {
                if (_type == "plant" && IsLegacyBulletCfg(cfg)) continue;   // 植物列表排除旧 bullets/ 子目录
                var dir = Path.GetDirectoryName(cfg) ?? "";
                var name = Path.GetFileName(dir) ?? cfg;
                CardGrid.Children.Add(BuildProjectCard(name, cfg, dir));
                n++;
            }
        }
        ListHint.Text = n == 0 ? "（暂无" + TypeZh(_type) + "，点「新建" + TypeZh(_type) + "」创建）" : "共 " + n + " 个";
    }

    static string TypeZh(string type) => type switch
    {
        "plant" => "植物",
        "zombie" => "僵尸",
        "bullet" => "子弹",
        "card" => "卡牌",
        _ => "项目",
    };

    /// <summary>MOD 命令名：CustomPlant/CustomZombie/CustomBullet/CustomCard + Load/Remove。</summary>
    string TypeCmd(string action) => _type switch
    {
        "plant" => "CustomPlant" + action,
        "zombie" => "CustomZombie" + action,
        "bullet" => "CustomBullet" + action,
        "card" => "CustomCard" + action,
        _ => "CustomPlant" + action,
    };

    /// <summary>项目卡片：缩略图 + 名称 + 编辑/生成/删除/导出（纯文字小按钮）。</summary>
    Border BuildProjectCard(string name, string cfgPath, string dir)
    {
        var card = new Border
        {
            Width = 172,
            Height = 224,
            Background = (Brush)FindResource("CardBrushW"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(20),
            Margin = new Thickness(8),
            Effect = FindResource("SoftShadow") as System.Windows.Media.Effects.DropShadowEffect,
        };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var img = new Image
        {
            Width = 96,
            Height = 96,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
        var thumb = LoadThumb(dir, _type == "bullet" ? "bullet.png" : "idle.png");
        if (thumb != null) img.Source = thumb;
        var imgBox = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x10, 0, 0, 0)),
            CornerRadius = new CornerRadius(12),
            Margin = new Thickness(10, 10, 10, 4),
            Child = img,
        };
        Grid.SetRow(imgBox, 0);
        grid.Children.Add(imgBox);

        var nm = new TextBlock
        {
            Text = name,
            FontFamily = (FontFamily)FindResource("AppFont"),
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("TextBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(8, 2, 8, 0),
            MaxWidth = 148,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetRow(nm, 1);
        grid.Children.Add(nm);

        var btns = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(4, 6, 4, 8),
        };
        var row1 = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        row1.Children.Add(MakeCardBtn("编辑", name, OnCardEditClick));
        row1.Children.Add(MakeCardBtn("生成", name, OnCardDeployClick));
        row1.Children.Add(MakeCardBtn("删除", name, OnCardDeleteClick));
        row1.Children.Add(MakeCardBtn("导出", name, OnCardExportClick));
        var row2 = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        row2.Children.Add(MakeCardBtn("重命名", name, OnCardRenameClick));
        row2.Children.Add(MakeCardBtn("复制", name, OnCardCopyClick));
        btns.Children.Add(row1);
        btns.Children.Add(row2);
        Grid.SetRow(btns, 2);
        grid.Children.Add(btns);

        card.Child = grid;
        return card;
    }

    Button MakeCardBtn(string text, string name, RoutedEventHandler handler)
    {
        var b = new Button
        {
            Content = text,
            Tag = name,
            Style = (Style)FindResource("CardMiniBtn"),
            Margin = new Thickness(3, 0, 3, 0),
        };
        b.Click += handler;
        return b;
    }

    void OnCardEditClick(object sender, RoutedEventArgs e)
    {
        var name = (string)((Button)sender).Tag;
        var cfg = Path.Combine(GetTypeDir(_type), name, "config.json");
        if (!File.Exists(cfg)) { StatusText.Text = "配置不存在: " + cfg; return; }
        LoadIntoEditor(cfg);
        ShowEditView(true);
    }

    async void OnCardDeployClick(object sender, RoutedEventArgs e)
    {
        var name = (string)((Button)sender).Tag;
        var cfg = Path.Combine(GetTypeDir(_type), name, "config.json");
        if (!File.Exists(cfg)) { StatusText.Text = "请先保存" + TypeZh(_type); return; }
        StatusText.Text = "注入中…";
        var r = await _get("/cmd?name=" + TypeCmd("Load") + "&file=" + Uri.EscapeDataString(cfg));
        StatusText.Text = r == "ok" ? "已生成到游戏" : (r ?? "未连接游戏");
    }

    async void OnCardDeleteClick(object sender, RoutedEventArgs e)
    {
        var name = (string)((Button)sender).Tag;
        var dir = Path.Combine(GetTypeDir(_type), name);
        if (!Directory.Exists(dir)) { StatusText.Text = "目录不存在"; return; }
        if (MessageBox.Show(this, "确定删除" + TypeZh(_type) + "「" + name + "」？\n（删除本地文件并通知游戏移除）",
                "删除确认", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try { Directory.Delete(dir, true); }
        catch (Exception ex) { StatusText.Text = "删除失败: " + ex.Message; return; }
        var r = await _get("/cmd?name=" + TypeCmd("Remove") + "&name=" + Uri.EscapeDataString(name));
        StatusText.Text = r == "ok" ? "已删除 " + name : (r ?? "未连接游戏");
        RefreshListView();
    }

    void OnCardExportClick(object sender, RoutedEventArgs e)
    {
        var name = (string)((Button)sender).Tag;
        try
        {
            var dir = Path.Combine(GetTypeDir(_type), name);
            if (!Directory.Exists(dir)) { StatusText.Text = "目录不存在: " + dir; return; }
            ExportProject(_type, name);
            var dst = Path.Combine(ExportRoot, _type + "_" + name + ".cproj");
            StatusText.Text = "已导出: " + dst;
            MessageBox.Show(this, "已导出到：\n" + dst + "\n\n可将该 .cproj 文件分享给他人导入。", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshShareView();
        }
        catch (Exception ex) { StatusText.Text = "导出失败: " + ex.Message; }
    }

    // ================= 导出 / 导入（.cproj，SP7） =================

    /// <summary>把项目目录（config.json + png）打包成 <导出>\<type>_<name>.cproj，并在 zip 根写 meta.txt。</summary>
    void ExportProject(string type, string name)
    {
        var srcDir = Path.Combine(GetTypeDir(type), name);
        if (!Directory.Exists(srcDir)) return;
        Directory.CreateDirectory(ExportRoot);
        var dst = Path.Combine(ExportRoot, type + "_" + name + ".cproj");
        using (var fs = File.Create(dst))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            foreach (var file in Directory.GetFiles(srcDir))   // 项目目录平铺：config.json + png
            {
                var entry = zip.CreateEntry(Path.GetFileName(file), CompressionLevel.Optimal);
                using var es = entry.Open();
                using var src = File.OpenRead(file);
                src.CopyTo(es);
            }
            var meta = zip.CreateEntry("meta.txt", CompressionLevel.Optimal);
            using var ms = meta.Open();
            using var sw = new StreamWriter(ms, new UTF8Encoding(false));
            sw.Write("type=" + type + "\nname=" + name + "\nversion=1");
        }
    }

    void OnExportAll(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ExportRoot);
            int n = 0;
            foreach (var type in new[] { "plant", "zombie", "bullet", "card" })
            {
                var root = GetTypeDir(type);
                if (!Directory.Exists(root)) continue;
                foreach (var cfg in Directory.GetFiles(root, "config.json", SearchOption.AllDirectories))
                {
                    if (type == "plant" && IsLegacyBulletCfg(cfg)) continue;
                    var name = Path.GetFileName(Path.GetDirectoryName(cfg) ?? "");
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    ExportProject(type, name);
                    n++;
                }
            }
            RefreshShareView();
            MessageBox.Show(this, "已导出 " + n + " 个项目到：\n" + ExportRoot, "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, "导出失败: " + ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    /// <summary>读取 .cproj：meta.txt 的 type/name（缺失则从文件名 <type>_<name> 或 config.json 推断）→ 解压到 custom_projects\<type>\<name>（重名确认覆盖）。返回 (type,name) 或 null。</summary>
    (string, string)? ImportProject(string file)
    {
        string type = "", name = "";
        using (var zip = ZipFile.OpenRead(file))
        {
            var meta = zip.GetEntry("meta.txt");
            if (meta != null)
            {
                using var r = new StreamReader(meta.Open(), Encoding.UTF8);
                foreach (var raw in r.ReadToEnd().Split('\n'))
                {
                    var ln = raw.Trim();
                    var eq = ln.IndexOf('=');
                    if (eq <= 0) continue;
                    var k = ln.Substring(0, eq).Trim();
                    var v = ln.Substring(eq + 1).Trim();
                    if (k == "type") type = v;
                    else if (k == "name") name = v;
                }
            }
            // 回退：文件名 <type>_<name>.cproj 推断类型
            if (string.IsNullOrEmpty(type))
            {
                var fn = Path.GetFileNameWithoutExtension(file);
                var us = fn.IndexOf('_');
                if (us > 0)
                {
                    var t = fn.Substring(0, us);
                    if (t is "plant" or "zombie" or "bullet" or "card") type = t;
                }
            }
            // 回退：从 config.json 读 name
            if (string.IsNullOrEmpty(name))
            {
                var cfgEntry = zip.GetEntry("config.json");
                if (cfgEntry != null)
                {
                    using var r = new StreamReader(cfgEntry.Open(), Encoding.UTF8);
                    using var doc = JsonDocument.Parse(r.ReadToEnd());
                    if (doc.RootElement.TryGetProperty("name", out var n)) name = n.GetString() ?? "";
                }
            }
            if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(name))
            {
                MessageBox.Show(this, "无法识别该包的类型/名称（缺少 meta.txt）", "导入失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
            var target = Path.Combine(GetTypeDir(type), name);
            if (Directory.Exists(target))
            {
                if (MessageBox.Show(this, "已存在同类型项目「" + name + "」，覆盖？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return null;
                Directory.Delete(target, true);
            }
            Directory.CreateDirectory(target);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith("/")) continue;   // 目录条目
                var safeName = Path.GetFileName(entry.FullName);   // 防路径穿越，仅取文件名
                if (string.IsNullOrEmpty(safeName)) continue;
                if (safeName == "meta.txt") continue;   // 元数据不落盘，保持项目目录只含 config.json + png
                var dest = Path.Combine(target, safeName);
                Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? target);
                using var input = entry.Open();
                using var output = File.Create(dest);
                input.CopyTo(output);
            }
        }
        return (type, name);
    }

    void OnImportCproj(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "导入自定义项目包", Filter = "自定义项目 (*.cproj)|*.cproj" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var res = ImportProject(dlg.FileName);
            if (res == null) return;
            var (type, name) = res.Value;
            SetProjectType(type);   // 跳转到对应类型列表视图
            MessageBox.Show(this, "已导入「" + name + "」。\n可点「生成」注入游戏，或点「保存」编辑后重新生成。", "导入成功", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshShareView();
        }
        catch (Exception ex) { MessageBox.Show(this, "导入失败: " + ex.Message, "导入失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    void OnImportPackageClick(object sender, RoutedEventArgs e)
    {
        var file = (string)((Button)sender).Tag;
        try
        {
            var res = ImportProject(file);
            if (res == null) return;
            var (type, name) = res.Value;
            SetProjectType(type);
            MessageBox.Show(this, "已导入「" + name + "」。", "导入成功", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshShareView();
        }
        catch (Exception ex) { MessageBox.Show(this, "导入失败: " + ex.Message, "导入失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    // ================= 分享分类页（SP7） =================

    void BuildShareView()
    {
        SharePanel.Children.Clear();
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text = "分享",
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("TextBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        sp.Children.Add(new TextBlock
        {
            Text = "项目导出为 .cproj 单文件包（含配置与帧图），导入他人分享的包即可使用",
            FontSize = 13,
            Foreground = (Brush)FindResource("SubBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        });

        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 0) };
        var exportAll = new Button { Content = "导出全部", Style = (Style)FindResource("CardBtn") };
        exportAll.Click += OnExportAll;
        var import = new Button { Content = "导入 .cproj", Style = (Style)FindResource("CardBtn"), Margin = new Thickness(10, 0, 0, 0) };
        import.Click += OnImportCproj;
        var refresh = new Button { Content = "刷新", Style = (Style)FindResource("CardBtn"), Margin = new Thickness(10, 0, 0, 0) };
        refresh.Click += (s, e) => RefreshShareView();
        btns.Children.Add(exportAll);
        btns.Children.Add(import);
        btns.Children.Add(refresh);
        sp.Children.Add(btns);

        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 20, 0, 8) };
        head.Children.Add(new TextBlock { Text = "已导出的包：", FontSize = 13, Foreground = (Brush)FindResource("TextBrush"), VerticalAlignment = VerticalAlignment.Center });
        _shareHint = new TextBlock { FontSize = 11, Foreground = (Brush)FindResource("SubBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        head.Children.Add(_shareHint);
        sp.Children.Add(head);

        _shareList = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        sp.Children.Add(_shareList);
        SharePanel.Children.Add(sp);
        RefreshShareView();
    }

    void RefreshShareView()
    {
        if (_shareList == null) return;
        _shareList.Children.Clear();
        try
        {
            Directory.CreateDirectory(ExportRoot);
            foreach (var f in Directory.GetFiles(ExportRoot, "*.cproj"))
                _shareList.Children.Add(BuildSharePackageRow(f));
            _shareHint.Text = _shareList.Children.Count == 0 ? "（暂无导出包，先在列表中导出）" : "共 " + _shareList.Children.Count + " 个";
        }
        catch (Exception ex) { _shareHint.Text = "读取失败: " + ex.Message; }
    }

    Border BuildSharePackageRow(string cprojPath)
    {
        var fn = Path.GetFileName(cprojPath);
        var card = new Border
        {
            Background = (Brush)FindResource("CardBrushW"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Margin = new Thickness(6),
            Padding = new Thickness(14, 8, 10, 8),
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var nm = new TextBlock { Text = fn, FontSize = 12, Foreground = (Brush)FindResource("TextBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var imp = new Button { Content = "导入", Style = (Style)FindResource("CardMiniBtn"), Tag = cprojPath, VerticalAlignment = VerticalAlignment.Center };
        imp.Click += OnImportPackageClick;
        Grid.SetColumn(imp, 1);
        grid.Children.Add(nm);
        grid.Children.Add(imp);
        card.Child = grid;
        return card;
    }

    // ================= 项目管理：重命名 / 复制（SP7） =================

    void OnCardRenameClick(object sender, RoutedEventArgs e)
    {
        var name = (string)((Button)sender).Tag;
        var dir = Path.Combine(GetTypeDir(_type), name);
        if (!Directory.Exists(dir)) { StatusText.Text = "目录不存在"; return; }
        var newName = PromptInput(this, "重命名" + TypeZh(_type), "输入新名称：", name);
        if (string.IsNullOrWhiteSpace(newName)) return;
        newName = newName.Trim();
        if (newName == name) return;
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { MessageBox.Show(this, "名称包含非法字符", "重命名", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var newDir = Path.Combine(GetTypeDir(_type), newName);
        if (Directory.Exists(newDir)) { MessageBox.Show(this, "已存在同名项目「" + newName + "」", "重命名", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        try
        {
            Directory.Move(dir, newDir);
            var cfg = Path.Combine(newDir, "config.json");
            if (File.Exists(cfg)) UpdateConfigName(cfg, newName);
            StatusText.Text = "已重命名为 " + newName;
            RefreshListView();
        }
        catch (Exception ex) { MessageBox.Show(this, "重命名失败: " + ex.Message, "重命名", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    void OnCardCopyClick(object sender, RoutedEventArgs e)
    {
        var name = (string)((Button)sender).Tag;
        var dir = Path.Combine(GetTypeDir(_type), name);
        if (!Directory.Exists(dir)) { StatusText.Text = "目录不存在"; return; }
        var newName = name + "_副本";
        var newDir = Path.Combine(GetTypeDir(_type), newName);
        int i = 1;
        while (Directory.Exists(newDir)) { newName = name + "_副本" + (i++); newDir = Path.Combine(GetTypeDir(_type), newName); }
        try
        {
            CopyDirectory(dir, newDir);
            var cfg = Path.Combine(newDir, "config.json");
            if (File.Exists(cfg)) UpdateConfigName(cfg, newName);
            StatusText.Text = "已复制为 " + newName;
            RefreshListView();
        }
        catch (Exception ex) { MessageBox.Show(this, "复制失败: " + ex.Message, "复制", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    /// <summary>config.json 的 name 字段改为新名（保留其余字段与顺序）。</summary>
    static void UpdateConfigName(string cfgPath, string newName)
    {
        var node = JsonNode.Parse(File.ReadAllText(cfgPath));
        if (node is JsonObject obj)
        {
            obj["name"] = newName;
            File.WriteAllText(cfgPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.GetFiles(src))
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), true);
        foreach (var sub in Directory.GetDirectories(src))
            CopyDirectory(sub, Path.Combine(dst, Path.GetFileName(sub)));
    }

    /// <summary>简单输入对话框（WPF 无内置 InputBox）。返回输入文本；取消返回 null。</summary>
    static string? PromptInput(Window owner, string title, string label, string def = "")
    {
        var tb = new TextBox
        {
            Text = def,
            Height = 30,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(8, 0, 8, 0),
            FontSize = 13,
        };
        tb.SelectAll();
        var ok = new Button { Content = "确定", Width = 90, Height = 32, FontSize = 13 };
        var cancel = new Button { Content = "取消", Width = 90, Height = 32, FontSize = 13, Margin = new Thickness(10, 0, 0, 0) };
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        btnRow.Children.Add(ok);
        btnRow.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 13, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(tb);
        panel.Children.Add(btnRow);
        var win = new Window
        {
            Title = title,
            Width = 400,
            Height = 172,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            WindowStyle = WindowStyle.ToolWindow,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xFF, 0xFF)),
            Content = panel,
        };
        tb.Focus();
        string? result = null;
        ok.Click += (s, e) => { result = tb.Text; win.DialogResult = true; };
        cancel.Click += (s, e) => { win.DialogResult = false; };
        win.ShowDialog();
        return result;
    }

    // ================= 列表 ↔ 编辑视图切换 =================

    void ShowListView()
    {
        ListView.Visibility = Visibility.Visible;
        EditView.Visibility = Visibility.Collapsed;
        ShareView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        RefreshListView();
    }

    void ShowEditView(bool edit)
    {
        ListView.Visibility = edit ? Visibility.Collapsed : Visibility.Visible;
        EditView.Visibility = edit ? Visibility.Visible : Visibility.Collapsed;
        ShareView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        if (edit) EditTitle.Text = "编辑" + TypeZh(_type);
    }

    void OnEditBack(object sender, RoutedEventArgs e) => ShowListView();

    void OnEditSave(object sender, RoutedEventArgs e)
    {
        switch (_type)
        {
            case "zombie": OnZombieSave(sender, e); break;
            case "bullet": OnBulletSave(sender, e); break;
            case "card": OnCardSave(sender, e); break;
            default: OnSave(sender, e); break;
        }
    }

    /// <summary>从项目目录加载缩略图（idle.png / bullet.png），缺失返回 null。</summary>
    static ImageSource? LoadThumb(string dir, string file)
    {
        try
        {
            var p = Path.Combine(dir, file);
            if (!File.Exists(p)) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(p);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>旧目录兼容：custom_plants 下若还有 bullets 子目录（旧子弹存储位置），植物列表要排除。</summary>
    bool IsLegacyBulletCfg(string cfg)
    {
        var oldBullets = System.IO.Path.Combine(PlantRoot, "bullets");
        return cfg.StartsWith(oldBullets + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>左栏条目点击：按当前类型分发到对应载入器。</summary>
    void LoadIntoEditor(string cfgPath)
    {
        switch (_type)
        {
            case "zombie": LoadZombieIntoEditor(cfgPath); break;
            case "bullet": LoadBulletIntoEditor(cfgPath); break;
            case "card": LoadCardIntoEditor(cfgPath); break;
            default: LoadPlantIntoEditor(cfgPath); break;
        }
    }

    // ================= 分类切换（植物 / 僵尸 / 子弹 / 卡牌 / 分享 / 设置） =================

    void OnCatPlant(object sender, RoutedEventArgs e) => SetProjectType("plant");
    void OnCatZombie(object sender, RoutedEventArgs e) => SetProjectType("zombie");
    void OnCatBullet(object sender, RoutedEventArgs e) => SetProjectType("bullet");
    void OnCatCard(object sender, RoutedEventArgs e) => SetProjectType("card");

    void OnCatShare(object sender, RoutedEventArgs e)
    {
        SetCatActive("share");
        ListView.Visibility = Visibility.Collapsed;
        EditView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        ShareView.Visibility = Visibility.Visible;
        RefreshShareView();   // 每次进入分享页刷新导出包列表
    }

    void OnCatSettings(object sender, RoutedEventArgs e)
    {
        SetCatActive("settings");
        ListView.Visibility = Visibility.Collapsed;
        EditView.Visibility = Visibility.Collapsed;
        ShareView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Visible;
    }

    void SetCatActive(string key)
    {
        CatPlant.Style = (Style)FindResource(key == "plant" ? "CatTileActive" : "CatTile");
        CatZombie.Style = (Style)FindResource(key == "zombie" ? "CatTileActive" : "CatTile");
        CatBullet.Style = (Style)FindResource(key == "bullet" ? "CatTileActive" : "CatTile");
        CatCard.Style = (Style)FindResource(key == "card" ? "CatTileActive" : "CatTile");
        CatShare.Style = (Style)FindResource(key == "share" ? "CatTileActive" : "CatTile");
        CatSettings.Style = (Style)FindResource(key == "settings" ? "CatTileActive" : "CatTile");
    }

    void SetProjectType(string type)
    {
        _type = type;
        SetCatActive(type);
        NewBtn.Content = "新建" + TypeZh(type);
        ListTitle.Text = TypeZh(type);
        if (_plantPanel != null) _plantPanel.Visibility = type == "plant" ? Visibility.Visible : Visibility.Collapsed;
        if (_zombiePanel != null) _zombiePanel.Visibility = type == "zombie" ? Visibility.Visible : Visibility.Collapsed;
        if (_bulletPanel != null) _bulletPanel.Visibility = type == "bullet" ? Visibility.Visible : Visibility.Collapsed;
        if (_cardPanel != null) _cardPanel.Visibility = type == "card" ? Visibility.Visible : Visibility.Collapsed;
        // 植物/僵尸双帧；子弹/卡牌单帧（隐藏攻击帧按钮）
        bool twoFrames = type == "plant" || type == "zombie";
        FrameIdleBtn.Visibility = twoFrames ? Visibility.Visible : Visibility.Collapsed;
        FrameAttackBtn.Visibility = twoFrames ? Visibility.Visible : Visibility.Collapsed;
        _frameAttack = false;
        UpdateFrameUi();
        _editingPath = "";
        ShowListView();
        UpdatePreview();
    }

    void LoadPlantIntoEditor(string cfgPath)
    {
        try
        {
            var def = JsonSerializer.Deserialize<CustomPlantDef>(File.ReadAllText(cfgPath));
            if (def == null) { StatusText.Text = "解析失败: " + cfgPath; return; }
            _editingPath = cfgPath;
            _tbName.Text = def.Name;
            _tbCost.Text = def.Cost.ToString();
            _tbCooldown.Text = def.Cooldown.ToString();
            _tbHp.Text = def.Hp.ToString();
            // 未知模板回填：不在列表则补一项并选中
            var tIdx = -1;
            for (int i = 0; i < _cbTemplate.Items.Count; i++)
                if (((string)_cbTemplate.Items[i]).StartsWith(def.Template + ":")) { tIdx = i; break; }
            if (tIdx < 0) { _cbTemplate.Items.Add(def.Template + ":" + def.Template); tIdx = _cbTemplate.Items.Count - 1; }
            _cbTemplate.SelectedIndex = tIdx;
            _tbDamage.Text = def.Attack.Damage.ToString();
            _tbInterval.Text = def.Attack.Interval.ToString();
            _tbRange.Text = def.Attack.Range.ToString();
            // 未知子弹回填：不在列表则补一项并选中。I1: 兼容 ★ 前缀（自制子弹项是 ★名:名，存储值是名）——先精确匹配 key:，再匹配 ★key:
            var bIdx = -1;
            for (int i = 0; i < _cbBullet.Items.Count; i++)
            {
                var bItem = (string)_cbBullet.Items[i];
                if (bItem.StartsWith(def.Attack.Bullet + ":") || bItem.StartsWith("★" + def.Attack.Bullet + ":")) { bIdx = i; break; }
            }
            if (bIdx < 0) { _cbBullet.Items.Add(def.Attack.Bullet + ":" + def.Attack.Bullet); bIdx = _cbBullet.Items.Count - 1; }
            _cbBullet.SelectedIndex = bIdx;
            _tbSun.Text = def.Attack.SunAmount.ToString();
            _tbDisplayName.Text = def.DisplayName ?? "";   // 旧 config 无此字段 → STJ 留默认空
            _tbLevelRestrict.Text = def.LevelRestrict ?? "";
            SelectComboByKey(_cbPassive, def.Passive?.Kind ?? "none");
            _tbPassiveValue.Text = (def.Passive?.Value ?? 5).ToString();
            _frameAttack = false;
            UpdateFrameUi();
            var idleP = Path.Combine(Path.GetDirectoryName(cfgPath) ?? "", def.Frame.Idle);
            if (File.Exists(idleP)) { _canvas.FromPng(File.ReadAllBytes(idleP)); CanvasImage.Source = _canvas.Bitmap; }
            StatusText.Text = "已载入 " + def.Name + "（待机帧）";
        }
        catch (Exception ex) { StatusText.Text = "载入失败: " + ex.Message; }
    }

    void OnNew(object sender, RoutedEventArgs e)
    {
        switch (_type)
        {
            case "zombie": OnZombieNew(); break;
            case "bullet": OnBulletNew(); break;
            case "card": OnCardNew(); break;
            default: OnPlantNew(); break;
        }
        ShowEditView(true);
    }

    void OnPlantNew()
    {
        _editingPath = "";
        _tbName.Text = "";
        _tbCost.Text = "125";
        _tbCooldown.Text = "7.5";
        _tbHp.Text = "300";
        _cbTemplate.SelectedIndex = 0;
        _tbDamage.Text = "40";
        _tbInterval.Text = "1.5";
        _tbRange.Text = "2000";
        _cbBullet.SelectedIndex = 0;
        _tbSun.Text = "25";
        _cbPassive.SelectedIndex = 0;
        _tbPassiveValue.Text = "5";
        _tbDisplayName.Text = "";
        _tbLevelRestrict.Text = "";
        _frameAttack = false;
        UpdateFrameUi();
        _canvas.Clear();
        StatusText.Text = "已新建，画完当前帧后点「保存定义」";
    }

    // ================= 全量子弹库（Task 1） =================

    async Task LoadBulletTypesAsync()
    {
        try
        {
            var s = await _get("/bullettypes");
            if (string.IsNullOrWhiteSpace(s)) return;   // 失败回退：保留 BuildParamForm 内置 5 个
            using var doc = JsonDocument.Parse(s);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array) return;
            var items = new List<string>();
            foreach (var el in root.EnumerateArray())
            {
                string key = "", name = "";
                if (el.TryGetProperty("key", out var k)) key = k.GetString() ?? "";
                if (el.TryGetProperty("name", out var n)) name = n.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(key)) continue;
                items.Add(string.IsNullOrWhiteSpace(name) ? key : key + ":" + name);
            }
            if (items.Count == 0) return;
            FillBulletCombos(items);
            StatusText.Text = "已载入全量子弹库（" + _cbBullet.Items.Count + " 种，含自制）";
        }
        catch
        {
            // 失败回退：保留内置 5 个
        }
    }

    /// <summary>SP6：拉 /cmd?name=ZombieTemplates 全量僵尸模板 [{key,name}] 填充 _cbZTemplate（失败回退内置 6 个）。
    /// key 是游戏真实僵尸 id（MOD SP6 支持任意 id + 模糊探测），存储值 = key 本身。</summary>
    async Task LoadZombieTemplatesAsync()
    {
        try
        {
            var s = await _get("/cmd?name=ZombieTemplates");
            if (string.IsNullOrWhiteSpace(s)) return;   // 失败回退：保留 BuildParamForm 内置 6 个
            using var doc = JsonDocument.Parse(s);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array) return;
            var items = new List<string>();
            foreach (var el in root.EnumerateArray())
            {
                string key = "", name = "";
                if (el.TryGetProperty("key", out var k)) key = k.GetString() ?? "";
                if (el.TryGetProperty("name", out var n)) name = n.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(key)) continue;
                items.Add(string.IsNullOrWhiteSpace(name) ? key : key + ":" + name);
            }
            if (items.Count == 0) return;
            // 保留当前选中（内置项 snake_case → PascalCase 存储键；动态项 key 即游戏 id）
            var curStored = "";
            if (_cbZTemplate.SelectedItem is string cur)
            {
                var curKey = cur.Split(':')[0];
                curStored = curKey;
                foreach (var t in ZombieTemplates)
                    if (string.Equals(t.Key, curKey, StringComparison.OrdinalIgnoreCase)) { curStored = t.Stored; break; }
            }
            _cbZTemplate.Items.Clear();
            foreach (var it in items) _cbZTemplate.Items.Add(it);
            int idx = 0;
            if (!string.IsNullOrEmpty(curStored))
            {
                for (int i = 0; i < _cbZTemplate.Items.Count; i++)
                {
                    var k = ((string)_cbZTemplate.Items[i]).Split(':')[0];
                    if (string.Equals(k, curStored, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
                }
            }
            _cbZTemplate.SelectedIndex = idx;
            StatusText.Text = "已载入全量僵尸模板（" + _cbZTemplate.Items.Count + " 个）";
        }
        catch
        {
            // 失败回退：保留内置 6 个
        }
    }

    /// <summary>全量植物模板（游戏内植物）：拉 /cmd?name=PlantTemplates [{key,name}] 填充 _cbTemplate（失败回退内置 13 个）。
    /// key 是游戏真实植物 id（MOD 全量化支持任意 id + 模糊探测），存储值 = key 本身。</summary>
    async Task LoadPlantTemplatesAsync()
    {
        try
        {
            var s = await _get("/cmd?name=PlantTemplates");
            if (string.IsNullOrWhiteSpace(s)) return;   // 失败回退：保留 BuildParamForm 内置 13 个
            using var doc = JsonDocument.Parse(s);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array) return;
            var items = new List<string>();
            foreach (var el in root.EnumerateArray())
            {
                string key = "", name = "";
                if (el.TryGetProperty("key", out var k)) key = k.GetString() ?? "";
                if (el.TryGetProperty("name", out var n)) name = n.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(key)) continue;
                items.Add(string.IsNullOrWhiteSpace(name) ? key : key + ":" + name);
            }
            if (items.Count == 0) return;
            // 保留当前选中：内置 key（如 shooter）→ 映射为游戏 id（PlantPeaShooter）
            var curStored = "";
            if (_cbTemplate.SelectedItem is string cur)
            {
                var curKey = cur.Split(':')[0];
                curStored = curKey;
                foreach (var t in PlantTemplateMap)
                    if (string.Equals(t.Key, curKey, StringComparison.OrdinalIgnoreCase)) { curStored = t.Id; break; }
            }
            _cbTemplate.Items.Clear();
            foreach (var it in items) _cbTemplate.Items.Add(it);
            int idx = 0;
            if (!string.IsNullOrEmpty(curStored))
            {
                for (int i = 0; i < _cbTemplate.Items.Count; i++)
                {
                    var k = ((string)_cbTemplate.Items[i]).Split(':')[0];
                    if (string.Equals(k, curStored, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
                }
            }
            _cbTemplate.SelectedIndex = idx;
            StatusText.Text = "已载入全量植物模板（" + _cbTemplate.Items.Count + " 个，可任意选择游戏内植物作模板）";
        }
        catch
        {
            // 失败回退：保留内置 13 个
        }
    }

    /// <summary>把全量子弹库 + 本地自制子弹同时填进植物/僵尸子弹下拉（含子弹克隆模板下拉）。</summary>
    void FillBulletCombos(IEnumerable<string> items)
    {
        _cbBullet.Items.Clear();
        _cbZBullet.Items.Clear();
        _cbBTemplate.Items.Clear();
        foreach (var it in items) { _cbBullet.Items.Add(it); _cbZBullet.Items.Add(it); _cbBTemplate.Items.Add(it); }
        AppendCustomBullets();
        _cbBullet.SelectedIndex = 0;
        _cbZBullet.SelectedIndex = 0;
        _cbBTemplate.SelectedIndex = 0;
    }

    /// <summary>把本地已保存的自制子弹追加到子弹下拉（★ 前缀标识），供植物/僵尸绑定。</summary>
    void AppendCustomBullets()
    {
        try
        {
            var br = GetTypeDir("bullet");
            if (!Directory.Exists(br)) return;
            foreach (var cfg in Directory.GetFiles(br, "config.json", SearchOption.AllDirectories))
            {
                var nm = Path.GetFileName(Path.GetDirectoryName(cfg)) ?? "";
                if (string.IsNullOrWhiteSpace(nm)) continue;
                _cbBullet.Items.Add("★" + nm + ":" + nm);
                _cbZBullet.Items.Add("★" + nm + ":" + nm);
            }
        }
        catch { }
    }

    // ================= 子弹模式（Task 3）：新建 / 载入 =================

    void OnBulletNew()
    {
        _editingPath = "";
        _tbBName.Text = "";
        _cbBTemplate.SelectedIndex = 0;
        _tbBDamage.Text = "40";
        _tbBSpeed.Text = "600";
        _tbBRange.Text = "2000";
        _tbBPenetrate.Text = "0";
        _tbBScale.Text = "1.0";
        _cbEffect.SelectedIndex = 0;
        _cbSplat.SelectedIndex = 0;   // none
        _cbAudio.SelectedIndex = 0;   // Ignite
        UpdateIconStatus();
        _frameAttack = false;
        UpdateFrameUi();
        _canvas.Clear();
        StatusText.Text = "已新建子弹，画完子弹图后点「保存子弹」";
    }

    void LoadBulletIntoEditor(string cfgPath)
    {
        try
        {
            var def = JsonSerializer.Deserialize<CustomBulletDef>(File.ReadAllText(cfgPath));
            if (def == null) { StatusText.Text = "解析失败: " + cfgPath; return; }
            _editingPath = cfgPath;
            _tbBName.Text = def.Name;
            SelectComboByKey(_cbBTemplate, def.Template);   // SP6 克隆模板回填（未知自动追加）
            _tbBDamage.Text = def.Damage.ToString();
            _tbBSpeed.Text = def.Speed.ToString();
            _tbBRange.Text = def.Range.ToString();
            _tbBPenetrate.Text = def.Penetrate.ToString();
            _tbBScale.Text = def.Scale.ToString();
            var eIdx = -1;
            for (int i = 0; i < _cbEffect.Items.Count; i++)
                if (((string)_cbEffect.Items[i]).StartsWith(def.Effect + ":")) { eIdx = i; break; }
            _cbEffect.SelectedIndex = eIdx < 0 ? 0 : eIdx;
            SelectComboByKey(_cbSplat, def.Splat ?? "none");   // SP8 命中特效回填（未知自动追加）
            SelectComboByKey(_cbAudio, string.IsNullOrWhiteSpace(def.Audio) ? "Ignite" : def.Audio);   // SP8 音效回填
            UpdateIconStatus();
            _frameAttack = false;
            UpdateFrameUi();
            var imgP = Path.Combine(Path.GetDirectoryName(cfgPath) ?? "", "bullet.png");
            if (File.Exists(imgP)) { _canvas.FromPng(File.ReadAllBytes(imgP)); CanvasImage.Source = _canvas.Bitmap; }
            StatusText.Text = "已载入子弹 " + def.Name;
        }
        catch (Exception ex) { StatusText.Text = "载入失败: " + ex.Message; }
    }

    CustomBulletDef BuildBulletDef()
    {
        double D(TextBox t, double d) => double.TryParse(t.Text, out var v) ? v : d;
        int I(TextBox t, int d) => int.TryParse(t.Text, out var v) ? v : d;
        return new CustomBulletDef
        {
            Name = _tbBName.Text?.Trim() ?? "",
            Template = ((string)_cbBTemplate.SelectedItem).Split(':')[0],
            Damage = D(_tbBDamage, 40),
            Speed = D(_tbBSpeed, 600),
            Range = D(_tbBRange, 2000),
            Penetrate = I(_tbBPenetrate, 0),
            Scale = D(_tbBScale, 1.0),
            Effect = ((string)_cbEffect.SelectedItem).Split(':')[0],
            EffectValue = 2.0,
            Audio = ((string)_cbAudio.SelectedItem).Split(':')[0],
            Splat = ((string)_cbSplat.SelectedItem).Split(':')[0],
        };
    }

    // ================= 子弹表单：保存 / 部署 / 删除 =================

    void OnBulletSave(object sender, RoutedEventArgs e)
    {
        if (!_canvas.HasContent()) { MessageBox.Show("画板为空，请先画子弹图"); return; }
        var name = _tbBName.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name)) { MessageBox.Show("名称不能为空"); return; }
        if (!double.TryParse(_tbBDamage.Text, out var dmg) || dmg < 0) { MessageBox.Show("伤害需 ≥0"); return; }
        if (!double.TryParse(_tbBSpeed.Text, out var spd) || spd <= 0) { MessageBox.Show("速度需 >0"); return; }
        if (!double.TryParse(_tbBRange.Text, out var rng) || rng < 0) { MessageBox.Show("射程需 ≥0"); return; }
        if (!double.TryParse(_tbBPenetrate.Text, out var pen) || pen < 0) { MessageBox.Show("穿透需 ≥0"); return; }
        if (!double.TryParse(_tbBScale.Text, out var sc) || sc <= 0) { MessageBox.Show("大小需 >0"); return; }

        var dir = Path.Combine(GetTypeDir("bullet"), name);
        var targetCfg = Path.Combine(dir, "config.json");
        if (Directory.Exists(dir) && _editingPath != targetCfg)
            if (MessageBox.Show("同名子弹已存在，覆盖？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var def = BuildBulletDef();
        Directory.CreateDirectory(dir);
        File.WriteAllText(targetCfg, JsonSerializer.Serialize(def, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllBytes(Path.Combine(dir, "bullet.png"), _canvas.ToPng());
        _editingPath = targetCfg;
        StatusText.Text = "子弹已保存: " + name;
        RefreshLeftList();
    }

    async void OnBulletDeploy(object sender, RoutedEventArgs e)
    {
        var name = _tbBName.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name)) { StatusText.Text = "名称不能为空"; return; }
        var cfgPath = Path.Combine(GetTypeDir("bullet"), name, "config.json");
        if (!File.Exists(cfgPath)) { StatusText.Text = "请先保存子弹"; return; }
        StatusText.Text = "注入中…";
        var r = await _get("/cmd?name=CustomBulletLoad&file=" + Uri.EscapeDataString(cfgPath));
        StatusText.Text = r == "ok" ? "子弹已注入游戏" : (r ?? "未连接游戏");
    }

    async void OnBulletDelete(object sender, RoutedEventArgs e)
    {
        var name = _tbBName.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name)) { StatusText.Text = "名称为空，无法删除"; return; }
        var dir = Path.Combine(GetTypeDir("bullet"), name);
        if (!Directory.Exists(dir)) { StatusText.Text = "目录不存在: " + dir; return; }
        if (MessageBox.Show(this, "确定删除自制子弹「" + name + "」？\n（删除本地文件并通知游戏移除）",
                "删除确认", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try { Directory.Delete(dir, true); }
        catch (Exception ex) { StatusText.Text = "删除文件失败: " + ex.Message; return; }
        var r = await _get("/cmd?name=CustomBulletRemove&name=" + Uri.EscapeDataString(name));
        StatusText.Text = r == "ok" ? "已删除 " + name : (r ?? "未连接游戏");
        _tbBName.Text = "";
        _canvas.Clear();
        RefreshLeftList();
    }

    // ================= 僵尸表单：新建 / 载入 / 保存 / 部署 / 删除 =================

    void OnZombieNew()
    {
        _editingPath = "";
        _tbZName.Text = "";
        _cbZTemplate.SelectedIndex = 0;
        _tbZHp.Text = "200";
        _tbZSpeed.Text = "0.3";
        _tbZDamage.Text = "100";
        _cbZMove.SelectedIndex = 0;
        _cbZAtk.SelectedIndex = 0;
        _cbZBullet.SelectedIndex = 0;
        _cbZSpecial.SelectedIndex = 0;
        _tbZSpecialValue.Text = "2";
        UpdateZBulletVisibility();
        _tbZDisplayName.Text = "";
        _frameAttack = false;
        UpdateFrameUi();
        _canvas.Clear();
        StatusText.Text = "已新建僵尸，画完待机帧后点「保存僵尸」";
    }

    CustomZombieDef BuildZombieDef()
    {
        double D(TextBox t, double d) => double.TryParse(t.Text, out var v) ? v : d;
        var key = ((string)_cbZTemplate.SelectedItem).Split(':')[0];
        var stored = key;
        foreach (var t in ZombieTemplates)
            if (t.Key == key) { stored = t.Stored; break; }
        return new CustomZombieDef
        {
            Name = _tbZName.Text?.Trim() ?? "",
            // 存储值必须与 MOD TEMPLATES 键一致（ZombieNormal…），否则 CustomZombieLoad 返回 err:bad-template
            Template = stored,
            Hp = D(_tbZHp, 200),
            Speed = D(_tbZSpeed, 0.3),
            Damage = D(_tbZDamage, 100),
            Scale = 1.0,
            DisplayName = string.IsNullOrWhiteSpace(_tbZDisplayName?.Text) ? _tbZName.Text.Trim() : _tbZDisplayName.Text.Trim(),
            Frame = new CustomFrameDef { Idle = "idle.png", Attack = "attack.png" },
            Move = ((string)_cbZMove.SelectedItem).Split(':')[0],
            Attack = ((string)_cbZAtk.SelectedItem).Split(':')[0],
            RangedBullet = ((string)_cbZBullet.SelectedItem).Split(':')[0].TrimStart('★'),
            Special = ((string)_cbZSpecial.SelectedItem).Split(':')[0],
            SpecialValue = D(_tbZSpecialValue, 2),
        };
    }

    void LoadZombieIntoEditor(string cfgPath)
    {
        try
        {
            var def = JsonSerializer.Deserialize<CustomZombieDef>(File.ReadAllText(cfgPath));
            if (def == null) { StatusText.Text = "解析失败: " + cfgPath; return; }
            _editingPath = cfgPath;
            _tbZName.Text = def.Name;
            _tbZHp.Text = def.Hp.ToString();
            _tbZSpeed.Text = def.Speed.ToString();
            _tbZDamage.Text = def.Damage.ToString();
            _tbZDisplayName.Text = def.DisplayName ?? "";
            // 模板回填：按存储值匹配（兼容 MOD TEMPLATES 键）；未知补一项
            var tIdx = -1;
            for (int i = 0; i < _cbZTemplate.Items.Count; i++)
            {
                var k = ((string)_cbZTemplate.Items[i]).Split(':')[0];
                string st = k;
                foreach (var t in ZombieTemplates)
                    if (t.Key == k) { st = t.Stored; break; }
                if (string.Equals(st, def.Template, StringComparison.OrdinalIgnoreCase)) { tIdx = i; break; }
            }
            if (tIdx < 0) { _cbZTemplate.Items.Add(def.Template + ":" + def.Template); tIdx = _cbZTemplate.Items.Count - 1; }
            _cbZTemplate.SelectedIndex = tIdx;
            // SP4 行为回填：移动/攻击/技能（未知值追加选中）；远程子弹复用 SelectComboByKey（含 ★ 自制）
            SelectComboByKey(_cbZMove, def.Move ?? "walk");
            SelectComboByKey(_cbZAtk, def.Attack ?? "bite");
            SelectComboByKey(_cbZBullet, def.RangedBullet ?? "Pea", starAllowed: true);
            SelectComboByKey(_cbZSpecial, def.Special ?? "none");
            _tbZSpecialValue.Text = def.SpecialValue.ToString();
            UpdateZBulletVisibility();
            _frameAttack = false;
            UpdateFrameUi();
            var idleP = Path.Combine(Path.GetDirectoryName(cfgPath) ?? "", def.Frame.Idle);
            if (File.Exists(idleP)) { _canvas.FromPng(File.ReadAllBytes(idleP)); CanvasImage.Source = _canvas.Bitmap; }
            StatusText.Text = "已载入僵尸 " + def.Name + "（待机帧）";
        }
        catch (Exception ex) { StatusText.Text = "载入失败: " + ex.Message; }
    }

    void OnZombieSave(object sender, RoutedEventArgs e)
    {
        if (!_canvas.HasContent()) { MessageBox.Show("画板为空，请先画图"); return; }
        var name = _tbZName.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name)) { MessageBox.Show("名称不能为空"); return; }
        if (!double.TryParse(_tbZHp.Text, out var hp) || hp < 0) { MessageBox.Show("血量需 ≥0"); return; }
        if (!double.TryParse(_tbZSpeed.Text, out var spd) || spd <= 0) { MessageBox.Show("移速需 >0"); return; }
        if (!double.TryParse(_tbZDamage.Text, out var dmg) || dmg < 0) { MessageBox.Show("伤害需 ≥0"); return; }

        var dir = Path.Combine(GetTypeDir("zombie"), name);
        var targetCfg = Path.Combine(dir, "config.json");
        if (Directory.Exists(dir) && _editingPath != targetCfg)
            if (MessageBox.Show("同名僵尸已存在，覆盖？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var def = BuildZombieDef();
        Directory.CreateDirectory(dir);
        File.WriteAllText(targetCfg, JsonSerializer.Serialize(def, new JsonSerializerOptions { WriteIndented = true }));
        var file = _frameAttack ? "attack.png" : "idle.png";
        File.WriteAllBytes(Path.Combine(dir, file), _canvas.ToPng());
        _editingPath = targetCfg;
        StatusText.Text = "已保存（" + file + "）。" +
            (_frameAttack ? "再切「待机帧」画完保存一次" : "再切「攻击帧」画完保存一次");
        RefreshLeftList();
    }

    async void OnZombieDeploy(object sender, RoutedEventArgs e)
    {
        var name = _tbZName.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name)) { StatusText.Text = "名称不能为空"; return; }
        var cfgPath = Path.Combine(GetTypeDir("zombie"), name, "config.json");
        if (!File.Exists(cfgPath)) { StatusText.Text = "请先保存僵尸"; return; }
        StatusText.Text = "注入中…";
        var r = await _get("/cmd?name=CustomZombieLoad&file=" + Uri.EscapeDataString(cfgPath));
        StatusText.Text = r == "ok" ? "僵尸已注入游戏" : (r ?? "未连接游戏");
        RefreshLeftList();
    }

    async void OnZombieDelete(object sender, RoutedEventArgs e)
    {
        var name = _tbZName.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name)) { StatusText.Text = "名称为空，无法删除"; return; }
        var dir = Path.Combine(GetTypeDir("zombie"), name);
        if (!Directory.Exists(dir)) { StatusText.Text = "目录不存在: " + dir; return; }
        if (MessageBox.Show(this, "确定删除自定义僵尸「" + name + "」？\n（删除本地文件并通知游戏移除）",
                "删除确认", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try { Directory.Delete(dir, true); }
        catch (Exception ex) { StatusText.Text = "删除文件失败: " + ex.Message; return; }
        var r = await _get("/cmd?name=CustomZombieRemove&name=" + Uri.EscapeDataString(name));
        StatusText.Text = r == "ok" ? "已删除 " + name : (r ?? "未连接游戏");
        _tbZName.Text = "";
        _canvas.Clear();
        RefreshLeftList();
    }

    // ================= 卡牌表单：新建 / 载入 / 保存 / 部署 / 删除（单帧） =================

    void OnCardNew()
    {
        _editingPath = "";
        _tbCName.Text = "";
        _tbCCost.Text = "125";
        _tbCCooldown.Text = "7.5";
        _tbCDisplayName.Text = "";
        _tbCDesc.Text = "";
        _frameAttack = false;
        UpdateFrameUi();
        _canvas.Clear();
        StatusText.Text = "已新建卡牌，画完卡面图后点「保存卡牌」";
    }

    CustomCardDef BuildCardDef()
    {
        int I(TextBox t, int d) => int.TryParse(t.Text, out var v) ? v : d;
        double D(TextBox t, double d) => double.TryParse(t.Text, out var v) ? v : d;
        return new CustomCardDef
        {
            Name = _tbCName.Text?.Trim() ?? "",
            Cost = I(_tbCCost, 125),
            Cooldown = D(_tbCCooldown, 7.5),
            DisplayName = string.IsNullOrWhiteSpace(_tbCDisplayName?.Text) ? _tbCName.Text.Trim() : _tbCDisplayName.Text.Trim(),
            Desc = _tbCDesc?.Text?.Trim() ?? "",
            Frame = new CustomFrameDef { Idle = "idle.png", Attack = "idle.png" },
        };
    }

    void LoadCardIntoEditor(string cfgPath)
    {
        try
        {
            var def = JsonSerializer.Deserialize<CustomCardDef>(File.ReadAllText(cfgPath));
            if (def == null) { StatusText.Text = "解析失败: " + cfgPath; return; }
            _editingPath = cfgPath;
            _tbCName.Text = def.Name;
            _tbCCost.Text = def.Cost.ToString();
            _tbCCooldown.Text = def.Cooldown.ToString();
            _tbCDisplayName.Text = def.DisplayName ?? "";
            _tbCDesc.Text = def.Desc ?? "";
            _frameAttack = false;
            UpdateFrameUi();
            var imgP = Path.Combine(Path.GetDirectoryName(cfgPath) ?? "", def.Frame.Idle);
            if (File.Exists(imgP)) { _canvas.FromPng(File.ReadAllBytes(imgP)); CanvasImage.Source = _canvas.Bitmap; }
            StatusText.Text = "已载入卡牌 " + def.Name;
        }
        catch (Exception ex) { StatusText.Text = "载入失败: " + ex.Message; }
    }

    void OnCardSave(object sender, RoutedEventArgs e)
    {
        if (!_canvas.HasContent()) { MessageBox.Show("画板为空，请先画卡面图"); return; }
        var name = _tbCName.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name)) { MessageBox.Show("名称不能为空"); return; }
        if (!int.TryParse(_tbCCost.Text, out var cost) || cost < 0) { MessageBox.Show("阳光需 ≥0 的整数"); return; }
        if (!double.TryParse(_tbCCooldown.Text, out var cd) || cd < 0) { MessageBox.Show("冷却需 ≥0"); return; }

        var dir = Path.Combine(GetTypeDir("card"), name);
        var targetCfg = Path.Combine(dir, "config.json");
        if (Directory.Exists(dir) && _editingPath != targetCfg)
            if (MessageBox.Show("同名卡牌已存在，覆盖？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var def = BuildCardDef();
        Directory.CreateDirectory(dir);
        File.WriteAllText(targetCfg, JsonSerializer.Serialize(def, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllBytes(Path.Combine(dir, "idle.png"), _canvas.ToPng());   // 单帧：卡面图存 idle.png
        _editingPath = targetCfg;
        StatusText.Text = "卡牌已保存: " + name;
        RefreshLeftList();
    }

    async void OnCardDeploy(object sender, RoutedEventArgs e)
    {
        var name = _tbCName.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name)) { StatusText.Text = "名称不能为空"; return; }
        var cfgPath = Path.Combine(GetTypeDir("card"), name, "config.json");
        if (!File.Exists(cfgPath)) { StatusText.Text = "请先保存卡牌"; return; }
        StatusText.Text = "注入中…";
        var r = await _get("/cmd?name=CustomCardLoad&file=" + Uri.EscapeDataString(cfgPath));
        StatusText.Text = r == "ok" ? "卡牌已注入游戏" : (r ?? "未连接游戏");
        RefreshLeftList();
    }

    async void OnCardDelete(object sender, RoutedEventArgs e)
    {
        var name = _tbCName.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name)) { StatusText.Text = "名称为空，无法删除"; return; }
        var dir = Path.Combine(GetTypeDir("card"), name);
        if (!Directory.Exists(dir)) { StatusText.Text = "目录不存在: " + dir; return; }
        if (MessageBox.Show(this, "确定删除自定义卡牌「" + name + "」？\n（删除本地文件并通知游戏移除）",
                "删除确认", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try { Directory.Delete(dir, true); }
        catch (Exception ex) { StatusText.Text = "删除文件失败: " + ex.Message; return; }
        var r = await _get("/cmd?name=CustomCardRemove&name=" + Uri.EscapeDataString(name));
        StatusText.Text = r == "ok" ? "已删除 " + name : (r ?? "未连接游戏");
        _tbCName.Text = "";
        _canvas.Clear();
        RefreshLeftList();
    }
}
