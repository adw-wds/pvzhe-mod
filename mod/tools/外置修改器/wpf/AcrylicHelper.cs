using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PvzheRemote
{
    /// <summary>
    /// 窗口背景高斯模糊（白色毛玻璃）。
    /// 优先 Win11 22H2+ 的 DWM SystemBackdrop（TransientWindow = 高斯模糊，无着色）；
    /// 回退 Win10 的 SetWindowCompositionAttribute（ACCENT_ENABLE_BLURBEHIND = 高斯模糊）。
    /// 背景的白色 50% 由 WPF 窗口 Background 控制（半透明白色画刷），两层叠加即"白色高斯模糊"。
    /// 注意：窗口必须 WindowStyle=None，且不能 AllowsTransparency=true（会禁掉 DWM 特效）。
    /// </summary>
    public static class AcrylicHelper
    {
        // ---- Win11 22H2+ DWM API ----
        const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        const int DWMWCP_ROUND = 2;
        const int DWMSBT_TRANSIENTWINDOW = 2;   // 高斯模糊（无着色）

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // ---- Win10 fallback: SetWindowCompositionAttribute ----
        const int ACCENT_ENABLE_BLURBEHIND = 3;
        const int WCA_ACCENT_POLICY = 19;

        [StructLayout(LayoutKind.Sequential)]
        struct AccentPolicy
        {
            public int AccentState;
            public int AccentFlags;
            public int GradientColor;
            public int AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        [DllImport("user32.dll")]
        static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        /// <summary>为窗口启用背景高斯模糊（白色毛玻璃由 WPF 半透明白色背景呈现）。</summary>
        public static void Enable(Window win)
        {
            IntPtr hwnd = new WindowInteropHelper(win).Handle;

            // Win11 22H2+：DWM 高斯模糊背景（TransientWindow）
            int backdrop = DWMSBT_TRANSIENTWINDOW;
            if (DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) == 0)
            {
                int dark = 0;   // 浅色标题栏（配合白底）
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
                int corner = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
                return;
            }

            // Win10：组合器 BlurBehind（高斯模糊）
            var accent = new AccentPolicy { AccentState = ACCENT_ENABLE_BLURBEHIND, AccentFlags = 0 };
            var data = new WindowCompositionAttributeData
            {
                Attribute = WCA_ACCENT_POLICY,
                SizeOfData = Marshal.SizeOf(accent),
            };
            IntPtr p = Marshal.AllocHGlobal(data.SizeOfData);
            try
            {
                Marshal.StructureToPtr(accent, p, false);
                data.Data = p;
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(p);
            }
        }
    }
}
