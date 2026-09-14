using System;
using System.Collections.Generic;
using Godot;
using PvzheMod;

/// <summary>
/// M4b：开局准备门禁（选卡同步）。
///
/// 问题：共享战场要求所有人同时开打。以前没有任何"准备"概念 —— 房主开始对局后，
/// 已经选完卡的直接进战斗，还在选卡的留在选卡界面，于是：
///   「有的要看动画、有的还在选卡」「新号没选卡就进去了」「第二局全乱」。
///
/// 信号：游戏 Wave feature 上的 <c>readySetPlantOver</c>（游戏自己用来标记"选卡完成"的字段）。
///   -1 = 还没进关卡（未准备）   0 = 选卡中   1 = 已选好
///
/// 门禁：本机已选好、但还有人没选好 → **暂停整棵树** + 屏幕中央显示「等待成员准备 (n/N)」；
/// 所有人都选好 → 自动解除暂停继续打。
/// </summary>
public static class NetGate
{
    const int TickMs = 200;

    static readonly Dictionary<ushort, byte> _ready = new Dictionary<ushort, byte>();
    static ulong _lastTickMs;
    static bool _wePaused;
    static CanvasLayer _layer;
    static Label _title, _detail;
    static bool _lastBroadcast = true;   // 初值与真实状态相反，保证首帧一定会广播一次

    public static bool AllReady { get; private set; }
    public static int ReadyCount { get; private set; }
    public static int TotalCount { get; private set; }
    /// <summary>当前是否因为我们而暂停（面板显示用）。</summary>
    public static bool Waiting { get { return _wePaused; } }

    public static void Reset()
    {
        _ready.Clear();
        AllReady = false;
        ReadyCount = 0;
        TotalCount = 0;
        _lastBroadcast = true;
        HideOverlay();
        if (_wePaused)
        {
            _wePaused = false;
            try
            {
                var tree = Engine.GetMainLoop() as SceneTree;
                if (tree != null) tree.Paused = false;
            }
            catch { }
        }
    }

    public static void Tick(Node root)
    {
        try { TickCore(root); } catch { }
    }

    static void TickCore(Node root)
    {
        if (root == null) return;
        if (!NetSession.InRoom || !NetSession.Started)
        {
            if (_ready.Count > 0 || _wePaused) Reset();
            return;
        }

        ulong now = Time.GetTicksMsec();
        if (now - _lastTickMs < TickMs) return;
        _lastTickMs = now;

        bool myReady = GameCheats.NetGetPlantReady() == 1;

        // 自己的准备状态变化就广播（每 200ms 查一次，变了立刻发）
        if (myReady != _lastBroadcast)
        {
            _lastBroadcast = myReady;
            Broadcast(myReady);
        }
        _ready[NetSession.MyPlayerId] = (byte)(myReady ? 1 : 0);

        // 以房间名单为准统计：名单里没报过 ready 的一律算"未准备"
        int total = 0, ready = 0;
        var players = NetSession.Players;
        for (int i = 0; i < players.Count; i++)
        {
            var p = players[i];
            if (p == null) continue;
            total++;
            byte r;
            if (_ready.TryGetValue(p.Id, out r) && r != 0) ready++;
        }
        if (total == 0) total = 1;   // 名单还没下发，至少把自己算上，避免误判"已齐"

        ReadyCount = ready;
        TotalCount = total;
        AllReady = myReady && ready >= total;

        if (myReady && !AllReady) ShowWaiting(root, ready, total);
        else HideWaiting();
    }

    static void Broadcast(bool ready)
    {
        try
        {
            var w = new BitWriter();
            w.WriteByte(NetProto.SyncReady);
            w.WriteU16(NetSession.MyPlayerId);
            w.WriteByte((byte)(ready ? 1 : 0));
            NetSession.SendSync(w.ToArray());
        }
        catch { }
    }

    /// <summary>收到别人的准备状态（NetSession 分发）。</summary>
    public static void OnReady(ushort id, bool ready)
    {
        try { _ready[id] = (byte)(ready ? 1 : 0); } catch { }
    }

    // ================= 等待遮罩 =================
    static void ShowWaiting(Node root, int ready, int total)
    {
        EnsureLayer(root);
        if (_layer == null || !GodotObject.IsInstanceValid(_layer)) return;

        if (_title != null && GodotObject.IsInstanceValid(_title)) _title.Text = "等待成员准备";
        if (_detail != null && GodotObject.IsInstanceValid(_detail))
            _detail.Text = "已准备 " + ready + " / " + total + "\n所有人选完卡后自动开始";
        _layer.Visible = true;

        if (!_wePaused)
        {
            try
            {
                var tree = root.GetTree();
                if (tree != null && !tree.Paused) { tree.Paused = true; _wePaused = true; }
            }
            catch { }
        }
    }

    static void HideWaiting()
    {
        HideOverlay();
        if (!_wePaused) return;
        _wePaused = false;
        try
        {
            var tree = Engine.GetMainLoop() as SceneTree;
            if (tree != null) tree.Paused = false;
        }
        catch { }
    }

    static void HideOverlay()
    {
        try { if (_layer != null && GodotObject.IsInstanceValid(_layer)) _layer.Visible = false; } catch { }
    }

    static void EnsureLayer(Node root)
    {
        if (_layer != null && GodotObject.IsInstanceValid(_layer)) return;

        // ProcessMode=Always：树被暂停时遮罩自己还得能显示
        _layer = new CanvasLayer { Name = "NetGateLayer", Layer = 158, ProcessMode = Node.ProcessModeEnum.Always };

        var dim = new ColorRect { Color = new Color(0f, 0f, 0f, 0.45f) };
        dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        dim.MouseFilter = Control.MouseFilterEnum.Ignore;
        _layer.AddChild(dim);

        var cc = new CenterContainer();
        cc.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        cc.MouseFilter = Control.MouseFilterEnum.Ignore;
        _layer.AddChild(cc);

        var panel = new PanelContainer();
        var sb = new StyleBoxFlat();
        sb.BgColor = new Color(0.10f, 0.13f, 0.16f, 0.96f);
        sb.BorderColor = new Color(0.45f, 0.78f, 0.62f, 0.95f);
        sb.SetBorderWidthAll(2);
        sb.SetCornerRadiusAll(14);
        sb.SetContentMarginAll(22);
        panel.AddThemeStyleboxOverride("panel", sb);
        panel.MouseFilter = Control.MouseFilterEnum.Ignore;
        cc.AddChild(panel);

        var vb = new VBoxContainer();
        vb.Alignment = BoxContainer.AlignmentMode.Center;
        vb.AddThemeConstantOverride("separation", 10);
        panel.AddChild(vb);

        _title = new Label { Text = "等待成员准备", HorizontalAlignment = HorizontalAlignment.Center };
        _title.AddThemeFontSizeOverride("font_size", 26);
        _title.AddThemeColorOverride("font_color", new Color(0.75f, 1f, 0.86f));
        vb.AddChild(_title);

        _detail = new Label { Text = "", HorizontalAlignment = HorizontalAlignment.Center };
        _detail.AddThemeFontSizeOverride("font_size", 15);
        _detail.AddThemeColorOverride("font_color", new Color(0.85f, 0.9f, 0.95f));
        vb.AddChild(_detail);

        root.AddChild(_layer);
        _layer.Visible = false;
    }

    /// <summary>面板显示用的一行摘要。</summary>
    public static string Describe()
    {
        if (NetSession.State == NetState.Offline || !NetSession.Started) return "";
        if (AllReady) return "全员已准备";
        return "准备进度 " + ReadyCount + "/" + TotalCount + (Waiting ? "（等待中）" : "");
    }
}
