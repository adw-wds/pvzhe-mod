using System;
using System.Collections.Generic;
using Godot;

/// <summary>
/// 出站帧预算（客户端自律）。
///
/// 为什么必须有：
///   中继的限流是 <c>NetConstants.RateLimitPerSec = 60</c> 帧/秒、令牌桶容量 <c>RateBurst = 120</c>，
///   **超限不是丢帧，而是记违规**（`RelayServer` 的 `AddViolation("发送过快")`），
///   累计 <c>ViolationLimit = 20</c> 次就 **断开连接 + 给 IP 上 `IpCooldownSec = 300` 秒冷却**。
///   于是 ip 被冷却后连重连都会被「直接拒绝」—— 玩家看到的就是「一进关就断开，然后一直连不上」。
///
/// 之前为什么会超：`NetMirror.ResyncMine` 每 2 秒一次性最多发 100 帧（≈50/s），
///   再叠上光标 10/s + 阳光 10/s + 心跳 ≈ 72/s > 60/s → 桶抽干后每帧都违规 → 2 秒内满 20 次被踢。
///
/// 策略：**所有"可以由差分补回来"的帧都先过这里**，超预算就这一帧不发、下一帧自然重试。
///   预算取 38/s（远低于中继 60/s），给 Ping / 状态同步 / 建房信令留足余量。
/// </summary>
public static class NetBudget
{
    /// <summary>本机每秒允许发送的"可延迟帧"上限（中继是 60，这里留一大截余量）。</summary>
    public const int PerSec = 38;

    static readonly Queue<int> _stamps = new Queue<int>();

    /// <summary>因为超预算被推迟的帧数（诊断用；这些帧都会在后续帧补发）。</summary>
    public static int Deferred;

    public static void Reset()
    {
        _stamps.Clear();
    }

    /// <summary>取一个发送额度。返回 false = 这一帧先别发（下一帧自然重试）。</summary>
    public static bool TryTake()
    {
        try
        {
            int now = (int)(Time.GetTicksMsec() & 0x7FFFFFFF);
            while (_stamps.Count > 0 && now - _stamps.Peek() >= 1000) _stamps.Dequeue();
            if (_stamps.Count >= PerSec) { Deferred++; return false; }
            _stamps.Enqueue(now);
            return true;
        }
        catch { return true; }   // 出错就不限制，别把功能卡住
    }

    public static int Used { get { return _stamps.Count; } }

    public static string Describe()
    {
        return "发送预算 " + _stamps.Count + "/" + PerSec + " 推迟=" + Deferred;
    }
}
