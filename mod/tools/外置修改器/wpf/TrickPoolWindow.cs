using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PvzheRemote;

/// <summary>篡改·目标卡池 选择窗口（Tab 分页：植物 / 僵尸 多选，避免并排重叠）。
/// MOD 侧 AutoRandomizeSeedBank / ApplyTrickFeatures / AutoTrickConveyorSlots 读取 TrickPool 生效。</summary>
public class TrickPoolWindow : Window
{
    readonly Func<string, Task<string?>> _get;
    static TrickPoolWindow? _inst;

    // 一张可选卡：Id 是实际卡 key，ListBox 显示 Name(id)
    sealed class Card
    {
        public string Id = "";
        public string Name = "";
        public override string ToString() => Name + "   (" + Id + ")";
    }

    readonly List<Card> _plantAll = new();
    readonly List<Card> _zombieAll = new();
    readonly HashSet<Card> _sel = new();          // 已选卡片（对象引用，跨搜索过滤保持）

    TextBox _searchP = null!, _searchZ = null!;
    ListBox _plantList = null!, _zombieList = null!;
    ListBox _selBox = null!;
    TextBlock _stat = null!;

    TrickPoolWindow(Func<string, Task<string?>> get)
    {
        _get = get;
        Title = "篡改 · 目标卡池";
        Width = 880; Height = 640; MinWidth = 720; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 0 说明
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 1 列表Tab
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 2 已选
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 3 底部
        root.Margin = new Thickness(14);

        // ---- 第 0 行：说明 ----
        var tip = new TextBlock
        {
            Text = "勾选要刷出的植物/僵尸 —— 老虎机盲盒 / 种子雨 / 传送带会自动改成勾选的这些卡。" +
                   "只勾 1 张并开「固定第1张卡」= 只刷这一张；勾多张 = 从勾选里随机刷（可植物僵尸混搭）。" +
                   "需先在「篡改」页开启对应开关。",
            Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60)),
            FontSize = 12, Margin = new Thickness(2, 0, 2, 8),
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetRow(tip, 0);
        root.Children.Add(tip);

        // ---- 第 1 行：TabControl（植物 / 僵尸） ----
        var tabs = new TabControl { Margin = new Thickness(0, 0, 0, 8) };
        tabs.Items.Add(MakeTab("植物", ref _searchP, ref _plantList, _plantAll, true));
        tabs.Items.Add(MakeTab("僵尸", ref _searchZ, ref _zombieList, _zombieAll, false));
        Grid.SetRow(tabs, 1);
        root.Children.Add(tabs);

        // ---- 第 2 行：已选列表 ----
        var selGrid = new Grid();
        selGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        selGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(96) });
        var selTitle = new TextBlock { Text = "已选卡（显示为 中文名 (卡id)）", FontWeight = FontWeights.Bold, FontSize = 12, Margin = new Thickness(2, 0, 2, 4) };
        selGrid.Children.Add(selTitle);
        _selBox = new ListBox
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0)),
            BorderThickness = new Thickness(1),
            FontSize = 12,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_selBox, ScrollBarVisibility.Auto);
        Grid.SetRow(_selBox, 1);
        selGrid.Children.Add(_selBox);
        Grid.SetRow(selGrid, 2);
        root.Children.Add(selGrid);

        // ---- 第 3 行：统计 + 按钮 ----
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        _stat = new TextBlock { Foreground = Brushes.Gray, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Text = "加载中…" };
        bottom.Children.Add(_stat);
        var clear = MakeBtn("清空池", new SolidColorBrush(Color.FromRgb(0xEF, 0xEF, 0xEF)), Brushes.DimGray);
        clear.Click += (_, _) => { _sel.Clear(); SyncSelBox(); RefreshListSelections(); };
        var save = MakeBtn("保存并关闭", new SolidColorBrush(Color.FromRgb(0x1E, 0x88, 0xE5)), Brushes.White);
        save.Click += async (_, _) => await SaveAsync();
        bottom.Children.Add(clear);
        bottom.Children.Add(save);
        Grid.SetRow(bottom, 3);
        root.Children.Add(bottom);

        Content = root;
        Loaded += async (_, _) => await LoadAsync();
    }

    public static void Show(Func<string, Task<string?>> get)
    {
        if (_inst == null || !_inst.IsLoaded) _inst = new TrickPoolWindow(get);
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w is MainWindow);
        if (owner != null && owner.IsVisible) _inst.Owner = owner;
        _inst.Show();
        _inst.Activate();
    }

    static Button MakeBtn(string text, Brush bg, Brush fg) => new()
    {
        Content = text, Background = bg, Foreground = fg,
        Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0),
        FontSize = 12, BorderThickness = new Thickness(0),
        Cursor = System.Windows.Input.Cursors.Hand,
    };

    /// <summary>单页：搜索框（顶）+ 多选 ListBox（填满）。listChanged 时同步选中。</summary>
    TabItem MakeTab(string title, ref TextBox search, ref ListBox list, List<Card> all, bool isPlant)
    {
        var dock = new DockPanel { Margin = new Thickness(4) };
        search = new TextBox { Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(6, 3, 6, 3), FontSize = 12 };
        search.TextChanged += (_, _) => ApplyFilter();
        DockPanel.SetDock(search, Dock.Top);
        dock.Children.Add(search);

        list = new ListBox
        {
            SelectionMode = SelectionMode.Multiple,
            FontSize = 12,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
            BorderThickness = new Thickness(1),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        list.SelectionChanged += (_, _) => { SyncSelFromLists(); };
        dock.Children.Add(list);

        var tab = new TabItem { Header = title };
        tab.Content = dock;
        return tab;
    }

    void ApplyFilter()
    {
        string fp = (_searchP.Text ?? "").Trim().ToLower();
        string fz = (_searchZ.Text ?? "").Trim().ToLower();
        SetFiltered(_plantList, _plantAll, fp);
        SetFiltered(_zombieList, _zombieAll, fz);
    }

    void SetFiltered(ListBox list, List<Card> all, string f)
    {
        var items = all.Where(c => f.Length == 0 || c.Id.ToLower().Contains(f) || c.Name.ToLower().Contains(f)).ToList();
        list.ItemsSource = null;
        list.Items.Clear();
        foreach (var c in items) list.Items.Add(c);
        // 恢复该过滤范围内已勾选的高亮
        foreach (var c in items)
            if (_sel.Contains(c)) list.SelectedItems.Add(c);
    }

    async Task LoadAsync()
    {
        var pp = await _get("/packets?type=plant");
        if (pp != null && pp.StartsWith("[")) _plantAll.AddRange(ParsePackets(pp));
        var zz = await _get("/packets?type=zombie");
        if (zz != null && zz.StartsWith("[")) _zombieAll.AddRange(ParsePackets(zz));
        // 读当前 TrickPool
        var g = await _get("/get");
        if (g != null && g.StartsWith("{"))
        {
            try
            {
                using var doc = JsonDocument.Parse(g);
                if (doc.RootElement.TryGetProperty("TrickPool", out var tp))
                {
                    var s = tp.GetString();
                    if (!string.IsNullOrEmpty(s))
                    {
                        var want = new HashSet<string>(s.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0));
                        foreach (var c in _plantAll.Concat(_zombieAll))
                            if (want.Contains(c.Id)) _sel.Add(c);
                    }
                }
            }
            catch { }
        }
        SetFiltered(_plantList, _plantAll, "");
        SetFiltered(_zombieList, _zombieAll, "");
        SyncSelBox();
        _stat.Text = "共 " + _plantAll.Count + " 植物 / " + _zombieAll.Count + " 僵尸";
    }

    static List<Card> ParsePackets(string json)
    {
        var r = new List<Card>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    var s = el.GetString() ?? "";
                    if (s.Length > 0) r.Add(new Card { Id = s, Name = s });
                }
                else
                {
                    var id = el.TryGetProperty("id", out var idp) ? idp.GetString() ?? "" : "";
                    var nm = el.TryGetProperty("name", out var nmp) ? nmp.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(id)) continue;
                    r.Add(new Card { Id = id, Name = string.IsNullOrEmpty(nm) ? id : nm });
                }
            }
        }
        catch { }
        return r;
    }

    // ListBox 勾选变化 → 同步 _sel（两列表并集）
    void SyncSelFromLists()
    {
        _sel.Clear();
        foreach (Card c in _plantList.SelectedItems) _sel.Add(c);
        foreach (Card c in _zombieList.SelectedItems) _sel.Add(c);
        SyncSelBox();
    }

    void SyncSelBox()
    {
        _selBox.Items.Clear();
        foreach (var c in _sel) _selBox.Items.Add(c);
        _stat.Text = "已选 " + _sel.Count + " 张（植物 " + _sel.Count(x => x.Id.StartsWith("Plant", StringComparison.OrdinalIgnoreCase)) + " / 僵尸 " + _sel.Count(x => !x.Id.StartsWith("Plant", StringComparison.OrdinalIgnoreCase)) + "）";
    }

    void RefreshListSelections()
    {
        foreach (var c in _plantAll) { if (_sel.Contains(c) && !_plantList.SelectedItems.Contains(c)) _plantList.SelectedItems.Add(c); }
        foreach (var c in _zombieAll) { if (_sel.Contains(c) && !_zombieList.SelectedItems.Contains(c)) _zombieList.SelectedItems.Add(c); }
        SyncSelBox();
    }

    async Task SaveAsync()
    {
        var arr = _sel.Select(c => c.Id).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var v = string.Join(",", arr);
        _stat.Text = "保存中…";
        // 先写本地文件（游戏 MOD 同机读取），再发极短指令加载——避免几百张卡 id 拼成超长 URL 导致保存失败
        bool fileOk = false;
        try
        {
            var dir = @"mod";
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "trickpool.txt"), v);
            fileOk = true;
        }
        catch { }
        string? r = fileOk ? await _get("/set?name=TrickReload&val=1") : null;
        if (r != "queued" && !fileOk)
            r = await _get("/set?name=TrickPool&val=" + Uri.EscapeDataString(v));   // 兜底（无本地路径时走超长 URL）
        if (r == "queued")
        {
            _stat.Text = "已保存 " + arr.Length + " 张目标卡";
            await Task.Delay(700);
            Close();
            return;
        }
        // 失败自动重试一次
        _stat.Text = "请求未成功，自动重试…";
        await Task.Delay(300);
        r = fileOk ? await _get("/set?name=TrickReload&val=1") : await _get("/set?name=TrickPool&val=" + Uri.EscapeDataString(v));
        if (r == "queued")
        {
            _stat.Text = "已保存（重试成功）" + arr.Length + " 张目标卡";
            await Task.Delay(700);
            Close();
        }
        else _stat.Text = "保存失败：" + (r ?? "未连接") + "（请点 ⚡ 检测连接后再试）";
    }
}
