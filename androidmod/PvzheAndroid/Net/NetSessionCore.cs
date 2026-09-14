/// <summary>联机会话状态（纯逻辑，可单测）。</summary>
public enum NetState { Offline, Lobby, Signaling, Direct, Relay, Playing, Reconnecting, Ended }

public static class NetSessionCore
{
    /// <summary>状态转换白名单，避免非法跳转（例如 Offline 直接 Playing）。</summary>
    public static bool CanTransition(NetState from, NetState to)
    {
        switch (from)
        {
            case NetState.Offline: return to == NetState.Lobby || to == NetState.Signaling;
            case NetState.Lobby: return to == NetState.Signaling || to == NetState.Ended || to == NetState.Offline;
            case NetState.Signaling: return to == NetState.Direct || to == NetState.Relay || to == NetState.Lobby || to == NetState.Ended || to == NetState.Offline;
            case NetState.Direct:
            case NetState.Relay: return to == NetState.Playing || to == NetState.Reconnecting || to == NetState.Ended || to == NetState.Offline;
            case NetState.Playing: return to == NetState.Reconnecting || to == NetState.Ended || to == NetState.Offline;
            case NetState.Reconnecting: return to == NetState.Playing || to == NetState.Ended || to == NetState.Offline;
            case NetState.Ended: return to == NetState.Offline || to == NetState.Lobby || to == NetState.Signaling;
            default: return false;
        }
    }

    /// <summary>信令阶段直连超时：有中继则回落中继，否则结束会话。</summary>
    public static NetState NextOnDirectTimeout(NetState cur, bool relayAvailable)
        => relayAvailable ? NetState.Relay : NetState.Ended;

    /// <summary>直连建立成功。</summary>
    public static NetState NextOnDirectEstablished(NetState cur) => NetState.Direct;
}
