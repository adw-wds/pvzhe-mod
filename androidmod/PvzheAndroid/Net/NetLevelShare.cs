using System;
using Godot;
using PvzheMod;

/// <summary>
/// M4f：每日/在线关卡文件互传。
///
/// 为什么需要：
///   每日关卡 / 在线关卡的本体是 **房主机器上的 `user://DailyLevel|OnlineLevel/xxx.json`**。
///   客机（尤其手机）本地根本没有这个文件 —— 于是：
///     · 房主选了这个关，客机点「跟随」→ `OnlinePlayFile` 返回 `err:no-file` → **进不去**
///     · 客机的选关页「每日 / 在线」分类永远是空的
///   所以房主开局时要把**关卡文件本身**推给客机。
///
/// 传输：复用 `MsgRelayData(0x20)`（本来就盲转发，中继零改动），子类型 `SyncLevelFile = 0x05`。
///   `[0x05][str key][str jsonText]`
/// ★ 必须在 `MsgBattleStart` 之后发：中继在 `room.Started == false` 时会静默丢弃 E2E 帧。
///   房主是在收到自己那条 BattleStart（Started 已置位）之后才开始推，所以天然满足。
///
/// 只接受 `DailyLevel-` / `OnlineLevel-` 开头的 key（`NetUserLevelPath` 还会挡字符和目录穿越），
/// 不认的 key 一律不落盘 —— 别让对端决定我们写哪个文件。
/// </summary>
public static class NetLevelShare
{
    /// <summary>重复推送间隔（毫秒）。</summary>
    const int RepeatMs = 1200;
    /// <summary>重复推送次数（覆盖载入慢 / 晚到的客机）。</summary>
    const int RepeatTimes = 6;
    /// <summary>客机等待关卡文件的超时（毫秒）。★ 超时不再放弃，只是重新计时（见 Tick）。</summary>
    const int WaitTimeoutMs = 20000;
    /// <summary>客机向房主索要关卡文件的最小间隔（毫秒）。</summary>
    const int AskMs = 2000;
    /// <summary>单次可传的关卡文本上限（BitWriter.WriteStr 用 u16 长度，留足余量）。</summary>
    const int MaxTextLen = 60000;

    // 房主侧
    static string _pushKey = "";
    static string _pushText = "";
    static int _pushSent;
    static int _lastPushMs;

    // 客机侧
    static string _waitKey = "";
    static int _waitSinceMs;
    /// <summary>上次索要关卡文件的时刻 / 累计次数（自愈用）。</summary>
    static int _askLastMs;
    static int _askCount;
    /// <summary>超时提示只报一次，避免刷屏（但等待本身不放弃）。</summary>
    static bool _timeoutNotified;

    public static int SentFiles, RecvFiles;

    public static void Reset()
    {
        _pushKey = "";
        _pushText = "";
        _pushSent = 0;
        _lastPushMs = 0;
        _waitKey = "";
        _waitSinceMs = 0;
        _askLastMs = 0;
        _askCount = 0;
        _timeoutNotified = false;
    }

    /// <summary>客机是否正在等房主的关卡文件。</summary>
    public static bool IsWaiting { get { return _waitKey.Length > 0; } }

    public static string Describe()
    {
        return "关卡文件: 发出=" + SentFiles + " 收到=" + RecvFiles +
               (IsWaiting ? "（等待 " + _waitKey + "）" : "");
    }

    // ===================== 房主：推送 =====================

    /// <summary>房主：准备把选中关卡的文件推给客机（不是 user:// 关卡 / 读不到文件时什么都不做）。</summary>
    public static void StartPush(string key)
    {
        _pushKey = "";
        _pushText = "";
        _pushSent = 0;
        _lastPushMs = 0;
        try
        {
            if (string.IsNullOrEmpty(key)) return;
            if (GameCheats.NetUserLevelDir(key).Length == 0) return;    // res:// / saveKey 关卡不需要传
            string txt = GameCheats.NetReadUserLevelFile(key);
            if (string.IsNullOrEmpty(txt)) { NetLog.Warn("关卡文件: 本机读不到 " + key + "，无法推送给客机"); return; }
            if (txt.Length > MaxTextLen) { NetLog.Warn("关卡文件: " + key + " 太大（" + txt.Length + " 字符），跳过"); return; }
            _pushKey = key;
            _pushText = txt;
            NetLog.Info("关卡文件: 准备推送 " + key + "（" + txt.Length + " 字符，" + RepeatTimes + " 次）");
        }
        catch { }
    }

    // ===================== 客机：等待 =====================

    /// <summary>客机：本地没有这个 user:// 关卡文件 → 登记等待房主推送。
    /// 返回 true = 需要等（调用方应该先别进关，进了也进不去）。</summary>
    public static bool BeginWait(string key)
    {
        try
        {
            // ★ 这三个提前 return 原来是**静默**的 —— 客机不等待时日志里一点痕迹都没有，
            //   只能看到“房主在推、客机没在等”，查不出是哪个条件挡的。现在每个都打日志。
            if (string.IsNullOrEmpty(key))
            {
                NetLog.Warn("关卡文件: BeginWait 跳过 —— key 为空");
                return false;
            }
            if (GameCheats.NetUserLevelDir(key).Length == 0)
            {
                NetLog.Warn("关卡文件: BeginWait 跳过 —— NetUserLevelDir(\"" + key + "\") 为空（判定成不是 user:// 关卡）");
                return false;
            }
            if (GameCheats.NetHasUserLevelFile(key))
            {
                NetLog.Info("关卡文件: BeginWait 跳过 —— 本地已有 " + key + "，不用等房主");
                return false;
            }
            if (_waitKey == key) return true;                                // 已经在等这个
            _waitKey = key;
            _waitSinceMs = NowMs();
            NetLog.Info("关卡文件: 本地缺少 " + key + "，等房主推送");
            return true;
        }
        catch { return false; }
    }

    // ===================== 驱动 =====================

    /// <summary>主循环调用（OnFrame 里紧接 NetSun.Tick 之后）。</summary>
    public static void Tick()
    {
        try
        {
            if (!NetSession.InRoom || NetSession.State == NetState.Offline || !NetSession.Started) return;
            int now = NowMs();

            // 房主：重复推几次
            if (NetSession.IsHost)
            {
                if (_pushKey.Length == 0) return;
                if (_pushSent >= RepeatTimes) { _pushKey = ""; _pushText = ""; return; }
                if (_lastPushMs != 0 && now - _lastPushMs < RepeatMs) return;
                // 预算不足就不算一次（_pushSent 不动），下次 Tick 再试
                if (!Send(_pushKey, _pushText)) return;
                _lastPushMs = now;
                _pushSent++;
                return;
            }

            // 客机：文件到了就进关；没到就继续等，并且主动向房主索要。
            // ★ 自愈入口：不管是 BeginWait 没被调用、判定条件跳过、还是开局消息晚到，
            //   只要「已开战 + 房主要的是 user:// 关卡 + 本机没这个文件」，就一定会走到这里索要。
            //   房主收到 SyncLevelAsk 会重新 StartPush → 客机不会再因为错过一次时序就永远进不去。
            if (_waitKey.Length == 0)
            {
                string bk = NetBattle.LevelKey ?? "";
                if (bk.Length > 0
                    && GameCheats.NetUserLevelDir(bk).Length > 0
                    && !GameCheats.NetHasUserLevelFile(bk))
                {
                    if (_askCount == 0 || now - _askLastMs >= AskMs)
                    {
                        _askLastMs = now;
                        _askCount++;
                        AskHost(bk);
                        if (_askCount <= 3)
                            NetLog.Info("关卡文件: 本机缺 " + bk + "，向房主索要（第 " + _askCount + " 次）");
                    }
                    _waitKey = bk;          // 转入正常等待流程（文件到了由下面这段负责进关）
                    _waitSinceMs = now;
                    _timeoutNotified = false;
                }
                return;
            }
            if (GameCheats.NetHasUserLevelFile(_waitKey))
            {
                string k = _waitKey;
                _waitKey = "";
                _askCount = 0;
                NetLog.Info("关卡文件: 已收到 " + k + "，开始跟随进关");
                NetSession.PostNotice("已收到房主的关卡文件，正在进入…");
                NetBattle.EnterFollowedLevel(k);
            }
            else if (now - _waitSinceMs > WaitTimeoutMs)
            {
                // ★ 不放弃：重新计时继续等，只把提示压成一次
                _waitSinceMs = now;
                if (!_timeoutNotified)
                {
                    _timeoutNotified = true;
                    NetLog.Warn("关卡文件: 等 " + _waitKey + " 超时，继续重试");
                    NetSession.PostNotice("还在等待房主的关卡文件…");
                }
                AskHost(_waitKey);          // 超时后主动催一次
            }
        }
        catch { }
    }

    static int NowMs()
    {
        return (int)(Time.GetTicksMsec() & 0x7FFFFFFF);
    }

    static bool Send(string key, string text)
    {
        if (!NetBudget.TryTake()) return false;
        try
        {
            var w = new BitWriter();
            w.WriteByte(NetProto.SyncLevelFile);
            w.WriteStr(key);
            w.WriteStr(text);
            NetSession.SendSync(w.ToArray());
            SentFiles++;
            return true;
        }
        catch { return true; }
    }

    /// <summary>客机：主动向房主索要某个关卡文件（房主收到会重新推送）。</summary>
    static void AskHost(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (!NetSession.InRoom || NetSession.IsHost) return;
        if (!NetBudget.TryTake()) return;
        try
        {
            var w = new BitWriter();
            w.WriteByte(NetProto.SyncLevelAsk);
            w.WriteStr(key);
            NetSession.SendSync(w.ToArray());
        }
        catch { }
    }

    /// <summary>房主：收到客机的索要 → 重新推送该关卡文件（自愈）。</summary>
    public static void OnRemoteAsk(BitReader r)
    {
        try
        {
            if (!NetSession.IsHost) return;
            string key = r.ReadStr();
            if (string.IsNullOrEmpty(key)) return;
            if (GameCheats.NetUserLevelDir(key).Length == 0) return;
            NetLog.Info("关卡文件: 客机索要 " + key + "，重新推送");
            StartPush(key);
        }
        catch { }
    }

    /// <summary>收到关卡文件（NetSession 已剥掉子类型首字节）。</summary>
    public static void OnRemote(BitReader r)
    {
        try
        {
            if (NetSession.IsHost) return;              // 只有房主会推
            string key = r.ReadStr();
            string text = r.ReadStr();
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(text)) return;
            if (text.Length > MaxTextLen) return;
            // ★ 只认每日/在线关卡：别让对端指定我们要写哪个文件（配合 NetUserLevelPath 的字符白名单）
            if (GameCheats.NetUserLevelDir(key).Length == 0) { NetLog.Warn("关卡文件: 拒绝未知 key " + key); return; }
            if (!GameCheats.NetWriteUserLevelFile(key, text)) { NetLog.Warn("关卡文件: 写入失败 " + key); return; }
            RecvFiles++;
            NetLog.Info("关卡文件: 已写入 " + key + "（" + text.Length + " 字符）");
        }
        catch { }
    }
}
