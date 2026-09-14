using System;
using System.Threading;

namespace PvzheRelay
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            int port = NetConstants.RelayDefaultPort;
            int idleMs = 30000;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-port":
                        if (i + 1 < args.Length) int.TryParse(args[++i], out port);
                        break;
                    case "-idle":
                        if (i + 1 < args.Length) int.TryParse(args[++i], out idleMs);
                        break;
                    case "-quiet":
                        break;
                    case "-help":
                    case "--help":
                        Console.WriteLine("用法: PvzheRelay [-port 8231] [-idle 30000]");
                        Console.WriteLine("  中继服务器（杂交版联机 M1）：只做房间/信令/二进制转发，无游戏逻辑。");
                        return 0;
                }
            }

            var srv = new RelayServer(port) { IdleTimeoutMs = idleMs };
            try
            {
                srv.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine("启动失败: " + ex.Message);
                return 1;
            }

            Console.WriteLine("按 Ctrl+C 退出");
            var quit = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Set(); };
            AppDomain.CurrentDomain.ProcessExit += (_, __) => { try { srv.Stop(); } catch { } };
            quit.Wait();
            srv.Stop();
            return 0;
        }
    }
}
