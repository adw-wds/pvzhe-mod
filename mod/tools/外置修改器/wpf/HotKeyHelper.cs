using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace PvzheRemote
{
    /// <summary>全局快捷键（RegisterHotKey）封装。用于 Alt+F8 快速呼出/隐藏窗口。</summary>
    public static class HotKeyHelper
    {
        const int MOD_ALT = 0x0001;
        const int MOD_CONTROL = 0x0002;
        const int WM_HOTKEY = 0x0312;

        [DllImport("user32.dll")]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public const int HOTKEY_ID = 0x5051;   // "PV" 任意
        static HwndSource? _src;

        /// <summary>注册热键。key = 虚拟键码（如 (uint)Key.F8 需转义）。</summary>
        public static bool Register(Window win, System.Windows.Input.Key key, Action callback)
        {
            IntPtr hwnd = new WindowInteropHelper(win).Handle;
            _src = HwndSource.FromHwnd(hwnd);
            if (_src == null) return false;
            _src.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (msg == WM_HOTKEY && w.ToInt32() == HOTKEY_ID)
                {
                    callback();
                    handled = true;
                }
                return IntPtr.Zero;
            });
            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            return RegisterHotKey(hwnd, HOTKEY_ID, MOD_ALT, vk);
        }

        public static void Unregister(Window win)
        {
            IntPtr hwnd = new WindowInteropHelper(win).Handle;
            UnregisterHotKey(hwnd, HOTKEY_ID);
        }
    }
}
