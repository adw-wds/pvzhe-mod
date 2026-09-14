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
        private static string LogPath = @"mod\mod_log.txt";
        private static readonly System.Text.StringBuilder _logBuf = new System.Text.StringBuilder();
        private static int _logCount;
        private static readonly object _logLock = new object();

        /// <summary>写日志（内存缓冲，每 50 条/4KB 批量落盘一次——避免每帧 AppendAllText 打开关闭文件拖慢游戏）。</summary>
        public static void Log(string msg)
        {
            try
            {
                lock (_logLock)
                {
                    _logBuf.Append(msg).Append("\r\n");
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
                    if (_logBuf.Length == 0) return;
                    // 注意：合并进主程序集后带 Encoding 的重载可能 MissingMethod（.NET9），必须用无参重载
                    File.AppendAllText(LogPath, _logBuf.ToString());
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
