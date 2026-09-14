/// <summary>联机日志（统一前缀，便于在 mod_log.txt 里过滤）。全局命名空间以便与中继工程共享文件。</summary>
public static class NetLog
{
    public static void Info(string s) { PvzheMod.Bootstrap.Log("联机: " + s); }
    public static void Warn(string s) { PvzheMod.Bootstrap.Log("联机(警告): " + s); }
    public static void Error(string s) { PvzheMod.Bootstrap.Log("联机(错误): " + s); }
}
