using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PvzheRemote;

public enum CanvasTool { Pen, Eraser, Eyedrop, Fill }

public sealed class PixelCanvas : FrameworkElement
{
    public int GridSize { get; set; } = 48;
        public int BrushSize { get; set; } = 1;
    public WriteableBitmap Bitmap { get; private set; }
    public Color CurrentColor { get; set; } = Color.FromRgb(80, 200, 90);
    public CanvasTool Tool { get; set; } = CanvasTool.Pen;
    public System.Action<int, int> PixelChanged;

    byte[] _buf;   // BGRA，GridSize*GridSize*4（可随 Resize 重建）
    readonly System.Collections.Generic.List<byte[]> _history = new();
    bool _inStroke;

    public PixelCanvas()
    {
        Bitmap = new WriteableBitmap(GridSize, GridSize, 96, 96, PixelFormats.Bgra32, null);
        _buf = new byte[GridSize * GridSize * 4];
        Clear();
        MouseLeftButtonDown += (s, e) => { BeginStroke(); OnPaint(s, e); };
        MouseMove += (s, e) => { if (e.LeftButton == MouseButtonState.Pressed) OnPaint(s, e); };
        MouseRightButtonDown += (s, e) => { var old = Tool; Tool = CanvasTool.Eraser; OnPaint(s, e); Tool = old; };
        MouseLeftButtonUp += (s, e) => EndStroke();
    }

    void OnPaint(object sender, MouseEventArgs e)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var pos = e.GetPosition(this);
        int gx = (int)(pos.X / ActualWidth * GridSize);
        int gy = (int)(pos.Y / ActualHeight * GridSize);
        if (gx < 0 || gy < 0 || gx >= GridSize || gy >= GridSize) return;
        if (Tool == CanvasTool.Eyedrop) { CurrentColor = Pick(gx, gy); return; }
        if (Tool == CanvasTool.Fill) { FillRegion(gx, gy, CurrentColor); return; }
        SetPixel(gx, gy, Tool == CanvasTool.Eraser ? Colors.Transparent : CurrentColor);
    }

    /// <summary>开始一个笔画：先压入当前状态快照（供整笔画一次撤销）。</summary>
    public void BeginStroke()
    {
        if (_inStroke) return;
        _inStroke = true;
        PushHistory();
    }

    /// <summary>结束当前笔画。</summary>
    public void EndStroke()
    {
        _inStroke = false;
    }

    /// <summary>把当前缓冲区快照压入历史（上限 200）。</summary>
    void PushHistory()
    {
        _history.Add((byte[])_buf.Clone());
        if (_history.Count > 200) _history.RemoveAt(0);
    }

    public void SetPixel(int x, int y, Color c)
    {
        if (x < 0 || y < 0 || x >= GridSize || y >= GridSize) return;
        // 圆形笔刷：BrushSize 为直径，覆盖圆形范围内的像素
        double r = (BrushSize - 1) / 2.0;
        int minx = Math.Max(0, (int)Math.Floor(x - r));
        int maxx = Math.Min(GridSize - 1, (int)Math.Ceiling(x + r));
        int miny = Math.Max(0, (int)Math.Floor(y - r));
        int maxy = Math.Min(GridSize - 1, (int)Math.Ceiling(y + r));
        double rr = r * r + 0.5;
        for (int yy = miny; yy <= maxy; yy++)
        {
            for (int xx = minx; xx <= maxx; xx++)
            {
                double dx = xx - x, dy = yy - y;
                if (dx * dx + dy * dy > rr) continue;
                int i = (yy * GridSize + xx) * 4;
                _buf[i] = c.B; _buf[i + 1] = c.G; _buf[i + 2] = c.R; _buf[i + 3] = c.A;
                Bitmap.WritePixels(new Int32Rect(xx, yy, 1, 1), _buf, GridSize * 4, i);
            }
        }
        PixelChanged?.Invoke(x, y);
    }

    // ---- 笔画连线：快速拖动时自动在相邻采样点间插值，避免断线 ----
    int _lastSX = -1, _lastSY = -1;

    public void ResetStroke() { _lastSX = -1; _lastSY = -1; }

    /// <summary>从上一点到 (x,y) 画一条 Bresenham 直线（当前笔刷圆形填充）。</summary>
    public void StrokeTo(int x, int y, Color c)
    {
        if (_lastSX < 0 || _lastSY < 0)
        {
            SetPixel(x, y, c);
            _lastSX = x; _lastSY = y;
            return;
        }
        int dx = Math.Abs(x - _lastSX), dy = Math.Abs(y - _lastSY);
        int sx = x >= _lastSX ? 1 : -1, sy = y >= _lastSY ? 1 : -1;
        int err = dx - dy;
        int cx = _lastSX, cy = _lastSY;
        while (true)
        {
            SetPixel(cx, cy, c);
            if (cx == x && cy == y) break;
            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; cx += sx; }
            if (e2 < dx) { err += dx; cy += sy; }
        }
        _lastSX = x; _lastSY = y;
    }

    public void FillRegion(int x, int y, Color c)
    {
        var target = Pick(x, y);
        if (target == c) return;
        PushHistory();
        var stack = new System.Collections.Generic.Stack<(int, int)>();
        stack.Push((x, y));
        while (stack.Count > 0)
        {
            var (cx, cy) = stack.Pop();
            if (cx < 0 || cy < 0 || cx >= GridSize || cy >= GridSize) continue;
            if (Pick(cx, cy) != target) continue;
            int i = (cy * GridSize + cx) * 4;
            _buf[i] = c.B; _buf[i + 1] = c.G; _buf[i + 2] = c.R; _buf[i + 3] = c.A;
            stack.Push((cx + 1, cy)); stack.Push((cx - 1, cy));
            stack.Push((cx, cy + 1)); stack.Push((cx, cy - 1));
        }
        FlushAll();
    }

    void FlushAll()
    {
        Bitmap.WritePixels(new Int32Rect(0, 0, GridSize, GridSize), _buf, GridSize * 4, 0);
        PixelChanged?.Invoke(0, 0);
    }

    public Color Pick(int x, int y)
    {
        if (x < 0 || y < 0 || x >= GridSize || y >= GridSize) return Colors.Transparent;
        int i = (y * GridSize + x) * 4;
        return Color.FromArgb(_buf[i + 3], _buf[i + 2], _buf[i + 1], _buf[i]);
    }

    public void Clear()
    {
        System.Array.Clear(_buf, 0, _buf.Length);
        _history.Clear();
        FlushAll();
    }

    public bool Undo()
    {
        if (_history.Count == 0) return false;
        var prev = _history[^1];
        _history.RemoveAt(_history.Count - 1);
        System.Array.Copy(prev, _buf, _buf.Length);
        FlushAll();
        return true;
    }

    /// <summary>是否已有内容（扫描 alpha 通道非 0）。</summary>
    public bool HasContent()
    {
        for (int i = 3; i < _buf.Length; i += 4)
            if (_buf[i] != 0) return true;
        return false;
    }

    /// <summary>最近邻重采样：srcSize×srcSize(BGRA) → dstSize×dstSize。</summary>
    internal static byte[] ResampleNearest(byte[] src, int srcSize, int dstSize)
    {
        var dst = new byte[dstSize * dstSize * 4];
        for (int y = 0; y < dstSize; y++)
        {
            int sy = y * srcSize / dstSize;
            for (int x = 0; x < dstSize; x++)
            {
                int sx = x * srcSize / dstSize;
                int si = (sy * srcSize + sx) * 4;
                int di = (y * dstSize + x) * 4;
                dst[di] = src[si]; dst[di + 1] = src[si + 1];
                dst[di + 2] = src[si + 2]; dst[di + 3] = src[si + 3];
            }
        }
        return dst;
    }

    /// <summary>改为新分辨率，旧内容最近邻缩放保留；清空撤销历史。</summary>
    public void Resize(int newSize)
    {
        if (newSize == GridSize) return;
        if (newSize < 8) newSize = 8;
        if (newSize > 1080) newSize = 1080;
        var old = GridSize;
        var oldBuf = _buf;
        GridSize = newSize;
        _buf = new byte[GridSize * GridSize * 4];
        Array.Clear(_buf, 0, _buf.Length);
        var resized = ResampleNearest(oldBuf, old, GridSize);
        Array.Copy(resized, _buf, _buf.Length);
        Bitmap = new WriteableBitmap(GridSize, GridSize, 96, 96, PixelFormats.Bgra32, null);
        Bitmap.WritePixels(new Int32Rect(0, 0, GridSize, GridSize), _buf, GridSize * 4, 0);
        _history.Clear();
        PixelChanged?.Invoke(0, 0);
    }

    public byte[] ToPng()
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(Bitmap));
        using var ms = new System.IO.MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    public void FromPng(byte[] png)
    {
        try
        {
            var img = new BitmapImage();
            using (var ms = new System.IO.MemoryStream(png))
            {
                img.BeginInit(); img.CacheOption = BitmapCacheOption.OnLoad; img.StreamSource = ms; img.EndInit();
            }
            var src = new FormatConvertedBitmap(img, PixelFormats.Bgra32, null, 0);
            var w = GridSize; var h = GridSize;
            if (src.PixelWidth != GridSize || src.PixelHeight != GridSize)
            {
                // 缩放到 GridSize（使用 RenderTargetBitmap 硬缩放）
                var rtb = new RenderTargetBitmap(GridSize, GridSize, 96, 96, PixelFormats.Default);
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                    dc.DrawImage(src, new Rect(0, 0, GridSize, GridSize));
                rtb.Render(dv);
                src = new FormatConvertedBitmap(rtb, PixelFormats.Bgra32, null, 0);
            }
            var stride = src.PixelWidth * 4;
            var px = new byte[stride * src.PixelHeight];
            src.CopyPixels(px, stride, 0);
            System.Array.Copy(px, _buf, System.Math.Min(_buf.Length, px.Length));
            Bitmap = new WriteableBitmap(GridSize, GridSize, 96, 96, PixelFormats.Bgra32, null);
            Bitmap.WritePixels(new Int32Rect(0, 0, GridSize, GridSize), _buf, GridSize * 4, 0);
            PixelChanged?.Invoke(0, 0);
        }
        catch { }
    }
}
