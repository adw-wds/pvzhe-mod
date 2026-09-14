using System;
using Godot;
using PvzheMod;

/// <summary>
/// M4c：阳光共享（合作模式两边共用一个经济池）。
///
/// 为什么不用"房主绝对值覆盖客机"：
///   那样客机刚捡起来的阳光会被下一秒的状态同步抹掉 —— 玩家反馈过「客机捡阳光不算数」。
///   而且双方各自在跑模拟（向日葵各自产阳光），单方面覆盖必然和本地模拟打架。
///
/// 这里用**增量共享**：
///   1. 每一方只上报自己本地的阳光**变化量**（捡的、种的、向日葵产的）；
///   2. 收到对方的增量就加到自己钱袋上；
///   3. 双方都能采光、都能花钱，总额 = 两边之和，谁捡的都算数；
///   4. 每 3 秒广播一次绝对值做兜底纠偏（只有差异 &gt; 25 才纠正，避免来回抖）。
///
/// 传输复用 MsgRelayData，子类型 0x04。
/// </summary>
public static class NetSun
{
    /// <summary>增量上报的最小间隔（毫秒）——攒一小会儿再发，避免每帧发包。</summary>
    const int SendMs = 200;
    /// <summary>绝对值纠偏周期（毫秒）。</summary>
    const int ResyncMs = 3000;
    /// <summary>纠偏阈值：差距不超过这个值就不动（防止和本地模拟来回打架）。</summary>
    const long CorrectThreshold = 25;

    static long _base = -1;      // 上次记账时的本地阳光
    static long _pending;        // 攒着还没发出去的本地增量
    static int _lastSendMs;
    static int _lastResyncMs;

    public static int SentDeltas, RecvDeltas, Corrections;
    public static long LastDelta;

    public static void Reset()
    {
        _base = -1;
        _pending = 0;
        _lastSendMs = 0;
        _lastResyncMs = 0;
    }

    /// <summary>主循环调用（OnFrame 里紧接 NetMirror.Tick 之后）。</summary>
    public static void Tick()
    {
        try
        {
            if (!NetSession.InRoom || NetSession.State == NetState.Offline || !NetSession.Started)
            {
                Reset();
                return;
            }

            long cur = GameCheats.NetGetSun();
            if (cur < 0) { _base = -1; _pending = 0; return; }   // 不在战斗里（进/出关卡都会走到这）

            if (_base < 0) { _base = cur; _lastSendMs = 0; return; }   // 刚进关卡：只记账，不上报

            long d = cur - _base;
            if (d != 0) { _pending += d; _base = cur; }

            int now = (int)(Time.GetTicksMsec() & 0x7FFFFFFF);

            if (_pending != 0 && now - _lastSendMs >= SendMs)
            {
                // 预算不足就留着继续攒（SendDelta 返回 false），下次 Tick 再试
                if (SendDelta(_pending))
                {
                    _lastSendMs = now;
                    _pending = 0;
                }
            }

            if (now - _lastResyncMs >= ResyncMs)
            {
                if (SendAbsolute(cur)) _lastResyncMs = now;
            }
        }
        catch { }
    }

    public static string Describe()
    {
        return "阳光共享: 本地=" + _base + " 待上报=" + _pending +
               " 发出增量=" + SentDeltas + " 收到增量=" + RecvDeltas + " 纠偏=" + Corrections;
    }

    // ===================== 发送 =====================

    static bool SendDelta(long d)
    {
        if (d == 0) return true;
        if (!NetBudget.TryTake()) return false;
        try
        {
            var w = new BitWriter();
            w.WriteByte(NetProto.SyncSun);
            w.WriteByte(0);                                   // 0 = 增量
            w.WriteU32(unchecked((uint)(int)d));
            NetSession.SendSync(w.ToArray());
            SentDeltas++;
            LastDelta = d;
            return true;
        }
        catch { return true; }
    }

    static bool SendAbsolute(long v)
    {
        if (!NetBudget.TryTake()) return false;
        try
        {
            var w = new BitWriter();
            w.WriteByte(NetProto.SyncSun);
            w.WriteByte(1);                                   // 1 = 绝对值
            w.WriteU32(unchecked((uint)(int)v));
            NetSession.SendSync(w.ToArray());
            return true;
        }
        catch { return true; }
    }

    // ===================== 接收 =====================

    /// <summary>收到对方的阳光消息（NetSession 已剥掉子类型首字节）。</summary>
    public static void OnRemote(BitReader r)
    {
        try
        {
            byte mode = r.ReadByte();
            long v = unchecked((int)r.ReadU32());

            long cur = GameCheats.NetGetSun();
            if (cur < 0) return;                              // 自己不在战斗里，无视

            if (mode == 0)
            {
                // 增量：直接加到本地钱袋
                if (v == 0) return;
                long t = cur + v;
                if (t < 0) t = 0;
                GameCheats.NetSetSun(t);
                _base = t;
                RecvDeltas++;
            }
            else
            {
                // 绝对值：只在差异明显时才纠正（把还没上报的本地增量算进去，别抹掉自己的收益）
                long target = v + _pending;
                if (target < 0) target = 0;
                long diff = cur - target;
                if (diff < 0) diff = -diff;
                if (diff > CorrectThreshold)
                {
                    GameCheats.NetSetSun(target);
                    _base = target;
                    Corrections++;
                }
            }
        }
        catch { }
    }
}
