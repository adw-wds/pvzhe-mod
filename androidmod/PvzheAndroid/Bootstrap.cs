using System;
using System.IO;

namespace PvzheMod
{
    /// <summary>
    /// mod 入口：被合并进主程序集后由注入点调用。
    /// 不引用任何外部库（Godot 无法加载主程序集以外的 dll）。
    /// </summary>
    public static class Bootstrap
    {
        private static string _logPath;   // 手机：user:// 实际目录（应用私有，可写）；电脑兜底工作区路径
        private static readonly System.Text.StringBuilder _logBuf = new System.Text.StringBuilder();
        private static int _logCount;
        private static readonly object _logLock = new object();

        /// <summary>写日志（内存缓冲，每 50 条/4KB 批量落盘一次——避免每帧 AppendAllText 打开关闭文件拖慢游戏）。</summary>
        public static void Log(string msg)
        {
            try
            {
                if (_logPath == null)
                {
                    string dir = null;
                    try { dir = Godot.OS.GetUserDataDir(); } catch { }
                    if (string.IsNullOrEmpty(dir)) dir = @"mod";
                    _logPath = dir + "/mod_log.txt";
                }
                lock (_logLock)
                {
                    _logBuf.Append(msg).Append("\r\n");
                    // ★ 手机上 user:// 在应用私有目录，adb 拉不到（run-as 也因未开 debug 不可用）。
                    //   镜像一份到 logcat：`adb logcat -s godot` 就能看到 mod 的全部日志。
                    try { Godot.GD.Print("[PvzheMod] " + msg); } catch { }
                    if (++_logCount >= 50 || _logBuf.Length >= 4096) FlushLog();
                }
            }
            catch { }
        }

        /// <summary>把缓冲日志落盘（OnFrame 定期调用，确保退出前不丢日志）。</summary>
        public static void FlushLog()
        {
            try
            {
                lock (_logLock)
                {
                    if (_logPath == null || _logBuf.Length == 0) return;
                    File.AppendAllText(_logPath, _logBuf.ToString());
                    _logBuf.Clear();
                    _logCount = 0;
                }
            }
            catch { }
        }

        public static void Init()
        {
            ModSettings.Load();
            Log("===== PvzheMod Init 被调用 ==== Settings已加载");
        }
    }
}
