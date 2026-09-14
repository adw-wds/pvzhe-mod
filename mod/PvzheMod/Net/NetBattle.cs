using System;
using Godot;
using PvzheMod;

/// <summary>
/// M2：对局状态权威同步。
/// 房主每秒广播「是否在战斗 / 关卡标识 / 阳光 / 波次 / 僵尸数」；
/// 客机记录房主状态，并在房主开启 AuthoritySync 时把本地阳光校正到房主值。
/// （僵尸/植物的镜像同步属于 M3。）
/// </summary>
public static class NetBattle
{
    /// <summary>房主当前关卡标识（saveKey/name）。</summary>
    public static string LevelKey = "";
    /// <summary>房主阳光（-1 = 未知）。</summary>
    public static long Sun = -1;
    /// <summary>房主当前波次（-1 = 未知）。</summary>
    public static int Wave = -1;
    /// <summary>房主场上僵尸数（-1 = 未知）。</summary>
    public static int ZombieCount = -1;
    /// <summary>房主是否在战斗中。</summary>
    public static bool HostInBattle;
    /// <summary>最近一次收到/发出的同步时刻。</summary>
    public static ulong LastSyncMs;

    static ulong _lastSendMs;
    // 自动结束对局用：房主是否进过战斗 / 离开战斗的起始时刻
    static bool _wasInBattle;
    static ulong _leftBattleMs;

    /// <summary>房主每帧调用：每秒广播一次（未联机时零开销）。</summary>
    public static void HostTick(ulong nowMs)
    {
        if (!NetSession.IsHost || NetSession.State == NetState.Offline) return;
        // 未开战不上报对局状态：中继会丢弃未开战时的游戏数据帧（白传且无意义）
        if (!NetSession.Started) { _wasInBattle = false; _leftBattleMs = 0; return; }
        if (nowMs - _lastSendMs < 1000) return;
        _lastSendMs = nowMs;

        // ★ 房主离开战斗 → 自动结束对局。
        //   旧行为：打完/退出后 Started 一直是 true，必须手点「结束对局」才能开下一局，
        //   而且不开下一局的话第二局状态是残留的（“第二局啥都不同步”）。
        // ★ 但装填/切场景**不算**离开战斗：在线/每日关卡载入很慢，
        //   旧逻辑在载入期间就倒计时 → 5 秒后自动 EndBattle → 客机立刻看到「房主退出」。
        bool loading = GameCheats.NetIsSceneLoading();
        bool inBattle = GameCheats.NetIsInBattleLevel();
        if (inBattle) { _wasInBattle = true; _leftBattleMs = 0; }
        else if (loading) { _leftBattleMs = 0; }
        else if (_wasInBattle)
        {
            if (_leftBattleMs == 0) _leftBattleMs = nowMs;
            else if (nowMs - _leftBattleMs > 12000)
            {
                _wasInBattle = false;
                _leftBattleMs = 0;
                NetLog.Info("房主已离开战斗，自动结束对局");
                NetSession.PostNotice("已离开战斗，对局自动结束（可直接开下一局）");
                NetSession.EndBattle();
                return;
            }
        }

        long sun = GameCheats.NetGetSun();
        int wave = GameCheats.NetGetWave();
        int zc = GameCheats.NetGetZombieCount();
        string key = GameCheats.NetGetLevelRef() ?? "";

        HostInBattle = !(sun < 0 && wave < 0 && zc < 0);
        LevelKey = key;
        Sun = sun;
        Wave = wave;
        ZombieCount = zc;
        LastSyncMs = nowMs;

        var w = new BitWriter();
        w.WriteByte((byte)(HostInBattle ? 1 : 0));
        w.WriteU32((uint)(sun < 0 ? 0 : sun));
        w.WriteU16((ushort)(wave < 0 ? 0 : wave));
        w.WriteU16((ushort)(zc < 0 ? 0 : zc));
        w.WriteStr(key);
        NetSession.BroadcastState(w.ToArray());
    }

    /// <summary>客机：收到房主对局状态。</summary>
    public static void OnStateSync(byte[] payload)
    {
        var r = new BitReader(payload);
        HostInBattle = r.ReadByte() != 0;
        Sun = r.ReadU32();
        Wave = r.ReadU16();
        ZombieCount = r.ReadU16();
        LevelKey = r.ReadStr();
        LastSyncMs = Time.GetTicksMsec();

        // 客机波次跟随房主：两边波次进度/显示一致（僵尸本来就是房主同步过来的）
        if (!NetSession.IsHost && HostInBattle && Wave >= 0) GameCheats.NetSetWaveIndex(Wave);

        // ★ 不再把客机阳光校回房主值。
        //   旧行为：客机每收到一次状态同步（每秒 1 次）就被强制改回房主阳光 →
        //   客机刚捡的阳光瞬间被抹掉，表现就是「客机捡阳光不算数」。
        //   共享战场里两边各自采光、各自种植，阳光本来就该各算各的；
        //   真要共享经济，应该做「双方采集量汇入同一个池」，而不是单方面覆盖。
        //   （Sun 仍照常记录下来，面板会显示房主阳光，方便对比排查。）
    }

    /// <summary>
    /// 对局开始（房主广播 / 房主本地发起时调用）。
    /// ★ 双方都要落到同一关：旧实现只在客机侧进关，而且不检查“是不是已经在别的关里”，
    ///   结果一边还在房间里看聊天、另一边已经玩上了，两边根本不是同一局。
    /// </summary>
    public static void OnBattleStart()
    {
        _lastSendMs = 0;
        LevelKey = NetSession.HostLevelKey ?? "";
        NetMirror.Reset();
        NetSun.Reset();
        NetLevelShare.Reset();

        // ★ 开局先无条件给一条可见提示：以前失败是"静默"的，
        //   玩家只看到「点了开始对局完全没反应」，查不出卡在哪一步。
        NetSession.PostNotice((NetSession.IsHost ? "开始对局：" : "房主开始对局：") +
                             (string.IsNullOrEmpty(LevelKey) ? "(未指定关卡)" : LevelKey));

        if (string.IsNullOrEmpty(LevelKey))
        {
            NetSession.PostNotice("房主已开始对局（未指定关卡，各自跟随当前进度）");
            return;
        }

        // ★ 每日/在线关卡的文件只在房主机器上（user://），客机本地没有：
        //   房主读出来推过去，客机先等着，收到文件再进关（否则 OnlinePlayFile 直接 err:no-file）。
        if (NetSession.IsHost) NetLevelShare.StartPush(LevelKey);
        else if (NetLevelShare.BeginWait(LevelKey))
        {
            NetSession.PostNotice("正在接收房主的关卡文件（每日/在线关卡不在本机）…");
            return;
        }

        string cur = GameCheats.NetGetLevelKey() ?? "";
        if (SameLevel(cur, LevelKey))
        {
            NetSession.PostNotice("已在房主关卡内，开始实体同步");
            return;
        }

        // 客机（以及没在自己关卡里的房主）按房主的关卡标识进关；进关会走排队，等玩法资源就绪
        if (GameCheats.EnterLevelByAnyKey(LevelKey))
            NetSession.PostNotice("正在进入房主关卡：" + LevelKey);
        else
            NetSession.PostNotice("房主关卡不在本机，无法自动进入：" + LevelKey);
    }

    /// <summary>客机：收到房主推来的关卡文件后进关（走与 OnBattleStart 相同的路径）。</summary>
    public static void EnterFollowedLevel(string key)
    {
        try
        {
            if (string.IsNullOrEmpty(key)) return;
            string cur = GameCheats.NetGetLevelKey() ?? "";
            if (SameLevel(cur, key))
            {
                NetSession.PostNotice("已在房主关卡内，开始实体同步");
                return;
            }
            if (GameCheats.EnterLevelByAnyKey(key))
                NetSession.PostNotice("正在进入房主关卡：" + key);
            else
                NetSession.PostNotice("无法进入房主关卡：" + key);
        }
        catch { }
    }

    /// <summary>
    /// 判断两个关卡标识是不是同一关。
    /// HostLevelKey 通常来自 NetGetLevelRef()（优先 res:// 资源路径），
    /// 而本地 NetGetLevelKey() 读到的是 saveKey —— 两者字符串不同，必须做包含/后缀匹配，
    /// 否则客机每次都会被判定成“不在房主关卡”而反复重载。
    /// </summary>
    public static bool SameLevel(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;

        bool aPath = a.IndexOf("res://", StringComparison.Ordinal) >= 0 || a.IndexOf("uid://", StringComparison.Ordinal) >= 0;
        bool bPath = b.IndexOf("res://", StringComparison.Ordinal) >= 0 || b.IndexOf("uid://", StringComparison.Ordinal) >= 0;

        // res://.../Level1_1.tres  ←→  Level1_1
        if (aPath && a.EndsWith(b + ".tres", StringComparison.OrdinalIgnoreCase)) return true;
        if (bPath && b.EndsWith(a + ".tres", StringComparison.OrdinalIgnoreCase)) return true;
        // 路径里带着对方的名字（去掉扩展名再比一次）
        if (aPath && a.IndexOf("/" + b, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (bPath && b.IndexOf("/" + a, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    /// <summary>对局结束（房主广播）：解除同步与镜像，并回主菜单（让结束对局有可见效果）。</summary>
    public static void OnBattleEnd()
    {
        Reset();
        NetMirror.Reset();               // 解除镜像 + 还原本地刷怪（之前漏了 → 客机永远不刷怪）
        NetSun.Reset();                  // 阳光共享复位
        NetLevelShare.Reset();           // 关卡文件推送/等待复位
        NetGate.Reset();                 // 解除等待暂停（否则下一局一直卡在暂停）
        NetCursor.MarkBattleStarted();   // 清掉对局中残留的光标
        GameCheats.BackToMenu();         // 双方退回主菜单
    }

    public static void Reset()
    {
        LevelKey = "";
        Sun = -1;
        Wave = -1;
        ZombieCount = -1;
        HostInBattle = false;
        LastSyncMs = 0;
        _lastSendMs = 0;
    }

    /// <summary>面板显示用的一行摘要。</summary>
    public static string Describe()
    {
        if (NetSession.State == NetState.Offline) return "";
        if (NetSession.IsHost)
            return "房主状态：" + (HostInBattle ? "战斗中" : "未开战") + " 关卡=" + (string.IsNullOrEmpty(LevelKey) ? "?" : LevelKey) +
                   " 阳光=" + (Sun < 0 ? "?" : Sun.ToString()) + " 波次=" + (Wave < 0 ? "?" : Wave.ToString()) +
                   " 僵尸=" + (ZombieCount < 0 ? "?" : ZombieCount.ToString());
        return "房主：" + (HostInBattle ? "战斗中" : "未开战") + " 关卡=" + (string.IsNullOrEmpty(LevelKey) ? "?" : LevelKey) +
               " 阳光=" + (Sun < 0 ? "?" : Sun.ToString()) + " 波次=" + (Wave < 0 ? "?" : Wave.ToString()) +
               " 僵尸=" + (ZombieCount < 0 ? "?" : ZombieCount.ToString());
    }
}
