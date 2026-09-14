using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;

namespace PvzInject;

public partial class MainWindow : Window
{
    const string TargetDll = "PlantsVsZombies.dll";
    const string RemoteExe = "PvzheRemote.exe";
    const string BakName = "PlantsVsZombies.dll.mod_bak";

    // 内嵌资源名（LogicalName，在 csproj EmbeddedResource 里定义）
    const string ResDll = "PvzInject.res.dll";
    const string ResRemoteZip = "PvzInject.res.remotezip";
    // 外置修改器解压目标目录名（放游戏根目录下；framework 版，依赖游戏自带 .NET8）
    const string RemoteDirName = "外置修改器";

    string? _gameDir;   // 游戏 data_PlantsVsZombies_windows_x86_64 目录
    bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        Log("欢迎使用杂交版 MOD 一键注入器（内置版）");
        Log("已内置 电脑版 MOD + 外置修改器，点「一键解压并注入游戏」自动安装。");
        ShowBuiltinInfo();
        _ = DetectGameDirAsync();
    }

    // ============ 日志 ============
    void Log(string msg, string color = "#BFE3C6")
    {
        var t = DateTime.Now.ToString("HH:mm:ss");
        LogText.Inlines.Add(new Run($"[{t}] {msg}\n")
        {
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color))
        });
        (LogText.Parent as ScrollViewer)?.ScrollToEnd();
    }

    void SetReady(bool dirOk)
    {
        BtnInject.IsEnabled = dirOk && !_busy;
        BtnRestore.IsEnabled = dirOk && !_busy;
    }

    // ============ 内置资源信息 ============
    void ShowBuiltinInfo()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            foreach (var n in asm.GetManifestResourceNames())
                Log("内嵌资源: " + n);
        }
        catch { }
        // 尝试读取内置 dll / remote 大小，展示
        try { var b = ReadEmbedded(ResDll); BuiltinDllText.Text = $"🧩 电脑版 MOD：PlantsVsZombies.dll（{b.Length / 1048576.0:0.0} MB 注入成品）"; }
        catch (Exception ex) { Log("读取内置 DLL 失败: " + ex.Message, "#F2C94C"); }
        try { var b = ReadEmbedded(ResRemoteZip); BuiltinRemoteText.Text = $"🎮 外置修改器：{RemoteDirName}（{b.Length / 1048576.0:0.0} MB framework 版，用游戏自带 .NET8）"; }
        catch (Exception ex) { Log("读取内置外置修改器失败: " + ex.Message, "#F2C94C"); }
    }

    static byte[] ReadEmbedded(string logicalName)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var s = asm.GetManifestResourceStream(logicalName)
            ?? throw new Exception($"未找到内置资源 {logicalName}");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>把内嵌 zip（内存字节）解压到目标目录（含防路径穿越）。</summary>
    static void ExtractZipToDir(byte[] zip, string destDir, bool overwrite)
    {
        Directory.CreateDirectory(destDir);
        using var ms = new MemoryStream(zip);
        using var z = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Read);
        foreach (var e in z.Entries)
        {
            var rel = e.FullName.Replace('\\', '/');
            var target = Path.GetFullPath(Path.Combine(destDir, rel));
            if (!target.StartsWith(Path.GetFullPath(destDir), StringComparison.OrdinalIgnoreCase))
                continue;   // 防路径穿越
            if (string.IsNullOrEmpty(e.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (overwrite || !File.Exists(target))
            {
                using var src = e.Open();
                using var dst = File.Create(target);
                src.CopyTo(dst);
            }
        }
    }

    // ============ 游戏目录 ============
    async Task DetectGameDirAsync()
    {
        DirText.Text = "正在检测游戏目录…";
        var cands = await Task.Run(FindCandidates);
        var dir = cands.Count > 0 ? PickNewest(cands) : null;
        _gameDir = dir;
        if (dir != null)
        {
            if (cands.Count > 1)
            {
                Log("自动扫描到以下游戏目录（自动选择 0.27 或最高版本）：");
                foreach (var c in cands)
                    Log("  " + (c == dir ? "✓ " : "· ") + c, c == dir ? "#7DFFA0" : "#BFE3C6");
            }
            DirText.Text = "✓ 找到游戏：" + dir;
            DirText.Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59));
            var dll = Path.Combine(dir, TargetDll);
            long len = 0; try { len = new FileInfo(dll).Length; } catch { }
            var bak = Path.Combine(dir, BakName);
            string bakSt = File.Exists(bak) ? "（已有备份，可还原）" : "（暂无备份）";
            DllStateText.Text = $"当前游戏 DLL：{dll}\n大小：{len / 1048576.0:0.0} MB · {bakSt}";
            DllStateText.Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA8, 0xB8));
        }
        else
        {
            DirText.Text = "未自动检测到游戏目录，请点「🖥 检测 / 选择游戏目录」手动选择";
            DirText.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
        }
        SetReady(dir != null);
    }

    void OnDetectDir(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择游戏目录（含 PlantsVsZombies.dll 的文件夹）" };
        if (dlg.ShowDialog(this) == true)
        {
            var d = dlg.FolderName;
            if (File.Exists(Path.Combine(d, TargetDll)))
            {
                _gameDir = d;
                DirText.Text = "✓ 已手动选择：" + d;
                DirText.Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59));
                Log("已手动选择游戏目录：" + d);
                // 顺手显示 dll 状态
                var len = new FileInfo(Path.Combine(d, TargetDll)).Length;
                DllStateText.Text = $"当前游戏 DLL：{Path.Combine(d, TargetDll)}\n大小：{len / 1048576.0:0.0} MB";
            }
            else Log("该文件夹里没有 " + TargetDll, "#F2C94C");
            SetReady(_gameDir != null);
        }
    }

    // 收集所有疑似游戏目录，返回候选列表（后续按版本挑选）
    static List<string> FindCandidates()
    {
        var cands = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddCands(string root)
        {
            if (!Directory.Exists(root)) return;
            var l = new List<string>();
            CollectData(root, 5, l);
            foreach (var x in l) if (seen.Add(x)) cands.Add(x);
        }

        try
        {
            foreach (var drv in DriveInfo.GetDrives())
            {
                var dn = drv.Name;
                // 1) 惯例安装根：盘:\植物大战僵尸杂交版
                AddCands(Path.Combine(dn, "植物大战僵尸杂交版"));
                // 2) 盘根下所有疑似游戏目录（名字含 杂交/植物大战僵尸/PvZ，或含版本号 0.2x）
                IEnumerable<string> topDirs;
                try { topDirs = Directory.EnumerateDirectories(dn); }
                catch { continue; }
                foreach (var sub in topDirs)
                {
                    var n = Path.GetFileName(sub);
                    if (n.Contains("植物大战僵尸") || n.Contains("杂交") || n.Contains("PvZ", StringComparison.OrdinalIgnoreCase)
                        || n.Contains("PlantsVsZombies", StringComparison.OrdinalIgnoreCase)
                        || System.Text.RegularExpressions.Regex.IsMatch(n, @"0\.2\d"))
                        AddCands(sub);
                }
            }
        }
        catch { }

        // 3) 兜底：还没找到任何候选，才整盘深扫（较慢）
        if (cands.Count == 0)
        {
            try
            {
                foreach (var drv in DriveInfo.GetDrives())
                    AddCands(drv.Name);
            }
            catch { }
        }
        return cands;
    }

    // 收集所有符合条件的 data 目录（含 PlantsVsZombies.dll 且父目录有游戏 exe）
    static void CollectData(string root, int depth, List<string> hits)
    {
        try
        {
            if (!Directory.Exists(root) || depth <= 0) return;
            var name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar));
            if (name.Equals("data_PlantsVsZombies_windows_x86_64", StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(root, TargetDll)))
            {
                var parent = Directory.GetParent(root);
                if (parent != null && HasGameExe(parent.FullName)) { hits.Add(root); return; }
                // 父目录没有游戏 exe → 不是游戏本体（可能只是提取的 DLL 副本），继续找
            }
            foreach (var sub in Directory.EnumerateDirectories(root))
            {
                var n = Path.GetFileName(sub);
                if (n.StartsWith("$") || n.Equals("Windows") || n.Equals("System Volume Information")
                    || n.Equals("node_modules") || n.StartsWith("."))
                    continue;
                CollectData(sub, depth - 1, hits);
            }
        }
        catch { }
    }

    // 从候选里挑：优先 0.27 或更新版本；否则退回最高版本（避免误挑旧版本）
    static string PickNewest(List<string> cands)
    {
        Version? best = null;
        string bestPath = cands[0];
        Version? best27 = null;
        string path27 = null!;
        foreach (var c in cands)
        {
            var v = ExtractVersion(c);
            if (v == null) continue;
            if (best == null || v > best) { best = v; bestPath = c; }
            // 0.27 及以上优先（Major=0, Minor>=27）
            if (v.Major == 0 && v.Minor >= 27 && (best27 == null || v > best27))
            { best27 = v; path27 = c; }
        }
        return path27 != null ? path27 : bestPath;
    }

    static Version? ExtractVersion(string path)
    {
        try
        {
            var m = System.Text.RegularExpressions.Regex.Match(path, @"(\d+\.\d+(?:\.\d+)?)");
            if (m.Success && Version.TryParse(m.Groups[1].Value, out var v)) return v;
        }
        catch { }
        return null;
    }

    // 判断目录里是否有杂交版游戏启动 exe（游戏根目录特征）
    static bool HasGameExe(string dir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
            {
                var n = Path.GetFileNameWithoutExtension(f);
                if (n.Contains("植物大战僵尸") || n.Contains("杂交")
                    || n.StartsWith("PvZ", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("PlantsVsZombies", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { }
        return false;
    }

    // ============ 一键解压并注入 ============
    async void OnInject(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_gameDir == null) { Log("请先确定游戏目录", "#F2C94C"); return; }
        if (ConfirmCloseGame() == false) { Log("已取消：请关闭游戏后再注入", "#F2C94C"); return; }

        var gameRoot = Path.GetDirectoryName(_gameDir.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(gameRoot)) { Log("无法确定游戏根目录", "#F2C94C"); return; }

        _busy = true;
        SetReady(false);
        BtnInject.Content = "注入中…";
        try
        {
            Log("开始注入 → " + _gameDir);
            Log("游戏根目录 → " + gameRoot);

            // 1) 读内置资源
            Log("读取内置 MOD DLL …");
            byte[] dll = await Task.Run(() => ReadEmbedded(ResDll));
            var zipBytes = await Task.Run(() => ReadEmbedded(ResRemoteZip));
            Log($"内置 DLL {dll.Length / 1048576.0:0.0} MB · 外置修改器包 {zipBytes.Length / 1048576.0:0.0} MB ✓");

            // 2) 备份 data 目录原 DLL（保留最早一份）
            var dllPath = Path.Combine(_gameDir, TargetDll);
            var bakPath = Path.Combine(_gameDir, BakName);
            if (File.Exists(dllPath) && !File.Exists(bakPath))
            {
                await Task.Run(() => File.Copy(dllPath, bakPath, true));
                Log("已备份原版 DLL → " + BakName);
            }
            else if (File.Exists(bakPath)) Log("已存在备份（保留不动）");

            // 3) 写入 DLL 到 data 目录（临时文件再覆盖，尽量原子）
            var tmpDll = dllPath + ".tmp";
            await Task.Run(() =>
            {
                File.WriteAllBytes(tmpDll, dll);
                if (File.Exists(dllPath)) File.Delete(dllPath);
                File.Move(tmpDll, dllPath);
            });
            var len = new FileInfo(dllPath).Length;
            if (len < 15_000_000) throw new Exception("写入后文件异常（过小），可能失败");
            Log($"✅ 已写入 MOD DLL 到 data 目录：{len / 1048576.0:0.0} MB", "#7DFFA0");

            // 4) 解压外置修改器（framework 版目录）到游戏根目录\外置修改器
            var remoteTarget = Path.Combine(gameRoot, RemoteDirName);
            await Task.Run(() => ExtractZipToDir(zipBytes, remoteTarget, overwrite: true));
            Log($"✅ 已放置外置修改器目录：{remoteTarget}", "#7DFFA0");

            DllStateText.Text = $"已注入 ✓\nMOD DLL：{dllPath}\n外置修改器：{remoteTarget}";
            DllStateText.Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59));
            Log("🎉 全部完成！可以启动游戏了。", "#7DFFA0");
        }
        catch (Exception ex)
        {
            Log("注入失败：" + ex.Message, "#FF6B6B");
            Log("可点「还原 MOD」恢复原版 DLL。", "#FF6B6B");
        }
        finally
        {
            _busy = false;
            BtnInject.Content = "⚡ 一键解压并注入游戏";
            SetReady(_gameDir != null);
        }
    }

    // ============ 还原 ============
    async void OnRestore(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_gameDir == null) { Log("请先确定游戏目录", "#F2C94C"); return; }
        var bakPath = Path.Combine(_gameDir, BakName);
        var dllPath = Path.Combine(_gameDir, TargetDll);
        if (!File.Exists(bakPath)) { Log("没有找到备份（无法还原）", "#F2C94C"); return; }
        if (ConfirmCloseGame() == false) { Log("已取消：请关闭游戏后再还原", "#F2C94C"); return; }

        _busy = true;
        SetReady(false);
        try
        {
            await Task.Run(() => File.Copy(bakPath, dllPath, true));
            Log("✅ 已从备份还原原版 DLL（MOD 已卸载）", "#7DFFA0");
        }
        catch (Exception ex) { Log("还原失败：" + ex.Message, "#FF6B6B"); }
        finally
        {
            _busy = false;
            SetReady(_gameDir != null);
        }
    }

    /// <summary>若游戏在运行则询问是否自动关闭；返回 true=可以继续。</summary>
    bool ConfirmCloseGame()
    {
        var me = Process.GetCurrentProcess();
        var procs = Process.GetProcesses().Where(p =>
        {
            if (p.Id == me.Id) return false;
            try
            {
                var n = p.ProcessName;
                if (n.Contains("一键注入") || n.Contains("PvzInject")) return false;
                return n.Contains("植物大战僵尸") || n.Contains("PlantsVsZombies")
                    || n.StartsWith("PvZ") || n.Contains("杂交重制版");
            }
            catch { return false; }
        }).ToList();
        if (procs.Count == 0) return true;
        var r = MessageBox.Show(this,
            $"检测到游戏正在运行（{string.Join("、", procs.Select(p => p.ProcessName))}）。\n注入前需要关闭游戏，是否自动帮你关闭？",
            "请先关闭游戏", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return false;
        foreach (var p in procs)
        {
            try { p.Kill(); p.WaitForExit(3000); } catch { }
        }
        Log("已帮你关闭游戏");
        return true;
    }
}
