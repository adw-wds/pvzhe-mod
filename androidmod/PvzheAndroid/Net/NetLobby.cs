using System.Collections.Generic;

/// <summary>
/// 大厅里的一个房间（中继下发的公开信息）。
/// 只含房间码/房主昵称/人数/是否已开始/是否需要口令/剩余时间 —— **不含任何 IP**。
/// </summary>
public class RoomInfo
{
    public string Code = "";
    public string HostNick = "";
    public byte Players;
    public byte Max;
    public bool Started;
    public bool NeedPass;
    /// <summary>房主是否允许使用修改器（大厅列表里展示）。</summary>
    public bool AllowCheats = true;
    /// <summary>剩余存活分钟（0xFFFF = 不限制）。</summary>
    public ushort TtlMinutesLeft;

    public bool Joinable { get { return !Started && Players < Max; } }
}

/// <summary>房间列表（MsgRoomList）编解码：count + [code:str, hostNick:str, players:u8, max:u8, flags:u8, ttl:u16]×N。</summary>
public static class RoomListCodec
{
    public const byte FlagStarted = 1;
    public const byte FlagNeedPass = 2;
    public const byte FlagNoCheats = 4;
    public const ushort TtlUnlimited = 0xFFFF;

    /// <summary>单帧最多能放多少个房间（受 MaxFrame 限制，按最坏情况估算：昵称 12 字 → 3+24+2+2+1+2 ≈ 34 字节）。</summary>
    public const int MaxRoomsPerFrame = 48;

    public static byte[] Write(List<RoomInfo> rooms)
    {
        var w = new BitWriter();
        int n = rooms?.Count ?? 0;
        if (n > MaxRoomsPerFrame) n = MaxRoomsPerFrame;
        w.WriteByte((byte)n);
        for (int i = 0; i < n; i++)
        {
            var r = rooms[i] ?? new RoomInfo();
            w.WriteStr(r.Code ?? "");
            w.WriteStr(PeerListCodec.Shorten(r.HostNick));
            w.WriteByte(r.Players);
            w.WriteByte(r.Max);
            byte flags = 0;
            if (r.Started) flags |= FlagStarted;
            if (r.NeedPass) flags |= FlagNeedPass;
            if (!r.AllowCheats) flags |= FlagNoCheats;
            w.WriteByte(flags);
            w.WriteU16(r.TtlMinutesLeft);
        }
        return w.ToArray();
    }

    public static List<RoomInfo> Read(byte[] payload)
    {
        var list = new List<RoomInfo>();
        var r = new BitReader(payload);
        int n = r.ReadByte();
        for (int i = 0; i < n; i++)
        {
            var info = new RoomInfo();
            info.Code = r.ReadStr();
            info.HostNick = r.ReadStr();
            info.Players = r.ReadByte();
            info.Max = r.ReadByte();
            byte flags = r.ReadByte();
            info.Started = (flags & FlagStarted) != 0;
            info.NeedPass = (flags & FlagNeedPass) != 0;
            info.AllowCheats = (flags & FlagNoCheats) == 0;
            info.TtlMinutesLeft = r.ReadU16();
            list.Add(info);
        }
        return list;
    }
}
