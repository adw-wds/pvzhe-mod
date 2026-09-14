using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace PvzheRemote;

public partial class ScriptWindow : Window
{
    static ScriptWindow? _inst;
    public static void ShowWindow()
    {
        if (_inst == null || !_inst.IsLoaded) _inst = new ScriptWindow();
        _inst.Show();
        _inst.Activate();
    }

    ScriptDef? _cur;

    public ScriptWindow()
    {
        InitializeComponent();
        CbCat.ItemsSource = new[] { "scripts", "custom", "system", "plant", "level", "fx" };
        RefreshList();
    }

    void OnTitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed) DragMove();
    }

    void OnWinClose(object sender, RoutedEventArgs e) => Close();

    sealed class ScriptRow
    {
        public ScriptDef Def = null!;
        public string Label = "";
    }

    void RefreshList()
    {
        ScriptList.ItemsSource = null;
        ScriptList.ItemsSource = ScriptStore.List
            .Select(d => new ScriptRow { Def = d, Label = (d.Enabled ? "" : "🚫 ") + d.Icon + "  " + d.Name + "（" + d.Cat + "）" })
            .ToList();
    }

    void OnSelect(object sender, SelectionChangedEventArgs e)
    {
        if (ScriptList.SelectedItem is ScriptRow r) LoadDef(r.Def);
    }

    void LoadDef(ScriptDef d)
    {
        _cur = d;
        TbName.Text = d.Name;
        TbIcon.Text = d.Icon;
        TbDesc.Text = d.Desc;
        var cats = CbCat.ItemsSource as string[];
        if (cats != null)
            for (int i = 0; i < cats.Length; i++)
                if (cats[i] == d.Cat) { CbCat.SelectedIndex = i; break; }
        TbCode.Text = ScriptStore.ReadCode(d);
        StText.Text = "已打开：" + d.Name;
    }

    void OnNew(object sender, RoutedEventArgs e)
    {
        _cur = ScriptStore.Create("新脚本", "scripts");
        ScriptStore.Save();
        RefreshList();
        // 选中新项
        for (int i = 0; i < ScriptList.Items.Count; i++)
            if ((ScriptList.Items[i] as ScriptRow)?.Def == _cur) { ScriptList.SelectedIndex = i; break; }
        LoadDef(_cur);
        StText.Text = "已新建，填好名称/分类/代码后点「保存」";
    }

    void OnSave(object sender, RoutedEventArgs e)
    {
        if (_cur == null) { StText.Text = "请先在左侧选择或新建脚本"; return; }
        _cur.Name = string.IsNullOrWhiteSpace(TbName.Text) ? "新脚本" : TbName.Text.Trim();
        _cur.Icon = string.IsNullOrWhiteSpace(TbIcon.Text) ? "📜" : TbIcon.Text.Trim();
        _cur.Desc = TbDesc.Text.Trim();
        _cur.Cat = (CbCat.SelectedItem as string) ?? "scripts";
        ScriptStore.WriteCode(_cur, TbCode.Text);
        ScriptStore.Save();
        StText.Text = "已保存（当前在脚本分类会即时刷新；其它分类下次进入生效）";
        RefreshList();
        MainWindow.ReloadScriptFeatures();
    }

    void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_cur == null) return;
        if (MessageBox.Show(this, "删除脚本「" + _cur.Name + "」？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        ScriptStore.Remove(_cur);
        ScriptStore.Save();
        _cur = null;
        RefreshList();
        StText.Text = "已删除";
        MainWindow.ReloadScriptFeatures();
    }

    async void OnRun(object sender, RoutedEventArgs e)
    {
        if (_cur == null) { StText.Text = "请先选择脚本"; return; }
        var code = TbCode.Text;
        StText.Text = "执行中…";
        var res = await ScriptEngine.RunAsync(_cur, code);
        StText.Text = "结果: " + res;
    }
}
