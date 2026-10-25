using Godot;
using System;

namespace PvzheMod
{
    /// <summary>
    /// 内置外置程序的安装引导：
    /// 游戏 APK 的 assets 里带了 pvzhe_remote.apk，首次进入游戏后调用
    /// OS.ShellOpen("res://pvzhe_remote.apk") —— Godot 会用自带的 FileProvider +
    /// MimeTypeMap（.apk → application/vnd.android.package-archive）拉起【系统安装程序】。
    /// 系统仍会弹一次「是否安装」，这是 Android 的硬限制，绕不过去。
    /// </summary>
    public static class RemoteInstaller
    {
        static bool _done;
        static ulong _firstFrame;

        public static void Tick()
        {
            if (_done) return;
            try
            {
                ulong now = Engine.GetProcessFrames();
                if (_firstFrame == 0) _firstFrame = now;
                // 等 3 秒再弹，别跟游戏启动/资源加载抢
                if (now - _firstFrame < 180) return;
                _done = true;

                if (Godot.FileAccess.FileExists("user://remote_install_prompted.txt"))
                {
                    Bootstrap.Log("内置安装器: 已提示过，跳过");
                    return;
                }
                if (!Godot.FileAccess.FileExists("res://pvzhe_remote.apk"))
                {
                    Bootstrap.Log("内置安装器: 找不到 res://pvzhe_remote.apk");
                    return;
                }

                Error e = Godot.OS.ShellOpen("res://pvzhe_remote.apk");
                Bootstrap.Log("内置安装器: ShellOpen(res://) -> " + e);

                if (e != Error.Ok)
                {
                    // 回退：复制到 user:// 再交给系统（Godot 会转成 content:// 并带 MIME）
                    try
                    {
                        var src = Godot.FileAccess.Open("res://pvzhe_remote.apk", Godot.FileAccess.ModeFlags.Read);
                        if (src != null)
                        {
                            long len = (long)src.GetLength();
                            byte[] buf = src.GetBuffer(len);
                            src.Close();
                            var dst = Godot.FileAccess.Open("user://pvzhe_remote.apk", Godot.FileAccess.ModeFlags.Write);
                            if (dst != null)
                            {
                                dst.StoreBuffer(buf);
                                dst.Close();
                                Error e2 = Godot.OS.ShellOpen("user://pvzhe_remote.apk");
                                Bootstrap.Log("内置安装器: 回退 user:// -> " + e2);
                                if (e2 == Error.Ok) e = e2;
                            }
                        }
                    }
                    catch (Exception ex) { Bootstrap.Log("内置安装器回退异常: " + ex.Message); }
                }

                if (e == Error.Ok)
                {
                    var fa = Godot.FileAccess.Open("user://remote_install_prompted.txt", Godot.FileAccess.ModeFlags.Write);
                    if (fa != null)
                    {
                        fa.StoreString(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                        fa.Close();
                    }
                }
            }
            catch (Exception ex) { Bootstrap.Log("内置安装器异常: " + ex.Message); }
        }
    }
}
