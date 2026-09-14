using System.Collections.Generic;
using Godot;

/// <summary>
/// M2c：联机光标。
/// 每个「其他玩家」一个彩色箭头 + 右下角小字昵称；颜色取自中继分配的名单颜色槽。
/// 坐标以「世界坐标 ×8 定点」传输，绘制时按相机变换换算回屏幕坐标
/// （因此跟随地图滚动/缩放，但文字大小固定不缩放）。
/// </summary>
public static class NetCursor
{
    /// <summary>发送间隔（毫秒）。</summary>
    const int SendIntervalMs = 100;
    /// <summary>超过该时长未更新则隐藏光标（避免残影）。</summary>
    const int StaleMs = 3000;
    /// <summary>世界坐标定点倍率。</summary>
    const float FixedScale = 8f;

    static CanvasLayer _layer;
    static ulong _lastSendMs;
    static readonly Dictionary<ushort, Cursor> _cursors = new Dictionary<ushort, Cursor>();

    sealed class Cursor
    {
        public Node2D Node;
        public Polygon2D Arrow;
        public Label Name;
        public Vector2 World;
        public ulong LastMs;
        public byte Slot = 255;
    }

    /// <summary>清空所有光标（离开房间 / 销毁房间 / 开新局时调用）。</summary>
    public static void Reset()
    {
        foreach (var kv in _cursors) Free(kv.Value);
        _cursors.Clear();
        _lastSendMs = 0;
    }

    /// <summary>开战时清掉上一局残留的光标。</summary>
    public static void MarkBattleStarted() { Reset(); }

    static void Free(Cursor c)
    {
        try { if (c != null && c.Node != null && GodotObject.IsInstanceValid(c.Node)) c.Node.QueueFree(); } catch { }
    }

    /// <summary>收到其他玩家的光标（坐标为中继转发来的定点值）。</summary>
    public static void OnRemoteCursor(ushort id, int fx, int fy)
    {
        var c = EnsureCursor(id);
        if (c == null) return;
        c.World = new Vector2(fx / FixedScale, fy / FixedScale);
        c.LastMs = Time.GetTicksMsec();
        if (c.Node != null && GodotObject.IsInstanceValid(c.Node)) c.Node.Visible = true;
    }

    public static void Tick(Node root)
    {
        try { TickCore(root); }
        catch { }   // 每帧调用：任何异常都不能冒泡出去
    }

    static ulong _lastErrMs;

    static void TickCore(Node root)
    {
        if (root == null) return;
        if (!NetSession.InRoom)
        {
            if (_cursors.Count > 0) Reset();
            return;
        }
        EnsureLayer(root);

        ulong now = Time.GetTicksMsec();
        if (now - _lastErrMs < 3000) return;   // 出错后降频，避免每帧刷错误
        _lastErrMs = now - 3000;

        // 未开战不上报光标：中继会丢弃未开战时的游戏数据帧，白传只会浪费带宽
        if (!NetSession.Started)
        {
            if (_cursors.Count > 0) Reset();
            return;
        }

        Viewport vp = null;
        Camera2D cam = null;
        try
        {
            vp = root.GetViewport();
            if (vp != null) cam = vp.GetCamera2D();
        }
        catch { }

        SendMine(now, vp, cam);
        SyncPeers();
        Layout(vp, cam, now);
    }

    static void EnsureLayer(Node root)
    {
        if (_layer != null && GodotObject.IsInstanceValid(_layer)) return;
        _layer = new CanvasLayer { Name = "NetCursorLayer", Layer = 152 };
        root.AddChild(_layer);
        _cursors.Clear();
    }

    /// <summary>每 100ms 上报一次自己的世界鼠标坐标。</summary>
    static void SendMine(ulong now, Viewport vp, Camera2D cam)
    {
        if (now - _lastSendMs < SendIntervalMs) return;
        // ★ 走发送预算：光标丢一帧完全无感，但超了中继限流会被记违规
        if (!NetBudget.TryTake()) return;
        _lastSendMs = now;
        Vector2 w = MouseWorld(vp, cam);
        NetSession.SendCursor((int)(w.X * FixedScale), (int)(w.Y * FixedScale));
    }

    /// <summary>鼠标的世界坐标（优先相机换算；无相机时退回视口坐标）。</summary>
    static Vector2 MouseWorld(Viewport vp, Camera2D cam)
    {
        try
        {
            if (cam != null && GodotObject.IsInstanceValid(cam)) return cam.GetGlobalMousePosition();
            if (vp != null) return vp.GetMousePosition();
        }
        catch { }
        return Vector2.Zero;
    }

    /// <summary>世界坐标 → 屏幕坐标（CanvasLayer 下的节点用屏幕坐标布局）。</summary>
    static Vector2 ToScreen(Viewport vp, Camera2D cam, Vector2 world)
    {
        try
        {
            if (cam == null || !GodotObject.IsInstanceValid(cam)) return world;
            Vector2 half = vp != null ? vp.GetVisibleRect().Size * 0.5f : new Vector2(640, 360);
            return (world - cam.GetScreenCenterPosition()) * cam.Zoom + half;
        }
        catch { }
        return world;
    }

    /// <summary>按中继下发的玩家名单同步光标：新增/更新昵称与颜色，移除已离开的玩家。</summary>
    static void SyncPeers()
    {
        var seen = new List<ushort>();
        foreach (var p in NetSession.Players)
        {
            if (p == null || p.Id == NetSession.MyPlayerId) continue;
            var c = EnsureCursor(p.Id);
            if (c == null) continue;
            seen.Add(p.Id);

            if (c.Slot != p.Color)
            {
                c.Slot = p.Color;
                PeerColors.GetRgb(p.Color, out float r, out float g, out float b);
                var col = new Color(r, g, b);
                if (c.Arrow != null && GodotObject.IsInstanceValid(c.Arrow)) c.Arrow.Color = col;
                if (c.Name != null && GodotObject.IsInstanceValid(c.Name)) c.Name.Modulate = col;
            }
            if (c.Name != null && GodotObject.IsInstanceValid(c.Name)) c.Name.Text = ": " + (p.Nick ?? "");
        }

        // ★ 名单还没到 / 为空时绝不能清理光标：否则刚通过 OnRemoteCursor 建出来的箭头
        //   会被这里当成“已离开的玩家”立刻删掉 → 表现就是「完全没有光标同步」。
        //   （旧代码还有个 `_cursors.Count == seen.Count` 的短路，计数碰巧相等时连清理都跳过，一并去掉）
        if (seen.Count == 0) return;

        List<ushort> gone = null;
        foreach (var kv in _cursors)
        {
            bool stillHere = false;
            for (int i = 0; i < seen.Count; i++) if (seen[i] == kv.Key) { stillHere = true; break; }
            if (!stillHere) { if (gone == null) gone = new List<ushort>(); gone.Add(kv.Key); }
        }
        if (gone == null) return;
        foreach (var id in gone)
        {
            Free(_cursors[id]);
            _cursors.Remove(id);
        }
    }

    static Cursor EnsureCursor(ushort id)
    {
        if (_cursors.TryGetValue(id, out var c)) return c;
        if (_layer == null || !GodotObject.IsInstanceValid(_layer)) return null;

        var node = new Node2D { Name = "NetCur" + id };
        var arrow = new Polygon2D
        {
            Polygon = new Vector2[] { new Vector2(0, 0), new Vector2(0, 18), new Vector2(13, 13) },
            Color = new Color(1, 1, 1)
        };
        node.AddChild(arrow);

        var label = new Label { Text = "", Position = new Vector2(14, 12) };
        node.AddChild(label);

        _layer.AddChild(node);
        c = new Cursor { Node = node, Arrow = arrow, Name = label };
        _cursors[id] = c;
        return c;
    }

    /// <summary>把世界坐标换算到屏幕并更新节点位置；超时的光标隐藏。</summary>
    static void Layout(Viewport vp, Camera2D cam, ulong now)
    {
        foreach (var kv in _cursors)
        {
            var c = kv.Value;
            if (c?.Node == null || !GodotObject.IsInstanceValid(c.Node)) continue;
            bool fresh = c.LastMs != 0 && now - c.LastMs <= StaleMs;
            c.Node.Visible = fresh;
            if (!fresh) continue;
            c.Node.Position = ToScreen(vp, cam, c.World);
        }
    }
}
