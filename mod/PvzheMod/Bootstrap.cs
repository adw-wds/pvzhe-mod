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
            // 必须在 Load 之前取基线：这样「启动恢复配置」才会被识别成变化，汇总成一条提示
            ChangeLog.Prime();
            ModSettings.Load();
            Log("===== PvzheMod Init 被调用 ==== Settings已加载");
        }

        // ==================== 被吞异常的追踪（全局钩子方案已放弃） ====================
        // 背景：全工程有 600+ 处 catch { }（许多是故意吞的，比如每帧入口抛一个异常会让主循环停摆）。
        // 问题不是“吞”，而是“吞了不留痕”—— 功能莫名不生效时什么都查不到。
        //
        // 【教训 2026-09-18｜禁止重犯】曾用 AppDomain.FirstChanceException 做全局钩子
        //   （一处覆盖 600 处 catch），结果游戏**卡在进主菜单之前**。根因：
        //     该事件参数类型 FirstChanceExceptionEventArgs 已被 AOT 裁剪
        //     （游戏目录 System.Runtime.dll 门面里查无此类型），于是在 MOD 里
        //     **仅仅声明**一个带该参数的方法签名，就足以让游戏自身的反射枚举炸掉：
        //       SceneManager.ChangeScene -> ResourceManager.ReleaseTransientResources
        //         -> TransientStaticTextureRelease..cctor() -> DiscoverTextureFields()
        //         -> TypeLoadException -> TypeInitializationException -> 每次切场景都失败
        //   致命之处：**与调用点无关**。外层 try/catch 完全拦不住（异常抛在别人的静态构造里），
        //   所以“被裁掉时降级”的兜底写法是假安全。
        //   规则：任何对已裁剪类型的引用（哪怕是签名、哪怕永不调用）都不允许出现在 MOD 中。
        //   本运行时亦确认不存在该类型，故全局吞异常追踪在此方案下不可行。
        //   要排查静默失效，请在被怀疑的 catch 里手动调用 LogSwallowed(tag, ex)。

        private static readonly System.Collections.Generic.List<string> _swallowOnce =
            new System.Collections.Generic.List<string>();

        /// <summary>只记一次的吞异常日志（带去重上限，防刷屏）。</summary>
        public static void LogSwallowed(string tag, Exception ex)
        {
            try
            {
                lock (_logLock)
                {
                    if (_swallowOnce.Contains(tag)) return;
                    if (_swallowOnce.Count >= 300) return;   // 硬上限，防止异常风暴拖垮日志
                    _swallowOnce.Add(tag);
                }
                Log("[吞掉] " + tag + ": " + (ex == null ? "<null>" : ex.Message));
                Log("[吞掉] st: " + (ex == null ? "" : ex.StackTrace));
            }
            catch { }
        }

        /// <summary>取堆栈里第一条有意义的帧（跳过首行的“异常类型: 消息”）。</summary>
        private static string FirstStackFrame(string st)
        {
            try
            {
                int i = st.IndexOf("\n");
                if (i < 0 || i + 1 >= st.Length) return st;
                var rest = st.Substring(i + 1);
                int j = rest.IndexOf("\n");
                var line = (j < 0 ? rest : rest.Substring(0, j)).Trim();
                if (line.Length > 160) line = line.Substring(0, 160);
                return line;
            }
            catch { return "<st>"; }
        }
    }
}
